using System.Text.Json;
using EqLog.Application.Settings;

namespace EqLog.Infrastructure.Tests;

public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eql-settings-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadedAndRepeatedUnchangedSettingsAreNotRewritten()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "settings.json");
        await File.WriteAllTextAsync(path, """{"FontSize":19,"FutureSetting":true}""");
        var repository = new SettingsRepository(path);
        var settings = await repository.LoadAsync();
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);
        await repository.SaveAsync(settings.Snapshot());
        await repository.SaveAsync(settings.Snapshot());
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        settings.FontSize++;
        await repository.SaveAsync(settings.Snapshot());
        Assert.Equal(20, (await repository.LoadAsync()).FontSize);
        Assert.True(settings.ExtensionData!["FutureSetting"].GetBoolean());
    }

    [Fact]
    public async Task MissingDocumentAndRoundTripUseExistingFormat()
    {
        var repository = new SettingsRepository(Path.Combine(_folder, "settings.json"));
        Assert.NotNull(await repository.LoadAsync());
        await repository.SaveAsync(new()
        {
            FontSize = 19,
            LogPath = "log"
        });
        var loaded = await repository.LoadAsync();
        Assert.Equal(19, loaded.FontSize);
        Assert.Equal("log", loaded.LogPath);
    }

    [Fact]
    public async Task DisplayScaleDefaultsForFreshAndExistingSettingsAndRoundTrips()
    {
        string path = Path.Combine(_folder, "settings.json");
        var repository = new SettingsRepository(path);
        Assert.Equal(1, (await repository.LoadAsync()).DisplayScale);

        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(path, """{"FontSize":15}""");
        repository = new SettingsRepository(path);
        var settings = await repository.LoadAsync();
        Assert.Equal(1, settings.DisplayScale);

        settings.DisplayScale = 1.35;
        await repository.SaveAsync(settings.Snapshot());
        var loaded = await repository.LoadAsync();
        Assert.Equal(1.35, loaded.DisplayScale);
        Assert.Equal(15, loaded.FontSize);
    }

    [Fact]
    public async Task FreshInstallIncludesRecastPresetAndPreservesItOnSave()
    {
        var repository = new SettingsRepository(Path.Combine(_folder, "settings.json"));
        var fresh = await repository.LoadAsync();
        var dirge = Assert.Single(fresh.Triggers, trigger => trigger.Name == "DDD");
        Assert.Equal(15, dirge.DelaySeconds);
        Assert.Contains(fresh.Triggers, trigger => trigger.Name == "Feign Death failed");
        Assert.Contains(fresh.Triggers, trigger => trigger.Name == "Feign Death broken");

        await repository.SaveAsync(fresh.Snapshot());
        var loaded = await repository.LoadAsync();

        Assert.Equal(15, Assert.Single(loaded.Triggers, trigger => trigger.Name == "DDD").DelaySeconds);
    }

    [Theory]
    [InlineData("""{"Triggers":[]}""")]
    [InlineData("""{"Triggers":[{"Name":"Custom","Simple":"my message"}]}""")]
    public async Task ExistingTriggerListsAreNotReplacedOrGivenAdditionalPresets(string json)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "settings.json");
        await File.WriteAllTextAsync(path, json);
        var repository = new SettingsRepository(path);

        var settings = await repository.LoadAsync();
        await repository.SaveAsync(settings.Snapshot());
        var loaded = await repository.LoadAsync();

        Assert.DoesNotContain(loaded.Triggers, trigger => trigger.Name == "DDD");
        Assert.DoesNotContain(loaded.Triggers, trigger => trigger.Name == "Feign Death failed");
        Assert.Equal(json.Contains("Custom") ? 1 : 0, loaded.Triggers.Count);
    }

    [Fact]
    public async Task SpellTimersKnownSpellsVisibility_DefaultsVisibleAndRoundTrips()
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "settings.json");
        await File.WriteAllTextAsync(path, """{"FontSize":15}""");
        var repository = new SettingsRepository(path);

        var settings = await repository.LoadAsync();
        Assert.True(settings.SpellTimersShowKnownSpells);

        settings.SpellTimersShowKnownSpells = false;
        await repository.SaveAsync(settings.Snapshot());
        var hidden = await repository.LoadAsync();
        Assert.False(hidden.SpellTimersShowKnownSpells);
        Assert.Equal(15, hidden.FontSize);

        hidden.SpellTimersShowKnownSpells = true;
        await repository.SaveAsync(hidden.Snapshot());
        Assert.True((await repository.LoadAsync()).SpellTimersShowKnownSpells);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task MalformedDocumentThrowsAndIsNotReplaced(string text)
    {
        Directory.CreateDirectory(_folder);
        string path = Path.Combine(_folder, "settings.json");
        await File.WriteAllTextAsync(path, text);
        await Assert.ThrowsAsync<JsonException>(() => new SettingsRepository(path).LoadAsync());
        Assert.Equal(text, await File.ReadAllTextAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }
}
