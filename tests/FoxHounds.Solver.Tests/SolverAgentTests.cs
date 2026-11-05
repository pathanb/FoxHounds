using FoxHounds.Core;

namespace FoxHounds.Solver.Tests;

/// <summary>SolverAgent (spec 10.5): perfect play, tie-breaking and the fox start choice.</summary>
public sealed class SolverAgentTests
{
    private static GameOptions Options(GameState? start = null) => new()
    {
        FoxBudget = SearchBudget.Unlimited,
        HoundsBudget = SearchBudget.Unlimited,
        Start = start,
        Clock = new ManualClock(DateTimeOffset.UnixEpoch),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Solver_against_solver_plays_the_perfect_game(bool adjudicate)
    {
        // Hounds win in 44 from the Classic start; escape adjudication never shortens a hounds win.
        var table = TestTables.Classic(8);
        var rules = TestTables.Rules(8, FoxWinCondition.ReachHomeRow, adjudicate: adjudicate);
        var record = GameRunner.Play(rules, new SolverAgent(table), new SolverAgent(table), Options(), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal(GameResult.HoundsWin, record.Outcome.Result);
        Assert.Equal(EndReason.FoxTrapped, record.Outcome.Reason);
        Assert.Equal(44, record.Plies.Count);
    }

    [Theory]
    [InlineData("8/b4/b8,h8,e7,d6/F", 7)]
    [InlineData("8/f2/f8,h8,e7,d6/F", 15)]
    [InlineData("8/d2/b8,f8,e7,h6/F", 11)]
    public void Labelled_openings_play_out_to_their_label(string notation, int plies)
    {
        var table = TestTables.Classic(8);
        var rules = TestTables.Rules(8, FoxWinCondition.ReachHomeRow);
        var start = rules.FromNotation(notation);
        var record = GameRunner.Play(rules, new SolverAgent(table), new SolverAgent(table), Options(start), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal(GameResult.FoxWins, record.Outcome.Result);
        Assert.Equal(plies, record.Plies.Count);
    }

    [Fact]
    public void Solver_hounds_never_lose_to_a_random_fox_from_the_start()
    {
        var table = TestTables.Classic(8);
        var rules = new Rules(RuleSet.Classic);
        for (int game = 0; game < 100; game++)
        {
            var record = GameRunner.Play(rules, new UniformRandomAgent(), new SolverAgent(table, randomTies: true), Options(), Rng.ForGame(5, game), TestContext.Current.CancellationToken);
            Assert.Equal(GameResult.HoundsWin, record.Outcome.Result);
        }
    }

    [Fact]
    public void Solver_fox_converts_every_fox_won_position_against_random_hounds()
    {
        var table = TestTables.Classic(8);
        var rules = new Rules(RuleSet.Classic);
        var start = rules.FromNotation("8/d2/b8,f8,e7,h6/F");
        for (int game = 0; game < 100; game++)
        {
            var record = GameRunner.Play(rules, new SolverAgent(table), new UniformRandomAgent(), Options(start), Rng.ForGame(6, game), TestContext.Current.CancellationToken);
            Assert.Equal(GameResult.FoxWins, record.Outcome.Result);
        }
    }

    [Fact]
    public void Random_ties_vary_the_game_but_never_its_value()
    {
        var table = TestTables.Classic(8);
        var rules = TestTables.Rules(8, FoxWinCondition.ReachHomeRow);
        var lines = new HashSet<string>();
        for (int game = 0; game < 20; game++)
        {
            var record = GameRunner.Play(rules, new SolverAgent(table, true), new SolverAgent(table, true), Options(), Rng.ForGame(9, game), TestContext.Current.CancellationToken);
            Assert.Equal(44, record.Plies.Count);
            Assert.Equal(GameResult.HoundsWin, record.Outcome.Result);
            lines.Add(string.Join(' ', record.Plies.Select(p => rules.Geo.FormatMove(p.Move))));
        }

        Assert.True(lines.Count > 1, "random ties should produce different perfect games");
    }

    [Fact]
    public void Decision_info_carries_the_proven_score_and_line()
    {
        var table = TestTables.Classic(8);
        var rules = TestTables.Rules(8, FoxWinCondition.ReachHomeRow);
        var agent = new SolverAgent(table);
        agent.NewGame(rules, Side.Fox, new Rng(1));
        var decision = agent.ChooseMove(rules.Initial(), [], SearchBudget.Unlimited, TestContext.Current.CancellationToken);
        Assert.Equal(AgentInfo.SourceSolver, decision.Info.Source);
        Assert.Equal(-(Scores.Win - 44), decision.Info.ScoreFox);
        Assert.Equal("Hounds win in 44", Scores.DescribeFox(decision.Info.ScoreFox!.Value));
        Assert.Equal(44, decision.Info.PrincipalVariation.Count);
        Assert.Equal(decision.Move, decision.Info.PrincipalVariation[0]);
        Assert.Equal("solver", agent.Name);
    }

    [Fact]
    public void Fox_start_choice_prefers_the_best_square_and_breaks_ties_by_order_or_randomly()
    {
        var table = TestTables.Classic(6);
        var rules = new Rules(RuleSet.Classic with { BoardSize = 6, FoxStart = FoxStart.ChooseOnBackRow });
        var agent = new SolverAgent(table);
        agent.NewGame(rules, Side.Fox, new Rng(1));
        Assert.Equal(rules.FoxStartSquares[0], agent.ChooseFoxStart(rules)); // all three starts: fox wins in 25

        var picks = new HashSet<byte>();
        for (int i = 0; i < 50; i++)
        {
            var random = new SolverAgent(table, randomTies: true);
            random.NewGame(rules, Side.Fox, new Rng((ulong)i));
            picks.Add(random.ChooseFoxStart(rules));
        }

        Assert.Equal(rules.FoxStartSquares.Order(), picks.Order());
    }

    [Fact]
    public void A_table_for_another_board_or_win_condition_is_refused()
    {
        var agent = new SolverAgent(TestTables.Classic(6));
        Assert.Throws<ArgumentException>(() => agent.NewGame(new Rules(RuleSet.Classic), Side.Fox, new Rng(1)));
        Assert.Throws<ArgumentException>(() => agent.NewGame(new Rules(RuleSet.Gilith with { BoardSize = 6 }), Side.Fox, new Rng(1)));
    }

    /// <summary>Uniform random plies (Solver.Tests may reference only Solver and Core).</summary>
    private sealed class UniformRandomAgent : IAgent
    {
        private Rules? _rules;
        private Rng? _rng;

        public string Name => "uniform";

        public void NewGame(Rules rules, Side side, Rng rng)
        {
            _rules = rules;
            _rng = rng;
        }

        public byte ChooseFoxStart(Rules rules) => rules.DefaultFoxStart;

        public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct)
        {
            Span<Move> moves = stackalloc Move[Rules.MaxMoves];
            int n = _rules!.GenerateMoves(state, moves);
            return new AgentDecision(moves[_rng!.NextInt(n)], AgentInfo.Random);
        }
    }
}
