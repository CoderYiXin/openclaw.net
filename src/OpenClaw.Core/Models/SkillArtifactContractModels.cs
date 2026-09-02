namespace OpenClaw.Core.Skills;

// ArtifactContract and SkillProjection model types.
// Migrated from kingcrab (OpenClaw Gateway fork) to openclaw.net upstream.
// See docs/ArtifactContract-技术文档.md for the design document.

// ────────────────────────────────────────────────────────────
// Projection discovery / contract set models
// ────────────────────────────────────────────────────────────

public sealed class SkillProjectionDiscovery
{
    public required string Status { get; init; }
    public int IndexCount { get; init; }
    public int BoundCount { get; init; }
    public IReadOnlyList<string> IndexPaths { get; init; } = [];
    public string? Message { get; init; }
}

/// <summary>
/// Bound projection contracts attached to a skill.
/// </summary>
public sealed class SkillProjectionContractSet
{
    public string? ProducerName { get; init; }
    public int ProducerPriority { get; init; }
    public required string RootPath { get; init; }
    public required ProjectionContractIndex Index { get; init; }
}

public sealed class SkillProjectionResolution
{
    public required string SkillName { get; init; }
    public bool HasContracts { get; init; }
    public bool IsBlocked { get; init; }
    public string? BlockReason { get; init; }
    public string? SelectedTopic { get; init; }
    public string? SelectedTargetView { get; init; }
    public string? ProjectionFilePath { get; init; }
    public ProjectionDocument? Projection { get; init; }
}

// ────────────────────────────────────────────────────────────
// Projection contract index / scoring models
// ────────────────────────────────────────────────────────────

public sealed class ProjectionContractIndex
{
    public string? ProducerSkill { get; init; }
    public int ProducerPriority { get; init; }
    public ProjectionSelectionPolicy DefaultSelectionPolicy { get; init; } = new();
    public ProjectionTopicScoring? TopicScoring { get; init; }
    public ProjectionTargetViewScoring? TargetViewScoring { get; init; }
    public IReadOnlyList<ProjectionTopicRecord> Topics { get; init; } = [];
}

public sealed class ProjectionSelectionPolicy
{
    public bool PreferReadyOnly { get; init; }
    public bool BlockOnOpenQuestions { get; init; }
    public IReadOnlyList<string> FallbackOrderByTargetView { get; init; } = [];
}

public sealed class ProjectionTopicScoring
{
    public int ClarifyWhenScoreGapBelow { get; init; } = 2;
    public IReadOnlyList<ProjectionScoreDimension> ScoreDimensions { get; init; } = [];
    public IReadOnlyList<ProjectionTopicSignals> Topics { get; init; } = [];
}

public sealed class ProjectionTargetViewScoring
{
    public int ClarifyWhenScoreGapBelow { get; init; } = 2;
    public bool PreferExplicitUserArtifactRequests { get; init; }
    public IReadOnlyList<ProjectionScoreDimension> ScoreDimensions { get; init; } = [];
    public IReadOnlyList<ProjectionViewSignals> Views { get; init; } = [];
    public IReadOnlyList<ProjectionTopicViewOverride> WithinTopicOverrides { get; init; } = [];
}

public sealed class ProjectionScoreDimension
{
    public required string Dimension { get; init; }
    public int Score { get; init; }
}

public sealed class ProjectionTopicSignals
{
    public required string DomainSlug { get; init; }
    public IReadOnlyList<string> PrimaryIntentSignals { get; init; } = [];
    public IReadOnlyList<string> SupportingSignals { get; init; } = [];
    public IReadOnlyList<string> ExplicitArtifactSignals { get; init; } = [];
    public IReadOnlyList<string> DemoteWhenCompetingTopicSignals { get; init; } = [];
}

public sealed class ProjectionViewSignals
{
    public required string TargetView { get; init; }
    public IReadOnlyList<string> ExplicitOutputSignals { get; init; } = [];
    public IReadOnlyList<string> StrongSignals { get; init; } = [];
    public IReadOnlyList<string> SupportingSignals { get; init; } = [];
    public IReadOnlyList<string> DemoteWhenCompetingViewSignals { get; init; } = [];
}

public sealed class ProjectionTopicViewOverride
{
    public required string DomainSlug { get; init; }
    public IReadOnlyList<ProjectionTopicViewBonus> Bonuses { get; init; } = [];
}

public sealed class ProjectionTopicViewBonus
{
    public required string TargetView { get; init; }
    public IReadOnlyList<string> WhenRequestSignals { get; init; } = [];
    public int Score { get; init; }
}

public sealed class ProjectionTopicRecord
{
    public required string DomainSlug { get; init; }
    public required string DefaultTargetView { get; init; }
    public IReadOnlyList<ProjectionViewRecord> Views { get; init; } = [];
}

public sealed class ProjectionViewRecord
{
    public required string TargetView { get; init; }
    public required string Status { get; init; }
    public required string Path { get; init; }
}

// ────────────────────────────────────────────────────────────
// Projection document (loaded from projection.json)
// ────────────────────────────────────────────────────────────

public sealed class ProjectionDocument
{
    public ProjectionMappingPolicy MappingPolicy { get; init; } = new();
    public ProjectionPromptPayload PromptProjection { get; init; } = new();
    public IReadOnlyList<ProjectionDeliveryArtifact> DeliveryArtifacts { get; init; } = [];
    public IReadOnlyList<string> DroppedItems { get; init; } = [];
    public IReadOnlyList<string> OpenQuestions { get; init; } = [];
}

public sealed class ProjectionMappingPolicy
{
    public string? UnresolvedItemPolicy { get; init; }
    public string? PromptAssumptionPolicy { get; init; }
}

public sealed class ProjectionPromptPayload
{
    public IReadOnlyList<string> AllowedTerms { get; init; } = [];
    public IReadOnlyList<string> ForbiddenAssumptions { get; init; } = [];
    public IReadOnlyList<string> RequiredClarifications { get; init; } = [];
    public IReadOnlyList<string> ReasoningPaths { get; init; } = [];
    public IReadOnlyList<string> SourceDigest { get; init; } = [];
}

public sealed class ProjectionDeliveryArtifact
{
    public required string ArtifactName { get; init; }
    public required string ArtifactType { get; init; }
    public required string Path { get; init; }
    public string? Status { get; init; }
}

// ────────────────────────────────────────────────────────────
// Artifact contract models
// ────────────────────────────────────────────────────────────

public sealed class SkillArtifactContract
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<SkillArtifactStageContract> Stages { get; init; } = [];
}

public sealed class SkillArtifactStageContract
{
    public required string Name { get; init; }
    public string? Label { get; init; }
    public SkillArtifactStageGateContract? Gate { get; init; }
    public IReadOnlyList<SkillArtifactTypeContract> Artifacts { get; init; } = [];
}

public sealed class SkillArtifactStageGateContract
{
    public string? RequiresStage { get; init; }
}

public sealed class SkillArtifactTypeContract
{
    public required string Type { get; init; }
    public string? Label { get; init; }
    public string? Display { get; init; }
    public bool? Terminal { get; init; }
}
