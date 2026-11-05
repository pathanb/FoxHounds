using FoxHounds.Core;
using FoxHounds.Engine.Evaluation;

namespace FoxHounds.Engine;

/// <summary>The product AI: the alpha-beta <see cref="Search"/> with a pluggable evaluator (spec 7.2, 8).</summary>
public sealed class MinimaxAgent : IAgent
{
    private readonly IEvaluator _evaluator;
    private readonly SearchOptions _options;
    private readonly IClock _clock;
    private readonly string _name;
    private Search? _search;
    private Rng? _rng;

    /// <summary>Creates the agent. The transposition table is allocated at the first <see cref="NewGame"/>.</summary>
    public MinimaxAgent(IEvaluator evaluator, SearchOptions? options = null, IClock? clock = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        _evaluator = evaluator;
        _options = options ?? new SearchOptions();
        _clock = clock ?? SystemClock.Instance;
        _name = name ?? $"minimax[eval={evaluator.Name}]";
    }

    /// <inheritdoc />
    public string Name => _name;

    /// <summary>The search of the current game, for progress counters in the UI; null before the first game.</summary>
    public Search? CurrentSearch => Volatile.Read(ref _search);

    /// <inheritdoc />
    public void NewGame(Rules rules, Side side, Rng rng)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rng);
        _rng = rng;
        if (_search is null || _search.Rules.Config != rules.Config)
        {
            Volatile.Write(ref _search, new Search(rules, _evaluator, _options, _clock));
        }
        else
        {
            _search.NewGame();
        }
    }

    /// <inheritdoc />
    /// <remarks>Searches each start position with <see cref="SearchOptions.FoxStartBudget"/> and picks the best score
    /// for the fox; ties go to the first square (decision D-38).</remarks>
    public byte ChooseFoxStart(Rules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var search = _search ?? throw new InvalidOperationException("Call NewGame first.");
        byte best = rules.FoxStartSquares[0];
        int bestScore = int.MinValue;
        foreach (byte square in rules.FoxStartSquares)
        {
            var start = rules.Initial(square);
            int scoreFox = rules.Evaluate(start).IsOver
                ? (rules.Evaluate(start).Winner == Side.Fox ? Scores.Win : -Scores.Win)
                : Scores.ToFox(search.Run(start, _options.FoxStartBudget, CancellationToken.None, _rng).Score, start.SideToMove);
            if (scoreFox > bestScore)
            {
                bestScore = scoreFox;
                best = square;
            }
        }

        return best;
    }

    /// <inheritdoc />
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled during the search.</exception>
    public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct)
    {
        var search = _search ?? throw new InvalidOperationException("Call NewGame first.");
        var result = search.Run(state, budget, ct, _rng);
        ct.ThrowIfCancellationRequested();
        var info = new AgentInfo(
            Scores.ToFox(result.Score, state.SideToMove),
            result.Depth,
            result.Nodes,
            result.Elapsed,
            search.PrincipalVariation.ToArray(),
            AgentInfo.SourceSearch,
            result.Exact);
        return new AgentDecision(result.Move, info);
    }
}

/// <summary>Wraps another agent; with probability ε plays a uniformly random legal ply instead (spec 7.2).</summary>
public sealed class EpsilonAgent : IAgent
{
    private readonly IAgent _inner;
    private Rules? _rules;
    private Rng? _rng;

    /// <summary>Creates the wrapper.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="epsilon"/> is outside [0, 1].</exception>
    public EpsilonAgent(IAgent inner, double epsilon)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (epsilon is < 0 or > 1 || double.IsNaN(epsilon))
        {
            throw new ArgumentOutOfRangeException(nameof(epsilon), epsilon, "Epsilon must be in [0, 1].");
        }

        _inner = inner;
        Epsilon = epsilon;
    }

    /// <summary>Probability of a random ply.</summary>
    public double Epsilon { get; }

    /// <summary>The wrapped agent.</summary>
    public IAgent Inner => _inner;

    /// <inheritdoc />
    public string Name => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_inner.Name}+eps={Epsilon:0.###}");

    /// <inheritdoc />
    public void NewGame(Rules rules, Side side, Rng rng)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rng);
        _rules = rules;
        _rng = rng;
        _inner.NewGame(rules, side, rng.Split());
    }

    /// <inheritdoc />
    public byte ChooseFoxStart(Rules rules) => _inner.ChooseFoxStart(rules);

    /// <inheritdoc />
    public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct)
    {
        var rules = _rules ?? throw new InvalidOperationException("Call NewGame first.");
        if (_rng!.NextDouble() < Epsilon)
        {
            Span<Move> moves = stackalloc Move[Rules.MaxMoves];
            int count = rules.GenerateMoves(state, moves);
            return new AgentDecision(moves[_rng.NextInt(count)], AgentInfo.Random);
        }

        return _inner.ChooseMove(state, history, budget, ct);
    }
}
