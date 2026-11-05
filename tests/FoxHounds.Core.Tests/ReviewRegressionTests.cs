namespace FoxHounds.Core.Tests;

/// <summary>Regression tests for the independent M1 review: each one failed (or was missing) before its fix.</summary>
public sealed class ReviewRegressionTests
{
    private static GameOptions Options(GameState? start = null, SearchBudget? houndsBudget = null) => new()
    {
        FoxBudget = SearchBudget.Unlimited,
        HoundsBudget = houndsBudget ?? SearchBudget.Unlimited,
        Start = start,
        Clock = new ManualClock(DateTimeOffset.UnixEpoch),
    };

    [Fact]
    public void ToNotation_refuses_a_position_of_another_board_size()
    {
        // Finding 1: an 8×8 Rules used to write a 9×9 position as a different, valid-looking 8×8 position.
        var rules8 = new Rules(RuleSet.Classic);
        var nine = new Rules(RuleSet.Classic with { BoardSize = 9 }).FromNotation("9/c6/b9,d9,f9,h9/H");
        Assert.Throws<ArgumentException>(() => rules8.ToNotation(nine));
    }

    [Fact]
    public void An_invariant_violation_inside_an_agent_propagates_instead_of_becoming_a_forfeit()
    {
        // Finding 2: a detected rules bug must fail the run (spec 7.3), not appear as an ordinary forfeit.
        var rules = new Rules(RuleSet.Classic);
        Assert.Throws<InvariantViolationException>(() =>
            GameRunner.Play(rules, new BrokenAgent(), new UniformAgent(), Options(), new Rng(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_given_start_is_recorded_even_when_an_agent_fails_in_NewGame()
    {
        // Finding 3: the record used to show the default initial position.
        var rules = new Rules(RuleSet.Classic);
        var start = rules.FromNotation("8/c5/d6,b4,d4,h4/H");
        var record = GameRunner.Play(rules, new UniformAgent(), new ThrowingNewGameAgent(), Options(start), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal(ForfeitCause.AgentThrew, record.Forfeit!.Cause);
        Assert.Equal(start, record.Start);
    }

    [Fact]
    public void Extreme_hard_time_limits_neither_overflow_nor_forfeit_and_negative_limits_are_rejected()
    {
        // Finding 4.
        var rules = new Rules(RuleSet.Classic);
        var budget = new SearchBudget(null, null, null, TimeSpan.MaxValue);
        var record = GameRunner.Play(rules, new UniformAgent(), new UniformAgent(), Options(houndsBudget: budget), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Null(record.Forfeit);

        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchBudget(null, null, null, Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchBudget(null, null, TimeSpan.FromSeconds(-1), null));
        Assert.Throws<ArgumentOutOfRangeException>(() => SearchBudget.Nodes(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SearchBudget.Depth(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SearchBudget.Depth(3) with { MaxNodes = -5 });
        Assert.Equal(SearchBudget.Depth(3), new SearchBudget(3, null, null, null));
    }

    [Fact]
    public void Replay_refuses_rules_for_another_ruleset()
    {
        // Finding 5: replaying an adjudication-off game with Classic rules gave wrong positions or bogus errors.
        var off = new Rules(RuleSet.Classic with { AdjudicateEscape = false });
        var record = GameRunner.Play(off, new UniformAgent(), new UniformAgent(), Options(), new Rng(3), TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentException>(() => record.Replay(new Rules(RuleSet.Classic)));
        Assert.NotEmpty(record.Replay(new Rules(RuleSet.Classic with { AdjudicateEscape = false })));
    }

    [Fact]
    public void Replay_validates_the_stored_start_position()
    {
        var rules = new Rules(RuleSet.Classic);
        var record = GameRunner.Play(rules, new UniformAgent(), new UniformAgent(), Options(), new Rng(3), TestContext.Current.CancellationToken);
        var tampered = record with { Start = new GameState(rules.Geo.RowMask[0], 30, Side.Fox, 8, 999) };
        Assert.Throws<InvalidPositionException>(() => tampered.Replay(rules));
    }

    [Fact]
    public void GetHashCode_is_the_folded_Zobrist_hash_and_the_same_in_every_process()
    {
        var rules = new Rules(RuleSet.Classic);
        var s = rules.Initial();
        Assert.Equal((int)s.Hash ^ (int)(s.Hash >> 32), s.GetHashCode());
    }

    [Fact]
    public void Agents_get_the_first_and_second_split_of_the_game_generator()
    {
        // Gap 6: D-23 says the fox's stream is the first Split, the hounds' the second.
        var rules = new Rules(RuleSet.Classic);
        var fox = new SeedRecordingAgent();
        var hounds = new SeedRecordingAgent();
        GameRunner.Play(rules, fox, hounds, Options(), new Rng(123), TestContext.Current.CancellationToken);
        var expected = new Rng(123);
        Assert.Equal(expected.Split().Seed, fox.Seed);
        Assert.Equal(expected.Split().Seed, hounds.Seed);
    }

    [Fact]
    public void Zobrist_draw_order_is_hound_keys_then_fox_keys_then_side_key()
    {
        // Gap 7: values from reference/core_vectors.txt (the prototype's SplitMix64 stream).
        var keys = Geometry.For(8).Zobrist;
        Assert.Equal(0xFB5FF89C958322BCUL, keys.FoxKey[0]);
        Assert.Equal(0x02453A2E2B9627ABUL, keys.FoxKey[31]);
        Assert.Equal(0xEF40430CCA1BECBCUL, keys.SideKey);
    }

    [Fact]
    public void An_invalid_given_start_is_rejected_and_a_finished_one_ends_at_once()
    {
        // Gap 8.
        var rules = new Rules(RuleSet.Classic);
        var invalid = new GameState(rules.Geo.RowMask[0], 30, Side.Fox, 8, 42);
        Assert.Throws<InvalidPositionException>(() =>
            GameRunner.Play(rules, new UniformAgent(), new UniformAgent(), Options(invalid), new Rng(1), TestContext.Current.CancellationToken));

        var trapped = rules.FromNotation("8/a1/b2,b8,d8,f8/F");
        var record = GameRunner.Play(rules, new UniformAgent(), new UniformAgent(), Options(trapped), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Empty(record.Plies);
        Assert.Equal(new Outcome(GameResult.HoundsWin, EndReason.FoxTrapped), record.Outcome);
    }

    [Theory]
    [InlineData(4, 13)]
    [InlineData(6, 31)]
    [InlineData(8, 57)]
    public void The_runner_accepts_a_game_of_exactly_MaxPlies(int n, int maxPlies)
    {
        // Gap 8: agents that always prolong the game play the longest possible game without tripping the invariant.
        var rules = new Rules(RuleSet.Classic with { BoardSize = n, AdjudicateEscape = false });
        var memo = new Dictionary<GameState, int>();
        var record = GameRunner.Play(rules, new LongestLineAgent(memo), new LongestLineAgent(memo), Options(), new Rng(1), TestContext.Current.CancellationToken);
        Assert.Equal(maxPlies, record.Plies.Count);
        Assert.Equal(rules.MaxPlies, record.Plies.Count);
    }

    [Fact]
    public void NextDouble_never_returns_one()
    {
        // Gap 9: the old range test admitted 1.0.
        var rng = new Rng(3);
        for (int i = 0; i < 100_000; i++)
        {
            double x = rng.NextDouble();
            Assert.True(x is >= 0.0 and < 1.0, $"draw {i}: {x}");
        }
    }

    private sealed class BrokenAgent : IAgent
    {
        public string Name => "broken";

        public void NewGame(Rules rules, Side side, Rng rng)
        {
        }

        public byte ChooseFoxStart(Rules rules) => rules.DefaultFoxStart;

        public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct) =>
            throw new InvariantViolationException("rules bug detected inside the agent");
    }

    private sealed class ThrowingNewGameAgent : IAgent
    {
        public string Name => "throws-in-new-game";

        public void NewGame(Rules rules, Side side, Rng rng) => throw new InvalidOperationException("cannot start");

        public byte ChooseFoxStart(Rules rules) => rules.DefaultFoxStart;

        public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct) =>
            throw new InvalidOperationException("unreachable");
    }

    private sealed class SeedRecordingAgent : IAgent
    {
        private readonly UniformAgent _inner = new();

        public ulong Seed { get; private set; }

        public string Name => "seed-recorder";

        public void NewGame(Rules rules, Side side, Rng rng)
        {
            Seed = rng.Seed;
            _inner.NewGame(rules, side, rng);
        }

        public byte ChooseFoxStart(Rules rules) => _inner.ChooseFoxStart(rules);

        public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct) =>
            _inner.ChooseMove(state, history, budget, ct);
    }

    /// <summary>Plays the ply that leaves the longest possible remaining game (memoised over positions).</summary>
    private sealed class LongestLineAgent(Dictionary<GameState, int> memo) : IAgent
    {
        private Rules? _rules;

        public string Name => "longest-line";

        public void NewGame(Rules rules, Side side, Rng rng) => _rules = rules;

        public byte ChooseFoxStart(Rules rules) => rules.DefaultFoxStart;

        public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct)
        {
            var rules = _rules!;
            var moves = new Move[Rules.MaxMoves];
            int count = rules.GenerateMoves(state, moves);
            Move best = moves[0];
            int bestLength = -1;
            for (int i = 0; i < count; i++)
            {
                int length = Longest(rules, rules.Apply(state, moves[i]));
                if (length > bestLength)
                {
                    bestLength = length;
                    best = moves[i];
                }
            }

            return new AgentDecision(best, AgentInfo.Random);
        }

        private int Longest(Rules rules, GameState s)
        {
            if (memo.TryGetValue(s, out int known))
            {
                return known;
            }

            int best = 0;
            if (!rules.Evaluate(s).IsOver)
            {
                var moves = new Move[Rules.MaxMoves];
                int count = rules.GenerateMoves(s, moves);
                for (int i = 0; i < count; i++)
                {
                    best = Math.Max(best, 1 + Longest(rules, rules.Apply(s, moves[i])));
                }
            }

            memo[s] = best;
            return best;
        }
    }
}
