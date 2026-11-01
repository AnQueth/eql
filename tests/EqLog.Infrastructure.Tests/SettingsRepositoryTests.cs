using System.Text.Json;
using EqLog.Application.Settings;

namespace EqLog.Infrastructure.Tests;

public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eql-settings-" + Guid.NewGuid().ToString("N"));

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
