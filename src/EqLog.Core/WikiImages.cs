using System.Security.Cryptography;
using System.Text;

namespace EqLog.Core;

/// <summary>What a wiki icon depicts, which decides how its page is looked up and how large it is fetched.</summary>
public enum WikiIconKind
{
    /// <summary>An item's 40px inventory icon.</summary>
    Item,

    /// <summary>A thumbnail of an NPC's screenshot.</summary>
    Mob,
}

/// <summary>Identifies an icon by what it depicts; equality ignores the case of the name.</summary>
/// <param name="Kind">Item or mob.</param>
/// <param name="Name">The item or mob name.</param>
public sealed record WikiIconKey(WikiIconKind Kind, string Name)
{
    public bool Equals(WikiIconKey? other)
    {
        if (other is null)
        {
            return false;
        }
        return Kind == other.Kind && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(Name));
    }
}

/// <summary>Builds EQLWiki image file names and upload URLs without an API round trip.</summary>
public static class WikiImages
{
    /// <summary>The width mob screenshots are fetched at; the originals are full screenshots.</summary>
    public const int MobThumbnailWidth = 48;

    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif"];

    /// <summary>The icon file of an item page's <c>lucy_img_ID</c>, or empty when the page has no icon.</summary>
    public static string ItemIconFile(string? lucyImageId)
    {
        string id = (lucyImageId ?? "").Trim();
        if (id.Length == 0 || id == "0" || !id.All(char.IsAsciiDigit))
        {
            return "";
        }
        return "Item_" + id + ".png";
    }

    /// <summary>A wiki file name as MediaWiki stores it: no namespace, underscores, first letter upper case.</summary>
    /// <returns>The stored name, or empty when the value is not a plain image file name.</returns>
    public static string NormalizeFileName(string? fileName)
    {
        string name = (fileName ?? "").Trim();
        int colon = name.IndexOf(':');
        if (colon >= 0 && name[..colon].Trim() is var prefix &&
            (prefix.Equals("File", StringComparison.OrdinalIgnoreCase) || prefix.Equals("Image", StringComparison.OrdinalIgnoreCase)))
        {
            name = name[(colon + 1)..].Trim();
        }
        name = name.Replace(' ', '_');
        if (name.Length == 0 || name.IndexOfAny(['/', '\\', ':', '|', '[', ']', '{', '}', '<', '>', '#', '?', '*', '"']) >= 0 ||
            name.Contains("..", StringComparison.Ordinal) ||
            !ImageExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            return "";
        }
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>The upload URL of <paramref name="fileName"/>, or of its server-scaled thumbnail.</summary>
    /// <param name="fileName">A normalized file name.</param>
    /// <param name="thumbnailWidth">The thumbnail width in pixels, or null for the original.</param>
    public static string ImageUrl(string fileName, int? thumbnailWidth = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        string hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(fileName)));
        string path = hash[..1] + "/" + hash[..2] + "/" + Uri.EscapeDataString(fileName);
        if (thumbnailWidth is int width)
        {
            return "https://eqlwiki.com/images/thumb/" + path + "/" + width + "px-" + Uri.EscapeDataString(fileName);
        }
        return "https://eqlwiki.com/images/" + path;
    }

    /// <summary>Whether <paramref name="data"/> starts like a PNG, JPEG or GIF, so error pages are never cached as icons.</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]))
        {
            return true;
        }
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return true;
        }
        return data.StartsWith("GIF8"u8);
    }
}
