using FoxHounds.Core;
using FoxHounds.Engine.Evaluation;

namespace FoxHounds.Engine;

/// <summary>Which root move is played (spec 8.7).</summary>
public enum RootSelection
{
    /// <summary>The first move with the best score after ordering. Fully deterministic.</summary>
    Best,

    /// <summary>Every root move gets a full window; a uniformly random move within the margin of the best is played,
    /// never a proven loss when a non-losing move exists.</summary>
    RandomAmongBest,
}

/// <summary>Search configuration (spec 8.4–8.7).</summary>
public sealed record SearchOptions
{
    /// <summary>Transposition table size; 0 disables it.</summary>
    public int TtMegabytes { get; init; } = 64;

    /// <summary>Root move selection.</summary>
    public RootSelection RootSelection { get; init; } = RootSelection.Best;

    /// <summary>Score margin for <see cref="RootSelection.RandomAmongBest"/>.</summary>
    public int Margin { get; init; }

    /// <summary>Keep the transposition table between games (default false, for reproducibility).</summary>
    public bool KeepTtBetweenGames { get; init; }

    /// <summary>Killer-move ordering (spec 8.5); off only for experiments.</summary>
    public bool UseKillers { get; init; } = true;

    /// <summary>History-heuristic ordering (spec 8.5); off only for experiments.</summary>
    public bool UseHistory { get; init; } = true;

    /// <summary>Budget for rating each fox start square under ChooseOnBackRow (decision D-38).</summary>
    public SearchBudget FoxStartBudget { get; init; } = SearchBudget.Depth(8);
}

/// <summary>Outcome of one <see cref="Search.Run"/>.</summary>
/// <param name="Move">The move to play.</param>
/// <param name="Score">Root score for the side to move (negamax convention).</param>
/// <param name="Depth">Last completed iteration in plies; 0 when none completed.</param>
/// <param name="Nodes">Nodes searched.</param>
/// <param name="Elapsed">Time spent, from the search's clock.</param>
/// <param name="Exact">
/// True when the score is final: a proven result that fits within the completed depth (decision D-39), or a search
/// that reached every game end. A proof carried over in the table from an earlier, deeper search can make the score
/// proven but not exact: the winner is right and the distance is an upper bound.
/// </param>
public readonly record struct SearchResult(Move Move, int Score, int Depth, long Nodes, TimeSpan Elapsed, bool Exact)
{
    /// <summary>True when no iteration completed and <see cref="Move"/> is the first ordered legal ply (spec 8.6).</summary>
    public bool NoIterationCompleted => Depth == 0;
}

/// <summary>
/// Negamax alpha-beta with iterative deepening, a transposition table, TT move, killers and history ordering
/// (spec 8). Single-threaded: one instance per worker thread. After construction a search allocates nothing.
/// </summary>
public sealed class Search
{
    /// <summary>Budget and cancellation are checked every this many nodes (spec 8.2).</summary>
    public const int CheckInterval = 1024;

    private const int HistoryCap = 1 << 29;
    private const int TtMoveKey = int.MaxValue;
    private const int Killer1Key = int.MaxValue - 1;
    private const int Killer2Key = int.MaxValue - 2;

    private readonly IEvaluator _evaluator;
    private readonly SearchOptions _options;
    private readonly IClock _clock;
    private readonly int _squares;
    private readonly int _width;            // PV row width = longest line + 1
    private readonly Move[] _killers;       // 2 per ply
    private readonly int[] _history;        // [side][from][to]
    private readonly Move[] _pv;            // triangular PV table, _width rows of _width
    private readonly int[] _pvLength;
    private readonly Move[] _rootPv;
    private readonly Move[] _rootMoves = new Move[Rules.MaxMoves];
    private readonly int[] _rootScores = new int[Rules.MaxMoves];
    private readonly Move[] _completedMoves = new Move[Rules.MaxMoves];   // root moves and scores of the last
    private readonly int[] _completedScores = new int[Rules.MaxMoves];    // completed iteration (RandomAmongBest)
    private readonly Move[] _rootLines;     // RandomAmongBest: each root move's line, _width per move
    private readonly int[] _rootLineLengths = new int[Rules.MaxMoves];
    private readonly Move[] _completedLines;
    private readonly int[] _completedLineLengths = new int[Rules.MaxMoves];
    private int _rootPvLength;
    private long _nodes;
    private int _depthInProgress;
    private bool _aborted;
    private long _maxNodes;
    private TimeSpan _hardDeadline;
    private bool _hasHardDeadline;
    private CancellationToken _ct;

