using System.Runtime.CompilerServices;
using FoxHounds.Agents;
using FoxHounds.Core;
using FoxHounds.Engine.Evaluation;
using FoxHounds.Solver;

namespace FoxHounds.Engine.Tests;

/// <summary>T-ENG-01 to T-ENG-04 and T-ENG-06, plus the budget, ordering and root-selection contracts of spec 8.</summary>
public sealed class SearchTests
{
    private static readonly IEvaluator LinV0 = WeightFile.LinV0.CreateEvaluator(8);
    private static readonly ManualClock Clock = new(DateTimeOffset.UnixEpoch);

    private static Search NewSearch(Rules rules, IEvaluator? evaluator = null, int ttMegabytes = 4, SearchOptions? options = null) =>
        new(rules, evaluator ?? WeightFile.LinV0.CreateEvaluator(rules.Geo.N), (options ?? new SearchOptions()) with { TtMegabytes = ttMegabytes }, Clock);

    [Fact]
    public void Search_always_returns_a_legal_ply()
    {
        // T-ENG-01, across sizes, budgets and options.
        var rng = new Rng(101);
        foreach (int n in new[] { 4, 5, 8, 11, 16 })
        {
            var rules = new Rules(RuleSet.Classic with { BoardSize = n });
            var searches = new[]
            {
                NewSearch(rules),
                NewSearch(rules, ttMegabytes: 0),
                NewSearch(rules, options: new SearchOptions { RootSelection = RootSelection.RandomAmongBest, Margin = 20 }),
            };
            for (int i = 0; i < 150; i++)
            {
                var s = Positions.RandomOngoing(rules, rng);
                var search = searches[i % searches.Length];
                var budget = (i % 3) switch { 0 => SearchBudget.Depth(1 + (i % 5)), 1 => SearchBudget.Nodes(3_000), _ => SearchBudget.Depth(3) };
                var result = search.Run(s, budget, TestContext.Current.CancellationToken, rng);
                Assert.True(rules.IsLegal(s, result.Move), $"{s}: {rules.Geo.FormatMove(result.Move)}");
            }
        }
    }

    [Fact]
    public void Alpha_beta_equals_plain_minimax_at_depths_1_to_6_on_600_positions() => AlphaBetaEqualsMinimax(600, 0xAB01);

    [Fact]
    [Trait("Category", "Slow")]
    public void Alpha_beta_equals_plain_minimax_at_depths_1_to_6_on_10000_positions() => AlphaBetaEqualsMinimax(10_000, 0xAB02);

    [Fact]
    public void Exhaustive_search_equals_the_solver_on_every_reachable_4x4_position()
    {
        // T-ENG-03, first part.
        var rules = new Rules(RuleSet.Classic with { BoardSize = 4, AdjudicateEscape = false });
        var table = SolverTable.Build(rules, null, TestContext.Current.CancellationToken);
        var search = NewSearch(rules);
        int compared = 0;
        foreach (var s in Reachable(rules))
        {
            if (rules.Evaluate(s).IsOver)
            {
                continue;
            }

            search.NewGame();
            var result = search.Run(s, SearchBudget.Unlimited, TestContext.Current.CancellationToken);
            Assert.True(result.Exact);
            AssertMatchesSolver(table, s, result.Score);
            compared++;
        }

        Assert.Equal(87, compared); // 114 reachable minus 27 terminal (spec 15.2, reference output)
    }

    [Fact]
    public void Exhaustive_search_equals_the_solver_on_600_late_8x8_positions() => ExhaustiveEqualsSolver8x8(600, 0xE803);

    [Fact]
    [Trait("Category", "Slow")]
    public void Exhaustive_search_equals_the_solver_on_5000_late_8x8_positions() => ExhaustiveEqualsSolver8x8(5_000, 0xE804);

