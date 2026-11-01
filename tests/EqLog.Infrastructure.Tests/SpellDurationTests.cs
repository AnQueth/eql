using EqLog.Core.Tracking;
using EqLog.Infrastructure.Persistence;

namespace EqLog.Core.Tests;

public class SpellDurationTests
{
    private static readonly DateTime T0 = new(2026, 10, 28, 12, 0, 0);

    [Fact]
    public async Task WikiAndRemovedSpellsPersistAndOnlyManualAdditionRevivesRemovedSpell()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eql-durations-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SpellDurationStore();
            store.SetWikiDuration("Whirl Till You Hurl X", 12);
            store.SetWikiDuration("Mesmerization X", 24);
            store.SetOverride("Whirl Till You Hurl X", 15);
            store.AddSample("Whirl Till You Hurl X", 10, T0);
            store.Remove("Whirl Till You Hurl X");
            await store.SaveAsync(path);

            var loaded = await TrackingTestPersistence.LoadSpellDurationsAsync(path);
            loaded.SetWikiDuration("Whirl Till You Hurl X", 12);
            loaded.AddSample("Whirl Till You Hurl X", 10, T0.AddMinutes(1));
            loaded.SetTracked("Whirl Till You Hurl X", true);
            Assert.False(loaded.IsTracked("Whirl Till You Hurl X"));
            Assert.Null(loaded.GetDuration("Whirl Till You Hurl X"));
            Assert.Equal(24, Assert.Single(loaded.List()).WikiSeconds);

            loaded.SetOverride("whirl till you hurl x", 14);
            Assert.True(loaded.IsTracked("Whirl Till You Hurl X"));
            Assert.Equal(14, loaded.GetDuration("Whirl Till You Hurl X"));
            var revived = Assert.Single(loaded.List(), d => d.Spell.Equals("Whirl Till You Hurl X", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, revived.Samples);
            await loaded.SaveAsync(path);
            var reloaded = await TrackingTestPersistence.LoadSpellDurationsAsync(path);
            Assert.Equal(14, reloaded.GetDuration("Whirl Till You Hurl X"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RelearnClearsSamplesKeepsOverrideAndIgnoresOlderWearOffsAfterReload()
    {
        string path = Path.Combine(Path.GetTempPath(), $"eql-durations-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SpellDurationStore();
            store.AddSample("Mesmerize", 30, T0);
            store.AddSample("Mesmerize", 31, T0.AddMinutes(1));
            store.SetOverride("Mesmerize", 25);
            store.Relearn("Mesmerize", T0.AddMinutes(1));

            var info = Assert.Single(store.List());
            Assert.Equal(0, info.Samples);
            Assert.Null(info.LearnedSeconds);
            Assert.Equal(25, info.OverrideSeconds);

            await store.SaveAsync(path);
            var loaded = await TrackingTestPersistence.LoadSpellDurationsAsync(path);
            loaded.AddSample("Mesmerize", 30, T0);
            loaded.AddSample("Mesmerize", 31, T0.AddMinutes(1));
            Assert.Equal(0, Assert.Single(loaded.List()).Samples);

            loaded.AddSample("Mesmerize", 18, T0.AddMinutes(2));
            Assert.Equal(18, Assert.Single(loaded.List()).LearnedSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

}
