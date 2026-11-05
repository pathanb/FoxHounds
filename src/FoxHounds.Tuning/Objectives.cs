using FoxHounds.Core;
using FoxHounds.Engine;
using FoxHounds.Engine.Evaluation;
using FoxHounds.Sim;
using FoxHounds.Solver;

namespace FoxHounds.Tuning;

/// <summary>Builds the evaluator for a weight vector: 11 linear weights, or 11 early then 11 late.</summary>
public static class TunedEvaluator
{
    /// <summary>The evaluator for <paramref name="weights"/>.</summary>
    public static IEvaluator Create(string name, int[] weights, bool tapered)
    {
        ArgumentNullException.ThrowIfNull(weights);
        int n = FeatureExtractor.Count;
        return tapered
            ? new TaperedEvaluator(name, weights.AsSpan(0, n), weights.AsSpan(n, n))
            : new LinearEvaluator(name, weights);
    }
}

/// <summary>
/// GradeAccuracy (spec 12.3, 11.5): the share of won positions where a fixed-budget search keeps the win, with equal
/// weight per side. The batch of an iteration is a draw without replacement from the won train positions.
/// </summary>
public sealed class GradeAccuracyObjective : ISpsaObjective
{
    private readonly Rules _rules;
    private readonly SolverTable _table;
    private readonly LabelledPosition[] _train;
    private readonly LabelledPosition[] _validation;
    private readonly SearchBudget _budget;
    private readonly int _batchSize;
    private readonly int _workers;
    private readonly bool _tapered;

    /// <summary>Creates the objective. Only positions won for the side to move are kept.</summary>
    public GradeAccuracyObjective(Rules rules, SolverTable table, IReadOnlyList<LabelledPosition> train,
        IReadOnlyList<LabelledPosition> validation, SearchBudget budget, int batchSize, int workers, bool tapered)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(train);
        ArgumentNullException.ThrowIfNull(validation);
        _rules = rules;
        _table = table;
        _train = Won(train);
        _validation = Won(validation);
        _budget = budget;
        _batchSize = Math.Min(batchSize, _train.Length);
        _workers = Math.Max(1, workers);
        _tapered = tapered;
        if (_batchSize < 1 || _validation.Length == 0)
        {
            throw new ArgumentException("Train and validation need won positions.", nameof(train));
        }
    }

    /// <summary>Won train positions.</summary>
    public int TrainCount => _train.Length;

    /// <summary>Won validation positions.</summary>
    public int ValidationCount => _validation.Length;

    /// <inheritdoc />
    public int[] DrawBatch(Rng rng)
    {
        ArgumentNullException.ThrowIfNull(rng);

        // A partial Fisher–Yates shuffle over a virtual identity array.
        var swapped = new Dictionary<int, int>();
        var batch = new int[_batchSize];
        for (int i = 0; i < _batchSize; i++)
        {
            int j = i + rng.NextInt(_train.Length - i);
            int atJ = swapped.TryGetValue(j, out int vj) ? vj : j;
            int atI = swapped.TryGetValue(i, out int vi) ? vi : i;
            swapped[j] = atI;
            batch[i] = atJ;
        }

        return batch;
    }

    /// <inheritdoc />
    public (double Plus, double Minus) Evaluate(int[] plus, int[] minus, int[] batch, Rng rng, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var positions = batch.Select(i => _train[i]).ToArray();
        return (Accuracy(plus, positions, ct), Accuracy(minus, positions, ct));
    }

    /// <inheritdoc />
    public double Validate(int[] weights, CancellationToken ct) => Accuracy(weights, _validation, ct);

    /// <summary>Accuracy of a weight vector on positions: (fox accuracy + hounds accuracy) / 2.</summary>
    public double Accuracy(int[] weights, LabelledPosition[] positions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(positions);
        var kept = new bool[positions.Length];
        var evaluator = TunedEvaluator.Create("candidate", weights, _tapered);
        Parallel.For(0, positions.Length, new ParallelOptions { MaxDegreeOfParallelism = _workers, CancellationToken = ct },
            () => new Search(_rules, evaluator, new SearchOptions { TtMegabytes = 1 }, SystemClock.Instance),
            (i, _, search) =>
            {
                search.NewGame();
                var p = positions[i].Position;
                var move = search.Run(p, _budget, ct).Move;
                kept[i] = KeepsWin(p, move);
                return search;
            },
            _ => { });

        long[] won = new long[2];
        long[] good = new long[2];
        for (int i = 0; i < positions.Length; i++)
        {
            int side = (int)positions[i].Position.SideToMove;
            won[side]++;
            good[side] += kept[i] ? 1 : 0;
        }

        double fox = won[0] == 0 ? double.NaN : (double)good[0] / won[0];
        double hounds = won[1] == 0 ? double.NaN : (double)good[1] / won[1];
        return double.IsNaN(fox) ? hounds : double.IsNaN(hounds) ? fox : (fox + hounds) / 2;
    }

    private static LabelledPosition[] Won(IReadOnlyList<LabelledPosition> positions) =>
        positions.Where(p => p.Value?.SideToMoveWins == true).ToArray();

    private bool KeepsWin(in GameState s, Move move)
    {
        var next = _rules.Apply(s, move);
        var outcome = _rules.Evaluate(next);
        return outcome.IsOver ? outcome.Winner == s.SideToMove : _table.Get(next).Winner(next.SideToMove) == s.SideToMove;
    }
}

