namespace FoxHounds.Core.Tests;

/// <summary>GameRunner (spec 7.3): normal play, every forfeit cause, the ply-count invariant and D-23.</summary>
public sealed class GameRunnerTests
{
    private static readonly SearchBudget Budget = SearchBudget.Depth(1);

    private static GameOptions Options(IClock? clock = null, SearchBudget? houndsBudget = null) => new()
    {
        FoxBudget = Budget,
        HoundsBudget = houndsBudget ?? Budget,
        Clock = clock ?? new ManualClock(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero)),
    };

    [Fact]
    public void Random_games_finish_with_a_rules_outcome_and_replay_to_it()
    {
        var rules = new Rules(RuleSet.Classic);
        for (int game = 0; game < 200; game++)
        {
            var record = GameRunner.Play(rules, new UniformAgent(), new UniformAgent(), Options(), Rng.ForGame(77, game), TestContext.Current.CancellationToken);
            Assert.True(record.Outcome.IsOver);
            Assert.NotEqual(EndReason.Forfeit, record.Outcome.Reason);
            Assert.Null(record.Forfeit);
            var positions = record.Replay(rules);
            Assert.Equal(record.Outcome, rules.Evaluate(positions[^1]));
            Assert.All(positions.Take(positions.Count - 1), p => Assert.False(rules.Evaluate(p).IsOver));
            Assert.Equal(Rng.ForGame(77, game).Seed, record.Seed);
            Assert.Equal("uniform-test", record.Fox.Name);
            Assert.All(record.Plies, p => Assert.Equal(AgentInfo.SourceRandom, p.Info!.Source));
        }
    }

    [Fact]
    public void Same_seed_gives_the_same_game()
    {
        var rules = new Rules(RuleSet.Classic);
        var a = GameRunner.Play(rules, new UniformAgent(), new UniformAgent(), Options(), new Rng(9), TestContext.Current.CancellationToken);
        var b = GameRunner.Play(rules, new UniformAgent(), new UniformAgent(), Options(), new Rng(9), TestContext.Current.CancellationToken);
        Assert.Equal(a.Plies.Select(p => p.Move), b.Plies.Select(p => p.Move));
        Assert.Equal(a.Outcome, b.Outcome);
    }

    [Fact]
    public void Each_agent_has_its_own_random_stream()
    {
        // D-23: the fox's draws do not depend on how many draws the hounds' agent makes.
        var rules = new Rules(RuleSet.Classic);
        var fox1 = new UniformAgent();
        var fox2 = new UniformAgent();
        GameRunner.Play(rules, fox1, new UniformAgent(), Options(), new Rng(31), TestContext.Current.CancellationToken);
        GameRunner.Play(rules, fox2, new GreedyFirstMoveAgent(), Options(), new Rng(31), TestContext.Current.CancellationToken);
        int common = Math.Min(fox1.Draws.Count, fox2.Draws.Count);
        Assert.True(common > 0);
        Assert.Equal(fox1.Draws.Take(common), fox2.Draws.Take(common));
    }

    [Fact]
    public void An_agent_that_throws_forfeits()
    {
        var rules = new Rules(RuleSet.Classic);
        var record = GameRunner.Play(rules, new UniformAgent(), new ScriptedAgent(throwOnMove: 2), Options(), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal(new Outcome(GameResult.FoxWins, EndReason.Forfeit), record.Outcome);
        Assert.Equal(Side.Hounds, record.Forfeit!.Side);
        Assert.Equal(ForfeitCause.AgentThrew, record.Forfeit.Cause);
        Assert.Contains("boom", record.Forfeit.Message, StringComparison.Ordinal);
        Assert.Equal(3, record.Plies.Count); // fox, hounds, fox; the hounds threw on their second turn
    }

    [Fact]
    public void An_agent_that_returns_an_illegal_ply_or_nothing_forfeits()
    {
        var rules = new Rules(RuleSet.Classic);
        var illegal = GameRunner.Play(rules, new ScriptedAgent(illegalOnMove: 1), new UniformAgent(), Options(), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal(new Outcome(GameResult.HoundsWin, EndReason.Forfeit), illegal.Outcome);
        Assert.Equal(ForfeitCause.IllegalMove, illegal.Forfeit!.Cause);
        Assert.Empty(illegal.Plies);

        var none = GameRunner.Play(rules, new ScriptedAgent(nullOnMove: 1), new UniformAgent(), Options(), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal(ForfeitCause.IllegalMove, none.Forfeit!.Cause);
    }

    [Fact]
    public void Overrunning_the_hard_time_limit_by_more_than_20_percent_forfeits()
    {
        var rules = new Rules(RuleSet.Classic);
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var budget = new SearchBudget(null, null, null, TimeSpan.FromMilliseconds(1000));
        var withinTolerance = GameRunner.Play(rules, new UniformAgent(), new ScriptedAgent(clock: clock, spend: TimeSpan.FromMilliseconds(1200)),
            Options(clock, budget), new Rng(1), TestContext.Current.CancellationToken);
        Assert.NotEqual(EndReason.Forfeit, withinTolerance.Outcome.Reason);

        var overrun = GameRunner.Play(rules, new UniformAgent(), new ScriptedAgent(clock: clock, spend: TimeSpan.FromMilliseconds(1201)),
            Options(clock, budget), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal(ForfeitCause.TimeLimit, overrun.Forfeit!.Cause);
        Assert.Equal(Side.Hounds, overrun.Forfeit.Side);
    }

    [Fact]
    public void ChooseOnBackRow_asks_the_fox_and_forfeits_a_square_off_the_back_row()
    {
        var rules = new Rules(RuleSet.Classic with { FoxStart = FoxStart.ChooseOnBackRow });
        var good = GameRunner.Play(rules, new ScriptedAgent(foxStart: 28), new UniformAgent(), Options(), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal("a1", rules.Geo.SquareName(good.Start.Fox));
        Assert.Null(good.Forfeit);

        // Square 24 is b2: playable, but not on the fox's back row.
        var bad = GameRunner.Play(rules, new ScriptedAgent(foxStart: 24), new UniformAgent(), Options(), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal(ForfeitCause.InvalidFoxStart, bad.Forfeit!.Cause);
        Assert.Equal(GameResult.HoundsWin, bad.Outcome.Result);
        Assert.Empty(bad.Plies);
    }

    [Fact]
    public void Fixed_start_never_asks_the_fox()
    {
        var rules = new Rules(RuleSet.Classic);
        var record = GameRunner.Play(rules, new ScriptedAgent(foxStart: 0), new UniformAgent(), Options(), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Null(record.Forfeit);
        Assert.Equal(rules.DefaultFoxStart, record.Start.Fox);
    }

    [Fact]
    public void A_given_start_position_is_used_and_validated()
    {
        var rules = new Rules(RuleSet.Classic);
        var start = rules.FromNotation("8/c5/d6,b4,d4,h4/H");
        var record = GameRunner.Play(rules, new UniformAgent(), new UniformAgent(), Options() with { Start = start }, new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal(start, record.Start);
        Assert.Equal(start, record.Replay(rules)[0]);
    }

    [Fact]
    public void The_agent_sees_the_history_and_its_budget()
    {
        var rules = new Rules(RuleSet.Classic);
        var hounds = new ScriptedAgent();
        var budget = SearchBudget.Nodes(1234);
        var record = GameRunner.Play(rules, new UniformAgent(), hounds, Options(houndsBudget: budget), new Rng(2), TestContext.Current.CancellationToken);
        Assert.All(hounds.SeenBudgets, b => Assert.Equal(budget, b));
        // On its k-th turn the hounds' agent has seen 2k − 1 plies (fox first).
        Assert.Equal(Enumerable.Range(1, hounds.SeenHistoryLengths.Count).Select(k => (2 * k) - 1), hounds.SeenHistoryLengths);
        Assert.True(record.Plies.Count >= hounds.SeenHistoryLengths.Count);
    }

    [Fact]
    public void Cancellation_stops_the_game_with_OperationCanceledException()
    {
        var rules = new Rules(RuleSet.Classic);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            GameRunner.Play(rules, new UniformAgent(), new UniformAgent(), Options(), new Rng(1), cts.Token));
    }

    [Fact]
    public void The_same_agent_instance_cannot_play_both_sides()
    {
        var rules = new Rules(RuleSet.Classic);
        var agent = new UniformAgent();
        Assert.Throws<ArgumentException>(() =>
            GameRunner.Play(rules, agent, agent, Options(), new Rng(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_record_with_an_illegal_ply_fails_to_replay()
    {
        var rules = new Rules(RuleSet.Classic);
        var record = GameRunner.Play(rules, new UniformAgent(), new UniformAgent(), Options(), new Rng(4), TestContext.Current.CancellationToken);
        var broken = record with { Plies = [.. record.Plies.Take(3), new PlyRecord(new Move(0, 31), null)] };
        Assert.Throws<IllegalMoveException>(() => broken.Replay(rules));
    }

    [Fact]
    public void Throwing_in_NewGame_or_ChooseFoxStart_forfeits_before_the_first_ply()
    {
        var rules = new Rules(RuleSet.Classic with { FoxStart = FoxStart.ChooseOnBackRow });
        var foxNewGame = GameRunner.Play(rules, new FaultyAgent(Fault.NewGame), new UniformAgent(), Options(), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal((Side.Fox, ForfeitCause.AgentThrew), (foxNewGame.Forfeit!.Side, foxNewGame.Forfeit.Cause));
        Assert.Equal(rules.Initial(), foxNewGame.Start);

        var houndsNewGame = GameRunner.Play(rules, new UniformAgent(), new FaultyAgent(Fault.NewGame), Options(), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal((Side.Hounds, ForfeitCause.AgentThrew), (houndsNewGame.Forfeit!.Side, houndsNewGame.Forfeit.Cause));
        Assert.Equal(GameResult.FoxWins, houndsNewGame.Outcome.Result);

        var foxStart = GameRunner.Play(rules, new FaultyAgent(Fault.FoxStart), new UniformAgent(), Options(), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal((Side.Fox, ForfeitCause.AgentThrew), (foxStart.Forfeit!.Side, foxStart.Forfeit.Cause));
        Assert.Contains("fox start", foxStart.Forfeit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelling_during_a_decision_is_not_a_forfeit()
    {
        var rules = new Rules(RuleSet.Classic);
        using var cts = new CancellationTokenSource();
        var agent = new FaultyAgent(Fault.CancelDuringMove, cts);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            GameRunner.Play(rules, agent, new UniformAgent(), Options(), new Rng(1), cts.Token));
    }

    [Fact]
    public void An_agent_throwing_OperationCanceledException_on_its_own_is_a_forfeit()
    {
        var rules = new Rules(RuleSet.Classic);
        var record = GameRunner.Play(rules, new FaultyAgent(Fault.SpuriousCancel), new UniformAgent(), Options(), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal(ForfeitCause.AgentThrew, record.Forfeit!.Cause);
    }

    [Fact]
    public void An_off_board_ply_is_described_by_its_raw_squares()
    {
        var rules = new Rules(RuleSet.Classic);
        var record = GameRunner.Play(rules, new FaultyAgent(Fault.OffBoardMove), new UniformAgent(), Options(), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal(ForfeitCause.IllegalMove, record.Forfeit!.Cause);
        Assert.Contains("(200, 201)", record.Forfeit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Players_can_be_named_explicitly()
    {
        var rules = new Rules(RuleSet.Classic);
        var options = Options() with { FoxPlayer = PlayerInfo.Human, HoundsPlayer = new PlayerInfo(PlayerInfo.AgentKind, "x", "{}") };
        var record = GameRunner.Play(rules, new UniformAgent(), new UniformAgent(), options, new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal(PlayerInfo.HumanKind, record.Fox.Kind);
        Assert.Equal("{}", record.Hounds.SpecJson);
    }

    private enum Fault
    {
        NewGame,
        FoxStart,
        CancelDuringMove,
        SpuriousCancel,
        OffBoardMove,
    }

    /// <summary>Fails in one specific way.</summary>
    private sealed class FaultyAgent(Fault fault, CancellationTokenSource? cts = null) : IAgent
    {
        public string Name => "faulty";

        public void NewGame(Rules rules, Side side, Rng rng)
        {
            if (fault == Fault.NewGame)
            {
                throw new InvalidOperationException("new game");
            }
        }

        public byte ChooseFoxStart(Rules rules) =>
            fault == Fault.FoxStart ? throw new InvalidOperationException("fox start") : rules.DefaultFoxStart;

        public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct)
        {
            switch (fault)
            {
                case Fault.CancelDuringMove:
                    cts!.Cancel();
                    ct.ThrowIfCancellationRequested();
                    break;
                case Fault.SpuriousCancel:
                    throw new OperationCanceledException("not ours");
                case Fault.OffBoardMove:
                    return new AgentDecision(new Move(200, 201), AgentInfo.Random);
            }

            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>Always plays the first generated ply.</summary>
    private sealed class GreedyFirstMoveAgent : IAgent
    {
        private Rules? _rules;

        public string Name => "first-move";

        public void NewGame(Rules rules, Side side, Rng rng) => _rules = rules;

        public byte ChooseFoxStart(Rules rules) => rules.DefaultFoxStart;

        public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct)
        {
            Span<Move> moves = stackalloc Move[Rules.MaxMoves];
            _rules!.GenerateMoves(state, moves);
            return new AgentDecision(moves[0], AgentInfo.Random);
        }
    }

    /// <summary>Plays the first generated ply, misbehaving on request.</summary>
    private sealed class ScriptedAgent(
        int throwOnMove = 0,
        int illegalOnMove = 0,
        int nullOnMove = 0,
        byte foxStart = 255,
        ManualClock? clock = null,
        TimeSpan spend = default) : IAgent
    {
        private Rules? _rules;
        private int _turn;

        public List<SearchBudget> SeenBudgets { get; } = [];

        public List<int> SeenHistoryLengths { get; } = [];

        public string Name => "scripted";

        public void NewGame(Rules rules, Side side, Rng rng) => _rules = rules;

        public byte ChooseFoxStart(Rules rules) => foxStart;

        public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct)
        {
            _turn++;
            SeenBudgets.Add(budget);
            SeenHistoryLengths.Add(history.Count);
            clock?.Advance(spend);
            if (_turn == throwOnMove)
            {
                throw new InvalidOperationException("boom");
            }

            if (_turn == nullOnMove)
            {
                return null!;
            }

            if (_turn == illegalOnMove)
            {
                return new AgentDecision(new Move(state.Fox, state.Fox), AgentInfo.Random);
            }

            Span<Move> moves = stackalloc Move[Rules.MaxMoves];
            _rules!.GenerateMoves(state, moves);
            return new AgentDecision(moves[0], AgentInfo.Random);
        }
    }
}