    /// <summary>Creates a search; allocates the transposition table and all working buffers.</summary>
    public Search(Rules rules, IEvaluator evaluator, SearchOptions options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        Rules = rules;
        _evaluator = evaluator;
        _options = options;
        _clock = clock;
        Table = new TranspositionTable(options.TtMegabytes);
        _squares = rules.Geo.SquareCount;
        _width = rules.MaxPlies + 2;
        _killers = new Move[_width * 2];
        _history = new int[2 * _squares * _squares];
        _pv = new Move[_width * _width];
        _pvLength = new int[_width + 1];
        _rootPv = new Move[_width];
        _rootLines = new Move[Rules.MaxMoves * _width];
        _completedLines = new Move[Rules.MaxMoves * _width];
        Array.Fill(_killers, Move.None);
    }

    /// <summary>The rules this search plays by.</summary>
    public Rules Rules { get; }

    /// <summary>The transposition table.</summary>
    public TranspositionTable Table { get; }

    /// <summary>The evaluator at the leaves.</summary>
    public IEvaluator Evaluator => _evaluator;

    /// <summary>Nodes searched so far in the current or last search; safe to read from another thread.</summary>
    public long Nodes => Volatile.Read(ref _nodes);

    /// <summary>Iteration in progress; safe to read from another thread.</summary>
    public int DepthInProgress => Volatile.Read(ref _depthInProgress);

    /// <summary>Principal variation of the last completed iteration of the last search.</summary>
    public ReadOnlySpan<Move> PrincipalVariation => _rootPv.AsSpan(0, _rootPvLength);

    /// <summary>Resets per-game state: the transposition table (unless kept), killers and history.</summary>
    public void NewGame()
    {
        if (!_options.KeepTtBetweenGames)
        {
            Table.Clear();
        }

        Array.Fill(_killers, Move.None);
        Array.Clear(_history);
    }

    /// <summary>
    /// Searches a non-terminal position by iterative deepening (spec 8.6) until the budget runs out, the result is
    /// proven within the searched depth, or the search is exhaustive. Keeps the result of the last completed depth.
    /// </summary>
    /// <param name="root">The position; must not be finished.</param>
    /// <param name="budget">Depth, node and time limits. With no depth limit the search may go to the end of the game.</param>
    /// <param name="ct">Aborts the search; the last completed depth is returned.</param>
    /// <param name="rng">Required for <see cref="RootSelection.RandomAmongBest"/>.</param>
    /// <exception cref="ArgumentException">The position is finished.</exception>
    public SearchResult Run(in GameState root, SearchBudget budget, CancellationToken ct, Rng? rng = null)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (Rules.Evaluate(root).IsOver)
        {
            throw new ArgumentException($"Cannot search a finished position: {root}", nameof(root));
        }

        bool randomRoot = _options.RootSelection == RootSelection.RandomAmongBest;
        if (randomRoot && rng is null)
        {
            throw new ArgumentNullException(nameof(rng), "RandomAmongBest needs a random generator.");
        }

        TimeSpan start = _clock.Elapsed;
        _nodes = 0;
        _aborted = false;
        _ct = ct;
        _maxNodes = budget.MaxNodes ?? 0;
        _hasHardDeadline = budget.HardTime is not null;
        _hardDeadline = start + (budget.HardTime ?? TimeSpan.Zero);
        Table.NewSearch();
        for (int i = 0; i < _history.Length; i++)
        {
            _history[i] >>= 1;
        }

        int pliesLeft = Rules.MaxPliesFrom(root);
        int maxDepth = Math.Max(1, budget.MaxDepth ?? pliesLeft);
        int rootCount = Rules.GenerateMoves(root, _rootMoves);

        int completedDepth = 0;
        int bestScore = 0;
        Move bestMove = Move.None;
        bool exact = false;
        int bestIndex = 0;
        for (int depth = 1; depth <= maxDepth; depth++)
        {
            if (depth > 1)
            {
                // Between iterations: honour the soft time limit (spec 8.6), and do not start an iteration that the
                // node budget or a cancellation would abort anyway.
                if ((budget.SoftTime is { } soft && _clock.Elapsed - start >= soft) || _ct.IsCancellationRequested
                    || (_maxNodes > 0 && _nodes >= _maxNodes))
                {
                    break;
                }
            }

            Volatile.Write(ref _depthInProgress, depth);
            int score = SearchRoot(root, rootCount, depth, randomRoot, out int index);
            if (_aborted)
            {
                break;
            }

            completedDepth = depth;
            bestScore = score;
            bestIndex = index;
            bestMove = _rootMoves[index];
            _rootMoves.AsSpan(0, rootCount).CopyTo(_completedMoves);
            _rootScores.AsSpan(0, rootCount).CopyTo(_completedScores);
            _rootPvLength = Math.Max(0, _pvLength[0]);
            _pv.AsSpan(0, _rootPvLength).CopyTo(_rootPv);
            if (randomRoot)
            {
                for (int i = 0; i < rootCount; i++)
                {
                    int length = _rootLineLengths[i];
                    _rootLines.AsSpan(i * _width, length).CopyTo(_completedLines.AsSpan(i * _width));
                    _completedLineLengths[i] = length;
                }
            }

            if ((Scores.IsProven(score) && Scores.PliesToEnd(score) <= depth) || depth >= pliesLeft)
            {
                exact = true;
                break;
            }
        }

