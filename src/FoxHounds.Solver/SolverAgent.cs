using FoxHounds.Core;

namespace FoxHounds.Solver;

/// <summary>
/// Perfect play from a solver table (spec 10.5): when winning, the shortest win; when losing, the longest loss.
/// Ties go to the first ply in normative order, or to a uniformly random tied ply when <c>randomTies</c> is set.
/// </summary>
public sealed class SolverAgent : IAgent
{
    private readonly SolverTable _table;
    private readonly bool _randomTies;
    private Rules? _rules;
    private Rng? _rng;

    /// <summary>Creates an agent that plays from <paramref name="table"/>.</summary>
    public SolverAgent(SolverTable table, bool randomTies = false)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
        _randomTies = randomTies;
    }

    /// <inheritdoc />
    public string Name => _randomTies ? "solver[random-ties]" : "solver";

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The table does not cover the game's board size and win condition.</exception>
    public void NewGame(Rules rules, Side side, Rng rng)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rng);
        if (!_table.Covers(rules.Config))
        {
            throw new ArgumentException(
                $"The solver table is for {_table.BoardSize}×{_table.BoardSize} {_table.WinCondition}; the game is {rules.Config.BoardSize}×{rules.Config.BoardSize} {rules.Config.FoxWinCondition}.",
                nameof(rules));
        }

        _rules = rules;
        _rng = rng;
    }

    /// <inheritdoc />
    public byte ChooseFoxStart(Rules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var starts = rules.FoxStartSquares;
        Span<RatedMove> rated = stackalloc RatedMove[starts.Count];
        for (int i = 0; i < starts.Count; i++)
        {
            var start = rules.Initial(starts[i]);
            var v = _table.Get(start);
            bool foxWins = v.Winner(start.SideToMove) == Side.Fox;

            // Rate the choice like a ply of the fox's: the value from the fox's perspective.
            rated[i] = new RatedMove(new Move(starts[i], starts[i]), new SolvedValue(foxWins, v.Plies));
        }

        return starts[Pick(rated)];
    }

    /// <inheritdoc />
    public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct)
    {
        var rules = _rules ?? throw new InvalidOperationException("Call NewGame first.");
        Span<RatedMove> rated = stackalloc RatedMove[Rules.MaxMoves];
        int count = _table.RateMoves(state, rated);
        if (count == 0)
        {
            throw new InvalidOperationException($"No legal ply in {state}.");
        }

        int best = Pick(rated[..count]);
        var value = rated[best].ValueForMover;
        int scoreFox = Scores.ToFox(value.Score, state.SideToMove);
        var pv = PrincipalVariation(rules, state, rated[best].Move);
        var info = new AgentInfo(scoreFox, value.Plies, 0, TimeSpan.Zero, pv, AgentInfo.SourceSolver);
        return new AgentDecision(rated[best].Move, info);
    }

    /// <summary>Index of the best entry: shortest win, else longest loss; ties first or random.</summary>
    private int Pick(ReadOnlySpan<RatedMove> rated)
    {
        int best = 0;
        int ties = 1;
        for (int i = 1; i < rated.Length; i++)
        {
            int cmp = Compare(rated[i].ValueForMover, rated[best].ValueForMover);
            if (cmp > 0)
            {
                best = i;
                ties = 1;
            }
            else if (cmp == 0)
            {
                ties++;
            }
        }

        if (!_randomTies || ties == 1)
        {
            return best;
        }

        int chosen = (_rng ?? throw new InvalidOperationException("Call NewGame first.")).NextInt(ties);
        var target = rated[best].ValueForMover;
        for (int i = 0; i < rated.Length; i++)
        {
            if (Compare(rated[i].ValueForMover, target) == 0 && chosen-- == 0)
            {
                return i;
            }
        }

        return best;
    }

    /// <summary>Positive when <paramref name="a"/> is better for the mover than <paramref name="b"/>.</summary>
    private static int Compare(SolvedValue a, SolvedValue b)
    {
        if (a.SideToMoveWins != b.SideToMoveWins)
        {
            return a.SideToMoveWins ? 1 : -1;
        }

        return a.SideToMoveWins ? b.Plies.CompareTo(a.Plies) : a.Plies.CompareTo(b.Plies);
    }

    /// <summary>The expected line: the chosen ply, then deterministic perfect play until the game ends under the
    /// game's own rules (which may adjudicate escapes earlier than the table's).</summary>
    private Move[] PrincipalVariation(Rules rules, GameState state, Move first)
    {
        var line = new List<Move>(64) { first };
        var s = rules.Apply(state, first);
        Span<RatedMove> rated = stackalloc RatedMove[Rules.MaxMoves];
        while (!rules.Evaluate(s).IsOver && line.Count < rules.MaxPlies)
        {
            int count = _table.RateMoves(s, rated);
            int best = 0;
            for (int i = 1; i < count; i++)
            {
                if (Compare(rated[i].ValueForMover, rated[best].ValueForMover) > 0)
                {
                    best = i;
                }
            }

            line.Add(rated[best].Move);
            s = rules.Apply(s, rated[best].Move);
        }

        return [.. line];
    }
}
