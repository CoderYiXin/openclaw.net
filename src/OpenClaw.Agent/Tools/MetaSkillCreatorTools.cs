using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent.Tools;

public sealed class EmitTextTool : ITool
{
    public string Name => "emit_text";
    public string Description => "Emit fixed text as tool output.";
    public string ParameterSchema => "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"]}";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        if (!args.RootElement.TryGetProperty("text", out var textNode) || textNode.ValueKind != JsonValueKind.String)
            return ValueTask.FromResult(MetaSkillCreatorToolResult.SerializeError("invalid_arguments", "'text' is required."));

        return ValueTask.FromResult(textNode.GetString() ?? string.Empty);
    }
}

public sealed class MetaSkillFillSlotsTool : ITool
{
    public string Name => "meta_skill_fill_slots";
    public string Description => "Drive slot-filling and return validated JSON consumed by meta_skill_assemble.";
    public string ParameterSchema => "{\"type\":\"object\",\"properties\":{\"pattern_id\":{\"type\":\"string\"},\"history_summary\":{\"type\":\"string\"},\"user_intent\":{\"type\":\"string\"}},\"required\":[\"pattern_id\",\"history_summary\",\"user_intent\"]}";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

        var patternId = GetString(args.RootElement, "pattern_id");
        var userIntent = GetString(args.RootElement, "user_intent");
        var historySummary = GetString(args.RootElement, "history_summary");

        if (!MetaSkillCreatorTemplateCatalog.IsSupportedPattern(patternId))
            return ValueTask.FromResult(MetaSkillCreatorToolResult.SerializeError("unknown_pattern_id", $"Unsupported pattern_id '{patternId}'."));

        if (string.IsNullOrWhiteSpace(userIntent))
            return ValueTask.FromResult(MetaSkillCreatorToolResult.SerializeError("invalid_arguments", "'user_intent' is required."));

        if (string.IsNullOrWhiteSpace(historySummary))
            return ValueTask.FromResult(MetaSkillCreatorToolResult.SerializeError("invalid_arguments", "'history_summary' is required."));

        var requiredTriggers = ExtractRequiredTriggersFromIntent(userIntent);
        var fallbackTrigger = "create a meta-skill";
        var triggers = requiredTriggers.Count == 0 ? [fallbackTrigger] : requiredTriggers;

        var baseName = BuildNameFromIntent(userIntent, patternId);
        var description = BuildDescription(userIntent);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("name", baseName);
            writer.WriteString("description", description);
            writer.WriteNumber("meta_priority", 50);
            writer.WritePropertyName("triggers");
            writer.WriteStartArray();
            foreach (var trigger in triggers.Take(8))
                writer.WriteStringValue(trigger);
            writer.WriteEndArray();

            switch (patternId)
            {
                case "p1_sequential":
                    WriteP1Steps(writer, userIntent);
                    break;
                case "p2_fan_out_merge":
                    WriteP2Branches(writer, userIntent);
                    break;
                case "p3_condition_gated":
                    WriteP3Steps(writer, userIntent);
                    break;
            }

