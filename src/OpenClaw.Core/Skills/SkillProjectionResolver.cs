using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Core.Skills;

/// <summary>
/// Resolves a bound projection contract for a skill against the current request text.
/// </summary>
public static class SkillProjectionResolver
{
    public static SkillProjectionResolution ResolveForRequest(
        SkillDefinition skill,
        string requestText,
        ILogger logger)
    {
        if (skill.ProjectionContracts.Count == 0)
        {
            return new SkillProjectionResolution
            {
                SkillName = skill.Name,
                HasContracts = false
            };
        }

        var normalizedRequest = requestText?.Trim().ToLowerInvariant() ?? string.Empty;
        var matchedAttempts = new List<ProjectionRouteAttempt>(skill.ProjectionContracts.Count);
        var ambiguousAttempts = new List<string>();

        foreach (var contract in skill.ProjectionContracts)
        {
            var attempt = TryResolveContract(skill.Name, contract, normalizedRequest, logger);
            if (attempt is null)
                continue;

            if (attempt.IsAmbiguous)
            {
                if (!string.IsNullOrWhiteSpace(attempt.AmbiguousReason))
                    ambiguousAttempts.Add(attempt.AmbiguousReason);
                continue;
            }

            matchedAttempts.Add(attempt);
        }

        if (matchedAttempts.Count == 0)
        {
            if (ambiguousAttempts.Count > 0)
                return Block(skill.Name, ambiguousAttempts[0]);

            return Block(skill.Name, "Projection topic selection did not produce a usable route for this request.");
        }

        var rankedAttempts = matchedAttempts
            .OrderByDescending(attempt => attempt.Score)
            .ThenByDescending(attempt => attempt.ProducerPriority)
            .ToList();

        if (rankedAttempts.Count > 1 &&
            rankedAttempts[0].Score == rankedAttempts[1].Score &&
            rankedAttempts[0].ProducerPriority == rankedAttempts[1].ProducerPriority)
        {
            return Block(skill.Name, "Projection route selection is ambiguous across multiple producers for this request.");
        }

        return rankedAttempts[0].Resolution;
    }

    public static string BuildPromptPatch(SkillProjectionResolution resolution)
    {
        if (resolution.Projection is null ||
            string.IsNullOrWhiteSpace(resolution.SelectedTopic) ||
            string.IsNullOrWhiteSpace(resolution.SelectedTargetView) ||
            string.IsNullOrWhiteSpace(resolution.ProjectionFilePath))
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("[Projection Route]");
        sb.AppendLine($"Selected topic: {resolution.SelectedTopic}");
        sb.AppendLine($"Selected target view: {resolution.SelectedTargetView}");
        sb.AppendLine($"Projection source: {resolution.ProjectionFilePath}");

        var promptConstraint = BuildPromptAssumptionConstraint(resolution.Projection.MappingPolicy.PromptAssumptionPolicy);
        if (!string.IsNullOrWhiteSpace(promptConstraint))
            sb.AppendLine($"Prompt constraint: {promptConstraint}");

        AppendList(sb, "Allowed terms", resolution.Projection.PromptProjection.AllowedTerms);
        AppendList(sb, "Forbidden assumptions", resolution.Projection.PromptProjection.ForbiddenAssumptions);
        AppendList(sb, "Required clarifications", resolution.Projection.PromptProjection.RequiredClarifications);
        AppendList(sb, "Reasoning paths", resolution.Projection.PromptProjection.ReasoningPaths);
        AppendList(sb, "Source digest", resolution.Projection.PromptProjection.SourceDigest);
        AppendList(sb, "Delivery artifacts", resolution.Projection.DeliveryArtifacts.Select(FormatDeliveryArtifact).ToArray());

        if (resolution.Projection.DroppedItems.Count > 0)
            AppendList(sb, "Dropped items", resolution.Projection.DroppedItems);

        return sb.ToString().TrimEnd();
    }