/// <summary>
/// HeadToHead (spec 12.3): θ⁺ plays θ⁻ on opening pairs, one game in each role, so J⁺ − J⁻ = 2 · score(θ⁺) − 1.
/// Validation plays the weights against the starting weights. Implemented, but only smoke-tested (D-19).
/// </summary>
public sealed class HeadToHeadObjective : ISpsaObjective
{
    private readonly Rules _rules;
    private readonly LabelledPosition[] _openings;
    private readonly LabelledPosition[] _validation;
    private readonly int[] _start;
    private readonly SearchBudget _budget;
    private readonly int _pairs;
    private readonly int _workers;
    private readonly bool _tapered;

    /// <summary>Creates the objective.</summary>
    public HeadToHeadObjective(Rules rules, IReadOnlyList<LabelledPosition> openings, IReadOnlyList<LabelledPosition> validation,
        int[] start, SearchBudget budget, int pairs, int workers, bool tapered)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(openings);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(start);
        _rules = rules;
        _openings = [.. openings];
        _validation = [.. validation];
        _start = (int[])start.Clone();
        _budget = budget;
        _pairs = Math.Min(pairs, _openings.Length);
        _workers = Math.Max(1, workers);
        _tapered = tapered;
    }

    /// <inheritdoc />
    public int[] DrawBatch(Rng rng)
    {
        ArgumentNullException.ThrowIfNull(rng);
        return Enumerable.Range(0, _pairs).Select(_ => rng.NextInt(_openings.Length)).ToArray();
    }

    /// <inheritdoc />
    public (double Plus, double Minus) Evaluate(int[] plus, int[] minus, int[] batch, Rng rng, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(rng);
        double score = Score(plus, minus, batch.Select(i => _openings[i]).ToArray(), rng.NextU64(), ct);
        return (score, 1 - score);
    }

    /// <inheritdoc />
    public double Validate(int[] weights, CancellationToken ct) => Score(weights, _start, _validation, 0x5A11_DA7EUL, ct);

    private double Score(int[] a, int[] b, LabelledPosition[] openings, ulong seed, CancellationToken ct)
    {
        var points = new double[openings.Length];
        var evalA = TunedEvaluator.Create("a", a, _tapered);
        var evalB = TunedEvaluator.Create("b", b, _tapered);
        Parallel.For(0, openings.Length, new ParallelOptions { MaxDegreeOfParallelism = _workers, CancellationToken = ct },
            () => (A: new MinimaxAgent(evalA, new SearchOptions { TtMegabytes = 1 }), B: new MinimaxAgent(evalB, new SearchOptions { TtMegabytes = 1 })),
            (i, _, agents) =>
            {
                var options = new GameOptions { FoxBudget = _budget, HoundsBudget = _budget, Start = openings[i].Position, Clock = SystemClock.Instance };
                var first = GameRunner.Play(_rules, agents.A, agents.B, options, Rng.ForGame(seed, 2L * i), ct);
                var second = GameRunner.Play(_rules, agents.B, agents.A, options, Rng.ForGame(seed, (2L * i) + 1), ct);
                points[i] = ((first.Outcome.Winner == Side.Fox ? 1 : 0) + (second.Outcome.Winner == Side.Hounds ? 1 : 0)) / 2.0;
                return agents;
            },
            _ => { });
        return points.Length == 0 ? 0.5 : points.Average();
    }
}
