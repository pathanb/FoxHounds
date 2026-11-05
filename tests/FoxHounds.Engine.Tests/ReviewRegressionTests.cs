using FoxHounds.Core;
using FoxHounds.Engine.Evaluation;
using FoxHounds.Solver;

namespace FoxHounds.Engine.Tests;

/// <summary>Regression tests for the independent M3 review: each one failed (or was missing) before its fix.</summary>
public sealed class ReviewRegressionTests
{
    private static readonly ManualClock Clock = new(DateTimeOffset.UnixEpoch);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(5, false, 40, 0xD105UL)]
    [InlineData(6, false, 40, 0xD106UL)]
    [InlineData(7, false, 25, 0xD107UL)]
    [InlineData(6, true, 40, 0xD206UL)]
    public void Tables_kept_across_a_game_never_give_a_wrong_exact_result(int n, bool gilith, int games, ulong seed)
    {
        // Finding 1: T-ENG-03/04 clear the table before every position. Within one search every node is the same
        // number of plies from the root, so a broken ToTable/FromTable cancels out and no proof can outgrow the
        // depth. Here each side keeps its table for a whole game, as MinimaxAgent does, with depth budgets and
        // node budgets that usually abort mid-iteration (finding 6: an aborted search must not store values).
        var config = (gilith ? RuleSet.Gilith : RuleSet.Classic) with { BoardSize = n, AdjudicateEscape = false };
        var rules = new Rules(config);
        var table = SolverTable.Build(rules, null, Ct);
        var evaluator = WeightFile.LinV0.CreateEvaluator(n);
        var fox = new Search(rules, evaluator, new SearchOptions { TtMegabytes = 1 }, Clock);
        var hounds = new Search(rules, evaluator, new SearchOptions { TtMegabytes = 1 }, Clock);
        var rng = new Rng(seed);
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        int exact = 0;
        int carried = 0;
        for (int game = 0; game < games; game++)
        {
            fox.NewGame();
            hounds.NewGame();
            var s = rules.Initial(rules.FoxStartSquares[rng.NextInt(rules.FoxStartSquares.Count)]);
            while (!rules.Evaluate(s).IsOver)
            {
                var search = s.SideToMove == Side.Fox ? fox : hounds;
                var budget = rng.NextInt(2) == 0 ? SearchBudget.Depth(1 + rng.NextInt(10)) : SearchBudget.Nodes(100 + rng.NextInt(4000));
                var result = search.Run(s, budget, Ct);
                var solved = table.Get(s);
                string where = $"{rules.ToNotation(s)} {budget}";
                Assert.True(!result.Exact || Scores.IsProven(result.Score), $"{where}: exact but not proven");
                if (Scores.IsProven(result.Score))
                {
                    Assert.True(solved.SideToMoveWins == result.Score > 0, $"{where}: wrong winner, solver {solved.Describe(s.SideToMove)}");
                    int plies = Scores.PliesToEnd(result.Score);
                    if (result.Exact)
                    {
                        Assert.True(solved.Plies == plies, $"{where}: exact {plies} plies, solver {solved.Describe(s.SideToMove)}");
                        AssertKeepsValue(rules, table, s, result.Move, solved, where);
                        exact++;
                    }
                    else
                    {
                        Assert.True(plies >= solved.Plies, $"{where}: {plies} plies is shorter than the solver's {solved.Plies}");
                        carried++;
                    }
                }

                int count = rules.GenerateMoves(s, moves);
                s = rules.Apply(s, rng.NextInt(5) == 0 ? moves[rng.NextInt(count)] : result.Move);
            }
        }

        Assert.True(exact > 100, $"only {exact} exact results");
        Assert.True(carried > 0, "no proof carried over from an earlier search: the test does not exercise the table");
    }

    [Fact]
    public void RandomAmongBest_reports_the_score_and_line_of_the_move_it_plays()
    {
        // Finding 2: the score and line were the best move's even when another move was played.
        var rules = new Rules(RuleSet.Classic);
        var lin = WeightFile.LinV0.CreateEvaluator(8);
        var random = new Search(rules, lin, new SearchOptions { RootSelection = RootSelection.RandomAmongBest, Margin = 40, TtMegabytes = 0 }, Clock);
        var plain = new Search(rules, lin, new SearchOptions { TtMegabytes = 0 }, Clock);
        var rng = new Rng(0xF2);
        int differed = 0;
        for (int i = 0; i < 150; i++)
        {
            var s = Positions.RandomOngoing(rules, rng);
            var result = random.Run(s, SearchBudget.Depth(4), Ct, rng.Split());
            var line = random.PrincipalVariation.ToArray();
            Assert.Equal(result.Move, line[0]);
            if (Scores.IsProven(result.Score))
            {
                continue;
            }

            // The table is off, so the played move's value is minus the child's depth-3 value, and the line is
            // full length with the root score at its leaf.
            var child = rules.Apply(s, result.Move);
            Assert.Equal(-plain.Run(child, SearchBudget.Depth(result.Depth - 1), Ct).Score, result.Score);
            AssertLineEndsAtScore(rules, lin, s, line, result.Depth, result.Score);
            if (plain.Run(s, SearchBudget.Depth(4), Ct).Move != result.Move)
            {
                differed++;
            }
        }

        Assert.True(differed >= 10, $"only {differed} decisions played a move other than the best: the test is weak");

        // The reviewer's other case: zero evaluator, margin 0, start position.
        var zero = new Search(rules, ZeroEvaluator.Instance, new SearchOptions { RootSelection = RootSelection.RandomAmongBest }, Clock);
        for (ulong seed = 0; seed < 40; seed++)
        {
            var result = zero.Run(rules.Initial(), SearchBudget.Depth(3), Ct, new Rng(seed));
            Assert.Equal(result.Move, zero.PrincipalVariation[0]);
        }
    }

    [Fact]
    public void With_the_table_off_the_line_is_full_length_and_its_leaf_has_the_root_score()
    {
        // Finding 6: a line one move short passed, because the old test only checked that the line was legal.
        foreach (int n in new[] { 6, 8, 10 })
        {
            var rules = new Rules(RuleSet.Classic with { BoardSize = n });
            var lin = WeightFile.LinV0.CreateEvaluator(n);
            var search = new Search(rules, lin, new SearchOptions { TtMegabytes = 0 }, Clock);
            var rng = new Rng((ulong)n);
            int checkedLines = 0;
            for (int i = 0; i < 60; i++)
            {
                var s = Positions.RandomOngoing(rules, rng);
                var result = search.Run(s, SearchBudget.Depth(5), Ct);
                if (!Scores.IsProven(result.Score))
                {
                    AssertLineEndsAtScore(rules, lin, s, search.PrincipalVariation.ToArray(), result.Depth, result.Score);
                    checkedLines++;
                }
            }

            Assert.True(checkedLines > 20, $"n = {n}: only {checkedLines} heuristic results");
        }
    }

    [Fact]
    public void RandomAmongBest_with_a_huge_margin_never_plays_a_proven_loss()
    {
        // Finding 3: the old position had no proven-losing root move, so the filter was never exercised.
        var rules = new Rules(RuleSet.Classic);
        var s = rules.FromNotation("8/f2/a7,e7,g3,d2/F");
        var check = new Search(rules, ZeroEvaluator.Instance, new SearchOptions { TtMegabytes = 1 }, Clock);
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        int count = rules.GenerateMoves(s, moves);
        int losing = 0;
        for (int i = 0; i < count; i++)
        {
            if (LosesForTheMover(rules, check, s, moves[i]))
            {
                losing++;
            }
        }

        Assert.True(losing > 0 && losing < count, $"{losing} of {count} moves lose: the position does not test the filter");

        var trap = new Search(rules, ZeroEvaluator.Instance, new SearchOptions { RootSelection = RootSelection.RandomAmongBest, Margin = 3 * Scores.Win }, Clock);
        for (ulong seed = 0; seed < 40; seed++)
        {
            var result = trap.Run(s, SearchBudget.Depth(6), Ct, new Rng(seed));
            Assert.False(LosesForTheMover(rules, check, s, result.Move), $"seed {seed} played the proven loss {rules.Geo.FormatMove(result.Move)}");
        }
    }

    [Fact]
    public void A_proof_that_is_not_exact_is_described_as_an_upper_bound_and_agents_say_which_they_give()
    {
        // Finding 4: a proof carried over from a deeper search was shown as an exact distance.
        Assert.Equal("Fox wins in <= 9", Scores.DescribeFox(Scores.Win - 9, exact: false));
        Assert.Equal("Hounds win in <= 4", Scores.DescribeFox(-(Scores.Win - 4), exact: false));
        Assert.Equal("Fox wins in 9", Scores.DescribeFox(Scores.Win - 9));
        Assert.Equal("+12", Scores.DescribeFox(12, exact: false));

        var rules = new Rules(RuleSet.Classic with { BoardSize = 4, AdjudicateEscape = false });
        var agent = new MinimaxAgent(WeightFile.LinV0.CreateEvaluator(4), new SearchOptions { TtMegabytes = 1 }, Clock);
        agent.NewGame(rules, Side.Fox, new Rng(1));
        var start = rules.Initial();
        var deep = agent.ChooseMove(start, [], SearchBudget.Unlimited, Ct);
        Assert.True(deep.Info.Exact);
        var shallow = agent.ChooseMove(start, [], SearchBudget.Depth(1), Ct);
        Assert.False(shallow.Info.Exact); // depth 1 cannot hold a 4x4 proof
    }

    [Theory]
    [InlineData("""{ "name": "x", "evaluator": "linear", "weights": { "GoalReachable": 250, "GoalReachable": 0 } }""")]
    [InlineData("""{ "name": "x", "name": "y", "evaluator": "linear", "weights": { "FoxToMove": 1 } }""")]
    [InlineData("""{ "name": "x", "evaluator": "linear", "boardsize": 10, "weights": { "FoxToMove": 1 } }""")]
    [InlineData("""{ "name": "x", "Evaluator": "linear", "evaluator": "linear", "weights": { "FoxToMove": 1 } }""")]
    public void Weight_files_with_duplicate_keys_or_misspelt_fields_are_rejected(string json)
    {
        // Finding 5: the last duplicate silently won, and "boardsize" silently dropped the board-size guard.
        Assert.Throws<InvalidDataException>(() => WeightFile.Parse(json));
    }

    [Fact]
    public void Unknown_weight_file_fields_are_still_ignored()
    {
        var file = WeightFile.Parse("""{ "name": "x", "evaluator": "linear", "comment": "spec 14: ignored", "weights": { "FoxToMove": 1 } }""");
        Assert.Equal("x", file.Name);
    }

    [Fact]
    public void Tapered_scores_are_truncated_toward_zero()
    {
        // Finding 6: the old middle case was 20.4, which rounding also gives.
        var rules = new Rules(RuleSet.Classic);
        var mid = rules.FromNotation("8/d4/a7,c7,e7,f6/F"); // phase 210, FoxAdvance 3, FoxToMove 1
        int[] early = [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var up = new TaperedEvaluator("t", early, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 103]);
        Assert.Equal(20, up.EvaluateFox(mid));   // (630 + 46·103) / 256 = 5368 / 256 = 20.97
        var down = new TaperedEvaluator("t", early, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -107]);
        Assert.Equal(-16, down.EvaluateFox(mid)); // (630 − 46·107) / 256 = −4292 / 256 = −16.77
    }

    private static bool LosesForTheMover(Rules rules, Search check, in GameState s, Move move)
    {
        var child = rules.Apply(s, move);
        var outcome = rules.Evaluate(child);
        if (outcome.IsOver)
        {
            return outcome.Winner != s.SideToMove;
        }

        check.NewGame();
        int v = check.Run(child, SearchBudget.Depth(5), Ct).Score; // for the opponent
        return Scores.IsProven(v) && v > 0;
    }

    private static void AssertKeepsValue(Rules rules, SolverTable table, in GameState s, Move move, SolvedValue solved, string where)
    {
        var child = rules.Apply(s, move);
        var winner = solved.Winner(s.SideToMove);
        var outcome = rules.Evaluate(child);
        if (outcome.IsOver)
        {
            Assert.True(outcome.Winner == winner && solved.Plies == 1, $"{where}: {rules.Geo.FormatMove(move)} ends the game wrongly");
            return;
        }

        var next = table.Get(child);
        Assert.True(next.Winner(child.SideToMove) == winner && next.Plies == solved.Plies - 1,
            $"{where}: {rules.Geo.FormatMove(move)} leads to {next.Describe(child.SideToMove)}, not keeping {solved.Describe(s.SideToMove)}");
    }

    private static void AssertLineEndsAtScore(Rules rules, IEvaluator evaluator, in GameState s, Move[] line, int depth, int score)
    {
        Assert.Equal(depth, line.Length);
        var p = s;
        foreach (var m in line)
        {
            p = rules.ApplyChecked(p, m);
        }

        int fox = evaluator.EvaluateFox(p);
        int leaf = p.SideToMove == Side.Fox ? fox : -fox;
        Assert.Equal(score, depth % 2 == 0 ? leaf : -leaf);
    }
}
