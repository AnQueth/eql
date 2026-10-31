using EqLog.Core;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class MobQuestPresentationTests
{
    [Fact]
    public void MobQuestRows_ShowEachQuestWithItsActualWikiLink()
    {
        var wiki = MobWikiLookup.ParseMob("NPC",
            "{{Namedmobpage\n| related_quests = [[Quest One|Alias]] [[Quest Two]]\n}}", DateTime.UtcNow);

        var rows = MainWindow.MobQuestRows(wiki);

        Assert.Equal("Associated quests (wiki)", rows[0].Left);
        Assert.True(rows[0].QuestHeading);
        Assert.Equal(["Quest One", "Quest Two"], rows.Where(row => row.Link is not null).Select(row => row.Left));
        Assert.All(rows.Where(row => row.Link is not null), row =>
        {
            Assert.Equal(QuestLookup.PageUrl(row.Left), row.Link);
            Assert.True(row.Wrap);
        });
    }

    [Fact]
    public void MobQuestRows_DistinguishUnknownLegacyCacheFromNoAssociations()
    {
        var wiki = MobWikiLookup.ParseMob("NPC", "{{Namedmobpage}}", DateTime.UtcNow);

        Assert.Contains(MainWindow.MobQuestRows(wiki), row => row.Left.StartsWith("No associated quests"));
        Assert.Contains(MainWindow.MobQuestRows(wiki with
        {
            Quests = null
        }),
            row => row.Left.Contains("not been loaded yet"));
        Assert.Empty(MainWindow.MobQuestRows(wiki with
        {
            Found = false
        }));
        Assert.Empty(MainWindow.MobQuestRows(null));
    }
}
