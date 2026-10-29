using System.Text.RegularExpressions;

namespace EqLog.Core.Tracking;

public static partial class SpellNames
{
    /// <summary>Strips a trailing rank such as " X", " IX" or " Rk. II" so "Envenomed Bolt IX" matches "Envenomed Bolt".</summary>
    public static string BaseName(string spell)
    {
        return RankSuffix().Replace(spell, string.Empty);
    }

    public static bool SameBase(string a, string b)
    {
        return string.Equals(BaseName(a), BaseName(b), StringComparison.OrdinalIgnoreCase);
    }

    public static int Rank(string spell)
    {
        var suffix = RankSuffix().Match(spell);
        int rank = 0;
        int previous = 0;
        for (int i = suffix.Value.Length - 1; i >= 0; i--)
        {
            int value = suffix.Value[i] switch
            {
                'I' => 1,
                'V' => 5,
                'X' => 10,
                'L' => 50,
                'C' => 100,
                _ => 0,
            };
            rank += value < previous ? -value : value;
            previous = Math.Max(previous, value);
        }
        return suffix.Success ? rank : 1;
    }

    public static string? FindByBase(IEnumerable<string> candidates, string spell)
    {
        string b = BaseName(spell);
        return candidates.FirstOrDefault(c => string.Equals(BaseName(c), b, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\s+(?:Rk\.\s*)?[IVXLC]+$")]
    private static partial Regex RankSuffix();
}