    [Fact]
    public void Proven_scores_are_identical_with_the_table_on_and_off()
    {
        // T-ENG-04: whenever either search reports an exact result at the same depth budget, both must agree.
        var rules = new Rules(RuleSet.Classic with { AdjudicateEscape = false });
        var withTt = NewSearch(rules, ttMegabytes: 8);
        var withoutTt = NewSearch(rules, ttMegabytes: 0);
        var rng = new Rng(0x7704);
        int proven = 0;
        for (int i = 0; i < 400; i++)
        {
            var s = Positions.RandomPlayed(rules, rng);
            if (rules.Evaluate(s).IsOver)
            {
                continue;
            }

            var budget = SearchBudget.Depth(4 + rng.NextInt(9));
            withTt.NewGame();
            var a = withTt.Run(s, budget, TestContext.Current.CancellationToken);
            var b = withoutTt.Run(s, budget, TestContext.Current.CancellationToken);
            if (a.Exact || b.Exact)
            {
                Assert.True(a.Exact && b.Exact, $"{s} {budget}: exact with TT {a.Exact} ({a.Score}), without {b.Exact} ({b.Score})");
                Assert.Equal(a.Score, b.Score);
                proven++;
            }
            else
            {
                Assert.Equal(b.Score, a.Score); // same depth, both heuristic: the table must not change the value
            }
        }

        Assert.True(proven > 50, $"only {proven} proven positions");
    }

    [Fact]
    public void A_depth_one_search_with_the_solver_evaluator_plays_perfectly()
    {
        // SolverEvaluator isolates search from evaluation (spec 9.2): the best ply's value equals the solver's.
        var rules = new Rules(RuleSet.Classic with { AdjudicateEscape = false });
        var table = SolverTable.Build(rules, null, TestContext.Current.CancellationToken);
        var search = NewSearch(rules, new SolverEvaluator(table));
        var rng = new Rng(5150);
        Span<RatedMove> rated = stackalloc RatedMove[Rules.MaxMoves];
        for (int i = 0; i < 500; i++)
        {
            var s = Positions.RandomPlayed(rules, rng);
            if (rules.Evaluate(s).IsOver)
            {
                continue;
            }

            var result = search.Run(s, SearchBudget.Depth(1), TestContext.Current.CancellationToken);
            int count = table.RateMoves(s, rated);
            var chosen = rated[..count].ToArray().Single(r => r.Move == result.Move).ValueForMover;
            Assert.Equal(table.Get(s), chosen);
        }
    }

    [Fact]
    public void Stops_early_when_proven_or_exhaustive_and_respects_MaxDepth()
    {
        var rules = new Rules(RuleSet.Classic with { AdjudicateEscape = false });
        var search = NewSearch(rules);

        // Fox to move wins at once by stepping onto row 0: proven at depth 1.
        var home = rules.FromNotation("8/c7/b6,d6,f6,h6/F");
        var r1 = search.Run(home, SearchBudget.Depth(30), TestContext.Current.CancellationToken);
        Assert.Equal(Scores.Win - 1, r1.Score);
        Assert.Equal(1, r1.Depth);
        Assert.True(r1.Exact);

        // From the start a depth-5 search is not proven and stops at depth 5.
        var r5 = search.Run(rules.Initial(), SearchBudget.Depth(5), TestContext.Current.CancellationToken);
        Assert.Equal(5, r5.Depth);
        Assert.False(r5.Exact);
        Assert.False(Scores.IsProven(r5.Score));
    }

    [Fact]
    public void Node_budget_is_respected_to_within_one_check_interval_and_is_deterministic()
    {
        var rules = new Rules(RuleSet.Classic);
        var a = NewSearch(rules).Run(rules.Initial(), SearchBudget.Nodes(50_000), TestContext.Current.CancellationToken);
        var b = NewSearch(rules).Run(rules.Initial(), SearchBudget.Nodes(50_000), TestContext.Current.CancellationToken);
        Assert.InRange(a.Nodes, 1, 50_000 + Search.CheckInterval);
        Assert.Equal(a, b with { Elapsed = a.Elapsed });
        Assert.True(a.Depth >= 1);
    }