        if (completedDepth == 0)
        {
            // Spec 8.6: no iteration completed; play the first legal ply in ordered move order.
            Span<Move> ordered = stackalloc Move[Rules.MaxMoves];
            _rootMoves.AsSpan(0, rootCount).CopyTo(ordered);
            Move ttMove = Table.Probe(root.Hash, out var entry) ? entry.Move : Move.None;
            Order(ordered[..rootCount], ttMove, 0, root.SideToMove);
            bestMove = ordered[0];
            _rootPv[0] = bestMove;
            _rootPvLength = 1;
        }
        else if (randomRoot)
        {
            // Report the chosen move's own value and line, not the best move's (AgentInfo: the line starts with
            // the chosen ply). Every root move was searched with a full window, so its score is its value.
            int chosen = PickRandomAmongBest(rootCount, bestScore, rng!, bestIndex);
            if (chosen != bestIndex)
            {
                bestScore = _completedScores[chosen];
                _rootPvLength = _completedLineLengths[chosen];
                _completedLines.AsSpan(chosen * _width, _rootPvLength).CopyTo(_rootPv);
                exact = completedDepth >= pliesLeft
                    || (Scores.IsProven(bestScore) && Scores.PliesToEnd(bestScore) <= completedDepth);
            }

            bestMove = _completedMoves[chosen];
        }

