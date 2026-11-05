using System.Collections.Concurrent;
using FoxHounds.Core;

namespace FoxHounds.Solver.Tests;

/// <summary>Solver tables built once per test run and shared by all tests (they are immutable).</summary>
internal static class TestTables
{
    private static readonly ConcurrentDictionary<(int, FoxWinCondition), Lazy<SolverTable>> Cache = new();

    public static SolverTable Get(int n, FoxWinCondition win) =>
        Cache.GetOrAdd((n, win), key => new Lazy<SolverTable>(() =>
            SolverTable.Build(new Rules(new RuleSet { BoardSize = key.Item1, FoxWinCondition = key.Item2 }), null, CancellationToken.None))).Value;

    public static SolverTable Classic(int n) => Get(n, FoxWinCondition.ReachHomeRow);

    public static SolverTable Gilith(int n) => Get(n, FoxWinCondition.HoundsImmobile);

    public static Rules Rules(int n, FoxWinCondition win, Side first = Side.Fox, bool adjudicate = false) =>
        new(new RuleSet { BoardSize = n, FoxWinCondition = win, FirstMover = first, AdjudicateEscape = adjudicate });

    public static FoxWinCondition ParseCondition(string label) => label switch
    {
        "classic" or "ReachHomeRow" => FoxWinCondition.ReachHomeRow,
        "gilith" or "HoundsImmobile" => FoxWinCondition.HoundsImmobile,
        _ => throw new ArgumentException(label),
    };
}
