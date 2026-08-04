using System.IO;
using OpenClaw.Cli;
using Xunit;

namespace OpenClaw.Tests;

public sealed class CompatibilityCommandsTests
{
    [Fact]
    public void Run_CatalogJson_PrintsFilteredCatalog()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CompatibilityCommands.Run(["catalog", "--status", "compatible", "--kind", "clawhub-skill", "--json"], output, error);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("\"version\":2", text, StringComparison.Ordinal);
        Assert.Contains("\"skillSlug\":\"peekaboo\"", text, StringComparison.Ordinal);
        Assert.Contains("\"skillRef\":\"@steipete/peekaboo\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\":\"npm-plugin\"", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_CatalogText_PrintsScenarioSummary()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CompatibilityCommands.Run(["catalog", "--category", "cli-plugin"], output, error);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("supermemory", text, StringComparison.Ordinal);
        Assert.Contains("lazy root CLI command", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compatible", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, error.ToString());
    }
}
