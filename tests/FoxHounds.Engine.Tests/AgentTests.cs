using FoxHounds.Core;
using FoxHounds.Engine.Evaluation;

namespace FoxHounds.Engine.Tests;

/// <summary>MinimaxAgent and EpsilonAgent (spec 7.2).</summary>
public sealed class AgentTests
{
    private static GameOptions Options(SearchBudget fox, SearchBudget hounds) => new()
    {
        FoxBudget = fox,
        HoundsBudget = hounds,
        Clock = new ManualClock(DateTimeOffset.UnixEpoch),
    };

    private static MinimaxAgent Minimax(SearchOptions? options = null) =>
        new(WeightFile.LinV0.CreateEvaluator(8), (options ?? new SearchOptions()) with { TtMegabytes = 4 });

    [Fact]
    public void Minimax_hounds_beat_a_random_fox_and_report_search_info()
    {
        var rules = new Rules(RuleSet.Classic);
        int houndWins = 0;
        for (int game = 0; game < 30; game++)
        {
            var record = GameRunner.Play(rules, new RandomAgent(), Minimax(), Options(SearchBudget.Unlimited, SearchBudget.Depth(6)),
                Rng.ForGame(2026, game), TestContext.Current.CancellationToken);
            Assert.Null(record.Forfeit);
            houndWins += record.Outcome.Result == GameResult.HoundsWin ? 1 : 0;
            foreach (var ply in record.Plies.Where(p => p.Info!.Source == AgentInfo.SourceSearch))
            {
                Assert.NotNull(ply.Info!.ScoreFox);
                Assert.InRange(ply.Info.DepthReached, 1, 6);
                Assert.Equal(ply.Move, ply.Info.PrincipalVariation[0]);
            }
        }

        Assert.True(houndWins >= 24, $"minimax hounds won only {houndWins} of 30 against a random fox");
    }

    [Fact]
    public void Same_seed_and_budget_give_the_same_game()
    {
        var rules = new Rules(RuleSet.Classic);
        string Play() => string.Join(' ', GameRunner.Play(rules, Minimax(), Minimax(new SearchOptions { RootSelection = RootSelection.RandomAmongBest }),
            Options(SearchBudget.Nodes(20_000), SearchBudget.Depth(5)), new Rng(42), TestContext.Current.CancellationToken).Plies.Select(p => p.Move));
        Assert.Equal(Play(), Play());
    }

    [Fact]
    public void Agent_reuses_its_search_between_games_of_the_same_rules()
    {
        var agent = Minimax();
        var rules = new Rules(RuleSet.Classic);
        agent.NewGame(rules, Side.Hounds, new Rng(1));
        var first = agent.CurrentSearch;
        agent.NewGame(rules, Side.Hounds, new Rng(2));
        Assert.Same(first, agent.CurrentSearch);
        agent.NewGame(new Rules(RuleSet.Classic with { BoardSize = 6 }), Side.Hounds, new Rng(3));
        Assert.NotSame(first, agent.CurrentSearch);
        Assert.StartsWith("minimax[eval=lin-v0]", agent.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Fox_start_choice_and_cancellation()
    {
        var rules = new Rules(RuleSet.Classic with { BoardSize = 6, FoxStart = FoxStart.ChooseOnBackRow });
        var agent = new MinimaxAgent(WeightFile.LinV0.CreateEvaluator(6), new SearchOptions { TtMegabytes = 1 });
        agent.NewGame(rules, Side.Fox, new Rng(1));
        Assert.Contains(agent.ChooseFoxStart(rules), rules.FoxStartSquares);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => agent.ChooseMove(rules.Initial(), [], SearchBudget.Depth(4), cts.Token));
        Assert.Throws<InvalidOperationException>(() => Minimax().ChooseMove(rules.Initial(), [], SearchBudget.Depth(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Epsilon_zero_plays_like_the_inner_agent_and_epsilon_one_plays_randomly()
    {
        var rules = new Rules(RuleSet.Classic);
        var options = Options(SearchBudget.Depth(3), SearchBudget.Depth(3));
        var plain = GameRunner.Play(rules, Minimax(), Minimax(), options, new Rng(8), TestContext.Current.CancellationToken);
        var wrapped = GameRunner.Play(rules, new EpsilonAgent(Minimax(), 0), new EpsilonAgent(Minimax(), 0), options, new Rng(8), TestContext.Current.CancellationToken);
        Assert.Equal(plain.Plies.Select(p => p.Move), wrapped.Plies.Select(p => p.Move));

        var random = GameRunner.Play(rules, new EpsilonAgent(Minimax(), 1), new RandomAgent(), options, new Rng(8), TestContext.Current.CancellationToken);
        Assert.All(random.Plies, p => Assert.Equal(AgentInfo.SourceRandom, p.Info!.Source));
        Assert.Equal("minimax[eval=lin-v0]+eps=0.25", new EpsilonAgent(Minimax(), 0.25).Name);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EpsilonAgent(Minimax(), 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EpsilonAgent(Minimax(), double.NaN));
    }
}