            writer.WriteEndObject();
        }

        return ValueTask.FromResult(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteP1Steps(Utf8JsonWriter writer, string userIntent)
    {
        writer.WritePropertyName("steps");
        writer.WriteStartArray();

        writer.WriteStartObject();
        writer.WriteString("id", "gather");
        writer.WriteString("skill", "history-explorer");
        writer.WriteString("task", "Collect relevant historical context for the request");
        writer.WritePropertyName("with_keys");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartObject();
        writer.WriteString("id", "synthesize");
        writer.WriteString("skill", "summarize");
        writer.WriteString("task", BuildTask("Synthesize a grounded answer", userIntent));
        writer.WritePropertyName("with_keys");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteEndArray();
    }

    private static void WriteP2Branches(Utf8JsonWriter writer, string userIntent)
    {
        writer.WritePropertyName("branches");
        writer.WriteStartArray();

        writer.WriteStartObject();
        writer.WriteString("id", "context");
        writer.WriteString("skill", "history-explorer");
        writer.WriteString("task", "Collect prior related context");
        writer.WritePropertyName("with_keys");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartObject();
        writer.WriteString("id", "analysis");
        writer.WriteString("skill", "summarize");
        writer.WriteString("task", BuildTask("Generate focused analysis", userIntent));
        writer.WritePropertyName("with_keys");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteEndArray();

        writer.WritePropertyName("merge");
        writer.WriteStartObject();
        writer.WriteString("id", "merge");
        writer.WriteString("skill", "summarize");
        writer.WriteString("task", "Merge branch outputs into one coherent deliverable");
        writer.WritePropertyName("with_keys");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WritePropertyName("tail");
        writer.WriteNullValue();
    }

    private static void WriteP3Steps(Utf8JsonWriter writer, string userIntent)
    {
        writer.WritePropertyName("steps");
        writer.WriteStartArray();

        writer.WriteStartObject();
        writer.WriteString("id", "intake");
        writer.WriteString("skill", "summarize");
        writer.WriteString("task", BuildTask("Extract constraints and missing information", userIntent));
        writer.WritePropertyName("with_keys");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartObject();
        writer.WriteString("id", "evidence");
        writer.WriteString("skill", "history-explorer");
        writer.WriteString("task", "Find relevant prior context when available");
        writer.WritePropertyName("with_keys");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartObject();
        writer.WriteString("id", "decision");
        writer.WriteString("skill", "summarize");
        writer.WriteString("task", "Produce final answer with caveats and next actions");
        writer.WritePropertyName("with_keys");
        writer.WriteStartObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteEndArray();
    }

    private static string BuildTask(string prefix, string userIntent)
    {
        var compact = string.Join(' ', userIntent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (compact.Length > 220)
            compact = compact[..220];
        var task = $"{prefix}: {compact}";
        return SanitizeYamlText(task.Length > 400 ? task[..400] : task, "task");
    }

    private static string BuildDescription(string userIntent)
    {
        var compact = string.Join(' ', userIntent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (compact.Length < 30)
            compact = $"Generated meta skill for workflow: {compact}";

        if (compact.Length > 200)
            compact = compact[..200];

        return SanitizeYamlText(compact, "description");
    }

    private static string BuildNameFromIntent(string intent, string patternId)
    {
        var normalized = new string(intent
            .ToLowerInvariant()
            .Where(static ch => char.IsLetterOrDigit(ch) || ch == ' ' || ch == '-' || ch == '_')
            .ToArray())
            .Trim();

        if (normalized.Length == 0)
            normalized = "generated";

        normalized = normalized.Replace(' ', '-');
        if (normalized.Length > 40)
            normalized = normalized[..40].Trim('-');

        var name = $"meta-{patternId}-{normalized}";
        name = Regex.Replace(name, "[^a-z0-9_-]", string.Empty, RegexOptions.CultureInvariant);
        if (!Regex.IsMatch(name, "^[a-z]", RegexOptions.CultureInvariant))
            name = "meta-" + name;

        return name.Length > 64 ? name[..64] : name;
    }

    private static List<string> ExtractRequiredTriggersFromIntent(string userIntent)
    {
        var patterns = new[]
        {
            "触发(?:短语|词)?(?:要|应|必须)?(?:包含|包括)\\s*[:：]\\s*([^\\n。；;]+)",
            "trigger phrases?\\s+(?:must\\s+)?(?:include|contain)\\s*[:：]\\s*([^\\n.;]+)",
        };

        string captured = string.Empty;
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(userIntent, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
            {
                captured = match.Groups[1].Value;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(captured))
            return [];

        var output = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in captured.Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries))
        {
            var phrase = raw.Trim().Trim('`', '\'', '"', '“', '”', '‘', '’', '[', ']', '(', ')');
            if (string.IsNullOrWhiteSpace(phrase) || phrase.Length > 80)
                continue;

            if (phrase.IndexOfAny(['"', '\\', '\r', '\n']) >= 0)
                continue;

            if (seen.Add(phrase))
                output.Add(phrase);
        }

        return output;
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
            return string.Empty;

        return node.GetString() ?? string.Empty;
    }

    private static string SanitizeYamlText(string value, string fieldName)
    {
        if (value.IndexOfAny(['"', '\\', '\r', '\n']) >= 0)
            throw new InvalidOperationException($"{fieldName} may not contain double quotes, newlines, or backslashes.");

        return value;
    }
}

public sealed class MetaSkillAssembleTool : ITool
{
    public string Name => "meta_skill_assemble";
    public string Description => "Render SKILL.md from pattern_id and validated slots_json.";
    public string ParameterSchema => "{\"type\":\"object\",\"properties\":{\"pattern_id\":{\"type\":\"string\"},\"slots_json\":{\"type\":\"string\"}},\"required\":[\"pattern_id\",\"slots_json\"]}";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

        var patternId = GetString(args.RootElement, "pattern_id");
        if (!MetaSkillCreatorTemplateCatalog.IsSupportedPattern(patternId))
            return ValueTask.FromResult(MetaSkillCreatorToolResult.SerializeError("unknown_pattern_id", $"Unsupported pattern_id '{patternId}'."));

        if (!args.RootElement.TryGetProperty("slots_json", out var slotsNode) || slotsNode.ValueKind != JsonValueKind.String)
            return ValueTask.FromResult(MetaSkillCreatorToolResult.SerializeError("invalid_arguments", "'slots_json' is required."));

        var slotsRaw = slotsNode.GetString() ?? "{}";
        JsonDocument slots;
        try
        {
            slots = JsonDocument.Parse(slotsRaw);
        }
        catch (JsonException)
        {
            return ValueTask.FromResult(MetaSkillCreatorToolResult.SerializeError("invalid_slots_json", "slots_json is not valid JSON."));
        }

        using (slots)
        {
            try
            {
                var rendered = patternId switch
                {
                    "p1_sequential" => RenderP1(slots.RootElement),
                    "p2_fan_out_merge" => RenderP2(slots.RootElement),
                    "p3_condition_gated" => RenderP3(slots.RootElement),
                    _ => throw new InvalidOperationException($"Unsupported pattern_id '{patternId}'.")
                };

                return ValueTask.FromResult(rendered);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                return ValueTask.FromResult(MetaSkillCreatorToolResult.SerializeError("invalid_slots_json", ex.Message));
            }
        }
    }

    private static string RenderP1(JsonElement slots)
    {
        var common = ParseCommonSlots(slots);
        var stepsNode = RequireArray(slots, "steps");
        if (stepsNode.GetArrayLength() < 2 || stepsNode.GetArrayLength() > 5)
            throw new InvalidOperationException("p1_sequential requires steps length between 2 and 5.");

        var steps = ParseStepList(stepsNode);

        var sb = new StringBuilder();
        AppendHeader(sb, common);
        sb.AppendLine("composition:");
        sb.AppendLine("  steps:");

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var kind = MetaSkillCreatorTemplateCatalog.ResolveCreatorStepKind(step.Skill);
            sb.AppendLine($"    - id: {step.Id}");
            if (kind is "skill_exec" or "llm_chat")
                sb.AppendLine($"      kind: {kind}");
            sb.AppendLine($"      skill: {ToJsonString(step.Skill)}");
            sb.AppendLine("      read_only: true");
            sb.AppendLine("      write_access: false");
            sb.AppendLine("      network_access: false");
            if (i > 0)
                sb.AppendLine($"      depends_on: [{steps[i - 1].Id}]");
            sb.AppendLine("      with:");

            if (step.WithKeys.Count > 0)
            {
                foreach (var entry in step.WithKeys)
                    sb.AppendLine($"        {entry.Key}: {ToJsonString(entry.Value)}");
            }
            else if (kind == "skill_exec" && string.Equals(step.Skill, "history-explorer", StringComparison.Ordinal))
            {
                sb.AppendLine($"        query: \"{step.Task}: {{{{ inputs.user_message | xml_escape | truncate(512) }}}}\"");
                sb.AppendLine("        window_days: \"30\"");
                sb.AppendLine("        include: \"co_occurrences,meta_usage\"");
            }
            else if (i == 0)
            {
                sb.AppendLine($"        task: \"{step.Task}: {{{{ inputs.user_message | xml_escape | truncate(512) }}}}\"");
            }
            else
            {
                sb.AppendLine($"        task: {ToJsonString(step.Task)}");
                sb.AppendLine("        user_request: \"{{ inputs.user_message | xml_escape | truncate(1200) }}\"");
                sb.AppendLine("        prior_outputs:");
                for (var p = 0; p < i; p++)
                    sb.AppendLine($"          {steps[p].Id}: \"{{{{ outputs.{steps[p].Id} | truncate(2000) }}}}\"");
                sb.AppendLine($"        upstream: \"{{{{ outputs.{steps[i - 1].Id} | truncate(2000) }}}}\"");
            }
        }

        AppendP1Body(sb, common.Name, common.Description, steps);
        return sb.ToString();
    }

    private static string RenderP2(JsonElement slots)
    {
        var common = ParseCommonSlots(slots);
        var branches = ParseStepList(RequireArray(slots, "branches"));
        if (branches.Count < 2 || branches.Count > 4)
            throw new InvalidOperationException("p2_fan_out_merge requires branches length between 2 and 4.");

        var merge = ParseStep(RequireObject(slots, "merge"));
        CreatorStep? tail = null;
        if (slots.TryGetProperty("tail", out var tailNode) && tailNode.ValueKind == JsonValueKind.Object)
            tail = ParseStep(tailNode);

        var sb = new StringBuilder();
        AppendHeader(sb, common);
        sb.AppendLine("composition:");
        sb.AppendLine("  steps:");

        foreach (var branch in branches)
            AppendStepWithPayload(sb, branch, dependsOn: null, branchMode: true, branchesForMerge: null);

        AppendStepWithPayload(sb, merge, dependsOn: branches.Select(static b => b.Id).ToArray(), branchMode: false, branchesForMerge: branches);
        if (tail is not null)
            AppendStepWithPayload(sb, tail.Value, dependsOn: [merge.Id], branchMode: false, branchesForMerge: null);

        AppendP2Body(sb, common.Name, common.Description, branches, merge, tail);
        return sb.ToString();
    }

    private static string RenderP3(JsonElement slots)
    {
        var common = ParseCommonSlots(slots);
        var steps = ParseStepList(RequireArray(slots, "steps"));
        if (steps.Count < 2 || steps.Count > 5)
            throw new InvalidOperationException("p3_condition_gated requires steps length between 2 and 5.");

        var sb = new StringBuilder();
        AppendHeader(sb, common);
        sb.AppendLine("composition:");
        sb.AppendLine("  steps:");

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var kind = MetaSkillCreatorTemplateCatalog.ResolveCreatorStepKind(step.Skill);
            sb.AppendLine($"    - id: {step.Id}");
            if (kind is "skill_exec" or "llm_chat")
                sb.AppendLine($"      kind: {kind}");
            sb.AppendLine($"      skill: {ToJsonString(step.Skill)}");
            sb.AppendLine("      read_only: true");
            sb.AppendLine("      write_access: false");
            sb.AppendLine("      network_access: false");
            if (i > 0)
                sb.AppendLine($"      depends_on: [{steps[i - 1].Id}]");
            sb.AppendLine("      with:");

            if (step.WithKeys.Count > 0)
            {
                foreach (var entry in step.WithKeys)
                    sb.AppendLine($"        {entry.Key}: {ToJsonString(entry.Value)}");
            }
            else if (kind == "skill_exec" && string.Equals(step.Skill, "history-explorer", StringComparison.Ordinal))
            {
                sb.AppendLine($"        query: \"{step.Task}: {{{{ inputs.user_message | xml_escape | truncate(512) }}}}\"");
                sb.AppendLine("        window_days: \"30\"");
                sb.AppendLine("        include: \"co_occurrences,meta_usage,router_fixtures\"");
            }
            else if (i == 0)
            {
                sb.AppendLine($"        task: \"{step.Task}: {{{{ inputs.user_message | xml_escape | truncate(512) }}}}\"");
            }
            else
            {
                sb.AppendLine($"        task: {ToJsonString(step.Task)}");
                sb.AppendLine("        user_request: \"{{ inputs.user_message | xml_escape | truncate(1200) }}\"");
                sb.AppendLine("        prior_outputs:");
                for (var p = 0; p < i; p++)
                    sb.AppendLine($"          {steps[p].Id}: \"{{{{ outputs.{steps[p].Id} | truncate(2000) }}}}\"");
            }
        }

        AppendP3Body(sb, common.Name, common.Description, steps);
        return sb.ToString();
    }

    private static void AppendStepWithPayload(
        StringBuilder sb,
        CreatorStep step,
        string[]? dependsOn,
        bool branchMode,
        IReadOnlyList<CreatorStep>? branchesForMerge)
    {
        var kind = MetaSkillCreatorTemplateCatalog.ResolveCreatorStepKind(step.Skill);
        sb.AppendLine($"    - id: {step.Id}");
        if (kind is "skill_exec" or "llm_chat")
            sb.AppendLine($"      kind: {kind}");
        sb.AppendLine($"      skill: {ToJsonString(step.Skill)}");
        sb.AppendLine("      read_only: true");
        sb.AppendLine("      write_access: false");
        sb.AppendLine("      network_access: false");

        if (dependsOn is { Length: > 0 })
            sb.AppendLine($"      depends_on: [{string.Join(", ", dependsOn)}]");

        sb.AppendLine("      with:");
        if (step.WithKeys.Count > 0)
        {
            foreach (var entry in step.WithKeys)
                sb.AppendLine($"        {entry.Key}: {ToJsonString(entry.Value)}");
            return;
        }

        if (kind == "skill_exec" && string.Equals(step.Skill, "history-explorer", StringComparison.Ordinal))
        {
            sb.AppendLine($"        query: \"{step.Task}: {{{{ inputs.user_message | xml_escape | truncate(512) }}}}\"");
            sb.AppendLine("        window_days: \"30\"");
            sb.AppendLine("        include: \"co_occurrences,meta_usage\"");
            return;
        }

        if (branchMode)
        {
            sb.AppendLine($"        task: \"{step.Task}: {{{{ inputs.user_message | xml_escape | truncate(512) }}}}\"");
            return;
        }

        sb.AppendLine($"        task: {ToJsonString(step.Task)}");
        if (branchesForMerge is { Count: > 0 })
        {
            foreach (var branch in branchesForMerge)
                sb.AppendLine($"        {branch.Id}_output: \"{{{{ outputs.{branch.Id} | truncate(2000) }}}}\"");
        }
        else if (dependsOn is { Length: > 0 })
        {
            sb.AppendLine($"        upstream: \"{{{{ outputs.{dependsOn[0]} | truncate(2000) }}}}\"");
        }
    }

    private static CommonSlots ParseCommonSlots(JsonElement root)
    {
        var name = RequireString(root, "name");
        var description = RequireString(root, "description");
        var metaPriority = root.TryGetProperty("meta_priority", out var priorityNode) && priorityNode.ValueKind == JsonValueKind.Number
            ? priorityNode.GetInt32()
            : 50;

        if (metaPriority < 30 || metaPriority > 80)
            throw new InvalidOperationException("meta_priority must be between 30 and 80.");

        if (description.Length < 30 || description.Length > 200)
            throw new InvalidOperationException("description length must be between 30 and 200.");

        var triggersNode = RequireArray(root, "triggers");
        if (triggersNode.GetArrayLength() is < 1 or > 8)
            throw new InvalidOperationException("triggers length must be between 1 and 8.");

        var triggers = new List<string>();
        foreach (var triggerNode in triggersNode.EnumerateArray())
            triggers.Add(RequireString(triggerNode, null));

        return new CommonSlots(name, description, metaPriority, triggers);
    }

    private static List<CreatorStep> ParseStepList(JsonElement arrayNode)
    {
        var steps = new List<CreatorStep>();
        foreach (var node in arrayNode.EnumerateArray())
            steps.Add(ParseStep(node));

        return steps;
    }

    private static CreatorStep ParseStep(JsonElement node)
    {
        var id = RequireString(node, "id");
        var skill = RequireString(node, "skill");
        var task = RequireString(node, "task");

        if (!Regex.IsMatch(id, "^[a-z][a-z0-9_]{0,30}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException($"invalid step id '{id}'.");

        if (task.Length > 400)
            throw new InvalidOperationException("step.task max length is 400.");

        EnforceYamlSafe(skill, "skill");
        EnforceYamlSafe(task, "task");

        var withKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node.TryGetProperty("with_keys", out var withNode) && withNode.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in withNode.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString() ?? string.Empty;
                    EnforceYamlSafe(value, "with_keys value");
                    withKeys[property.Name] = value;
                }
            }
        }

        return new CreatorStep(id, skill, task, withKeys);
    }

    private static string RequireString(JsonElement node, string? propertyName)
    {
        if (propertyName is not null)
        {
            if (!node.TryGetProperty(propertyName, out var valueNode) || valueNode.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"'{propertyName}' is required and must be string.");

            var value = valueNode.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"'{propertyName}' must not be empty.");

            return value;
        }

        if (node.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("string value expected.");

        var inline = node.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(inline))
            throw new InvalidOperationException("string value must not be empty.");

        return inline;
    }

    private static JsonElement RequireArray(JsonElement node, string propertyName)
    {
        if (!node.TryGetProperty(propertyName, out var valueNode) || valueNode.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"'{propertyName}' is required and must be array.");

        return valueNode;
    }

    private static JsonElement RequireObject(JsonElement node, string propertyName)
    {
        if (!node.TryGetProperty(propertyName, out var valueNode) || valueNode.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"'{propertyName}' is required and must be object.");

        return valueNode;
    }

    private static void EnforceYamlSafe(string value, string fieldName)
    {
        if (value.IndexOfAny(['"', '\\', '\r', '\n']) >= 0)
            throw new InvalidOperationException($"{fieldName} may not contain double quotes, newlines, or backslashes.");
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
            return string.Empty;

        return node.GetString() ?? string.Empty;
    }

    private static string ToJsonString(string value) => Quote(value);

    private static string Quote(string value)
    {
        var s = value ?? string.Empty;
        var escaped = s
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static void AppendHeader(StringBuilder sb, CommonSlots common)
    {
        sb.AppendLine("---");
        sb.AppendLine($"name: {ToJsonString(common.Name)}");
        sb.AppendLine($"description: {ToJsonString(common.Description)}");
        sb.AppendLine("kind: meta");
        sb.AppendLine($"meta_priority: {common.MetaPriority}");
        sb.AppendLine("always: false");
        sb.AppendLine("triggers:");
        foreach (var trigger in common.Triggers)
            sb.AppendLine($"  - {ToJsonString(trigger)}");
        sb.AppendLine("provenance:");
        sb.AppendLine("  origin: opensquilla-user");
        sb.AppendLine("  license: Apache-2.0");
        sb.AppendLine("metadata:");
        sb.AppendLine("  opensquilla:");
        sb.AppendLine("    risk: \"low\"");
        sb.AppendLine("    read_only: true");
        sb.AppendLine("    no_write: true");
        sb.AppendLine("    write_access: false");
        sb.AppendLine("    network_access: false");
        sb.AppendLine("    creator_gates:");
        sb.AppendLine("      - \"G1 structural lint\"");
        sb.AppendLine("      - \"G2 scheduler dry-run\"");
        sb.AppendLine("      - \"G3 positive trigger smoke\"");
        sb.AppendLine("      - \"G4 unrelated negative smoke\"");
        sb.AppendLine("      - \"acceptance_compare versus highest-tier no-meta baseline\"");
        sb.AppendLine("      - \"runtime_e2e versus highest-tier no-meta baseline\"");
    }

    private static void AppendP1Body(StringBuilder sb, string name, string description, IReadOnlyList<CreatorStep> steps)
    {
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {name} (Meta-Skill, P1 sequential)");
        sb.AppendLine();
        sb.AppendLine(description);
        sb.AppendLine();
        sb.AppendLine("## Execution Flow");
        for (var i = 0; i < steps.Count; i++)
        {
            sb.AppendLine();
            sb.AppendLine($"{i + 1}. `{steps[i].Id}` runs `{steps[i].Skill}`.");
            sb.AppendLine($"   Purpose: {steps[i].Task}");
        }

        sb.AppendLine();
        sb.AppendLine("## Expected Output");
        sb.AppendLine();
        sb.AppendLine("Return a grounded final deliverable from the ordered step outputs. Cite the");
        sb.AppendLine("specific upstream evidence used, keep claims within the user request and");
        sb.AppendLine("available step outputs, and do not invent missing facts.");
        sb.AppendLine();
        sb.AppendLine("## Safety");
        sb.AppendLine();
        sb.AppendLine("This proposal is read-only. It does not request file writes, network access, or");
        sb.AppendLine("destructive operations; every generated step carries explicit `read_only`,");
        sb.AppendLine("`write_access: false`, and `network_access: false` annotations for gate review.");
        sb.AppendLine();
        sb.AppendLine("## Creator Gates");
        sb.AppendLine();
        sb.AppendLine("Generated proposals must pass structural lint, scheduler dry-run, positive and");
        sb.AppendLine("negative trigger smoke tests, highest-tier no-meta acceptance comparison, and");
        sb.AppendLine("runtime E2E comparison before auto-enable eligibility.");
        sb.AppendLine();
        sb.AppendLine("## Fallback");
        sb.AppendLine();
        sb.AppendLine("LLM should invoke the listed skills in order.");
    }

    private static void AppendP2Body(StringBuilder sb, string name, string description, IReadOnlyList<CreatorStep> branches, CreatorStep merge, CreatorStep? tail)
    {
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {name} (Meta-Skill, P2 fan_out_merge)");
        sb.AppendLine();
        sb.AppendLine(description);
        sb.AppendLine();
        sb.AppendLine("## Execution Flow");
        foreach (var branch in branches)
        {
            sb.AppendLine();
            sb.AppendLine($"- Branch `{branch.Id}` runs `{branch.Skill}`.");
            sb.AppendLine($"  Purpose: {branch.Task}");
        }

        sb.AppendLine();
        sb.AppendLine($"- Merge `{merge.Id}` runs `{merge.Skill}`.");
        sb.AppendLine($"  Purpose: {merge.Task}");

        if (tail is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"- Tail `{tail.Value.Id}` runs `{tail.Value.Skill}`.");
            sb.AppendLine($"  Purpose: {tail.Value.Task}");
        }

        sb.AppendLine();
        sb.AppendLine("## Expected Output");
        sb.AppendLine();
        sb.AppendLine("Return a grounded final deliverable from the branch and merge outputs. Cite the");
        sb.AppendLine("specific upstream evidence used, keep claims within the user request and");
        sb.AppendLine("available step outputs, and do not invent missing facts.");
        sb.AppendLine();
        sb.AppendLine("## Safety");
        sb.AppendLine();
        sb.AppendLine("This proposal is read-only. It does not request file writes, network access, or");
        sb.AppendLine("destructive operations; every generated step carries explicit `read_only`,");
        sb.AppendLine("`write_access: false`, and `network_access: false` annotations for gate review.");
        sb.AppendLine();
        sb.AppendLine("## Creator Gates");
        sb.AppendLine();
        sb.AppendLine("Generated proposals must pass structural lint, scheduler dry-run, positive and");
        sb.AppendLine("negative trigger smoke tests, highest-tier no-meta acceptance comparison, and");
        sb.AppendLine("runtime E2E comparison before auto-enable eligibility.");
        sb.AppendLine();
        sb.AppendLine("## Fallback");
        sb.AppendLine();
        sb.AppendLine("LLM should invoke the branch skills (in parallel where possible), then call the merge skill to aggregate, then optionally call the tail skill.");
    }

    private static void AppendP3Body(StringBuilder sb, string name, string description, IReadOnlyList<CreatorStep> steps)
    {
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {name} (Meta-Skill, P3 condition_gated)");
        sb.AppendLine();
        sb.AppendLine(description);
        sb.AppendLine();
        sb.AppendLine("## Execution Flow");
        for (var i = 0; i < steps.Count; i++)
        {
            sb.AppendLine();
            sb.AppendLine($"{i + 1}. `{steps[i].Id}` runs `{steps[i].Skill}`.");
            sb.AppendLine($"   Purpose: {steps[i].Task}");
        }

        sb.AppendLine();
        sb.AppendLine("## Expected Output");
        sb.AppendLine();
        sb.AppendLine("Return a decision-ready final artifact with explicit assumptions, missing-data");
        sb.AppendLine("limits, and next actions. Keep claims grounded in the step outputs.");
        sb.AppendLine();
        sb.AppendLine("## Safety");
        sb.AppendLine();
        sb.AppendLine("This proposal is read-only and keeps all generated step inputs escaped or");
        sb.AppendLine("bounded for gate review.");
        sb.AppendLine();
        sb.AppendLine("## Creator Gates");
        sb.AppendLine();
        sb.AppendLine("Generated proposals must pass structural lint, scheduler dry-run, trigger");
        sb.AppendLine("smoke tests, collision/risk checks, acceptance comparison, and runtime E2E");
        sb.AppendLine("comparison before auto-enable eligibility.");
    }

    private readonly record struct CommonSlots(string Name, string Description, int MetaPriority, IReadOnlyList<string> Triggers);
    private readonly record struct CreatorStep(string Id, string Skill, string Task, IReadOnlyDictionary<string, string> WithKeys);
}

