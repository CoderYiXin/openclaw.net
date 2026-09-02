using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;
using OpenClaw.Gateway.Tools;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SkillArtifactRuntimeTests
{
    [Fact]
    public void NormalizeAndRecord_BlocksGatedStageUntilRequiredStageIsTerminal()
    {
        var runtime = new SkillArtifactRuntime();
        runtime.ReplaceSkills(
        [
            new SkillDefinition
            {
                Name = "artifact-skill",
                Description = "Emits staged artifacts.",
                Instructions = "Emit artifacts.",
                Location = "/skills/artifact-skill",
                ArtifactContract = new SkillArtifactContract
                {
                    SchemaVersion = 1,
                    Stages =
                    [
                        new SkillArtifactStageContract
                        {
                            Name = "draft",
                            Artifacts =
                            [
                                new SkillArtifactTypeContract
                                {
                                    Type = "draft_package",
                                    Terminal = true
                                }
                            ]
                        },
                        new SkillArtifactStageContract
                        {
                            Name = "final",
                            Gate = new SkillArtifactStageGateContract
                            {
                                RequiresStage = "draft"
                            },
                            Artifacts =
                            [
                                new SkillArtifactTypeContract
                                {
                                    Type = "final_package"
                                }
                            ]
                        }
                    ]
                }
            }
        ]);

        var blocked = runtime.NormalizeAndRecord("session-1", new SkillArtifact
        {
            Kind = "data",
            SkillName = "artifact-skill",
            Stage = "final",
            ArtifactType = "final_package"
        });

        Assert.False(blocked.Succeeded);
        Assert.Contains("draft", blocked.Error);

        var completedDraft = runtime.NormalizeAndRecord("session-1", new SkillArtifact
        {
            Kind = "data",
            SkillName = "artifact-skill",
            Stage = "draft",
            ArtifactType = "draft_package",
            IsTerminal = true
        });

        Assert.True(completedDraft.Succeeded);

        var allowed = runtime.NormalizeAndRecord("session-1", new SkillArtifact
        {
            Kind = "data",
            SkillName = "artifact-skill",
            Stage = "final",
            ArtifactType = "final_package"
        });

        Assert.True(allowed.Succeeded);
        Assert.Equal("final", allowed.Artifact!.Stage);
        Assert.Equal("final_package", allowed.Artifact.ArtifactType);
    }
}
