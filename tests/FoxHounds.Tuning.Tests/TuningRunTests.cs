using System.Text.Json.Nodes;
using FoxHounds.Core;
using FoxHounds.Engine.Evaluation;
using FoxHounds.Sim;
using FoxHounds.Solver;

namespace FoxHounds.Tuning.Tests;

/// <summary>fh tune end to end on 6×6: the files a run leaves (spec 12.1, 12.2, 12.3).</summary>
public sealed class TuningRunTests : IDisposable
{
    private static readonly RuleSet Six = RuleSet.Classic with { BoardSize = 6 };
    private static readonly SolverTableStore Tables = new(Path.Combine(Path.GetTempPath(), "fh-tuning-tests-tables"));
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fh-tune-" + Guid.NewGuid().ToString("N"));

    public TuningRunTests()
    {
        Directory.CreateDirectory(_folder);
        var table = Tables.GetOrBuild(new Rules(Six), null, Ct);
        foreach (var (name, seed) in new[] { ("train", 1UL), ("validation", 2UL) })
        {
            var result = Openings.Generate(new OpeningOptions { RuleSet = Six, MinPlies = 2, MaxPlies = 14, Count = 300, Seed = seed }, table, Ct);
            new PositionFile(Six, [name], result.Positions).Write(Path.Combine(_folder, name + ".txt"));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_regression_run_writes_weights_a_report_and_what_it_was_run_from()
    {
        string spec = Spec("""
            "tuner": "regression", "evaluator": "tapered", "name": "tap-test", "regression": { "maxSteps": 300 }
            """);
        var result = TuningRun.Run(spec, Path.Combine(_folder, "out"), null, Tables, Ct);
        var weights = WeightFile.Load(Path.Combine(_folder, "out", "weights.json"));
        Assert.Equal("tap-test", weights.Name);
        Assert.Equal(WeightFile.Tapered, weights.Evaluator);
        Assert.Equal(6, weights.BoardSize);
        Assert.Equal("test-run", weights.Provenance);
        Assert.Equal(result.Weights.ToJson(), weights.ToJson()); // the record holds dictionaries, compared by reference

        var run = JsonNode.Parse(File.ReadAllText(Path.Combine(_folder, "out", "run.json")))!;
        Assert.Equal(64, ((string)run["inputs"]!["train"]!["sha256"]!).Length);
        Assert.Equal(64, ((string)run["specSha256"]!).Length);
        Assert.NotNull(run["commit"]);
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(_folder, "out", "report.json")))!;
        Assert.Equal(300, (int)report["trainPositions"]!);
        Assert.True((double)report["regression"]!["validationLoss"]! < Math.Log(2));

        // Same spec, same weights (12.1: a run can be repeated bit for bit).
        var again = TuningRun.Run(spec, Path.Combine(_folder, "again"), null, Tables, Ct);
        Assert.Equal(result.Weights.ToJson(), again.Weights.ToJson());
    }

    [Fact]
    public void An_SPSA_run_writes_one_csv_row_per_iteration_and_keeps_the_best_validated_weights()
    {
        string spec = Spec("""
            "tuner": "spsa", "evaluator": "linear", "name": "spsa-test",
            "spsa": { "iterations": 4, "validateEvery": 2, "batch": 50, "budget": { "maxNodes": 200 }, "workers": 2 }
            """);
        var result = TuningRun.Run(spec, Path.Combine(_folder, "spsa"), null, Tables, Ct);
        string[] csv = File.ReadAllLines(Path.Combine(_folder, "spsa", "tuning.csv"));
        Assert.Equal(5, csv.Length);
        Assert.StartsWith("iteration,FoxAdvance,", csv[0], StringComparison.Ordinal);
        var report = JsonNode.Parse(File.ReadAllText(Path.Combine(_folder, "spsa", "report.json")))!["spsa"]!;
        Assert.True((double)report["bestValidation"]! >= (double)report["startValidation"]!);
        Assert.Equal("maxNodes 200", (string?)report["budget"]);
        Assert.Equal(6, result.Weights.BoardSize);
    }

    [Theory]
    [InlineData("\"tuner\": \"genetic\"")]
    [InlineData("\"tuner\": \"regression\", \"evaluator\": \"neural\"")]
    [InlineData("\"tuner\": \"spsa\", \"spsa\": { \"budget\": { \"softTimeMs\": 100 } }")]
    [InlineData("\"tuner\": \"spsa\", \"spsa\": { \"objective\": \"winRate\" }")]
    [InlineData("\"tuner\": \"spsa\", \"spsa\": { \"scales\": { \"NoSuchFeature\": 3 } }")]
    public void Invalid_tuning_specs_are_rejected(string members)
    {
        Assert.Throws<InvalidDataException>(() => TuningRun.Run(Spec(members), Path.Combine(_folder, "bad"), null, Tables, Ct));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Spec(string members)
    {
        string path = Path.Combine(_folder, $"spec-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""
            { "format": "tuning/1", "id": "test-run", "seed": 9, "ruleSet": "classic", "boardSize": 6,
              "train": "train.txt", "validation": "validation.txt", {{members}} }
            """);
        return path;
    }
}