    private static SkillProjectionResolution Block(string skillName, string reason)
        => new()
        {
            SkillName = skillName,
            HasContracts = true,
            IsBlocked = true,
            BlockReason = reason
        };

    private static ProjectionRouteAttempt? TryResolveContract(
        string skillName,
        SkillProjectionContractSet contract,
        string requestText,
        ILogger logger)
    {
        var index = contract.Index;

        var selectedTopic = SelectTopic(index, requestText);
        if (selectedTopic is null)
        {
            if (!TryResolveNoSignalFallback(index, out selectedTopic))
            {
                return ProjectionRouteAttempt.Ambiguous(
                    "Projection topic selection is ambiguous for this request.");
            }

            logger.LogDebug(
                "Projection routing for skill '{SkillName}' used fallback topic '{Topic}' because request text matched no projection signals.",
                skillName,
                selectedTopic!.Item.DomainSlug);
        }

        var resolvedTopic = selectedTopic!;

        var selectedView = SelectView(index, resolvedTopic.Item, requestText);
        if (selectedView is null)
        {
            if (!(resolvedTopic.Score == 0 && TryResolveFallbackView(index, resolvedTopic.Item, out selectedView)))
            {
                return ProjectionRouteAttempt.Ambiguous(
                    $"Projection target view selection is ambiguous for topic '{resolvedTopic.Item.DomainSlug}'.");
            }

            logger.LogDebug(
                "Projection routing for skill '{SkillName}' used fallback target view '{TargetView}' for topic '{Topic}'.",
                skillName,
                selectedView!.Item.TargetView,
                resolvedTopic.Item.DomainSlug);
        }

        var resolvedView = selectedView!;

        var totalScore = resolvedTopic.Score + resolvedView.Score;
        if (index.DefaultSelectionPolicy.PreferReadyOnly &&
            !resolvedView.Item.Status.Equals("READY", StringComparison.OrdinalIgnoreCase))
        {
            return ProjectionRouteAttempt.Blocked(
                Block(skillName, $"Projection '{resolvedTopic.Item.DomainSlug}/{resolvedView.Item.TargetView}' is not READY."),
                totalScore);
        }

        if (!TryResolveProjectionPath(contract.RootPath, resolvedView.Item.Path, out var projectionPath))
        {
            logger.LogWarning("Projection path escaped contract root for skill '{SkillName}': {ProjectionPath}", skillName, resolvedView.Item.Path);
            return ProjectionRouteAttempt.Blocked(
                Block(skillName, $"Projection file '{resolvedView.Item.Path}' is outside the projection contract root."),
                totalScore);
        }

        if (!File.Exists(projectionPath))
        {
            logger.LogWarning("Projection file not found for skill '{SkillName}' at {ProjectionPath}", skillName, projectionPath);
            return ProjectionRouteAttempt.Blocked(
                Block(skillName, $"Projection file '{resolvedView.Item.Path}' was not found."),
                totalScore);
        }

        ProjectionDocument? projection;
        try
        {
            projection = LoadProjection(projectionPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse projection file for skill '{SkillName}' at {ProjectionPath}", skillName, projectionPath);
            return ProjectionRouteAttempt.Blocked(
                Block(skillName, $"Projection file '{resolvedView.Item.Path}' could not be parsed."),
                totalScore);
        }

        if (projection is null)
        {
            return ProjectionRouteAttempt.Blocked(
                Block(skillName, $"Projection file '{resolvedView.Item.Path}' is missing required route fields."),
                totalScore);
        }

        if (index.DefaultSelectionPolicy.BlockOnOpenQuestions && projection.OpenQuestions.Count > 0)
        {
            return ProjectionRouteAttempt.Blocked(
                Block(skillName, $"Projection '{resolvedTopic.Item.DomainSlug}/{resolvedView.Item.TargetView}' has blocking open questions."),
                totalScore);
        }

        if (string.Equals(projection.MappingPolicy.UnresolvedItemPolicy, "block_or_escalate", StringComparison.OrdinalIgnoreCase) &&
            projection.OpenQuestions.Count > 0)
        {
            return ProjectionRouteAttempt.Blocked(
                Block(skillName, $"Projection '{resolvedTopic.Item.DomainSlug}/{resolvedView.Item.TargetView}' requires escalation before use."),
                totalScore);
        }

        return ProjectionRouteAttempt.Success(
            new SkillProjectionResolution
            {
                SkillName = skillName,
                HasContracts = true,
                SelectedTopic = resolvedTopic.Item.DomainSlug,
                SelectedTargetView = resolvedView.Item.TargetView,
                ProjectionFilePath = projectionPath,
                Projection = projection
            },
            totalScore,
            contract.ProducerPriority);
    }

    private static ProjectionScore<ProjectionTopicRecord>? SelectTopic(ProjectionContractIndex index, string requestText)
    {
        if (index.Topics.Count == 0)
            return null;

        var dimensionScores = ToScoreMap(index.TopicScoring?.ScoreDimensions);
        var explicitArtifactBonus = GetDimensionScore(dimensionScores, "explicit_artifact_bonus", 4);
        var primaryIntentMatch = GetDimensionScore(dimensionScores, "primary_intent_match", 5);
        var strongKeywordMatch = GetDimensionScore(dimensionScores, "strong_keyword_match", 3);
        var supportingKeywordMatch = GetDimensionScore(dimensionScores, "supporting_keyword_match", 1);
        var crossTopicPenalty = GetDimensionScore(dimensionScores, "cross_topic_conflict_penalty", -2);
        var threshold = index.TopicScoring?.ClarifyWhenScoreGapBelow ?? 2;

        var topicSignals = ToFirstByKey(index.TopicScoring?.Topics, static topic => topic.DomainSlug);

        var scored = index.Topics
            .Select(topic =>
            {
                topicSignals.TryGetValue(topic.DomainSlug, out var signals);
                var score = 0;

                if (signals is not null)
                {
                    score += CountMatches(requestText, signals.PrimaryIntentSignals) * strongKeywordMatch;
                    score += CountMatches(requestText, signals.SupportingSignals) * supportingKeywordMatch;
                    if (HasAnyMatch(requestText, signals.ExplicitArtifactSignals))
                        score += explicitArtifactBonus;
                    if (HasAnyMatch(requestText, signals.PrimaryIntentSignals))
                        score += primaryIntentMatch;
                    if (HasAnyMatch(requestText, signals.DemoteWhenCompetingTopicSignals))
                        score += crossTopicPenalty;
                }

                return new ProjectionScore<ProjectionTopicRecord>(topic, score);
            })
            .OrderByDescending(item => item.Score)
            .ToList();

        if (scored.Count == 0)
            return null;

        if (scored[0].Score <= 0)
            return null;

        if (scored.Count > 1 && (scored[0].Score - scored[1].Score) < threshold)
            return null;

        return scored[0];
    }

    private static ProjectionScore<ProjectionViewRecord>? SelectView(ProjectionContractIndex index, ProjectionTopicRecord topic, string requestText)
    {
        var candidates = index.DefaultSelectionPolicy.PreferReadyOnly
            ? topic.Views.Where(view => view.Status.Equals("READY", StringComparison.OrdinalIgnoreCase)).ToList()
            : topic.Views.ToList();

        if (candidates.Count == 0)
            return null;

        var dimensionScores = ToScoreMap(index.TargetViewScoring?.ScoreDimensions);
        var explicitOutputMatch = GetDimensionScore(dimensionScores, "explicit_output_match", 5);
        var strongSignalMatch = GetDimensionScore(dimensionScores, "strong_signal_match", 3);
        var supportingSignalMatch = GetDimensionScore(dimensionScores, "supporting_signal_match", 1);
        var crossViewPenalty = GetDimensionScore(dimensionScores, "cross_view_conflict_penalty", -2);
        var defaultViewBonus = GetDimensionScore(dimensionScores, "topic_default_view_bonus", 1);
        var explicitArtifactRequestBonus = GetDimensionScore(dimensionScores, "explicit_user_artifact_request_bonus", 4);
        var threshold = index.TargetViewScoring?.ClarifyWhenScoreGapBelow ?? 2;

        var viewSignals = ToFirstByKey(index.TargetViewScoring?.Views, static view => view.TargetView);
        var topicOverride = index.TargetViewScoring?.WithinTopicOverrides
            .FirstOrDefault(overrideRecord => overrideRecord.DomainSlug.Equals(topic.DomainSlug, StringComparison.OrdinalIgnoreCase));

        var scored = candidates
            .Select(view =>
            {
                viewSignals.TryGetValue(view.TargetView, out var signals);
                var score = 0;

                if (signals is not null)
                {
                    if (HasAnyMatch(requestText, signals.ExplicitOutputSignals))
                        score += explicitOutputMatch;
                    score += CountMatches(requestText, signals.StrongSignals) * strongSignalMatch;
                    score += CountMatches(requestText, signals.SupportingSignals) * supportingSignalMatch;
                    if (HasAnyMatch(requestText, signals.DemoteWhenCompetingViewSignals))
                        score += crossViewPenalty;
                }

                if (index.TargetViewScoring?.PreferExplicitUserArtifactRequests == true &&
                    HasExplicitArtifactRequestForView(requestText, view.TargetView))
                {
                    score += explicitArtifactRequestBonus;
                }

                if (view.TargetView.Equals(topic.DefaultTargetView, StringComparison.OrdinalIgnoreCase))
                    score += defaultViewBonus;

                if (topicOverride is not null)
                {
                    foreach (var bonus in topicOverride.Bonuses.Where(bonus =>
                        bonus.TargetView.Equals(view.TargetView, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (HasAnyMatch(requestText, bonus.WhenRequestSignals))
                            score += bonus.Score;
                    }
                }

                return new ProjectionScore<ProjectionViewRecord>(view, score);
            })
            .OrderByDescending(item => item.Score)
            .ToList();

        if (scored.Count == 0)
            return null;

        if (scored[0].Score <= 0)
            return null;

        if (scored.Count > 1 && (scored[0].Score - scored[1].Score) < threshold)
            return null;

        return scored[0];
    }

    private static bool TryResolveNoSignalFallback(
        ProjectionContractIndex index,
        out ProjectionScore<ProjectionTopicRecord>? selectedTopic)
    {
        selectedTopic = null;

        foreach (var targetView in index.DefaultSelectionPolicy.FallbackOrderByTargetView)
        {
            if (string.IsNullOrWhiteSpace(targetView))
                continue;

            foreach (var topic in index.Topics.Where(topic =>
                topic.Views.Any(view => view.TargetView.Equals(targetView, StringComparison.OrdinalIgnoreCase))))
            {
                selectedTopic = new ProjectionScore<ProjectionTopicRecord>(topic, 0);
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveFallbackView(
        ProjectionContractIndex index,
        ProjectionTopicRecord topic,
        out ProjectionScore<ProjectionViewRecord>? selectedView)
    {
        selectedView = null;

        var candidates = index.DefaultSelectionPolicy.PreferReadyOnly
            ? topic.Views.Where(view => view.Status.Equals("READY", StringComparison.OrdinalIgnoreCase)).ToList()
            : topic.Views.ToList();

        foreach (var targetView in index.DefaultSelectionPolicy.FallbackOrderByTargetView)
        {
            if (string.IsNullOrWhiteSpace(targetView))
                continue;

            var view = candidates.FirstOrDefault(candidate => candidate.TargetView.Equals(targetView, StringComparison.OrdinalIgnoreCase));
            if (view is null)
                continue;

            selectedView = new ProjectionScore<ProjectionViewRecord>(view, 0);
            return true;
        }

        return false;
    }

    private static ProjectionDocument? LoadProjection(string projectionPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(projectionPath));
        var root = document.RootElement;

        var mappingPolicy = root.TryGetProperty("mapping_policy", out var mappingPolicyElement)
            ? new ProjectionMappingPolicy
            {
                UnresolvedItemPolicy = GetOptionalString(mappingPolicyElement, "unresolved_item_policy"),
                PromptAssumptionPolicy = GetOptionalString(mappingPolicyElement, "prompt_assumption_policy")
            }
            : new ProjectionMappingPolicy();

        var promptProjection = root.TryGetProperty("prompt_projection", out var promptProjectionElement)
            ? new ProjectionPromptPayload
            {
                AllowedTerms = ReadStringArray(promptProjectionElement, "allowed_terms"),
                ForbiddenAssumptions = ReadStringArray(promptProjectionElement, "forbidden_assumptions"),
                RequiredClarifications = ReadStringArray(promptProjectionElement, "required_clarifications"),
                ReasoningPaths = ReadStringArray(promptProjectionElement, "reasoning_paths"),
                SourceDigest = ReadStringArray(promptProjectionElement, "source_digest")
            }
            : new ProjectionPromptPayload();

        var deliveryArtifacts = new List<ProjectionDeliveryArtifact>();
        if (root.TryGetProperty("delivery_artifacts", out var artifactsElement) && artifactsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var artifact in artifactsElement.EnumerateArray())
            {
                var artifactName = GetOptionalString(artifact, "artifact_name");
                var artifactType = GetOptionalString(artifact, "artifact_type");
                var path = GetOptionalString(artifact, "path");
                if (string.IsNullOrWhiteSpace(artifactName) || string.IsNullOrWhiteSpace(artifactType) || string.IsNullOrWhiteSpace(path))
                    continue;

                deliveryArtifacts.Add(new ProjectionDeliveryArtifact
                {
                    ArtifactName = artifactName,
                    ArtifactType = artifactType,
                    Path = path,
                    Status = GetOptionalString(artifact, "status")
                });
            }
        }

        return new ProjectionDocument
        {
            MappingPolicy = mappingPolicy,
            PromptProjection = promptProjection,
            DeliveryArtifacts = deliveryArtifacts,
            DroppedItems = ReadDisplayArray(root, "dropped_items"),
            OpenQuestions = ReadDisplayArray(root, "open_questions")
        };
    }

    private static void AppendList(StringBuilder sb, string label, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine(label + ":");
        foreach (var item in items)
            sb.AppendLine($"- {item}");
    }

    private static string FormatDeliveryArtifact(ProjectionDeliveryArtifact artifact)
    {
        var statusSuffix = string.IsNullOrWhiteSpace(artifact.Status)
            ? string.Empty
            : $" [{artifact.Status}]";
        return $"{artifact.ArtifactName} ({artifact.ArtifactType}) -> {artifact.Path}{statusSuffix}";
    }

    private static string? BuildPromptAssumptionConstraint(string? policy)
        => policy?.Trim().ToLowerInvariant() switch
        {
            "disallow_unmapped_terms" => "Do not use unmapped terms or invent terminology beyond this projection.",
            "warn_on_unmapped_terms" => "If you use terms not mapped by this projection, explicitly warn that they are unmapped assumptions.",
            "allow_unmapped_terms" => "Unmapped terms are allowed, but prefer mapped terminology when available.",
            null or "" => null,
            _ => $"Follow prompt assumption policy '{policy}' when introducing terms not mapped by this projection."
        };

    private static Dictionary<string, int> ToScoreMap(IReadOnlyList<ProjectionScoreDimension>? dimensions)
        => ToFirstByKey(dimensions, static item => item.Dimension)
            .ToDictionary(item => item.Key, item => item.Value.Score, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, T> ToFirstByKey<T>(IEnumerable<T>? items, Func<T, string?> keySelector)
    {
        var values = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        if (items is null)
            return values;

        foreach (var item in items)
        {
            var key = keySelector(item);
            if (string.IsNullOrWhiteSpace(key) || values.ContainsKey(key))
                continue;

            values[key] = item;
        }

        return values;
    }

    private static bool TryResolveProjectionPath(string rootPath, string relativePath, out string projectionPath)
    {
        projectionPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(relativePath))
            return false;

        var normalizedRelativePath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelativePath))
            return false;

        try
        {
            var rootFullPath = Path.GetFullPath(rootPath);
            var candidateFullPath = Path.GetFullPath(Path.Combine(rootFullPath, normalizedRelativePath));
            var rootWithSeparator = rootFullPath.EndsWith(Path.DirectorySeparatorChar)
                ? rootFullPath
                : rootFullPath + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!candidateFullPath.StartsWith(rootWithSeparator, comparison) &&
                !string.Equals(candidateFullPath, rootFullPath, comparison))
            {
                return false;
            }

            projectionPath = candidateFullPath;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int GetDimensionScore(Dictionary<string, int> scores, string name, int fallback)
        => scores.TryGetValue(name, out var value) ? value : fallback;

    private static int CountMatches(string requestText, IReadOnlyList<string> signals)
        => signals.Count(signal => ContainsPhrase(requestText, signal));

    private static bool HasAnyMatch(string requestText, IReadOnlyList<string> signals)
        => signals.Any(signal => ContainsPhrase(requestText, signal));

    private static bool HasExplicitArtifactRequestForView(string requestText, string targetView)
        => SkillProjectionArtifactTerms.GetExplicitArtifactTerms(targetView)
            .Any(signal => ContainsPhrase(requestText, signal));

    private static bool ContainsPhrase(string requestText, string signal)
        => !string.IsNullOrWhiteSpace(signal) && requestText.Contains(signal.Trim().ToLowerInvariant(), StringComparison.Ordinal);

    private static string? GetOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                values.Add(item.GetString()!);
        }

        return values;
    }

    private static IReadOnlyList<string> ReadDisplayArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            var displayText = ToDisplayText(item);
            if (!string.IsNullOrWhiteSpace(displayText))
                values.Add(displayText);
        }

