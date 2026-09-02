namespace OpenClaw.Core.Skills;

public static class SkillProjectionViewKeys
{
    public const string JsonSchema = "json-schema";
    public const string WorkflowContract = "workflow-contract";
    public const string DomainModel = "domain-model";
    public const string PromptConstraint = "prompt-constraint";
}

internal static class SkillProjectionArtifactTerms
{
    public static IReadOnlyList<string> GetExplicitArtifactTerms(string targetView)
        => targetView.Trim().ToLowerInvariant() switch
        {
            SkillProjectionViewKeys.JsonSchema => ["json schema", "schema file", "schema definition", "json schema 文件", "schema 文件", "schema 定义"],
            SkillProjectionViewKeys.WorkflowContract => ["workflow contract", "工作流契约"],
            SkillProjectionViewKeys.DomainModel => ["domain model", "领域模型"],
            SkillProjectionViewKeys.PromptConstraint => ["prompt policy", "prompt constraint", "提示词策略", "提示词约束"],
            _ => []
        };
}