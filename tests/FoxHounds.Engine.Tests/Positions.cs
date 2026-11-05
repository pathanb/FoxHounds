using FoxHounds.Core;
using FoxHounds.Engine.Evaluation;

namespace FoxHounds.Engine.Tests;

/// <summary>Seeded test positions and the plain-minimax oracle for the search tests.</summary>
internal static class Positions
{
    /// <summary>A position reached by random play from a random fox start; may be finished.</summary>
    public static GameState RandomPlayed(Rules rules, Rng rng, int maxPlies = 60)
    {
        var s = rules.Initial(rules.FoxStartSquares[rng.NextInt(rules.FoxStartSquares.Count)]);
        int plies = rng.NextInt(maxPlies);
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        for (int p = 0; p < plies && !rules.Evaluate(s).IsOver; p++)
        {
            int n = rules.GenerateMoves(s, moves);
            s = rules.Apply(s, moves[rng.NextInt(n)]);
        }

        return s;
    }

    /// <summary>A uniformly random legal position (H hounds, fox elsewhere, either side), not necessarily reachable.</summary>
    public static GameState RandomLegal(Rules rules, Rng rng)
    {
        int p = rules.Geo.SquareCount;
        int h = rules.Geo.HoundCount;
        Span<byte> squares = stackalloc byte[p];
        for (int i = 0; i < p; i++)
        {
            squares[i] = (byte)i;
        }

        for (int i = 0; i <= h; i++)
        {
            int j = i + rng.NextInt(p - i);
            (squares[i], squares[j]) = (squares[j], squares[i]);
        }

        UInt128 hounds = UInt128.Zero;
        for (int i = 0; i < h; i++)
        {
            hounds |= UInt128.One << squares[i];
        }

        return rules.CreateChecked(hounds, squares[h], rng.NextInt(2) == 0 ? Side.Fox : Side.Hounds);
    }

    /// <summary>A random non-terminal position, either reachable-looking or uniformly random.</summary>
    public static GameState RandomOngoing(Rules rules, Rng rng)
    {
        while (true)
        {
            var s = rng.NextInt(2) == 0 ? RandomPlayed(rules, rng) : RandomLegal(rules, rng);
            if (!rules.Evaluate(s).IsOver)
            {
                return s;
            }
        }
    }

    /// <summary>Negamax without pruning, table or ordering, with the terminal scoring and leaf evaluation of spec 8.2.</summary>
    public static int PlainMinimax(Rules rules, IEvaluator evaluator, in GameState s, int depth, int ply = 0)
    {
        var outcome = rules.Evaluate(s);
        if (outcome.IsOver)
        {
            return outcome.Winner == s.SideToMove ? Scores.Win - ply : -(Scores.Win - ply);
        }

        if (depth == 0)
        {
            int fox = evaluator.EvaluateFox(s);
            return s.SideToMove == Side.Fox ? fox : -fox;
        }

        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        int count = rules.GenerateMoves(s, moves);
        int best = -Scores.Infinity;
        for (int i = 0; i < count; i++)
        {
            best = Math.Max(best, -PlainMinimax(rules, evaluator, rules.Apply(s, moves[i]), depth - 1, ply + 1));
        }

        return best;
    }
}
