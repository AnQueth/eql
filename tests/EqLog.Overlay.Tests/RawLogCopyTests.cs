using System.Runtime.InteropServices;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class RawLogCopyTests
{
    [Theory]
    [InlineData("[Tue Oct 27 12:00:00 2026] Your Denon's Desperate Dirge hits a mob.", "Your Denon's Desperate Dirge hits a mob.")]
    [InlineData("  Untimestamped message  ", "Untimestamped message")]
    public void CopyMessageCopiesOnlyTheLogMessage(string raw, string expected)
    {
        string? copied = null;
        string status = MainWindow.CopyLogMessage(raw, text => copied = text);

        Assert.Equal(expected, copied);
        Assert.Contains("copied", status);
    }

    [Fact]
    public void CopyMessageRequiresASelectedNonemptyMessage()
    {
        bool copied = false;
        Assert.Contains("Select a line", MainWindow.CopyLogMessage(null, _ => copied = true));
        Assert.Contains("no message", MainWindow.CopyLogMessage("   ", _ => copied = true));
        Assert.False(copied);
    }

    [Fact]
    public void CopyMessageReportsClipboardFailure()
    {
        string status = MainWindow.CopyLogMessage("message", _ => throw new ExternalException("clipboard unavailable"));

        Assert.Contains("Could not copy", status);
        Assert.Contains("clipboard unavailable", status);
    }
}
