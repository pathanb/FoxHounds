using BenchmarkDotNet.Attributes;
using FoxHounds.Core;
using FoxHounds.Solver;

namespace FoxHounds.Bench;

/// <summary>Solver table build for 8×8, single-threaded: the floor of spec 15.4 is 10 s.</summary>
[MemoryDiagnoser]
public class SolverBenchmarks
{
    private Rules _rules = null!;

    [GlobalSetup]
    public void Setup() => _rules = new Rules(RuleSet.Classic);

    [Benchmark]
    public long BuildN8SingleThreaded() => SolverTable.Build(_rules, 1, null, CancellationToken.None).EntryCount;
}
