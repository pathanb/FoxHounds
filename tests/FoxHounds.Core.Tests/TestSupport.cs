namespace FoxHounds.Core.Tests;

/// <summary>Seeded generators of test data. All randomness goes through <see cref="Rng"/> (spec 4.3).</summary>
internal static class TestPositions
{
    public static readonly int[] AllSizes = [4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];

    /// <summary>A uniformly random legal position: H distinct hound squares, the fox elsewhere, either side to move.
    /// Not necessarily reachable from the initial position.</summary>
    public static GameState Random(Rules rules, Rng rng)
    {
        int p = rules.Geo.SquareCount;
        int h = rules.Geo.HoundCount;
        Span<byte> squares = stackalloc byte[p];
        for (int i = 0; i < p; i++)
        {
            squares[i] = (byte)i;
        }

        // Partial Fisher–Yates: the first h + 1 entries become a uniform random selection.
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

        Side side = rng.NextInt(2) == 0 ? Side.Fox : Side.Hounds;
        return rules.CreateChecked(hounds, squares[h], side);
    }

    /// <summary>Plays uniformly random plies until the game ends; calls <paramref name="onPosition"/> for every
    /// position including the start and the final one. Returns the number of plies.</summary>
    public static int RandomGame(Rules rules, GameState start, Rng rng, Action<GameState>? onPosition = null)
    {
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        GameState s = start;
        int plies = 0;
        onPosition?.Invoke(s);
        while (!rules.Evaluate(s).IsOver)
        {
            int n = rules.GenerateMoves(s, moves);
            s = rules.Apply(s, moves[rng.NextInt(n)]);
            plies++;
            onPosition?.Invoke(s);
        }

        return plies;
    }

    public static Rules Classic(int n, bool adjudicate = false) =>
        new(RuleSet.Classic with { BoardSize = n, AdjudicateEscape = adjudicate });

    public static RuleSet Variant(int n, FoxWinCondition win, bool adjudicate, Side first = Side.Fox) => new()
    {
        BoardSize = n,
        FoxWinCondition = win,
        AdjudicateEscape = adjudicate,
        FirstMover = first,
    };

    public static IEnumerable<RuleSet> AllVariants(int n)
    {
        foreach (var win in new[] { FoxWinCondition.ReachHomeRow, FoxWinCondition.HoundsImmobile })
        {
            foreach (bool adjudicate in new[] { false, true })
            {
                yield return Variant(n, win, adjudicate);
            }
        }
    }
}

/// <summary>
/// An independent, deliberately naive implementation of the rules on (row, column) coordinates. It shares no code
/// with <see cref="Rules"/> or <see cref="Geometry"/>; only the square numbering formula of spec 5.1 is common.
/// </summary>
internal static class NaiveRules
{
    public static int Index(int n, int r, int c) => ((r * n) + c) >> 1;

    public static bool Playable(int n, int r, int c) => r >= 0 && r < n && c >= 0 && c < n && ((r + c) % 2) == 1;

    public static (int R, int C) Coordinates(int n, int index)
    {
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                if (Playable(n, r, c) && Index(n, r, c) == index)
                {
                    return (r, c);
                }
            }
        }

        throw new ArgumentOutOfRangeException(nameof(index));
    }

    public static HashSet<(int R, int C)> HoundCoordinates(int n, UInt128 hounds)
    {
        var set = new HashSet<(int, int)>();
        for (int i = 0; i < 128; i++)
        {
            if (((hounds >> i) & UInt128.One) != UInt128.Zero)
            {
                set.Add(Coordinates(n, i));
            }
        }

        return set;
    }

    /// <summary>Legal plies in normative order (spec 6.1), as (fromIndex, toIndex).</summary>
    public static List<(int From, int To)> Moves(int n, GameState s)
    {
        var fox = Coordinates(n, s.Fox);
        var hounds = HoundCoordinates(n, s.Hounds);
        bool Empty(int r, int c) => Playable(n, r, c) && !hounds.Contains((r, c)) && (r, c) != fox;
        var result = new List<(int, int)>();
        if (s.SideToMove == Side.Fox)
        {
            foreach (var (dr, dc) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
            {
                if (Empty(fox.R + dr, fox.C + dc))
                {
                    result.Add((s.Fox, Index(n, fox.R + dr, fox.C + dc)));
                }
            }

            return result;
        }

        // Hounds in ascending square index = row-major order; forward-left then forward-right.
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                if (!hounds.Contains((r, c)))
                {
                    continue;
                }

                foreach (int dc in new[] { -1, 1 })
                {
                    if (Empty(r + 1, c + dc))
                    {
                        result.Add((Index(n, r, c), Index(n, r + 1, c + dc)));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>End-of-game rules of spec 3.4 and 3.5, applied in order.</summary>
    public static Outcome Evaluate(RuleSet rules, GameState s)
    {
        int n = rules.BoardSize;
        var fox = Coordinates(n, s.Fox);
        var hounds = HoundCoordinates(n, s.Hounds);
        bool reachHome = rules.FoxWinCondition == FoxWinCondition.ReachHomeRow;
        if (reachHome && fox.R == 0)
        {
            return new Outcome(GameResult.FoxWins, EndReason.FoxReachedHome);
        }

        if (Moves(n, s).Count == 0)
        {
            return s.SideToMove == Side.Fox
                ? new Outcome(GameResult.HoundsWin, EndReason.FoxTrapped)
                : new Outcome(GameResult.FoxWins, EndReason.HoundsImmobile);
        }

        int minHoundRow = hounds.Min(h => h.R);
        bool escaped = reachHome ? fox.R <= minHoundRow : fox.R < minHoundRow && minHoundRow >= 2;
        if (rules.AdjudicateEscape && escaped)
        {
            return new Outcome(GameResult.FoxWins, EndReason.FoxEscaped);
        }

        return Outcome.Ongoing;
    }
}

/// <summary>A minimal random agent for Core tests (Core.Tests may reference only Core).</summary>
internal sealed class UniformAgent : IAgent
{
    private Rules? _rules;
    private Rng? _rng;

    public List<ulong> Draws { get; } = [];

    public string Name => "uniform-test";

    public void NewGame(Rules rules, Side side, Rng rng)
    {
        _rules = rules;
        _rng = rng;
    }

    public byte ChooseFoxStart(Rules rules) => rules.FoxStartSquares[_rng!.NextInt(rules.FoxStartSquares.Count)];

    public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct)
    {
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        int n = _rules!.GenerateMoves(state, moves);
        ulong draw = _rng!.NextU64();
        Draws.Add(draw);
        return new AgentDecision(moves[(int)(draw % (ulong)n)], AgentInfo.Random);
    }
}
