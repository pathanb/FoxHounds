namespace FoxHounds.Core;

/// <summary>
/// Perft: the number of ply sequences of exactly d plies from a position; a game that ends earlier contributes
/// 0 (spec 15.2). The standard check that move generation and end-of-game rules are right.
/// </summary>
public static class Perft
{
    /// <summary>Counts sequences of exactly <paramref name="depth"/> plies from <paramref name="s"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is negative.</exception>
    public static long Count(Rules rules, in GameState s, int depth)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        return CountCore(rules, s, depth);
    }

    /// <summary>Perft per root ply ("divide"), in normative move order.</summary>
    public static IReadOnlyList<(Move Move, long Count)> Divide(Rules rules, in GameState s, int depth)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        var result = new List<(Move, long)>();
        if (rules.Evaluate(s).IsOver)
        {
            return result;
        }

        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        int n = rules.GenerateMoves(s, moves);
        for (int i = 0; i < n; i++)
        {
            result.Add((moves[i], CountCore(rules, rules.Apply(s, moves[i]), depth - 1)));
        }

        return result;
    }

    private static long CountCore(Rules rules, in GameState s, int depth)
    {
        if (depth == 0)
        {
            return 1;
        }

        if (rules.Evaluate(s).IsOver)
        {
            return 0;
        }

        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        int n = rules.GenerateMoves(s, moves);
        if (depth == 1)
        {
            return n;
        }

        long total = 0;
        for (int i = 0; i < n; i++)
        {
            total += CountCore(rules, rules.Apply(s, moves[i]), depth - 1);
        }

        return total;
    }
}