public sealed class MetaSkillLintRunTool : ITool
{
    public string Name => "meta_skill_lint_run";
    public string Description => "Run creator lint gates and return JSON summary.";
    public string ParameterSchema => "{\"type\":\"object\",\"properties\":{\"skill_md\":{\"type\":\"string\"},\"gates\":{\"type\":\"string\"}},\"required\":[\"skill_md\"]}";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var skillMd = GetString(args.RootElement, "skill_md");
        if (string.IsNullOrWhiteSpace(skillMd))
            return ValueTask.FromResult(MetaSkillCreatorToolResult.SerializeError("invalid_arguments", "'skill_md' is required."));

        var failed = new List<string>();

        var g1Passed = skillMd.Contains("kind: meta", StringComparison.Ordinal)
            && skillMd.Contains("composition:", StringComparison.Ordinal)
            && skillMd.Contains("  steps:", StringComparison.Ordinal)
            && skillMd.Contains("    - id:", StringComparison.Ordinal);
        if (!g1Passed)
            failed.Add("G1");

        var g2Passed = g1Passed && !HasInvalidDependency(skillMd);
        if (!g2Passed)
            failed.Add("G2");

        var passed = failed.Count == 0;
        var summary = passed
            ? "G1,G2 passed"
            : $"Lint failed for gates: {string.Join(',', failed)}";

