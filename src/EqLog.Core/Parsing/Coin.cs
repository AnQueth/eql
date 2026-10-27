using System.Text;
using System.Text.RegularExpressions;

namespace EqLog.Core.Parsing;

/// <summary>An amount of money stored as copper (1p = 10g = 100s = 1000c).</summary>
public readonly partial record struct Coin(long Copper)
{
    public static Coin Zero => new(0);

    public long Platinum => Copper / 1000;

    public static Coin operator +(Coin a, Coin b) => new(a.Copper + b.Copper);

    /// <summary>Parses text such as "3 platinum, 2 gold, 1 silver and 5 copper".</summary>
    public static Coin Parse(string text)
    {
        long total = 0;
        foreach (Match m in CoinRegex().Matches(text))
        {
            long n = long.Parse(m.Groups[1].Value);
            total += m.Groups[2].Value switch
            {
                "platinum" => n * 1000,
                "gold" => n * 100,
                "silver" => n * 10,
                _ => n,
            };
        }

        return new Coin(total);
    }

    public override string ToString()
    {
        if (Copper == 0)
        {
            return "0c";
        }

        var sb = new StringBuilder();
        long p = Copper / 1000, g = Copper / 100 % 10, s = Copper / 10 % 10, c = Copper % 10;
        if (p > 0) sb.Append(p.ToString("N0")).Append("p ");
        if (g > 0) sb.Append(g).Append("g ");
        if (s > 0) sb.Append(s).Append("s ");
        if (c > 0) sb.Append(c).Append('c');
        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex(@"(\d+) (platinum|gold|silver|copper)")]
    private static partial Regex CoinRegex();
}
