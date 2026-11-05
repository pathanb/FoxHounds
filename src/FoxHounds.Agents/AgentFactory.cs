using FoxHounds.Core;
using FoxHounds.Engine;
using FoxHounds.Engine.Evaluation;
using FoxHounds.Solver;

namespace FoxHounds.Agents;

/// <summary>
/// The one place agents are built from <see cref="AgentSpec"/>s (spec 7.2, decision D-13), for the app, the CLI and
/// experiments alike. Thread-safe; every call returns a new agent instance.
/// </summary>
public sealed class AgentFactory
{
    /// <summary>Type name of <see cref="RandomAgent"/>.</summary>
    public const string Random = "random";

    /// <summary>Type name of <see cref="MinimaxAgent"/>.</summary>
    public const string Minimax = "minimax";

    /// <summary>Type name of <see cref="SolverAgent"/>.</summary>
    public const string Solver = "solver";

    private readonly SolverTableStore _tables;
    private readonly IClock _clock;

    /// <summary>Creates a factory.</summary>
    /// <param name="tables">Where solver tables come from (default: the user data folder).</param>
    /// <param name="clock">Clock for search time limits (default: the system clock).</param>
    public AgentFactory(SolverTableStore? tables = null, IClock? clock = null)
    {
        _tables = tables ?? SolverTableStore.Default;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>The solver tables this factory uses.</summary>
    public SolverTableStore Tables => _tables;

    /// <summary>Table size for a spec that does not set one: 64 MB, or 16 MB in experiments (spec 11.3).</summary>
    public int DefaultTtMegabytes { get; init; } = 64;

    /// <summary>The transposition table, in megabytes, an agent built from <paramref name="spec"/> uses (0: none).</summary>
    public int TtMegabytesOf(AgentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Type == Minimax ? spec.Search?.TtMegabytes ?? DefaultTtMegabytes : 0;
    }

    /// <summary>Builds the agent described by <paramref name="spec"/> for games under <paramref name="rules"/>.</summary>
    /// <param name="spec">The agent.</param>
    /// <param name="rules">The game's rules: weights must fit its board size, and solver agents need its table.</param>
    /// <param name="progress">Progress of a solver table build, if one is needed.</param>
    /// <param name="ct">Cancels a solver table build.</param>
    /// <exception cref="InvalidDataException">The spec's weights do not fit the board or the evaluator.</exception>
    /// <exception cref="NotSupportedException">A solver agent was asked for on a board the solver cannot handle.</exception>
    public IAgent Create(AgentSpec spec, Rules rules, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(rules);
        IAgent agent = spec.Type switch
        {
            Random => new RandomAgent(),
            Minimax => CreateMinimax(spec, rules, progress, ct),
            Solver => new SolverAgent(_tables.GetOrBuild(rules, progress, ct), spec.RandomTies),
            _ => throw new InvalidDataException($"Unknown agent type '{spec.Type}'."),
        };
        return spec.Epsilon > 0 ? new EpsilonAgent(agent, spec.Epsilon) : agent;
    }

    /// <summary>Builds an evaluator from an evaluator spec.</summary>
    public IEvaluator CreateEvaluator(EvaluatorSpec? spec, string baseDirectory, Rules rules, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        spec ??= new EvaluatorSpec();
        if (spec.Type == SolverEvaluator.TypeName)
        {
            return new SolverEvaluator(_tables.GetOrBuild(rules, progress, ct));
        }

        // Linear agents without a weight file use the built-in lin-v0 (decision D-18); the registry checks that the
        // file's evaluator type matches and that its board size fits.
        WeightFile? weights = spec.WeightsFile is { } file
            ? WeightFile.Load(Path.IsPathRooted(file) || baseDirectory.Length == 0 ? file : Path.Combine(baseDirectory, file))
            : spec.Type == WeightFile.Linear ? WeightFile.LinV0 : null;
        return EvaluatorRegistry.Create(spec.Type, weights, rules.Geo.N);
    }

    private MinimaxAgent CreateMinimax(AgentSpec spec, Rules rules, IProgress<double>? progress, CancellationToken ct)
    {
        var search = spec.Search ?? new SearchSpec();
        var options = new SearchOptions
        {
            TtMegabytes = search.TtMegabytes ?? DefaultTtMegabytes,
            RootSelection = JsonNames.Parse<RootSelection>(search.RootSelection, "rootSelection"),
            Margin = search.Margin,
            KeepTtBetweenGames = search.KeepTtBetweenGames,
        };
        var evaluator = CreateEvaluator(spec.Evaluator, spec.BaseDirectory, rules, progress, ct);
        return new MinimaxAgent(evaluator, options, _clock, spec.Name);
    }
}
