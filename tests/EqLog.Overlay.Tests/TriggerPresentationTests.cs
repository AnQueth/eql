using EqLog.Core.Triggers;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class TriggerPresentationTests
{
    [Theory]
    [InlineData("{C}: FD broken by $1", "Que: FD broken by a spell hit you")]
    [InlineData("Tell from ${who}", "Tell from Que")]
    [InlineData("$& / $$", "Que: a spell hit you / $")]
    [InlineData("", "Que: a spell hit you")]
    [InlineData("  ", "Que: a spell hit you")]
    public void Format_AppliesCaptureAndCharacterTemplatesOnlyInPresentation(string template, string expected)
    {
        var matcher = new TriggerMatcher([new AlertTrigger { Pattern = @"^(?<who>\w+): (.+)$", Text = template }], "Que");
        var alert = Assert.Single(matcher.Match(["Que: a spell hit you"]));
        var result = TriggerPresentation.Format(alert);
        Assert.Equal(expected, result.Text);
        Assert.Null(result.Error);
    }

    [Fact]
    public void CompileError_AddsDisplayIdentityToTheDiagnostic()
    {
        Assert.Equal("bad: diagnostic", TriggerPresentation.CompileError(new("bad", "[", "diagnostic")));
        Assert.Equal("[: diagnostic", TriggerPresentation.CompileError(new("", "[", "diagnostic")));
    }
}
