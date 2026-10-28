using System.Security.Cryptography;
using System.Text.Json;
using EqLog.Core.Triggers;

namespace EqLog.Core.Tests;

public class AtomicFileTests
{
    [Fact]
    public void HashJson_MatchesHashOfSerializedBytes()
    {
        var palette = new ThemePalette { Name = "Night", Colors = new() { ["NightText"] = "#FFFFFF" } };

        byte[] expected = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(palette, AtomicFile.Json));

        Assert.Equal(expected, AtomicFile.HashJson(palette));
    }

    [Fact]
    public void HashJson_ChangesWhenContentChanges()
    {
        var trigger = new AlertTrigger { Name = "Tell" };
        byte[] before = AtomicFile.HashJson(trigger);

        trigger.Seconds = 12;

        Assert.NotEqual(before, AtomicFile.HashJson(trigger));
    }

    [Fact]
    public async Task WriteJsonAsync_StreamsValueAndLeavesNoTempFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eql-atomic-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "value.json");
        try
        {
            var palette = new ThemePalette { Name = "Night", Colors = new() { ["Pumpkin"] = "#FF8800" } };

            await AtomicFile.WriteJsonAsync(path, palette);

            var read = await AtomicFile.ReadJsonAsync<ThemePalette>(path);
            Assert.Equal("Night", read!.Name);
            Assert.Equal("#FF8800", read.Colors["Pumpkin"]);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.NotEqual(0xEF, (await File.ReadAllBytesAsync(path))[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Clones_AreDetachedFromTheOriginal()
    {
        var palette = new ThemePalette { Name = "Night", Colors = new() { ["Pumpkin"] = "#FF8800" } };
        var trigger = new AlertTrigger { Name = "Tell", Pattern = "tells you" };

        ThemePalette paletteCopy = palette.Clone();
        AlertTrigger triggerCopy = trigger.Clone();
        palette.Colors["Pumpkin"] = "#000000";
        trigger.Pattern = "changed";

        Assert.Equal("#FF8800", paletteCopy.Colors["Pumpkin"]);
        Assert.Equal("tells you", triggerCopy.Pattern);
    }
}