        return ValueTask.FromResult(MetaSkillCreatorToolResult.SerializeLintResult(passed, failed, summary));
    }

    private static bool HasInvalidDependency(string skillMd)
    {
        var idMatches = Regex.Matches(skillMd, "^\\s*-\\s+id:\\s*([a-zA-Z0-9_-]+)\\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in idMatches)
            ids.Add(match.Groups[1].Value);

        var depMatches = Regex.Matches(skillMd, "^\\s*depends_on:\\s*\\[([^\\]]*)\\]\\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        foreach (Match dep in depMatches)
        {
            var raw = dep.Groups[1].Value;
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var dependency = token.Trim();
                if (!ids.Contains(dependency))
                    return true;
            }
        }

        return false;
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
            return string.Empty;

        return node.GetString() ?? string.Empty;
    }
}

public sealed class MetaSkillSmokeRunTool : ITool
{
    public string Name => "meta_skill_smoke_run";
    public string Description => "Run G3/G4 smoke tests and return JSON summary.";
    public string ParameterSchema => "{\"type\":\"object\",\"properties\":{\"skill_md\":{\"type\":\"string\"},\"fixture_gen_model\":{\"type\":\"string\"},\"classifier_model\":{\"type\":\"string\"}},\"required\":[\"skill_md\"]}";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

