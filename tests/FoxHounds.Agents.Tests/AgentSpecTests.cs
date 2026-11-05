using FoxHounds.Core;
using FoxHounds.Engine;
using FoxHounds.Engine.Evaluation;
using FoxHounds.Solver;

namespace FoxHounds.Agents.Tests;

/// <summary>Agent specs (spec 14.1), the factory that builds every agent from them (7.2), and the app's presets (13.3).</summary>
public sealed class AgentSpecTests
{
    private static readonly SolverTableStore Tables = new(Path.Combine(Path.GetTempPath(), "fh-agents-tests-" + Guid.NewGuid().ToString("N")));

    private static AgentFactory Factory => new(Tables, new ManualClock(DateTimeOffset.UnixEpoch));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentSpec Preset(string name) => AgentSpec.Load(RepoRoot.File($"src/FoxHounds.App/presets/{name}.json"));

    [Fact]
    public void The_shipped_presets_build_the_agents_spec_13_3_describes()
    {
        var rules = new Rules(RuleSet.Classic);

        var easy = Assert.IsType<EpsilonAgent>(Factory.Create(Preset("easy"), rules, null, Ct));
        Assert.Equal(0.25, easy.Epsilon);
        Assert.IsType<MinimaxAgent>(easy.Inner);
        Assert.Equal(SearchBudget.Depth(2), Preset("easy").ToBudget());

        var medium = Assert.IsType<EpsilonAgent>(Factory.Create(Preset("medium"), rules, null, Ct));
        Assert.Equal(0.05, medium.Epsilon);
        Assert.Equal(SearchBudget.Depth(6), Preset("medium").ToBudget());

        var hard = Assert.IsType<MinimaxAgent>(Factory.Create(Preset("hard"), rules, null, Ct));
        Assert.Equal(RootSelection.RandomAmongBest, hard.Options.RootSelection);
        Assert.Equal(new SearchBudget(null, 2_000_000, null, TimeSpan.FromSeconds(3)), Preset("hard").ToBudget());
        Assert.Equal(WeightFile.LinV0.Name, hard.Evaluator.Name);

        var small = new Rules(RuleSet.Classic with { BoardSize = 5 });
        var perfect = Assert.IsType<SolverAgent>(Factory.Create(Preset("perfect"), small, null, Ct));
        Assert.True(perfect.RandomTies);
    }

    [Fact]
    public void Fields_missing_from_the_json_take_their_defaults()
    {
        // The source generator used to fill init-only properties with default(T): rootSelection became null.
        var spec = AgentSpec.Parse("""{ "type": "minimax", "name": "x", "search": { "ttMegabytes": 4 }, "evaluator": { "weightsFile": "w.json" } }""");
        Assert.Equal("best", spec.Search!.RootSelection);
        Assert.Equal(4, spec.Search.TtMegabytes);
        Assert.Equal("linear", spec.Evaluator!.Type);
        Assert.Equal(64, AgentSpec.Parse("""{ "type": "minimax", "name": "x", "search": {} }""").Search!.TtMegabytes);

        var agent = Assert.IsType<MinimaxAgent>(Factory.Create(AgentSpec.Parse("""{ "type": "minimax", "name": "x", "search": { "ttMegabytes": 1 } }"""), new Rules(RuleSet.Classic), null, Ct));
        Assert.Equal(RootSelection.Best, agent.Options.RootSelection);
        Assert.Equal("x", agent.Name);
    }

    [Theory]
    [InlineData("""{ "type": "alphazero", "name": "x" }""")]
    [InlineData("""{ "type": "minimax" }""")]
    [InlineData("""{ "type": "minimax", "name": " " }""")]
    [InlineData("""{ "name": "x" }""")]
    [InlineData("""{ "type": "minimax", "name": "x", "epsilon": 1.5 }""")]
    [InlineData("""{ "type": "minimax", "name": "x", "budget": { "maxNodes": 0 } }""")]
    [InlineData("""{ "type": "minimax", "name": "x", "budget": { "maxDepth": -1 } }""")]
    [InlineData("""{ "type": "minimax", "name": "x", "search": { "rootSelection": "random" } }""")]
    [InlineData("""{ "type": "minimax", "name": "x", "search": { "ttMegabytes": -1 } }""")]
    [InlineData("""{ "type": "minimax", "name": "x", "evaluator": { "type": "" } }""")]
    [InlineData("""{ "format": "agent/2", "type": "minimax", "name": "x" }""")]
    [InlineData("""{ "format": "weights/1", "type": "minimax", "name": "x" }""")]
    [InlineData("""{ "type": "minimax", "name": "x", """)]
    [InlineData("""[ 1, 2 ]""")]
    public void Invalid_specs_are_rejected_when_read(string json)
    {
        Assert.Throws<InvalidDataException>(() => AgentSpec.Parse(json));
    }

    [Fact]
    public void Unknown_fields_and_minor_versions_are_accepted()
    {
        var spec = AgentSpec.Parse("""{ "format": "agent/1.3", "type": "random", "name": "r", "comment": "ignored" }""");
        Assert.IsType<RandomAgent>(Factory.Create(spec, new Rules(RuleSet.Classic), null, Ct));
    }

    [Fact]
    public void A_spec_survives_a_round_trip_through_its_json()
    {
        foreach (string name in new[] { "easy", "medium", "hard", "perfect" })
        {
            var spec = Preset(name);
            var again = AgentSpec.Parse(spec.ToJson(), spec.BaseDirectory);
            Assert.Equal(spec, again);
        }
    }

    [Fact]
    public void Weight_files_resolve_against_the_specs_folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fh-spec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "w"));
        File.WriteAllText(Path.Combine(dir, "w", "mine.json"), """{ "name": "mine", "evaluator": "linear", "weights": { "FoxToMove": 3 } }""");
        File.WriteAllText(Path.Combine(dir, "agent.json"), """{ "type": "minimax", "name": "m", "evaluator": { "weightsFile": "w/mine.json" } }""");
        var spec = AgentSpec.Load(Path.Combine(dir, "agent.json"));
        Assert.Equal(dir, spec.BaseDirectory);
        var agent = Assert.IsType<MinimaxAgent>(Factory.Create(spec, new Rules(RuleSet.Classic), null, Ct));
        Assert.Equal("mine", agent.Evaluator.Name);

        // A weight file for another board size is refused by the factory (spec 9.3).
        File.WriteAllText(Path.Combine(dir, "w", "mine.json"), """{ "name": "mine", "evaluator": "linear", "boardSize": 10, "weights": { "FoxToMove": 3 } }""");
        Assert.Throws<InvalidDataException>(() => Factory.Create(spec, new Rules(RuleSet.Classic), null, Ct));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void A_solver_evaluator_comes_with_the_rules_table_and_a_solver_agent_needs_a_supported_board()
    {
        var rules = new Rules(RuleSet.Classic with { BoardSize = 5 });
        var evaluator = Assert.IsType<SolverEvaluator>(Factory.CreateEvaluator(new EvaluatorSpec { Type = SolverEvaluator.TypeName }, "", rules, null, Ct));
        Assert.True(evaluator.Table.Covers(rules.Config));

        var large = new Rules(RuleSet.Classic with { BoardSize = 12 });
        Assert.Throws<NotSupportedException>(() => Factory.Create(Preset("perfect"), large, null, Ct));
    }
}
