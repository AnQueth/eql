using System.Text.RegularExpressions;

namespace EqLog.Core;

public static partial class ItemNames
{
    public static string Normalize(string name)
    {
        return Spaces().Replace(Upgrade().Replace(name, " "), " ").Trim();
    }

    public static IEqualityComparer<string> Comparer { get; } = new ItemComparer();

    [GeneratedRegex(@"\+\s*[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Upgrade();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    private sealed class ItemComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y)
        {
            return x is null || y is null
            ? x == y : StringComparer.OrdinalIgnoreCase.Equals(Normalize(x), Normalize(y));
        }

        public int GetHashCode(string obj)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(Normalize(obj));
        }
    }
}
