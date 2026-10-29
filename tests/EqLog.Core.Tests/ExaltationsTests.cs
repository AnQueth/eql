namespace EqLog.Core.Tests;

public sealed class ExaltationsTests
{
    [Fact]
    public void WikiDefaults_StoredOriginalDrums_KeepUnverifiedEvidenceSeparateFromActualExaltations()
    {
        var items = InventoryExport.Parse("""
            Location	Name	ID	Count	Slots
            Head	Helm	1	1	11
            Head-Slot7	Known Item (Exaltation)	2	1	11

            KeyRing	Name	ID
            Equipment	Selo`s Drums of the March +6	11626
            Equipment	Sharkskin Drum +5	10373
            Equipment-Sharkskin Drum +5	Ornament	3
            Augmentation	Known Item (Exaltation)	2
            """);
        var candidates = Exaltations.WikiDefaults(items);
        Assert.Equal(2, candidates.Count);
        var drums = candidates[0];
        Assert.Equal("Selo`s Drums of the March", drums.BaseItem);
        Assert.Equal("Selo`s Drums of the March +6", drums.Host);
        Assert.True(drums.WikiDefault);
        Assert.False(drums.Installed);
        Assert.False(drums.InExaltStorage);
        Assert.Null(drums.Socket);
        Assert.Contains("export entry 1", drums.Where);
        Assert.Contains("in-game slot unverified", drums.Where);
        var effects = ItemEffectLookup.Parse(drums.BaseItem, drums.BaseItem, """
            {{Itempage
            |focus_effect = Percussion Resonance 14
            }}
            """, DateTime.UtcNow);
        Assert.True(Exaltations.Matches(drums, effects, "perc"));
        Assert.True(Exaltations.Matches(drums, effects, "drum"));
        Assert.DoesNotContain(Exaltations.Locate(items), location => location.WikiDefault);
    }

    [Fact]
    public void Locate_UsesNearestWornHostAndExactBagHost_IncludesExaltStorage()
    {
        var items = InventoryExport.Parse("""
            Location	Name	ID	Count	Slots
            Ear	First Earring +2	1	1	11
            Ear-Slot7	String Lute (Exaltation)	2	1	11
            Ear	Second Earring +4	3	1	11
            Ear-Slot8	String Lute (Exaltation)	2	1	11
            Bank6	Storage Trunk	4	1	40
            Bank6-Slot19	Banked Sword +4	5	1	11
            Bank6-Slot19-Slot7	String Lute (Exaltation)	2	1	11
            General 2	Backpack	6	1	40
            General 2-Slot3	Loose Lute (Exaltation)	7	2	11

            KeyRing	Name	ID
            Augmentation	Collection Lute (Exaltation)	8
            Equipment	Ordinary Stored Lute	9
            Equipment	Other Item (Exaltation)	10
            """);

        var locations = Exaltations.Locate(items);

        Assert.Equal(5, locations.Count);
        Assert.Equal(6, locations.Sum(location => location.Count));
        var stored = Assert.Single(locations, row => row.InExaltStorage);
        Assert.Equal("Collection Lute", stored.BaseItem);
        Assert.Equal("Exalt storage (Augmentation)", stored.Where);
        Assert.False(stored.Installed);
        Assert.Null(stored.Host);
        Assert.Null(stored.Socket);
        Assert.Contains(locations, row => row.Host == "First Earring +2" &&
            row.Where == "Worn Ear, equipment slot 1" && row.Socket == "7");
        Assert.Contains(locations, row => row.Host == "Second Earring +4" &&
            row.Where == "Worn Ear, equipment slot 4" && row.Socket == "8");
        Assert.Contains(locations, row => row.Host == "Banked Sword +4" &&
            row.Where == "Bank slot 6 (Storage Trunk), bag slot 19" && row.Socket == "7");
        Assert.Contains(locations, row => !row.Installed && row.Host is null &&
            row.Where == "Inventory slot 2 (Backpack), bag slot 3" && row.Count == 2);
    }

