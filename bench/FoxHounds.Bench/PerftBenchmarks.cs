using BenchmarkDotNet.Attributes;
using FoxHounds.Core;

namespace FoxHounds.Bench;

/// <summary>Perft(10) from the Classic initial position: the performance floor of spec 6.3 (&lt; 1 s).</summary>
[MemoryDiagnoser]
public class PerftBenchmarks
{
    private Rules _rules = null!;
    private GameState _start;

    [GlobalSetup]
    public void Setup()
    {
        _rules = new Rules(RuleSet.Classic with { AdjudicateEscape = false });
        _start = _rules.Initial();
    }

    [Benchmark]
    public long Perft10() => Perft.Count(_rules, _start, 10);
}