        var skillMd = GetString(args.RootElement, "skill_md");
        if (string.IsNullOrWhiteSpace(skillMd))
            return ValueTask.FromResult(MetaSkillCreatorToolResult.SerializeError("invalid_arguments", "'skill_md' is required."));

        var classifier = GetString(args.RootElement, "classifier_model");
        if (string.IsNullOrWhiteSpace(classifier))
            classifier = "stub";

        var positive = DeterministicFixture(skillMd, "positive");
        var negative = DeterministicFixture(skillMd, "negative");

        var g3Passed = SimulateMetaResolution(skillMd, positive);
        var g4Passed = !SimulateMetaResolution(skillMd, negative);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("G3");
            writer.WriteStartObject();
            writer.WriteBoolean("passed", g3Passed);
            writer.WriteString("positive_fixture", positive);
            writer.WriteString("classifier", classifier);
            writer.WriteBoolean("degraded", true);
            writer.WriteEndObject();

            writer.WritePropertyName("G4");
            writer.WriteStartObject();
            writer.WriteBoolean("passed", g4Passed);
            writer.WriteString("negative_fixture", negative);
            writer.WriteString("classifier", classifier);
            writer.WriteBoolean("degraded", true);
            writer.WriteEndObject();

            writer.WriteBoolean("degraded", true);
            writer.WriteEndObject();
        }

        return ValueTask.FromResult(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static string DeterministicFixture(string skillMd, string kind)
    {
        if (string.Equals(kind, "positive", StringComparison.Ordinal))
        {
            var doubleQuoted = Regex.Match(skillMd, "triggers:\\s*\\n((?:\\s*-\\s*\"[^\"]+\"\\s*\\n)+)", RegexOptions.CultureInvariant);
            if (doubleQuoted.Success)
            {
                var first = Regex.Match(doubleQuoted.Groups[1].Value, "-\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant);
                if (first.Success)
                {
                    var raw = first.Groups[1].Value;
                    try
                    {
                        return $"please use {raw.Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal)}";
                    }
                    catch (JsonException)
                    {
                        return $"please use {raw}";
                    }
                }
            }

            var unquoted = Regex.Match(skillMd, "triggers:\\s*\\n((?:\\s*-\\s*[^\"\\n]+\\n)+)", RegexOptions.CultureInvariant);
            if (unquoted.Success)
            {
                var first = Regex.Match(unquoted.Groups[1].Value, "-\\s*([^\"\\n]+)", RegexOptions.CultureInvariant);
                if (first.Success)
                    return $"please use {first.Groups[1].Value.Trim()}";
            }

            return "please run this meta-skill";
        }

        if (string.Equals(kind, "negative", StringComparison.Ordinal))
            return "what's the weather forecast for tomorrow?";

        throw new InvalidOperationException($"Unknown fixture kind: {kind}");
    }

    private static bool SimulateMetaResolution(string skillMd, string prompt)
    {
        var triggers = ExtractTriggers(skillMd);
        if (triggers.Count == 0)
            return false;

        var promptLower = prompt.ToLowerInvariant();
        foreach (var trigger in triggers)
        {
            if (TriggerMatches(trigger, promptLower))
                return true;
        }

        return false;
    }

    private static List<string> ExtractTriggers(string skillMd)
    {
        var lines = skillMd.Split(['\r', '\n'], StringSplitOptions.None);
        var triggers = new List<string>();
        var inTriggers = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!inTriggers)
            {
                if (string.Equals(line, "triggers:", StringComparison.OrdinalIgnoreCase))
                    inTriggers = true;
                continue;
            }

            if (!line.StartsWith("-", StringComparison.Ordinal))
                break;

            var value = line[1..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            if (!string.IsNullOrWhiteSpace(value))
                triggers.Add(value);
        }

        return triggers;
    }

    private static bool TriggerMatches(string trigger, string promptLower)
    {
        var needle = (trigger ?? string.Empty).Trim().ToLowerInvariant();
        if (needle.Length == 0)
            return false;

        if (promptLower.Contains(needle, StringComparison.Ordinal))
            return true;

        if (needle.Any(static ch => ch > 127))
            return false;

        var pattern = @"\b" + Regex.Escape(needle).Replace("\\ ", "\\s+") + @"\b";
        return Regex.IsMatch(promptLower, pattern, RegexOptions.CultureInvariant);
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
            return string.Empty;

        return node.GetString() ?? string.Empty;
    }
}