    [Fact]
    public void Hard_time_limit_aborts_and_soft_time_stops_between_iterations()
    {
        var rules = new Rules(RuleSet.Classic);
        var ticking = new TickingClock(TimeSpan.FromMilliseconds(1));
        var search = new Search(rules, LinV0, new SearchOptions { TtMegabytes = 4 }, ticking);
        var hard = search.Run(rules.Initial(), new SearchBudget(null, null, null, TimeSpan.FromMilliseconds(20)), TestContext.Current.CancellationToken);
        Assert.True(hard.Depth >= 1);
        Assert.False(hard.Exact);

        var soft = search.Run(rules.Initial(), new SearchBudget(null, null, TimeSpan.FromMilliseconds(1), null), TestContext.Current.CancellationToken);
        Assert.Equal(1, soft.Depth); // the clock advances on every read, so the soft limit has passed after depth 1
    }

    [Fact]
    public void A_search_allocates_nothing()
    {
        // T-ENG-06, allocation half. Coverage instrumentation adds allocating hit counters, so skip under coverage.
        Assert.SkipWhen(Instrumentation.IsCoverageInstrumented(typeof(Search).Assembly), "coverage instrumentation allocates");
        var rules = new Rules(RuleSet.Classic with { BoardSize = 12 });
        var search = new Search(rules, WeightFile.LinV0.CreateEvaluator(12), new SearchOptions { TtMegabytes = 16 }, SystemClock.Instance);
        // The first deep search triggers one-time runtime work (tiered JIT, on-stack replacement) that allocates
        // about 2–7 KB; every later search allocates nothing. Spec 4.3 concerns that steady state.
        var budget = SearchBudget.Nodes(200_000);
        var ct = TestContext.Current.CancellationToken;
        search.Run(rules.Initial(), budget, ct);
        search.Run(rules.Initial(), budget, ct);

        long before = GC.GetAllocatedBytesForCurrentThread();
        search.Run(rules.Initial(), budget, ct);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task Cancellation_returns_within_100_ms()
    {
        // T-ENG-06, cancellation half.
        var rules = new Rules(RuleSet.Classic with { BoardSize = 12 });
        var search = new Search(rules, WeightFile.LinV0.CreateEvaluator(12), new SearchOptions { TtMegabytes = 16 }, SystemClock.Instance);
        search.Run(rules.Initial(), SearchBudget.Nodes(200_000), TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var task = Task.Run(() => search.Run(rules.Initial(), SearchBudget.Unlimited, cts.Token), TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        TimeSpan cancelledAt = SystemClock.Instance.Elapsed;
        await cts.CancelAsync();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        TimeSpan latency = SystemClock.Instance.Elapsed - cancelledAt;
        Assert.True(latency < TimeSpan.FromMilliseconds(100), $"cancellation took {latency.TotalMilliseconds:F1} ms");
        Assert.True(rules.IsLegal(rules.Initial(), result.Move));
    }

    [Fact]
    public void Principal_variation_is_a_legal_line_starting_with_the_chosen_move()
    {
        var rules = new Rules(RuleSet.Classic);
        var search = NewSearch(rules);
        var rng = new Rng(3);
        for (int i = 0; i < 100; i++)
        {
            var s = Positions.RandomOngoing(rules, rng);
            var result = search.Run(s, SearchBudget.Depth(6), TestContext.Current.CancellationToken);
            var pv = search.PrincipalVariation.ToArray();
            Assert.NotEmpty(pv);
            Assert.Equal(result.Move, pv[0]);
            var p = s;
            foreach (var m in pv)
            {
                p = rules.ApplyChecked(p, m);
            }
        }
    }

    [Fact]
    public void RandomAmongBest_picks_only_best_moves_varies_with_the_seed_and_needs_a_generator()
    {
        var rules = new Rules(RuleSet.Classic);
        var random = NewSearch(rules, ZeroEvaluator.Instance, options: new SearchOptions { RootSelection = RootSelection.RandomAmongBest });
        var start = rules.Initial();
        var picks = new HashSet<Move>();
        for (ulong seed = 0; seed < 20; seed++)
        {
            picks.Add(random.Run(start, SearchBudget.Depth(3), TestContext.Current.CancellationToken, new Rng(seed)).Move);
        }

        Assert.Equal(2, picks.Count); // with a zero evaluator both fox moves score 0
        Assert.Throws<ArgumentNullException>(() => random.Run(start, SearchBudget.Depth(2), TestContext.Current.CancellationToken));

        // Never playing a proven loss is tested in ReviewRegressionTests (the position here had no losing move).
    }

    [Fact]
    public void Ordering_options_change_node_counts_but_never_the_value()
    {
        var rules = new Rules(RuleSet.Classic);
        var rng = new Rng(77);
        var plain = NewSearch(rules, ttMegabytes: 0, options: new SearchOptions { UseKillers = false, UseHistory = false });
        var full = NewSearch(rules, ttMegabytes: 0);
        long plainNodes = 0;
        long fullNodes = 0;
        for (int i = 0; i < 60; i++)
        {
            var s = Positions.RandomPlayed(rules, rng, 20);
            if (rules.Evaluate(s).IsOver)
            {
                continue;
            }

            var a = plain.Run(s, SearchBudget.Depth(7), TestContext.Current.CancellationToken);
            var b = full.Run(s, SearchBudget.Depth(7), TestContext.Current.CancellationToken);
            Assert.Equal(a.Score, b.Score);
            plainNodes += a.Nodes;
            fullNodes += b.Nodes;
        }

        Assert.True(fullNodes < plainNodes, $"ordering should cut nodes: {fullNodes} vs {plainNodes}");
    }

    [Fact]
    public void Transposition_table_entry_is_16_bytes_and_follows_the_replacement_rule()
    {
        Assert.Equal(16, Unsafe.SizeOf<TtEntry>());
        var tt = new TranspositionTable(1);
        Assert.Equal(65_536, tt.Capacity);
        ulong hash = 0x1234_5678_0000_0042UL;
        tt.Store(hash, 5, 100, Bound.Exact, new Move(1, 2));
        tt.Store(hash, 3, 200, Bound.Exact, new Move(3, 4));      // shallower, same age: kept
        Assert.True(tt.Probe(hash, out var e));
        Assert.Equal((100, (byte)5), (e.Score, e.Depth));
        tt.NewSearch();
        tt.Store(hash, 3, 200, Bound.Lower, new Move(3, 4));      // older entry: replaced
        Assert.True(tt.Probe(hash, out e));
        Assert.Equal((200, Bound.Lower), (e.Score, e.Bound));
        Assert.False(tt.Probe(hash ^ (1UL << 40), out _));        // same slot, other key check
        tt.Clear();
        Assert.False(tt.Probe(hash, out _));
        var off = new TranspositionTable(0);
        off.Store(hash, 1, 1, Bound.Exact, Move.None);
        Assert.False(off.Enabled);
        Assert.False(off.Probe(hash, out _));
    }

    [Fact]
    public void Searching_a_finished_position_is_an_error()
    {
        var rules = new Rules(RuleSet.Classic);
        Assert.Throws<ArgumentException>(() =>
            NewSearch(rules).Run(rules.FromNotation("8/a1/b2,b8,d8,f8/F"), SearchBudget.Depth(3), TestContext.Current.CancellationToken));
    }

    private static void AlphaBetaEqualsMinimax(int positions, ulong seed)
    {
        // T-ENG-02: TT off; killers and history on (ordering never changes the root value).
        var rng = new Rng(seed);
        var randomWeights = new LinearEvaluator("random-weights", [7, -13, 5, 2, 301, -17, 64, 9, -3, 11, -40]);
        int done = 0;
        foreach (int n in new[] { 4, 5, 6, 8 })
        {
            var rules = new Rules(RuleSet.Classic with { BoardSize = n, AdjudicateEscape = rng.NextInt(2) == 0 });
            var evaluator = n == 8 ? LinV0 : randomWeights;
            var search = NewSearch(rules, evaluator, ttMegabytes: 0);
            for (int i = 0; i < positions / 4; i++)
            {
                var s = Positions.RandomOngoing(rules, rng);
                for (int depth = 1; depth <= 6; depth++)
                {
                    int expected = Positions.PlainMinimax(rules, evaluator, s, depth);
                    var result = search.Run(s, SearchBudget.Depth(depth), TestContext.Current.CancellationToken);
                    if (expected != result.Score)
                    {
                        Assert.Fail($"{s} depth {depth}: minimax {expected}, alpha-beta {result.Score} (depth reached {result.Depth})");
                    }
                }

                done++;
            }
        }

        Assert.Equal(positions, done);
    }

    private static void ExhaustiveEqualsSolver8x8(int positions, ulong seed)
    {
        // T-ENG-03, second part: random 8×8 positions with MaxPliesFrom ≤ 15, i.e. hound row sum ≥ 21.
        var rules = new Rules(RuleSet.Classic with { AdjudicateEscape = false });
        var table = SolverTable.Build(rules, null, TestContext.Current.CancellationToken);
        var lateSets = new HoundSetIndex(32, 4).EnumerateAll().Where(h => RowSum(rules, h) >= 21).ToArray();
        var search = NewSearch(rules);
        var rng = new Rng(seed);
        int compared = 0;
        while (compared < positions)
        {
            UInt128 hounds = lateSets[rng.NextInt(lateSets.Length)];
            int fox = rng.NextInt(32);
            var side = rng.NextInt(2) == 0 ? Side.Fox : Side.Hounds;
            if (((hounds >> fox) & UInt128.One) != UInt128.Zero)
            {
                continue;
            }

            var s = rules.Create(hounds, (byte)fox, side);
            if (rules.Evaluate(s).IsOver || !table.TryGet(s, out _))
            {
                continue;
            }

            Assert.True(rules.MaxPliesFrom(s) <= 15);
            search.NewGame();
            var result = search.Run(s, SearchBudget.Unlimited, TestContext.Current.CancellationToken);
            Assert.True(result.Exact);
            AssertMatchesSolver(table, s, result.Score);
            compared++;
        }
    }

    private static int RowSum(Rules rules, UInt128 hounds)
    {
        int sum = 0;
        for (int sq = 0; sq < rules.Geo.SquareCount; sq++)
        {
            if (((hounds >> sq) & UInt128.One) != UInt128.Zero)
            {
                sum += rules.Geo.Row[sq];
            }
        }

        return sum;
    }

    private static void AssertMatchesSolver(SolverTable table, GameState s, int score)
    {
        Assert.True(Scores.IsProven(score), $"{s}: score {score} is not proven");
        var expected = table.Get(s);
        var actual = new SolvedValue(score > 0, Scores.PliesToEnd(score));
        if (expected != actual)
        {
            Assert.Fail($"{s}: solver {expected.Describe(s.SideToMove)}, search {actual.Describe(s.SideToMove)}");
        }
    }

    private static IEnumerable<GameState> Reachable(Rules rules)
    {
        var start = rules.Initial();
        var seen = new HashSet<GameState> { start };
        var queue = new Queue<GameState>([start]);
        var moves = new Move[Rules.MaxMoves];
        while (queue.Count > 0)
        {
            var s = queue.Dequeue();
            yield return s;
            if (rules.Evaluate(s).IsOver)
            {
                continue;
            }

            int count = rules.GenerateMoves(s, moves);
            for (int i = 0; i < count; i++)
            {
                var child = rules.Apply(s, moves[i]);
                if (seen.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }
    }

    /// <summary>A clock that advances by a fixed step on every reading.</summary>
    private sealed class TickingClock(TimeSpan step) : IClock
    {
        private long _ticks;

        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch + Elapsed;

        public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Add(ref _ticks, step.Ticks));
    }
}
