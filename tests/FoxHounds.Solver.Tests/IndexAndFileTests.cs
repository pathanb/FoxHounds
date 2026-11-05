using FoxHounds.Core;

namespace FoxHounds.Solver.Tests;

/// <summary>T-SOL-07 (ranking), T-SOL-06 (table files), the cache store and the Get contract.</summary>
public sealed class IndexAndFileTests
{
    public static TheoryData<int> SmallSizes => new(4, 5, 6, 7, 8);

    [Theory]
    [MemberData(nameof(SmallSizes))]
    public void Rank_and_unrank_round_trip_for_every_hound_set(int n)
    {
        var geo = Geometry.For(n);
        var index = new HoundSetIndex(geo.SquareCount, geo.HoundCount);
        var all = index.EnumerateAll();
        Assert.Equal(index.Count, all.LongLength);
        var seen = new HashSet<UInt128>();
        for (long r = 0; r < all.LongLength; r++)
        {
            Assert.Equal(geo.HoundCount, (int)UInt128.PopCount(all[r]));
            Assert.True(seen.Add(all[r]));
            Assert.Equal(r, index.Rank(all[r]));
            Assert.Equal(all[r], index.Unrank(r));
        }
    }

    [Fact]
    public void Rank_follows_the_combinatorial_number_system()
    {
        var index = new HoundSetIndex(32, 4);
        Assert.Equal(35_960, index.Count);
        Assert.Equal(0, index.Rank(0b1111));                              // {0,1,2,3}
        Assert.Equal(1, index.Rank(0b10111));                             // {0,1,2,4}: C(4,4)
        Assert.Equal(35_959, index.Rank((UInt128)0b1111 << 28));          // {28,29,30,31}
        Assert.Equal(10 + 6 + 3 + 1, index.Binomial(5, 2) + index.Binomial(4, 2) + index.Binomial(3, 2) + index.Binomial(2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Unrank(35_960));
    }

    [Fact]
    public void Save_and_load_round_trip()
    {
        var table = TestTables.Classic(6);
        string path = TempPath();
        table.Save(path);
        Assert.Equal(54 + table.EntryCount, new FileInfo(path).Length);
        var loaded = SolverTable.Load(path, RuleSet.Classic with { BoardSize = 6 });
        Assert.Equal(TableIntegrityTests.PayloadHash(table), TableIntegrityTests.PayloadHash(loaded));
        Assert.Equal(6, loaded.BoardSize);
        Assert.Equal(FoxWinCondition.ReachHomeRow, loaded.WinCondition);
        Assert.False(loaded.Rules.Config.AdjudicateEscape);
        File.Delete(path);
    }

    [Fact]
    public void A_flipped_payload_byte_is_rejected()
    {
        string path = TempPath();
        TestTables.Classic(5).Save(path);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^7] ^= 0x01;
        File.WriteAllBytes(path, bytes);
        var ex = Assert.Throws<InvalidDataException>(() => SolverTable.Load(path));
        Assert.Contains("hash", ex.Message, StringComparison.Ordinal);
        File.Delete(path);
    }

    [Fact]
    public void A_table_for_another_ruleset_is_rejected()
    {
        string path = TempPath();
        TestTables.Classic(5).Save(path);
        Assert.Throws<InvalidDataException>(() => SolverTable.Load(path, RuleSet.Gilith with { BoardSize = 5 }));
        Assert.Throws<InvalidDataException>(() => SolverTable.Load(path, RuleSet.Classic with { BoardSize = 6 }));
        Assert.NotNull(SolverTable.Load(path, RuleSet.Classic with { BoardSize = 5, FirstMover = Side.Hounds }));
        File.Delete(path);
    }

    [Theory]
    [InlineData(0, 0x58, "magic")]      // magic
    [InlineData(8, 0x02, "version")]    // version
    [InlineData(10, 0x09, "")]          // board size
    [InlineData(14, 0x01, "")]          // entry count
    public void Corrupt_headers_are_rejected(int offset, byte value, string message)
    {
        string path = TempPath();
        TestTables.Classic(5).Save(path);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[offset] = value;
        File.WriteAllBytes(path, bytes);
        var ex = Assert.Throws<InvalidDataException>(() => SolverTable.Load(path));
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        File.Delete(path);
    }