public sealed class MetaSkillRuntimeE2ERunTool : ITool
{
    private static readonly AsyncLocal<MetaSkillRuntimeE2EContext?> RuntimeContext = new();

    public string Name => "meta_skill_runtime_e2e_run";
    public string Description => "Run candidate meta skill against no-meta baseline and return gate result.";
    public string ParameterSchema => "{\"type\":\"object\",\"properties\":{\"skill_md\":{\"type\":\"string\"},\"eval_prompts\":{\"type\":\"string\"},\"baseline_model\":{\"type\":\"string\"}},\"required\":[\"skill_md\"]}";

    public static IDisposable PushContext(MetaSkillRuntimeE2EContext context)
    {
        var prior = RuntimeContext.Value;
        RuntimeContext.Value = context;
        return new PopScope(() => RuntimeContext.Value = prior);
    }

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

        var skillMd = GetString(args.RootElement, "skill_md");
        if (string.IsNullOrWhiteSpace(skillMd))
            return MetaSkillCreatorToolResult.SerializeError("invalid_arguments", "'skill_md' is required.");

        var evalPromptsRaw = GetString(args.RootElement, "eval_prompts");
        var baselineModel = GetString(args.RootElement, "baseline_model");

        var context = RuntimeContext.Value;
        if (context is null)
        {
            return "{\"status\":\"unavailable\",\"passed\":false,\"winner\":\"\",\"reason\":\"runtime_e2e_context_unavailable\",\"cases\":[]}";
        }

