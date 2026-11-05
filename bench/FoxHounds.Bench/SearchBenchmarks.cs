using BenchmarkDotNet.Attributes;
using FoxHounds.Core;
using FoxHounds.Engine;
using FoxHounds.Engine.Evaluation;

namespace FoxHounds.Bench;

/// <summary>Fixed-depth search on 20 positions, and evaluation throughput (spec 15.4).</summary>
[MemoryDiagnoser]
public class SearchBenchmarks
{
    private Rules _rules = null!;
    private Search _search = null!;
    private IEvaluator _evaluator = null!;
    private static readonly SearchBudget Depth10 = SearchBudget.Depth(10);
    private GameState[] _positions = [];

    /// <summary>Total nodes of one pass over the 20 positions, for computing nodes per second.</summary>
    public long NodesPerPass { get; private set; }

    [GlobalSetup]
    public void Setup()
    {
        _rules = new Rules(RuleSet.Classic);
        _evaluator = WeightFile.LinV0.CreateEvaluator(8);
        // A 1 MB table: clearing it per position costs little, so the benchmark measures search, not memset.
        _search = new Search(_rules, _evaluator, new SearchOptions { TtMegabytes = 1 }, SystemClock.Instance);
        _positions = BenchmarkPositions(_rules, 20);
        NodesPerPass = SearchAll();
        Console.WriteLine($"// nodes per pass of FixedDepthSearch20Positions: {NodesPerPass}");
    }

    /// <summary>Depth-10 searches with a 1 MB table cleared before each position, so every pass does the same work.</summary>
    [Benchmark]
    public long FixedDepthSearch20Positions() => SearchAll();

    /// <summary>Evaluates the 20 positions 1,000 times.</summary>
    [Benchmark]
    public long Evaluate20000Positions()
    {
        long sum = 0;
        for (int k = 0; k < 1_000; k++)
        {
            foreach (var s in _positions)
            {
                sum += _evaluator.EvaluateFox(s);
            }
        }

        return sum;
    }

    /// <summary>The benchmark positions: seeded random play from the start, 4 to 24 plies deep, none finished.</summary>
    internal static GameState[] BenchmarkPositions(Rules rules, int count)
    {
        var rng = new Rng(20261105);
        var result = new List<GameState>();
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        while (result.Count < count)
        {
            var s = rules.Initial();
            int plies = 4 + rng.NextInt(21);
            for (int p = 0; p < plies && !rules.Evaluate(s).IsOver; p++)
            {
                int n = rules.GenerateMoves(s, moves);
                s = rules.Apply(s, moves[rng.NextInt(n)]);
            }

            if (!rules.Evaluate(s).IsOver)
            {
                result.Add(s);
            }
        }

        return [.. result];
    }

    private long SearchAll()
    {
        long nodes = 0;
        foreach (var s in _positions)
        {
            _search.NewGame();
            nodes += _search.Run(s, Depth10, CancellationToken.None).Nodes;
        }

        return nodes;
    }
}