        return values;
    }

    private static string? ToDisplayText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString();

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (TryBuildOpenQuestionText(element, out var openQuestionText))
            return openQuestionText;

        if (TryBuildDroppedItemText(element, out var droppedItemText))
            return droppedItemText;

        return element.GetRawText();
    }

    private static bool TryBuildOpenQuestionText(JsonElement element, out string? text)
    {
        text = GetOptionalString(element, "question");
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var details = new List<string>();
        var impact = GetOptionalString(element, "impact");
        var requiredInput = GetOptionalString(element, "required_input");

        if (!string.IsNullOrWhiteSpace(impact))
            details.Add($"Impact: {impact}");

        if (!string.IsNullOrWhiteSpace(requiredInput))
            details.Add($"Required input: {requiredInput}");

        if (details.Count > 0)
            text = $"{text} ({string.Join("; ", details)})";

        return true;
    }

    private static bool TryBuildDroppedItemText(JsonElement element, out string? text)
    {
        text = GetOptionalString(element, "reason");
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var itemType = GetOptionalString(element, "item_type");
        var itemId = GetOptionalString(element, "item_id");
        var prefix = string.Join(" ", new[] { itemType, itemId }.Where(static value => !string.IsNullOrWhiteSpace(value)));

        if (!string.IsNullOrWhiteSpace(prefix))
            text = $"{prefix}: {text}";

        return true;
    }

    private sealed record ProjectionScore<T>(T Item, int Score);

    private sealed record ProjectionRouteAttempt(
        SkillProjectionResolution Resolution,
        int Score,
        int ProducerPriority,
        bool IsAmbiguous,
        string? AmbiguousReason)
    {
        public static ProjectionRouteAttempt Success(SkillProjectionResolution resolution, int score, int producerPriority)
            => new(resolution, score, producerPriority, false, null);

        public static ProjectionRouteAttempt Blocked(SkillProjectionResolution resolution, int score)
            => new(resolution, score, 0, false, null);

        public static ProjectionRouteAttempt Ambiguous(string reason)
            => new(Block(string.Empty, reason), int.MinValue, int.MinValue, true, reason);
    }
}
