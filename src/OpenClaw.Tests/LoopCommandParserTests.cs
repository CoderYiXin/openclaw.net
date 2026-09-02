using OpenClaw.Core.Loops;
using Xunit;

namespace OpenClaw.Tests;

public sealed class LoopCommandParserTests
{
    [Theory]
    [InlineData("/loop 5m check build status", LoopAction.Schedule, "5m", "check build status")]
    [InlineData("/loop 30s ping server", LoopAction.Schedule, "30s", "ping server")]
    [InlineData("/loop 1h run full audit", LoopAction.Schedule, "1h", "run full audit")]
    public void Parse_ValidSchedule_ReturnsScheduleAction(string input, LoopAction expectedAction, string expectedInterval, string expectedPrompt)
    {
        var cmd = LoopCommandParser.TryParse(input);
        Assert.NotNull(cmd);
        Assert.Equal(expectedAction, cmd.Action);
        Assert.Equal(expectedInterval, cmd.Interval);
        Assert.Equal(expectedPrompt, cmd.Prompt);
    }

    [Fact]
    public void Parse_Cancel_ReturnsCancelAction()
    {
        var cmd = LoopCommandParser.TryParse("/loop cancel");
        Assert.NotNull(cmd);
        Assert.Equal(LoopAction.Cancel, cmd.Action);
    }

    [Fact]
    public void Parse_Stop_ReturnsCancelAction()
    {
        var cmd = LoopCommandParser.TryParse("/loop stop");
        Assert.NotNull(cmd);
        Assert.Equal(LoopAction.Cancel, cmd.Action);
    }

    [Fact]
    public void Parse_Status_ReturnsStatusAction()
    {
        var cmd = LoopCommandParser.TryParse("/loop status");
        Assert.NotNull(cmd);
        Assert.Equal(LoopAction.Status, cmd.Action);
    }

    [Theory]
    [InlineData("/loop")]
    [InlineData("/loop ")]
    [InlineData("/loop 5m")]
    [InlineData("/loop abc check")]
    [InlineData("/loop 5x check")]
    public void Parse_Invalid_ReturnsInvalidAction(string input)
    {
        var cmd = LoopCommandParser.TryParse(input);
        Assert.NotNull(cmd);
        Assert.Equal(LoopAction.Invalid, cmd.Action);
    }

    [Theory]
    [InlineData("not a loop command")]
    [InlineData("/help")]
    [InlineData("/goal complete")]
    public void Parse_NotLoopCommand_ReturnsNull(string input)
    {
        var cmd = LoopCommandParser.TryParse(input);
        Assert.Null(cmd);
    }
}
