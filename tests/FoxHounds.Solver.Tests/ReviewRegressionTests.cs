using FoxHounds.Core;

namespace FoxHounds.Solver.Tests;

/// <summary>Regression tests for the independent M2 review: each one failed (or was missing) before its fix.</summary>
public sealed class ReviewRegressionTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "fh-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_cancelled_caller_does_not_fail_the_others_and_every_caller_sees_progress()
    {
        // Finding 1: the shared build used to capture the first caller's token and progress callback.
        string dir = TempDir();
        var store = new SolverTableStore(dir);
        var rules = TestTables.Rules(7, FoxWinCondition.ReachHomeRow);
        using var ctsA = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource();
        var progressA = new BlockingProgress(started, release);
        var progressB = new CountingProgress();

        var a = store.GetOrBuildAsync(rules, progressA, ctsA.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); // build is running, held in A's callback
        var b = store.GetOrBuildAsync(rules, progressB, CancellationToken.None);
        await ctsA.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        release.Set();

        var table = await b.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal(7, table.BoardSize);
        Assert.True(progressB.Count > 0, "the second caller received no progress");
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task When_every_caller_gives_up_the_build_stops_and_the_next_request_starts_again()
    {
        string dir = TempDir();
        var store = new SolverTableStore(dir);
        var rules = TestTables.Rules(7, FoxWinCondition.HoundsImmobile);
        using var cts = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource();
        var only = store.GetOrBuildAsync(rules, new BlockingProgress(started, release), cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);
        release.Set();

        var table = await store.GetOrBuildAsync(rules, null, TestContext.Current.CancellationToken);
        Assert.Equal(TableIntegrityTests.PayloadHash(TestTables.Gilith(7)), TableIntegrityTests.PayloadHash(table));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void A_table_that_cannot_be_saved_is_still_returned_with_a_warning()
    {
        // Finding 2: a failed save used to throw away a freshly built table.
        string file = Path.Combine(Path.GetTempPath(), "fh-not-a-dir-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "a file, so the store folder below it cannot be created");
        var warnings = new List<string>();
        var store = new SolverTableStore(Path.Combine(file, "tables")) { Warn = warnings.Add };
        var table = store.GetOrBuild(TestTables.Rules(5, FoxWinCondition.ReachHomeRow), null, TestContext.Current.CancellationToken);
        Assert.Equal(5, table.BoardSize);
        Assert.Contains(warnings, w => w.Contains("could not save", StringComparison.Ordinal));
        File.Delete(file);
    }

    [Fact]
    public void Saving_leaves_no_temporary_files_behind()
    {
        string dir = TempDir();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "t.fhsolv");
        TestTables.Classic(5).Save(path);
        TestTables.Classic(5).Save(path); // overwrite
        Assert.Equal(["t.fhsolv"], Directory.GetFiles(dir).Select(Path.GetFileName));
        Directory.Delete(dir, recursive: true);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void A_header_whose_win_condition_byte_was_flipped_is_rejected(int n)
    {
        // Finding 3: the hash covers only the payload, so byte 13 used to load a Classic table as Gilith.
        foreach (var table in new[] { TestTables.Classic(n), TestTables.Gilith(n) })
        {
            string path = Path.Combine(Path.GetTempPath(), "fh-wc-" + Guid.NewGuid().ToString("N") + ".fhsolv");
            table.Save(path);
            byte[] bytes = File.ReadAllBytes(path);
            bytes[13] ^= 1;
            File.WriteAllBytes(path, bytes);
            var ex = Assert.Throws<InvalidDataException>(() => SolverTable.Load(path));
            Assert.Contains("other win condition", ex.Message, StringComparison.Ordinal);
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(10, 0x09)] // n
    [InlineData(11, 0x03)] // H
    [InlineData(12, 0x0D)] // P
    [InlineData(14, 0x01)] // entry count
    public void Corrupt_size_fields_are_rejected_with_a_clear_message(int offset, byte value)
    {
        string path = Path.Combine(Path.GetTempPath(), "fh-hdr-" + Guid.NewGuid().ToString("N") + ".fhsolv");
        TestTables.Classic(5).Save(path);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[offset] = value;
        File.WriteAllBytes(path, bytes);
        var ex = Assert.Throws<InvalidDataException>(() => SolverTable.Load(path));
        Assert.Contains("header sizes do not match", ex.Message, StringComparison.Ordinal);
        File.Delete(path);
    }

    [Fact]
    public void RateMoves_and_the_agent_refuse_a_position_of_another_board_size()
    {
        // Finding 4: RateMoves used to rate a 9×9 position as if it were 8×8.
        var table = TestTables.Classic(8);
        var nine = new Rules(RuleSet.Classic with { BoardSize = 9 }).FromNotation("9/b3/b9,d9,f9,h9/F");
        var buffer = new RatedMove[Rules.MaxMoves];
        Assert.Throws<ArgumentException>(() => table.RateMoves(nine, buffer));
        var agent = new SolverAgent(table);
        agent.NewGame(new Rules(RuleSet.Classic), Side.Fox, new Rng(1));
        Assert.Throws<ArgumentException>(() => agent.ChooseMove(nine, [], SearchBudget.Unlimited, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_default_store_folder_is_always_absolute()
    {
        // Finding 5.
        Assert.True(Path.IsPathRooted(SolverTableStore.DefaultDirectory), SolverTableStore.DefaultDirectory);
        Assert.EndsWith(Path.Combine("FoxHounds", "tables"), SolverTableStore.DefaultDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void Fox_start_values_are_rated_from_the_fox_side_and_compared_as_the_solver_plays()
    {
        // Gap 7: on 4×4…10×10 all starts tie, so the preference itself needs a direct test.
        Assert.Equal(new SolvedValue(false, 43), SolverAgent.FoxPerspective(new SolvedValue(true, 43), Side.Hounds));
        Assert.Equal(new SolvedValue(true, 25), SolverAgent.FoxPerspective(new SolvedValue(true, 25), Side.Fox));
        Assert.Equal(new SolvedValue(true, 8), SolverAgent.FoxPerspective(new SolvedValue(false, 8), Side.Hounds));

        Assert.True(SolverAgent.Compare(new SolvedValue(true, 30), new SolvedValue(false, 2)) > 0);  // any win beats any loss
        Assert.True(SolverAgent.Compare(new SolvedValue(true, 7), new SolvedValue(true, 9)) > 0);    // shorter win
        Assert.True(SolverAgent.Compare(new SolvedValue(false, 44), new SolvedValue(false, 40)) > 0); // longer loss
        Assert.Equal(0, SolverAgent.Compare(new SolvedValue(false, 44), new SolvedValue(false, 44)));
    }

    /// <summary>On its first report, signals <paramref name="started"/> and holds the building thread until released.</summary>
    private sealed class BlockingProgress(TaskCompletionSource started, ManualResetEventSlim release) : IProgress<double>
    {
        private int _reports;

        public void Report(double value)
        {
            if (Interlocked.Increment(ref _reports) == 1)
            {
                started.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        }
    }

    private sealed class CountingProgress : IProgress<double>
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Report(double value) => Interlocked.Increment(ref _count);
    }
}
