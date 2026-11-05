using System.Globalization;
using System.Text;

namespace FoxHounds.Core;

/// <summary>Position notation {n}/{fox}/{hounds}/{side} (spec 5.6). Public entry points are on <see cref="Rules"/>.</summary>
internal static class Notation
{
    public static string Format(Geometry geo, in GameState s)
    {
        var sb = new StringBuilder(48);
        sb.Append(geo.N.ToString(CultureInfo.InvariantCulture)).Append('/').Append(geo.SquareName(s.Fox)).Append('/');
        UInt128 hounds = s.Hounds;
        bool first = true;
        while (hounds != UInt128.Zero)
        {
            int sq = (int)UInt128.TrailingZeroCount(hounds);
            if (!first)
            {
                sb.Append(',');
            }

            sb.Append(geo.SquareName(sq));
            first = false;
            hounds &= hounds - UInt128.One;
        }

        return sb.Append('/').Append(s.SideToMove.Letter()).ToString();
    }

    public static GameState Parse(Rules rules, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var geo = rules.Geo;
        string[] parts = text.Split('/');
        if (parts.Length != 4)
        {
            throw new FormatException($"'{text}': expected four parts n/fox/hounds/side.");
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n))
        {
            throw new FormatException($"'{text}': '{parts[0]}' is not a board size.");
        }

        if (n != geo.N)
        {
            throw new FormatException($"'{text}': board size {n} differs from the ruleset's {geo.N}.");
        }

        int fox = ParseSquare(geo, parts[1], text, "fox");
        UInt128 hounds = UInt128.Zero;
        int count = 0;
        foreach (string name in parts[2].Split(','))
        {
            int sq = ParseSquare(geo, name, text, "hound");
            UInt128 bit = UInt128.One << sq;
            if ((hounds & bit) != UInt128.Zero)
            {
                throw new FormatException($"'{text}': hound square '{name}' appears twice.");
            }

            hounds |= bit;
            count++;
        }

        if (count != geo.HoundCount)
        {
            throw new FormatException($"'{text}': {count} hounds instead of {geo.HoundCount}.");
        }

        if (((hounds >> fox) & UInt128.One) != UInt128.Zero)
        {
            throw new FormatException($"'{text}': the fox stands on a hound square.");
        }

        Side side = parts[3] switch
        {
            "F" => Side.Fox,
            "H" => Side.Hounds,
            _ => throw new FormatException($"'{text}': side to move must be F or H, not '{parts[3]}'."),
        };

        return rules.Create(hounds, (byte)fox, side);
    }

    private static int ParseSquare(Geometry geo, string name, string text, string what)
    {
        if (!geo.TryParseCoordinates(name, out int r, out int c))
        {
            throw new FormatException($"'{text}': {what} square '{name}' is not on the {geo.N}×{geo.N} board.");
        }

        int sq = geo.SquareAt(r, c);
        if (sq == Geometry.NoSquare)
        {
            throw new FormatException($"'{text}': {what} square '{name}' is not a playable square.");
        }

        return sq;
    }
}
