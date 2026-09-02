namespace OpenClaw.Agent.Tools;

internal static class MetaSkillCreatorTemplateCatalog
{
    private static readonly HashSet<string> SupportedPatterns =
    ["p1_sequential", "p2_fan_out_merge", "p3_condition_gated"];

    public static bool IsSupportedPattern(string patternId)
        => SupportedPatterns.Contains(patternId);

    public static string ResolveCreatorStepKind(string skillName)
    {
        if (string.Equals(skillName, "summarize", StringComparison.Ordinal))
            return "llm_chat";

        if (string.Equals(skillName, "history-explorer", StringComparison.Ordinal))
            return "skill_exec";

        return "agent";
    }
}
