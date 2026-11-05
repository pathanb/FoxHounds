using System.Text.Json.Nodes;
using FoxHounds.Core;

namespace FoxHounds.Sim.Tests;

/// <summary>The experiment runner (spec 11.1–11.4): T-SIM-01 determinism, T-SIM-02 resume, and the metrics.</summary>
public sealed class ExperimentTests
{
    private const string HeadToHead = """{ "a": "strong.json", "b": "weak.json" }""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("headToHead", HeadToHead, "")]
    [InlineData("vsReference", """{ "candidate": "weak.json", "reference": "solver.json" }""", "\"candidateSide\": \"hounds\", \"saveRecords\": \"all\"")]
    [InlineData("gauntlet", """{ "candidate": "strong.json", "opponents": ["weak.json", "random.json"] }""", "")]
    [InlineData("grade", """{ "candidate": "weak.json" }""", "")]
    public void One_and_eight_workers_give_identical_files(string mode, string agents, string extra)
    {
        // T-SIM-01.
        using var fx = new SimFixture();
        var spec = fx.Spec(mode, agents, extra);
        var one = fx.Run(spec, "one", workers: 1);
        var eight = fx.Run(spec, "eight", workers: 8);
        Assert.True(one.Complete && eight.Complete);
        Assert.Equal(fx.Files("one"), fx.Files("eight"));
        Assert.Equal(one.Total, one.Done);
    }

    [Fact]
    public void A_run_stopped_halfway_and_resumed_ends_with_the_files_of_an_uninterrupted_run()
    {
        // T-SIM-02, stopped by cancellation in the middle of the run, then by a game cap, each time resumed.
        using var fx = new SimFixture();
        var spec = fx.Spec("headToHead", HeadToHead, "\"saveRecords\": \"all\"");
        fx.Run(spec, "whole", workers: 2);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Assert.ThrowsAny<OperationCanceledException>(() => fx.RunUntil(spec, "stopped", 2, null, n =>
        {
            if (n == 30)
            {
                cancel.Cancel();
            }
        }, cancel.Token));
        var partial = JsonNode.Parse(File.ReadAllText(Path.Combine(fx.Folder, "stopped", "summary.json")))!;
        Assert.False((bool)partial["complete"]!);
        Assert.InRange((int)partial["done"]!, 30, 59);
        fx.Run(spec, "stopped", workers: 3);
        Assert.Equal(fx.Files("whole"), fx.Files("stopped"));

        fx.Run(spec, "capped", workers: 1, maxGames: 30);
        Assert.Equal(30, File.ReadAllLines(Path.Combine(fx.Folder, "capped", "games.jsonl")).Length);
        fx.Run(spec, "capped", workers: 1);
        Assert.Equal(fx.Files("whole"), fx.Files("capped"));
    }

    [Fact]
    public void A_damaged_last_line_is_dropped_and_its_game_played_again()
    {
        using var fx = new SimFixture(openings: 10);
        var spec = fx.Spec("headToHead", HeadToHead);
        fx.Run(spec, "whole", workers: 1);
        fx.Run(spec, "damaged", workers: 1, maxGames: 8);
        string games = Path.Combine(fx.Folder, "damaged", "games.jsonl");
        File.AppendAllText(games, "{\"game\":8,\"opening\":4,\"fox\":\"str");
        fx.Run(spec, "damaged", workers: 1);
        Assert.Equal(fx.Files("whole"), fx.Files("damaged"));
    }

    [Fact]
    public void A_folder_holding_another_spec_is_refused()
    {
        using var fx = new SimFixture(openings: 4);
        fx.Run(fx.Spec("headToHead", HeadToHead), "run", workers: 1);
        var other = fx.Spec("headToHead", """{ "a": "strong.json", "b": "random.json" }""");
        var ex = Assert.Throws<ExperimentException>(() => fx.Run(other, "run", workers: 1));
        Assert.Contains("different spec", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_count_game_cap_and_memory_limit_do_not_change_the_spec_hash()
    {
        using var fx = new SimFixture(openings: 4);
        var a = fx.Spec("headToHead", HeadToHead);
        var b = fx.Spec("headToHead", HeadToHead, "\"workers\": 3, \"maxGames\": 4, \"maxMemoryMb\": 100");
        Assert.Equal(a.Sha256, b.Sha256);
        var c = fx.Spec("headToHead", HeadToHead, "\"budgets\": { \"fox\": { \"maxNodes\": 50 } }");
        Assert.NotEqual(a.Sha256, c.Sha256);
        fx.Write("weak.json", """{ "type": "minimax", "name": "weak", "search": { "ttMegabytes": 1 }, "budget": { "maxDepth": 2 } }""");
        Assert.NotEqual(a.Sha256, fx.Spec("headToHead", HeadToHead).Sha256);
    }

    [Fact]
    public void The_memory_limit_is_checked_before_any_game()
    {
        using var fx = new SimFixture(openings: 4);
        fx.Write("big.json", """{ "type": "minimax", "name": "big", "search": { "ttMegabytes": 64 }, "budget": { "maxDepth": 1 } }""");
        var spec = fx.Spec("headToHead", """{ "a": "big.json", "b": "big.json" }""", "\"maxMemoryMb\": 200");
        var ex = Assert.Throws<ExperimentException>(() => fx.Run(spec, "run", workers: 2));
        Assert.Contains("maxMemoryMb", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fx.Folder, "run", "games.jsonl")));
    }

    [Theory]
    [InlineData("headToHead", """{ "a": "strong.json" }""", "")]
    [InlineData("gauntlet", """{ "candidate": "strong.json", "opponents": [] }""", "")]
    [InlineData("headToHead", HeadToHead, "\"budgets\": { \"hounds\": { \"softTimeMs\": 100 } }")]
    [InlineData("vsReference", HeadToHead, "")]
    [InlineData("vsReference", """{ "candidate": "strong.json", "reference": "solver.json" }""", "\"sprt\": { \"elo1\": 10 }")]
    [InlineData("headToHead", HeadToHead, "\"sprt\": { \"elo0\": 10, \"elo1\": 0 }")]
    [InlineData("headToHead", HeadToHead, "\"saveRecords\": \"some\"")]
    [InlineData("headToHead", HeadToHead, "\"maxGames\": 0")]
    [InlineData("match", HeadToHead, "")]
    public void Invalid_experiment_specs_are_rejected(string mode, string agents, string extra)
    {
        using var fx = new SimFixture(openings: 4);
        Assert.Throws<InvalidDataException>(() => fx.Spec(mode, agents, extra));
    }

    [Fact]
    public void Tables_kept_between_games_and_soft_time_limits_need_allowNonDeterministic()
    {
        using var fx = new SimFixture(openings: 4);
        fx.Write("keep.json", """{ "type": "minimax", "name": "keep", "search": { "keepTtBetweenGames": true }, "budget": { "maxDepth": 2 } }""");
        fx.Write("soft.json", """{ "type": "minimax", "name": "soft", "budget": { "softTimeMs": 50 } }""");
        Assert.Throws<InvalidDataException>(() => fx.Spec("headToHead", """{ "a": "keep.json", "b": "weak.json" }"""));
        Assert.Throws<InvalidDataException>(() => fx.Spec("headToHead", """{ "a": "soft.json", "b": "weak.json" }"""));
        Assert.True(fx.Spec("headToHead", """{ "a": "soft.json", "b": "keep.json" }""", "\"allowNonDeterministic\": true").AllowNonDeterministic);

        // A hard limit alone is a watchdog (D-21) and is allowed.
        fx.Write("hard.json", """{ "type": "minimax", "name": "hard", "budget": { "maxDepth": 2, "hardTimeMs": 5000 } }""");
        Assert.False(fx.Spec("headToHead", """{ "a": "hard.json", "b": "weak.json" }""").AllowNonDeterministic);
    }

    [Fact]
    public void Without_a_solver_only_head_to_head_and_gauntlet_run()
    {
        using var fx = new SimFixture(openings: 4);
        var big = RuleSet.Classic with { BoardSize = 12 };
        var openings = Openings.Generate(new OpeningOptions { RuleSet = big, Count = 4, Label = false }, null, Ct);
        new PositionFile(big, [], openings.Positions).Write(Path.Combine(fx.Folder, "o12.txt"));
        string Json(string mode, string agents) => $$"""
            { "id": "big", "seed": 1, "ruleSet": "classic", "boardSize": 12, "mode": "{{mode}}", "agents": {{agents}}, "openings": "o12.txt" }
            """;
        Assert.Throws<InvalidDataException>(() => Experiment.Load(fx.Write("v.json", Json("vsReference", """{ "candidate": "random.json", "reference": "random.json" }"""))));
        var spec = Experiment.Load(fx.Write("h.json", Json("headToHead", """{ "a": "random.json", "b": "random.json" }""")));
        var result = fx.Run(spec, "run", workers: 1);
        Assert.True(result.Complete);
        var line = JsonNode.Parse(File.ReadLines(Path.Combine(fx.Folder, "run", "games.jsonl")).First())!;
        Assert.Null(line["blunders"]);
        Assert.Null(line["openingLabel"]);
    }

    [Fact]
    public void The_solver_never_blunders_and_converts_every_won_opening()
    {
        using var fx = new SimFixture(openings: 30);
        var spec = fx.Spec("vsReference", """{ "candidate": "solver.json", "reference": "random.json" }""", "\"candidateSide\": \"fox\"");
        var summary = fx.Run(spec, "run", workers: 2).Summary;
        var fox = summary["agents"]!["candidate"]!["fox"]!;
        Assert.Equal(0, (long)fox["blunders"]!["successes"]!);
        Assert.True((long)fox["blunders"]!["trials"]! > 0);
        Assert.Equal((long)fox["conversion"]!["trials"]!, (long)fox["conversion"]!["successes"]!);
        Assert.Equal(30, (int)summary["openings"]!["foxWon"]! + (int)summary["openings"]!["houndsWon"]!);
    }

    [Fact]
    public void A_blunder_is_a_ply_from_a_won_position_to_a_lost_one()
    {
        // Every ply of a random agent is checked against the solver by hand here.
        using var fx = new SimFixture(openings: 20);
        var spec = fx.Spec("vsReference", """{ "candidate": "random.json", "reference": "solver.json" }""", "\"candidateSide\": \"fox\", \"saveRecords\": \"all\"");
        fx.Run(spec, "run", workers: 1);
        var table = SimFixture.Table;
        var rules = new Rules(SimFixture.Six);
        long blunders = 0, won = 0;
        foreach (string path in Directory.GetFiles(Path.Combine(fx.Folder, "run", "records")))
        {
            var record = GameRecordFile.Read(path);
            var positions = record.Replay(rules);
            for (int i = 0; i + 1 < positions.Count; i++)
            {
                var s = positions[i];
                if (s.SideToMove != Side.Fox || !table.Get(s).SideToMoveWins)
                {
                    continue;
                }

                won++;
                var next = positions[i + 1];
                var outcome = rules.Evaluate(next);
                bool lost = outcome.IsOver ? outcome.Winner != Side.Fox : table.Get(next).Winner(next.SideToMove) != Side.Fox;
                blunders += lost ? 1 : 0;
            }
        }

        var fox = JsonNode.Parse(File.ReadAllText(Path.Combine(fx.Folder, "run", "summary.json")))!["agents"]!["candidate"]!["fox"]!;
        Assert.True(blunders > 0, "the random fox should blunder");
        Assert.Equal(blunders, (long)fox["blunders"]!["successes"]!);
        Assert.Equal(won, (long)fox["blunders"]!["trials"]!);
    }

    [Fact]
    public void Grading_the_solver_gives_full_accuracy_and_resistance()
    {
        using var fx = new SimFixture(openings: 40);
        var summary = fx.Run(fx.Spec("grade", """{ "candidate": "solver.json" }"""), "solver", workers: 2).Summary;
        var all = summary["grade"]!["all"]!;
        Assert.Equal(1.0, (double)all["accuracy"]!["rate"]!);
        Assert.Equal(1.0, (double)all["resistance"]!["rate"]!);
        Assert.Equal(40, (int)all["positions"]!);

        var random = fx.Run(fx.Spec("grade", """{ "candidate": "random.json" }"""), "random", workers: 2).Summary["grade"]!["all"]!;
        Assert.True((double)random["accuracy"]!["rate"]! < 1.0);
    }

    [Fact]
    public void The_sequential_test_stops_a_lopsided_match_the_same_way_with_any_worker_count()
    {
        using var fx = new SimFixture(openings: 150, minPlies: 2, maxPlies: 12);
        var spec = fx.Spec("headToHead", """{ "a": "solver.json", "b": "random.json" }""", "\"sprt\": { \"elo0\": 0, \"elo1\": 50 }");
        var one = fx.Run(spec, "one", workers: 1);
        var four = fx.Run(spec, "four", workers: 4);
        Assert.Equal(fx.Files("one"), fx.Files("four"));
        var match = one.Summary["headToHead"]![0]!;
        Assert.Equal("acceptH1", (string?)match["sprt"]!["decision"]);
        Assert.Equal(50, (int)match["pairs"]!);           // decided at the first opportunity
        Assert.True(one.Complete);
        Assert.Equal(100, one.Done);
        Assert.Equal(300, one.Total);
    }

    [Fact]
    public void The_report_prints_the_summary_and_compares_two_runs()
    {
        using var fx = new SimFixture(openings: 6);
        var a = fx.Run(fx.Spec("headToHead", HeadToHead), "a", workers: 1).Summary;
        var b = fx.Run(fx.Spec("headToHead", """{ "a": "strong.json", "b": "random.json" }"""), "b", workers: 1).Summary;
        string text = Report.Format(a);
        Assert.Contains("a vs b: 6 pairs", text, StringComparison.Ordinal);
        Assert.Contains("as fox", text, StringComparison.Ordinal);
        string compare = Report.Compare(a, b);
        Assert.Contains("headToHead[a-vs-b].score", compare, StringComparison.Ordinal);
    }
}