        Volatile.Write(ref _depthInProgress, 0);
        return new SearchResult(bestMove, bestScore, completedDepth, _nodes, _clock.Elapsed - start, exact);
    }

    private int SearchRoot(in GameState root, int count, int depth, bool fullWindowForAll, out int bestIndex)
    {
        _pvLength[0] = 0;
        Move ttMove = Table.Probe(root.Hash, out var entry) ? entry.Move : Move.None;
        OrderRoot(count, ttMove, root.SideToMove);

        int alpha = -Scores.Infinity;
        int best = -Scores.Infinity;
        bestIndex = 0;
        for (int i = 0; i < count; i++)
        {
            var child = Rules.Apply(root, _rootMoves[i]);
            int v = fullWindowForAll
                ? -Negamax(child, depth - 1, -Scores.Infinity, Scores.Infinity, 1)
                : -Negamax(child, depth - 1, -Scores.Infinity, -alpha, 1);
            if (_aborted)
            {
                return 0;
            }

            _rootScores[i] = v;
            if (fullWindowForAll)
            {
                // The child's line is in PV row 1 (empty after a table cutoff, as for the best move's line).
                int at = i * _width;
                int end = Math.Max(_pvLength[1], 1);
                _rootLines[at] = _rootMoves[i];
                _pv.AsSpan(_width + 1, end - 1).CopyTo(_rootLines.AsSpan(at + 1));
                _rootLineLengths[i] = end;
            }

            if (v > best)
            {
                best = v;
                bestIndex = i;
                UpdatePv(0, _rootMoves[i]);
            }

            if (v > alpha)
            {
                alpha = v;
            }
        }

        Table.Store(root.Hash, depth, ToTable(best, 0), Bound.Exact, _rootMoves[bestIndex]);
        return best;
    }

    private int Negamax(in GameState s, int depth, int alpha, int beta, int ply)
    {
        if ((++_nodes & (CheckInterval - 1)) == 0)
        {
            CheckBudget();
        }

        if (_aborted)
        {
            return 0;
        }

        _pvLength[ply] = ply;
        var outcome = Rules.Evaluate(s);
        if (outcome.IsOver)
        {
            // Terminal before depth: a finished game is never scored by the evaluator (spec 8.2).
            return outcome.Winner == s.SideToMove ? Scores.Win - ply : -(Scores.Win - ply);
        }

        if (depth == 0)
        {
            int fox = _evaluator.EvaluateFox(s);
            return s.SideToMove == Side.Fox ? fox : -fox;
        }

        Move ttMove = Move.None;
        if (Table.Probe(s.Hash, out var entry))
        {
            ttMove = entry.Move;
            if (entry.Depth >= depth)
            {
                int v = FromTable(entry.Score, ply);
                if (entry.Bound == Bound.Exact
                    || (entry.Bound == Bound.Lower && v >= beta)
                    || (entry.Bound == Bound.Upper && v <= alpha))
                {
                    return v;
                }
            }
        }

        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        int count = Rules.GenerateMoves(s, moves);
        Order(moves[..count], ttMove, ply, s.SideToMove);

        int best = -Scores.Infinity;
        Move bestMove = Move.None;
        int alpha0 = alpha;
        for (int i = 0; i < count; i++)
        {
            var child = Rules.Apply(s, moves[i]);
            int v = -Negamax(child, depth - 1, -beta, -alpha, ply + 1);
            if (_aborted)
            {
                return 0;
            }

            if (v > best)
            {
                best = v;
                bestMove = moves[i];
            }

            if (v > alpha)
            {
                alpha = v;
                UpdatePv(ply, moves[i]);
            }

            if (alpha >= beta)
            {
                RecordCutoff(moves[i], ply, depth, s.SideToMove);
                break;
            }
        }

        Bound bound = best <= alpha0 ? Bound.Upper : best >= beta ? Bound.Lower : Bound.Exact;
        Table.Store(s.Hash, depth, ToTable(best, ply), bound, bestMove);
        return best;
    }

    private void CheckBudget()
    {
        if ((_maxNodes > 0 && _nodes >= _maxNodes)
            || _ct.IsCancellationRequested
            || (_hasHardDeadline && _clock.Elapsed >= _hardDeadline))
        {
            _aborted = true;
        }
    }

    // Spec 8.3: proven scores are stored relative to the node.
    private static int ToTable(int score, int ply) =>
        score >= Scores.Win - Scores.MaxPliesAnyBoard ? score + ply
        : score <= -(Scores.Win - Scores.MaxPliesAnyBoard) ? score - ply
        : score;

    private static int FromTable(int score, int ply) =>
        score >= Scores.Win - Scores.MaxPliesAnyBoard ? score - ply
        : score <= -(Scores.Win - Scores.MaxPliesAnyBoard) ? score + ply
        : score;

    private void UpdatePv(int ply, Move move)
    {
        int row = ply * _width;
        int childRow = (ply + 1) * _width;
        _pv[row + ply] = move;
        int end = Math.Max(_pvLength[ply + 1], ply + 1);
        for (int j = ply + 1; j < end; j++)
        {
            _pv[row + j] = _pv[childRow + j];
        }

        _pvLength[ply] = end;
    }

    private void RecordCutoff(Move move, int ply, int depth, Side side)
    {
        if (_options.UseKillers && _killers[ply * 2] != move)
        {
            _killers[(ply * 2) + 1] = _killers[ply * 2];
            _killers[ply * 2] = move;
        }

        if (_options.UseHistory)
        {
            ref int h = ref _history[HistoryIndex(side, move)];
            h = Math.Min(h + (depth * depth), HistoryCap);
        }
    }

    private int HistoryIndex(Side side, Move move) => ((((int)side * _squares) + move.From) * _squares) + move.To;

    /// <summary>Spec 8.5: TT move, two killers, history, then the normative generation order (stable sort).</summary>
    private void Order(Span<Move> moves, Move ttMove, int ply, Side side)
    {
        Span<int> keys = stackalloc int[Rules.MaxMoves];
        Move killer1 = _options.UseKillers ? _killers[ply * 2] : Move.None;
        Move killer2 = _options.UseKillers ? _killers[(ply * 2) + 1] : Move.None;
        for (int i = 0; i < moves.Length; i++)
        {
            Move m = moves[i];
            keys[i] = m == ttMove ? TtMoveKey
                : m == killer1 ? Killer1Key
                : m == killer2 ? Killer2Key
                : _options.UseHistory ? _history[HistoryIndex(side, m)] : 0;
        }

        for (int i = 1; i < moves.Length; i++)
        {
            Move m = moves[i];
            int key = keys[i];
            int j = i - 1;
            while (j >= 0 && keys[j] < key)
            {
                moves[j + 1] = moves[j];
                keys[j + 1] = keys[j];
                j--;
            }

            moves[j + 1] = m;
            keys[j + 1] = key;
        }
    }

    private void OrderRoot(int count, Move ttMove, Side side) => Order(_rootMoves.AsSpan(0, count), ttMove, 0, side);

    /// <summary>Index into the completed iteration's root moves of a random move within the margin of the best.</summary>
    private int PickRandomAmongBest(int count, int best, Rng rng, int bestIndex)
    {
        bool anyNonLosing = false;
        for (int i = 0; i < count; i++)
        {
            anyNonLosing |= !IsProvenLoss(_completedScores[i]);
        }

        Span<int> candidates = stackalloc int[Rules.MaxMoves];
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            int v = _completedScores[i];
            if (v >= best - _options.Margin && !(anyNonLosing && IsProvenLoss(v)))
            {
                candidates[n++] = i;
            }
        }

        return n == 0 ? bestIndex : candidates[rng.NextInt(n)];
    }

    private static bool IsProvenLoss(int score) => Scores.IsProven(score) && score < 0;
}
