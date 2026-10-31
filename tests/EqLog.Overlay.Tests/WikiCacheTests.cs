using System.IO;
using System.Xml.Linq;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class WikiCacheTests
{
    [Fact]
    public async Task LookupLease_CancellationDrainsWritersBeforeReset_WithoutCancellingApplicationLifetime()
    {
        using var lifetime = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int finished = 0;
        var lookup = new MainWindow.WikiLookupLease(lifetime.Token, default, _ => finished++);
        var token = lookup.Token;
        var writer = Task.Run(async () =>
        {
            using (lookup)
            {
                entered.TrySetResult();
                await released.Task;
                token.ThrowIfCancellationRequested();
            }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lookup.Cancel();

        Assert.True(token.IsCancellationRequested);
        Assert.False(lifetime.IsCancellationRequested);
        Assert.False(lookup.Completion.IsCompleted);
        released.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer);
        await lookup.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, finished);
        using var next = new MainWindow.WikiLookupLease(lifetime.Token, default, _ =>
        {
        });
        Assert.False(next.Token.IsCancellationRequested);
    }

    [Fact]
    public void Settings_ExposeSeparateInlineWikiCacheResetUnderGeneral()
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));
        var button = Assert.Single(document.Descendants(presentation + "Button"),
            element => (string?)element.Attribute(xaml + "Name") == "ResetWikiCacheButton");
        Assert.Equal("Reset all wiki caches", (string?)button.Attribute("Content"));
        Assert.Equal("ResetWikiCache_Click", (string?)button.Attribute("Click"));
        var group = button.Ancestors(presentation + "GroupBox").First();
        Assert.Equal("Wiki caches", (string?)group.Attribute("Header"));
        Assert.Contains(group.Descendants(), element => (string?)element.Attribute(xaml + "Name") == "ResetWikiCacheStatus");
        Assert.Equal("General", (string?)button.Ancestors(presentation + "TabItem").First().Attribute("Header"));
    }
}
