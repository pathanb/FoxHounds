using System.Security.Cryptography;
using FoxHounds.Core;

namespace FoxHounds.Solver.Tests;

/// <summary>T-SOL-03 (consistency), T-SOL-04 (escape soundness), full-table agreement with the prototype, and
/// determinism of the parallel build.</summary>
public sealed class TableIntegrityTests
{
    public static TheoryData<int, string> FastTables => new()
    {
        { 4, "classic" }, { 4, "gilith" }, { 5, "classic" }, { 5, "gilith" },
        { 6, "classic" }, { 6, "gilith" }, { 7, "classic" }, { 7, "gilith" },
    };

    public static TheoryData<int, string> SlowTables => new() { { 8, "classic" }, { 8, "gilith" } };

    [Theory]
    [MemberData(nameof(FastTables))]
    public void Every_entry_equals_the_combination_of_its_children(int n, string ruleset) =>
        CheckConsistency(TestTables.Get(n, TestTables.ParseCondition(ruleset)));

    [Theory]
    [MemberData(nameof(SlowTables))]
    [Trait("Category", "Slow")]
    public void Every_entry_equals_the_combination_of_its_children_8x8(int n, string ruleset) =>
        CheckConsistency(TestTables.Get(n, TestTables.ParseCondition(ruleset)));

    [Theory]
    [MemberData(nameof(FastTables))]
    public void Escape_adjudication_is_sound(int n, string ruleset) =>
        CheckEscapeSoundness(TestTables.Get(n, TestTables.ParseCondition(ruleset)));

    [Theory]
    [MemberData(nameof(SlowTables))]
    [Trait("Category", "Slow")]
    public void Escape_adjudication_is_sound_8x8(int n, string ruleset) =>
        CheckEscapeSoundness(TestTables.Get(n, TestTables.ParseCondition(ruleset)));

    [Fact]
    public void Every_table_byte_agrees_with_the_prototype()
    {
        // reference/solver_table_digests.txt: SHA-256 of complete tables built by the prototype's own solver.
        int compared = 0;
        foreach (string line in File.ReadLines(RepoRoot.File("reference/solver_table_digests.txt")))
        {
            if (line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split(' ');
            int n = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var table = TestTables.Get(n, TestTables.ParseCondition(parts[1]));
            Assert.Equal(long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), table.EntryCount);
            Assert.True(parts[3] == PayloadHash(table), $"{n}×{n} {parts[1]}: table differs from the prototype");
            compared++;
        }

        Assert.Equal(10, compared);
    }

    [Fact]
    public void Parallel_and_sequential_builds_are_byte_identical()
    {
        var rules = TestTables.Rules(8, FoxWinCondition.ReachHomeRow);
        var sequential = SolverTable.Build(rules, 1, null, TestContext.Current.CancellationToken);
        var parallel = SolverTable.Build(rules, 4, null, TestContext.Current.CancellationToken);
        Assert.Equal(PayloadHash(sequential), PayloadHash(parallel));
        Assert.Equal(PayloadHash(sequential), PayloadHash(TestTables.Classic(8)));
    }

    [Fact]
    public void Build_reports_progress_and_honours_cancellation()
    {
        var rules = TestTables.Rules(6, FoxWinCondition.ReachHomeRow);
        var reports = new List<double>();
        SolverTable.Build(rules, 1, new SynchronousProgress(reports.Add), TestContext.Current.CancellationToken);
        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1]);
        Assert.Equal(reports.Order(), reports);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SolverTable.Build(rules, null, cts.Token));
    }

    [Fact]
    public void Boards_above_11x11_are_refused_with_a_clear_error()
    {
        var ex = Assert.Throws<NotSupportedException>(() =>
            SolverTable.Build(new Rules(RuleSet.Classic with { BoardSize = 12 }), null, TestContext.Current.CancellationToken));
        Assert.Contains("22.5 GB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_sizes_match_spec_10_1()
    {
        Assert.Equal(2_024L * 24 * 2, SolverTable.EntryCountFor(7));
        Assert.Equal(35_960L * 32 * 2, SolverTable.EntryCountFor(8));
        Assert.Equal(91_390L * 40 * 2, SolverTable.EntryCountFor(9));
        Assert.Equal(2_118_760L * 50 * 2, SolverTable.EntryCountFor(10));
        Assert.Equal(5_461_512L * 60 * 2, SolverTable.EntryCountFor(11));
        Assert.Equal(156_238_908L * 72 * 2, SolverTable.EntryCountFor(12));
    }

    internal static string PayloadHash(SolverTable table)
    {
        var bytes = new byte[table.EntryCount];
        for (long i = 0; i < bytes.LongLength; i++)
        {
            bytes[i] = table.RawValue(i);
        }

        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static void CheckConsistency(SolverTable table)
    {
        var rules = table.Rules;
        bool reachHome = table.WinCondition == FoxWinCondition.ReachHomeRow;
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        for (long i = 0; i < table.EntryCount; i++)
        {
            byte stored = table.RawValue(i);
            if (!table.TryGetPosition(i, out var s))
            {
                Assert.Equal(0, stored); // fox on a hound square
                continue;
            }

            Assert.Equal(i, table.IndexOf(s));
            if (reachHome && s.SideToMove == Side.Fox && rules.Geo.Row[s.Fox] == 0)
            {
                Assert.Equal(0, stored); // cannot arise in play (spec 10.3)
                continue;
            }

            var outcome = rules.Evaluate(s);
            if (outcome.IsOver)
            {
                // Every stored terminal is a loss for the side to move in 0 plies.
                Assert.NotEqual(s.SideToMove, outcome.Winner);
                Assert.Equal(1, stored);
                continue;
            }

            int count = rules.GenerateMoves(s, moves);
            int shortestWin = int.MaxValue;
            int longestLoss = -1;
            for (int k = 0; k < count; k++)
            {
                var child = table.Get(rules.Apply(s, moves[k]));
                if (!child.SideToMoveWins)
                {
                    shortestWin = Math.Min(shortestWin, child.Plies + 1);
                }
                else
                {
                    longestLoss = Math.Max(longestLoss, child.Plies + 1);
                }
            }

            var expected = shortestWin != int.MaxValue ? new SolvedValue(true, shortestWin) : new SolvedValue(false, longestLoss);
            var actual = table.Get(s);
            if (expected != actual)
            {
                Assert.Fail($"{s}: stored {actual}, children give {expected}");
            }
        }
    }

    private static void CheckEscapeSoundness(SolverTable table)
    {
        var rules = table.Rules;
        long fired = 0;
        for (long i = 0; i < table.EntryCount; i++)
        {
            if (table.RawValue(i) == 0 || !table.TryGetPosition(i, out var s) || !rules.IsEscaped(s))
            {
                continue;
            }

            fired++;
            if (table.Get(s).Winner(s.SideToMove) != Side.Fox)
            {
                Assert.Fail($"Escape test fires in {s}, but the fox does not win.");
            }
        }

        Assert.True(fired > 0);
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
