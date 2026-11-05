using FoxHounds.Core;

namespace FoxHounds.Engine.Tests;

public sealed class RandomAgentTests
{
    [Fact]
    public void Plays_only_legal_plies_and_finishes_games()
    {
        var rules = new Rules(RuleSet.Classic with { FoxStart = FoxStart.ChooseOnBackRow });
        var options = new GameOptions { FoxBudget = SearchBudget.Unlimited, HoundsBudget = SearchBudget.Unlimited };
        for (int game = 0; game < 500; game++)
        {
            var record = GameRunner.Play(rules, new RandomAgent(), new RandomAgent(), options, Rng.ForGame(11, game), TestContext.Current.CancellationToken);
            Assert.Null(record.Forfeit);
            Assert.True(record.Outcome.IsOver);
            Assert.Contains(record.Start.Fox, rules.FoxStartSquares);
            Assert.Equal(record.Outcome, rules.Evaluate(record.Replay(rules)[^1]));
        }
    }

    [Fact]
    public void Choices_are_uniform_over_legal_plies()
    {
        var rules = new Rules(RuleSet.Classic);
        var agent = new RandomAgent();
        agent.NewGame(rules, Side.Hounds, new Rng(8));
        var s = rules.Apply(rules.Initial(), new Move(30, 26)); // 7 hound plies available
        var counts = new Dictionary<Move, int>();
        for (int i = 0; i < 70_000; i++)
        {
            var m = agent.ChooseMove(s, [], SearchBudget.Unlimited, TestContext.Current.CancellationToken).Move;
            counts[m] = counts.GetValueOrDefault(m) + 1;
        }

        Assert.Equal(7, counts.Count);
        Assert.All(counts.Values, c => Assert.InRange(c, 9_500, 10_500));
    }

    [Fact]
    public void Draws_its_plies_with_NextInt_exactly_as_the_reference()
    {
        // reference/core_vectors.txt: uniform plies from the 8×8 start with Rng(5), computed with the prototype.
        const string expected = "e1-d2 f8-e7 d2-c1 e7-d6 c1-d2 d6-c5 d2-c1 c5-b4 c1-b2 d8-c7 b2-c1 h8-g7 c1-b2 b4-a3";
        var rules = new Rules(RuleSet.Classic);
        var agent = new RandomAgent();
        agent.NewGame(rules, Side.Fox, new Rng(5));
        var s = rules.Initial();
        var plies = new List<string>();
        for (int i = 0; i < 14; i++)
        {
            var m = agent.ChooseMove(s, [], SearchBudget.Unlimited, TestContext.Current.CancellationToken).Move;
            plies.Add(rules.Geo.FormatMove(m));
            s = rules.Apply(s, m);
        }

        Assert.Equal(expected, string.Join(' ', plies));
    }

    [Fact]
    public void Reports_source_random_and_requires_NewGame()
    {
        var rules = new Rules(RuleSet.Classic);
        var agent = new RandomAgent();
        Assert.Throws<InvalidOperationException>(() => agent.ChooseMove(rules.Initial(), [], SearchBudget.Unlimited, TestContext.Current.CancellationToken));
        agent.NewGame(rules, Side.Fox, new Rng(1));
        var decision = agent.ChooseMove(rules.Initial(), [], SearchBudget.Unlimited, TestContext.Current.CancellationToken);
        Assert.Equal(AgentInfo.SourceRandom, decision.Info.Source);
        Assert.Null(decision.Info.ScoreFox);
        Assert.Equal("random", agent.Name);
    }
}