    [Fact]
    public void Truncated_files_are_rejected()
    {
        string path = TempPath();
        TestTables.Classic(5).Save(path);
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^1]);
        Assert.Throws<InvalidDataException>(() => SolverTable.Load(path));
        File.WriteAllBytes(path, bytes[..20]);
        Assert.Throws<InvalidDataException>(() => SolverTable.Load(path));
        File.Delete(path);
    }

    [Fact]
    public void The_store_builds_once_saves_and_rebuilds_a_corrupt_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fh-store-" + Guid.NewGuid().ToString("N"));
        var rules = TestTables.Rules(5, FoxWinCondition.HoundsImmobile, Side.Hounds, adjudicate: true);
        var store = new SolverTableStore(dir);
        Assert.False(store.Exists(rules.Config));
        var first = store.GetOrBuild(rules, null, TestContext.Current.CancellationToken);
        Assert.True(store.Exists(rules.Config));
        Assert.Same(first, store.GetOrBuild(rules, null, TestContext.Current.CancellationToken));
        Assert.EndsWith("n5-houndsImmobile.fhsolv", store.PathFor(5, FoxWinCondition.HoundsImmobile), StringComparison.Ordinal);

        // A fresh store reads the file; after corruption it rebuilds and overwrites it.
        string path = store.PathFor(5, FoxWinCondition.HoundsImmobile);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        var rebuilt = new SolverTableStore(dir).GetOrBuild(rules, null, TestContext.Current.CancellationToken);
        Assert.Equal(TableIntegrityTests.PayloadHash(first), TableIntegrityTests.PayloadHash(rebuilt));
        Assert.NotNull(SolverTable.Load(path));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Get_throws_for_positions_without_a_value_and_IndexOf_rejects_foreign_positions()
    {
        var table = TestTables.Classic(8);
        var home = table.Rules.FromNotation("8/d8/b8,f8,h8,e7/F"); // fox on row 0, fox to move
        Assert.Throws<InvalidOperationException>(() => table.Get(home));
        Assert.False(table.TryGet(home, out _));
        Assert.True(table.TryGet(table.Rules.FromNotation("8/d8/b8,f8,h8,e7/H"), out var hounds));
        Assert.Equal(new SolvedValue(false, 0), hounds); // FoxReachedHome: the hounds have lost

        var other = new Rules(RuleSet.Classic with { BoardSize = 6 }).Initial();
        Assert.Throws<ArgumentException>(() => table.Get(other));
        Assert.False(table.Covers(RuleSet.Classic with { BoardSize = 6 }));
        Assert.True(table.Covers(RuleSet.Classic with { AdjudicateEscape = true, FoxStart = FoxStart.ChooseOnBackRow }));
    }

    [Fact]
    public void RateMoves_rates_every_legal_ply_and_the_best_equals_the_position_value()
    {
        var table = TestTables.Classic(8);
        var rules = table.Rules;
        var rng = new Rng(42);
        Span<RatedMove> rated = stackalloc RatedMove[Rules.MaxMoves];
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        for (int i = 0; i < 2_000; i++)
        {
            var s = RandomGameState(rules, rng);
            int count = table.RateMoves(s, rated);
            if (rules.Evaluate(s).IsOver)
            {
                Assert.Equal(0, count);
                continue;
            }

            Assert.Equal(rules.GenerateMoves(s, moves), count);
            var value = table.Get(s);
            bool anyWin = false;
            int bestWin = int.MaxValue;
            int worstLoss = -1;
            for (int k = 0; k < count; k++)
            {
                Assert.Equal(moves[k], rated[k].Move);
                var v = rated[k].ValueForMover;
                if (v.SideToMoveWins)
                {
                    anyWin = true;
                    bestWin = Math.Min(bestWin, v.Plies);
                }
                else
                {
                    worstLoss = Math.Max(worstLoss, v.Plies);
                }
            }

            Assert.Equal(value, anyWin ? new SolvedValue(true, bestWin) : new SolvedValue(false, worstLoss));
        }
    }

    private static GameState RandomGameState(Rules rules, Rng rng)
    {
        // A position reached by random play from a random fox start: realistic, and never "fox home, fox to move".
        var s = rules.Initial(rules.FoxStartSquares[rng.NextInt(rules.FoxStartSquares.Count)]);
        int plies = rng.NextInt(40);
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        for (int p = 0; p < plies && !rules.Evaluate(s).IsOver; p++)
        {
            int n = rules.GenerateMoves(s, moves);
            s = rules.Apply(s, moves[rng.NextInt(n)]);
        }

        return s;
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "fh-test-" + Guid.NewGuid().ToString("N") + ".fhsolv");
}
