using EqLog.Core;
using Xunit;

namespace EqLog.Overlay.Tests;

public sealed class ExaltationPresentationTests
{
    [Fact]
    public void Card_WikiDefault_DoesNotClaimConfirmedHostSocketOrQuantity()
    {
        var candidate = new ExaltationLocation("Drums (Exaltation)", "Drums", 1, false,
            "Drums +6", "Equipment storage, export entry 78 (in-game slot unverified)", null, WikiDefault: true);
        var info = new ItemEffectInfo("Drums", "https://eqlwiki.com/Drums", DateTime.UtcNow, true,
            [new("Focus", "Percussion Resonance 14", null)], "");

        var card = MainWindow.BuildExaltationCard([candidate], info);

        Assert.Equal("Expected exaltation (unverified): Drums (Exaltation)", card.Left);
        var table = Assert.IsType<OverlayTable>(card.Table);
        Assert.Equal("Evidence", table.Rows[0][0]);
        Assert.Contains(table.Rows, row => row[0] == "Original item" && row[1] == "Drums +6");
        Assert.Contains(table.Rows, row => row[0] == "Installation" && row[1].Contains("Unverified"));
        Assert.DoesNotContain(table.Rows, row => row[0] is "Installed in" or "Socket" or "Quantity" or "Host location");
    }

    [Fact]
    public void Card_DistinguishesExaltationEffectSourceAndHost_InOneCard()
    {
        var location = new ExaltationLocation("White Dragonscale Cloak (Exaltation)",
            "White Dragonscale Cloak", 1, true, "Carmine Turban +8", "Worn Head, equipment slot 2", "8");
        var info = new ItemEffectInfo(location.BaseItem, "https://eqlwiki.com/White_Dragonscale_Cloak",
            DateTime.UtcNow, true,
            [new("Focus", "Improved Damage III", "https://eqlwiki.com/Improved_Damage_III", "Increases spell damage.")], "");

        var card = MainWindow.BuildExaltationCard([location], info);

        Assert.Equal("Exaltation: White Dragonscale Cloak (Exaltation)", card.Left);
        Assert.Null(card.Link);
        Assert.False(card.QuestHeading);
        Assert.True(card.Stacked);
        var table = Assert.IsType<OverlayTable>(card.Table);
        Assert.True(table.RowLabels);
        Assert.Collection(table.Rows,
            row => Assert.Equal(["Effect", "Focus: Improved Damage III"], row),
            row => Assert.Equal(["What it does", "Increases spell damage."], row),
            row => Assert.Equal(["Effect source", "White Dragonscale Cloak (original item wiki)"], row),
            row => Assert.Equal(["Installed in", "Carmine Turban +8"], row),
            row => Assert.Equal(["Host location", "Worn Head, equipment slot 2"], row),
            row => Assert.Equal(["Socket", "8"], row),
            row => Assert.Equal(["Quantity", "1"], row));
        Assert.Equal(info.Url, table.Links!["White Dragonscale Cloak (original item wiki)"]);
        Assert.Equal(info.Effects[0].Url, table.Links["Focus: Improved Damage III"]);
        Assert.DoesNotContain("Carmine Turban +8", table.Links.Keys);
    }

    [Fact]
    public void Card_MultipleCopies_KeepsEachHostWithItsLocationAndSocket()
    {
        ExaltationLocation[] copies =
        [
            new("Tome (Exaltation)", "Tome", 1, true, "Sword +4", "Bank slot 6, bag slot 19", "7"),
            new("Tome (Exaltation)", "Tome", 2, false, null, "Inventory slot 5, bag slot 23", null),
        ];

        var card = MainWindow.BuildExaltationCard(copies, null);

        var table = Assert.IsType<OverlayTable>(card.Table);
        Assert.Collection(table.Rows.Skip(2),
            row => Assert.Equal(["Copy 1 - Installed in", "Sword +4"], row),
            row => Assert.Equal(["Host location", "Bank slot 6, bag slot 19"], row),
            row => Assert.Equal(["Socket", "7"], row),
            row => Assert.Equal(["Quantity", "1"], row),
            row => Assert.Equal(["Copy 2 - Installed in", "Not installed (loose exaltation)"], row),
            row => Assert.Equal(["Stored at", "Inventory slot 5, bag slot 23"], row),
            row => Assert.Equal(["Quantity", "2"], row));
    }

    [Theory]
    [InlineData(false, "Unknown - base item page not found or unsupported.")]
    [InlineData(true, "Unknown - no supported effect listed on the base item.")]
    public void Card_UnknownEffect_DoesNotReplaceExaltationIdentity(bool found, string expected)
    {
        var location = new ExaltationLocation("Cap (Exaltation)", "Cap", 1, true, null, "Head-Slot8", "8");
        var info = new ItemEffectInfo("Cap", "https://eqlwiki.com/Cap", DateTime.UtcNow, found, [], "");

        var card = MainWindow.BuildExaltationCard([location], info, "Lookup failed; cached data kept.");

        Assert.Equal("Exaltation: Cap (Exaltation)", card.Left);
        Assert.Equal("Lookup failed; cached data kept.", card.Details);
        var table = Assert.IsType<OverlayTable>(card.Table);
        Assert.Equal(["Effect", expected], table.Rows[0]);
        Assert.Equal(["Installed in", "Unknown host - missing from export"], table.Rows[2]);
    }

    [Fact]
    public void Card_ExaltStorage_IsNotLabeledAsAnInstalledOrLooseItem()
    {
        var stored = new ExaltationLocation("Drums (Exaltation)", "Drums", 1, false,
            null, "Exalt storage (Augmentation)", null, InExaltStorage: true);

        var card = MainWindow.BuildExaltationCard([stored], null);

        var table = Assert.IsType<OverlayTable>(card.Table);
        Assert.Equal(["Installed in", "Not installed (in exalt storage)"], table.Rows[2]);
        Assert.Equal(["Stored at", "Exalt storage (Augmentation)"], table.Rows[3]);
        Assert.DoesNotContain(table.Rows, row => row[0] == "Socket" || row[0] == "Host location");
    }
}