        var prompts = NormalisePrompts(evalPromptsRaw, skillMd);
        var cases = new List<RuntimeCase>();
        var winners = new List<string>();

        foreach (var prompt in prompts)
        {
            var meta = await context.Runner("meta", prompt, skillMd, baselineModel, ct);
            var baseline = await context.Runner("baseline", prompt, skillMd, baselineModel, ct);
            var baselineInvalidReason = BaselineInvalidReason(baseline);
            if (!string.IsNullOrWhiteSpace(baselineInvalidReason))
            {
                winners.Add("invalid");
                cases.Add(new RuntimeCase(
                    prompt,
                    "invalid",
                    baselineInvalidReason,
                    "Baseline comparison was invalid because the no-meta route returned an error/refusal instead of its strongest standalone answer.",
                    meta,
                    baseline));
                continue;
            }

            var verdict = await context.Judge(prompt, meta, baseline, ct);
            var winner = NormaliseWinner(GetDictString(verdict, "winner"));
            winners.Add(winner);
            var regression = GetDictString(verdict, "regression");
            if (string.IsNullOrWhiteSpace(regression))
                regression = GetDictString(verdict, "required_improvements");

            cases.Add(new RuntimeCase(
                prompt,
                winner,
                regression,
                GetDictString(verdict, "reason"),
                meta,
                baseline));
        }

        var blocked = cases.Any(static c => c.Winner is not ("meta" or "tie") || !string.IsNullOrWhiteSpace(c.Regression));
        var aggregateWinner = winners.Contains("invalid", StringComparer.Ordinal)
            ? "invalid"
            : winners.Contains("baseline", StringComparer.Ordinal)
                ? "baseline"
                : winners.Contains("meta", StringComparer.Ordinal)
                    ? "meta"
                    : "tie";

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "ok");
            writer.WriteBoolean("passed", !blocked);
            writer.WriteString("winner", aggregateWinner);
            writer.WriteString("baseline_model", baselineModel);
            writer.WritePropertyName("cases");
            writer.WriteStartArray();
            foreach (var item in cases)
            {
                writer.WriteStartObject();
                writer.WriteString("prompt", item.Prompt);
                writer.WriteString("winner", item.Winner);
                writer.WriteString("regression", item.Regression);
                writer.WriteString("reason", item.Reason);
                writer.WritePropertyName("meta");
                WriteDictionary(writer, item.Meta);
                writer.WritePropertyName("baseline");
                WriteDictionary(writer, item.Baseline);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BaselineInvalidReason(IReadOnlyDictionary<string, string> baseline)
    {
        var text = GetDictString(baseline, "text").Trim().ToLowerInvariant();
        var error = GetDictString(baseline, "error").Trim();
        if (!string.IsNullOrWhiteSpace(error))
            return "baseline_error";

        var refusalMarkers = new[]
        {
            "runtime e2e baseline mode",
            "meta-skill creator tools are disabled",
            "meta_skill creator tools are disabled",
            "meta_skill_* creator tools are disabled",
            "i cannot complete this request",
            "i can’t complete this request",
        };

        return refusalMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal))
            ? "baseline_invalid_or_blocked"
            : string.Empty;
    }

    private static string NormaliseWinner(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "orchestrated" => "meta",
            "meta-skill" => "meta",
            "metaskill" => "meta",
            "no-meta" => "baseline",
            "single-model" => "baseline",
            var other => string.IsNullOrWhiteSpace(other) ? "tie" : other,
        };

    private static List<string> NormalisePrompts(string evalPromptsRaw, string skillMd)
    {
        if (!string.IsNullOrWhiteSpace(evalPromptsRaw))
        {
            var text = evalPromptsRaw.Trim();
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var prompts = doc.RootElement.EnumerateArray()
                        .Select(static item => item.GetString() ?? string.Empty)
                        .Where(static item => !string.IsNullOrWhiteSpace(item))
                        .ToList();
                    if (prompts.Count > 0)
                        return prompts;
                }
            }
            catch (JsonException)
            {
                var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(static line => line.Trim())
                    .Where(static line => !string.IsNullOrWhiteSpace(line))
                    .ToList();
                if (lines.Count > 0)
                    return lines;
            }
        }

        var match = Regex.Match(skillMd, "triggers:\\s*\\n(?:\\s*-\\s*\"?([^\"\\n]+)\"?\\s*\\n?)", RegexOptions.CultureInvariant);
        var trigger = match.Success ? match.Groups[1].Value.Trim() : "this meta skill";
        return [$"please use {trigger}"];
    }

    private static string GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
            return string.Empty;

        return node.GetString() ?? string.Empty;
    }

    private static string GetDictString(IReadOnlyDictionary<string, string> dictionary, string key)
        => dictionary.TryGetValue(key, out var value) ? value ?? string.Empty : string.Empty;

    private static void WriteDictionary(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> dictionary)
    {
        writer.WriteStartObject();
        foreach (var entry in dictionary)
            writer.WriteString(entry.Key, entry.Value);
        writer.WriteEndObject();
    }

    private readonly record struct RuntimeCase(
        string Prompt,
        string Winner,
        string Regression,
        string Reason,
        IReadOnlyDictionary<string, string> Meta,
        IReadOnlyDictionary<string, string> Baseline);

    private sealed class PopScope(Action onDispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            onDispose();
        }
    }
}

public sealed class MetaSkillPersistProposalTool : ITool
{
    public string Name => "meta_skill_persist_proposal";
    public string Description => "Write proposal candidate to ~/.opensquilla/proposals/<id>/ and return JSON metadata.";
    public string ParameterSchema => "{\"type\":\"object\",\"properties\":{\"skill_md\":{\"type\":\"string\"},\"lint_result\":{\"type\":\"string\"},\"smoke_result\":{\"type\":\"string\"},\"creator_mode\":{\"type\":\"string\"},\"acceptance_result\":{\"type\":\"string\"},\"runtime_e2e_result\":{\"type\":\"string\"},\"collision_result\":{\"type\":\"string\"},\"risk_result\":{\"type\":\"string\"},\"home\":{\"type\":\"string\"}},\"required\":[\"skill_md\",\"lint_result\",\"smoke_result\"]}";

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

