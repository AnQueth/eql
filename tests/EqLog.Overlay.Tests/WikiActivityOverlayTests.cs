using EqLog.Core;
using EqLog.Core.Parsing;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class WikiActivityOverlayTests
{
    [Fact]
    public void WikiActivity_CapsOperationRows()
    {
        var operations = Enumerable.Range(0, 500).Select(i => $"icon:Mob:mob {i}").ToArray();
        var activity = new WikiActivitySnapshot([], [], operations);
        var view = new OverlayView(new HashSet<ChatChannel>(), [], WikiActivity: activity);
        var presenter = Assert.Single(OverlayPresenters.All, definition => definition.Key == "wiki");

        var content = presenter.Render(null!, null!, view);

        Assert.True(content.Rows.Count < 40);
        Assert.Contains(content.Rows, row => row.Left == "485 more operations waiting");
    }
}