    [Fact]
    public void Locate_MissingHost_IsExplicitAndKeepsSocketLocation()
    {
        var locations = Exaltations.Locate(
        [
            new("Head-Slot7", "Cloak +4 (Exaltation)", 1, 1, InventoryStorage.Carried, Installed: true, WornSlot: 2),
        ]);

        var row = Assert.Single(locations);
        Assert.Null(row.Host);
        Assert.True(row.Installed);
        Assert.Equal("Cloak", row.BaseItem);
        Assert.Contains("augment slot 7", row.Where);
    }

    [Fact]
    public void Locate_SampleInventory_AccountsForEveryPhysicalExaltationAndKnownTomeHosts()
    {
        var items = InventoryExport.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "InventorySample.txt")));
        var locations = Exaltations.Locate(items);

        Assert.Equal(items.Where(item => (!item.Collection || item.Location == "Augmentation") &&
            item.Name.EndsWith(" (Exaltation)")).Sum(item => item.Count),
            locations.Sum(location => location.Count));
        Assert.Equal(items.Count(item => item.Collection && item.Location == "Augmentation" &&
            item.Name.EndsWith(" (Exaltation)")), locations.Count(location => location.InExaltStorage));
        Assert.All(locations.Where(location => location.Installed), location => Assert.NotNull(location.Host));
        var tomes = locations.Where(location => location.BaseItem == "Dark Ironbound Tome").ToArray();
        Assert.Equal(4, tomes.Sum(location => location.Count));
        Assert.All(tomes, location => Assert.True(location.Installed));
        Assert.Contains(tomes, location => location.Host == "Dark Ironbound Tome +4" &&
            location.Where.Contains("Bank slot 6") && location.Where.EndsWith("bag slot 19"));
    }

    [Theory]
    [InlineData("bard string instruments", true)]
    [InlineData("Improved damage", true)]
    [InlineData("20%", true)]
    [InlineData("bank sword", true)]
    [InlineData("installed", true)]
    [InlineData("percussion", false)]
    public void Matches_SearchesEffectsDescriptionsAndHostLocations(string query, bool expected)
    {
        var location = new ExaltationLocation("Unknown Name (Exaltation)", "Unknown Name", 1, true,
            "Banked Sword +4", "Bank slot 6, bag slot 19", "7");
        var effects = new ItemEffectInfo("Unknown Name", "", DateTime.UtcNow, true,
            [new("Focus", "Improved Damage III", null, "Increase damage by 20%")], "Bard string instruments");

        Assert.Equal(expected, Exaltations.Matches(location, effects, query));
    }

    [Fact]
    public void Matches_Percussion_FindsStoredAndInstalledCopiesWithoutIncludingOtherInstruments()
    {
        var items = InventoryExport.Parse("""
            Location	Name	ID	Count	Slots
            Head	Host Helm +4	1	1	11
            Head-Slot7	Percussion Item (Exaltation)	2	1	11

            KeyRing	Name	ID
            Augmentation	Percussion Item (Exaltation)	2
            Augmentation	Percussion Item (Exaltation)	2
            Augmentation	Brass Item (Exaltation)	3
            """);
        var info = ItemEffectLookup.Parse("Percussion Item", "Percussion Item", """
            {{Itempage
            |focus_effect = Percussion Resonance 11
            }}
            """, DateTime.UtcNow);
        var brass = ItemEffectLookup.Parse("Brass Item", "Brass Item", """
            {{Itempage
            |focus_effect = Brass Resonance 14
            }}
            """, DateTime.UtcNow);

        var all = Exaltations.Locate(items);
        var matches = all.Where(location => Exaltations.Matches(location,
            location.BaseItem == "Percussion Item" ? info : brass, "percussion")).ToArray();

        Assert.Equal(3, matches.Length);
        Assert.Equal(2, matches.Count(location => location.InExaltStorage));
        Assert.Single(matches, location => location.Installed && location.Host == "Host Helm +4");
    }
}