        if (!TryGetRequiredString(args.RootElement, "skill_md", out var skillMd)
            || !TryGetRequiredString(args.RootElement, "lint_result", out var lintResultRaw)
            || !TryGetRequiredString(args.RootElement, "smoke_result", out var smokeResultRaw))
        {
            return ValueTask.FromResult(MetaSkillCreatorToolResult.SerializeError("invalid_arguments", "'skill_md', 'lint_result', and 'smoke_result' are required."));
        }

        var creatorMode = GetOptionalString(args.RootElement, "creator_mode");
        var acceptanceResult = GetOptionalString(args.RootElement, "acceptance_result");
        var runtimeE2EResult = GetOptionalString(args.RootElement, "runtime_e2e_result");
        var collisionResult = GetOptionalString(args.RootElement, "collision_result");
        var riskResult = GetOptionalString(args.RootElement, "risk_result");
        var home = GetOptionalString(args.RootElement, "home");

        var homeDir = ResolveHomeDirectory(home);
        var proposalId = BuildProposalId(skillMd!);
        var proposalDir = Path.Combine(homeDir, "proposals", proposalId);
        Directory.CreateDirectory(proposalDir);

        File.WriteAllText(Path.Combine(proposalDir, "SKILL.md"), skillMd, Encoding.UTF8);

        var lintResult = TryParseJsonObject(lintResultRaw!);
        var smokeResult = TryParseJsonObject(smokeResultRaw!);
        var runtimeResult = TryParseJsonObject(runtimeE2EResult);

        var autoEnableEligible = EvaluateAutoEnableEligible(creatorMode, lintResult, smokeResult, runtimeResult);

        var gates = BuildGatesPayload(
            lintResultRaw!,
            smokeResultRaw!,
            creatorMode,
            acceptanceResult,
            runtimeE2EResult,
            collisionResult,
            riskResult,
            proposalId,
            autoEnableEligible);

        File.WriteAllText(Path.Combine(proposalDir, "gates.json"), gates, Encoding.UTF8);

        return ValueTask.FromResult(MetaSkillCreatorToolResult.SerializePersistResult(
            proposalId,
            proposalDir,
            autoEnableEligible));
    }

    private static string ResolveHomeDirectory(string? home)
    {
        if (!string.IsNullOrWhiteSpace(home))
        {
            var expanded = Environment.ExpandEnvironmentVariables(home);
            return Path.GetFullPath(expanded);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opensquilla");
    }

    private static string BuildGatesPayload(
        string lintResult,
        string smokeResult,
        string creatorMode,
        string acceptanceResult,
        string runtimeE2EResult,
        string collisionResult,
        string riskResult,
        string proposalId,
        bool autoEnableEligible)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("proposal_id", proposalId);
            writer.WriteBoolean("auto_enable_eligible", autoEnableEligible);
            writer.WriteString("creator_mode", creatorMode);

            WriteJsonStringOrRaw(writer, "lint", lintResult);
            WriteJsonStringOrRaw(writer, "smoke", smokeResult);
            WriteJsonStringOrRaw(writer, "acceptance_compare", acceptanceResult);
            WriteJsonStringOrRaw(writer, "runtime_e2e", runtimeE2EResult);
            WriteJsonStringOrRaw(writer, "collision", collisionResult);
            WriteJsonStringOrRaw(writer, "risk", riskResult);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonStringOrRaw(Utf8JsonWriter writer, string propertyName, string value)
    {
        writer.WritePropertyName(propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            writer.WriteStringValue(string.Empty);
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(value);
            doc.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(value);
        }
    }

    private static bool EvaluateAutoEnableEligible(
        string creatorMode,
        Dictionary<string, string>? lint,
        Dictionary<string, string>? smoke,
        Dictionary<string, string>? runtimeE2E)
    {
        var lintPassed = GetBoolean(lint, "passed", defaultValue: false);
        var smokePassed = GetBoolean(smoke, "G3.passed", defaultValue: false) && GetBoolean(smoke, "G4.passed", defaultValue: false);

        if (!lintPassed || !smokePassed)
            return false;

        if (string.Equals(creatorMode, "FULL_GATED", StringComparison.Ordinal))
            return GetBoolean(runtimeE2E, "passed", defaultValue: false);

        return true;
    }

    private static bool GetBoolean(Dictionary<string, string>? payload, string key, bool defaultValue)
    {
        if (payload is null)
            return defaultValue;

        if (!payload.TryGetValue(key, out var value))
            return defaultValue;

        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static Dictionary<string, string>? TryParseJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var output = new Dictionary<string, string>(StringComparer.Ordinal);
            Flatten(doc.RootElement, string.Empty, output);
            return output;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> output)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var nextPrefix = string.IsNullOrEmpty(prefix) ? property.Name : prefix + "." + property.Name;
                Flatten(property.Value, nextPrefix, output);
            }

            return;
        }

        output[prefix] = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => element.GetRawText(),
            _ => element.GetRawText(),
        };
    }

    private static string BuildProposalId(string skillMd)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(skillMd));
        return "proposal-" + Convert.ToHexString(bytes[..8]).ToLowerInvariant();
    }

    private static bool TryGetRequiredString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
            return false;

        value = node.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string GetOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
            return string.Empty;

        return node.GetString() ?? string.Empty;
    }
}

public sealed record MetaSkillRuntimeE2EContext(
    Func<string, string, string, string, CancellationToken, ValueTask<IReadOnlyDictionary<string, string>>> Runner,
    Func<string, IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, CancellationToken, ValueTask<IReadOnlyDictionary<string, string>>> Judge);

internal static class MetaSkillCreatorToolResult
{
    public static string SerializeError(string errorCode, string message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "error");
            writer.WriteString("errorCode", errorCode);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SerializeLintResult(bool passed, IReadOnlyList<string> failedGates, string summary)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "ok");
            writer.WriteBoolean("passed", passed);
            writer.WritePropertyName("failedGates");
            writer.WriteStartArray();
            foreach (var gate in failedGates)
                writer.WriteStringValue(gate);
            writer.WriteEndArray();
            writer.WriteString("summary", summary);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string SerializePersistResult(string proposalId, string path, bool autoEnableEligible)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("status", "ok");
            writer.WriteString("proposal_id", proposalId);
            writer.WriteString("proposalId", proposalId);
            writer.WriteString("path", path);
            writer.WriteBoolean("auto_enable_eligible", autoEnableEligible);
            writer.WriteBoolean("auto_enabled", false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
