using FoxHounds.Core;
using FoxHounds.Solver;

namespace FoxHounds.Sim.Tests;

/// <summary>Small experiments on 6×6 in a temporary folder.</summary>
internal sealed class SimFixture : IDisposable
{
    public static readonly SolverTableStore Tables = new(Path.Combine(Path.GetTempPath(), "fh-sim-tests-tables"));

    public static readonly RuleSet Six = RuleSet.Classic with { BoardSize = 6 };

    public SimFixture(int openings = 30, ulong seed = 3, int minPlies = 4, int maxPlies = 8)
    {
        Folder = Path.Combine(Path.GetTempPath(), "fh-sim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        var result = Openings.Generate(new OpeningOptions { RuleSet = Six, MinPlies = minPlies, MaxPlies = maxPlies, Count = openings, Seed = seed },
            Table, TestContext.Current.CancellationToken);
        new PositionFile(Six, ["test openings"], result.Positions).Write(Path.Combine(Folder, "openings.txt"));
        Write("strong.json", """{ "type": "minimax", "name": "strong", "search": { "ttMegabytes": 1 }, "budget": { "maxNodes": 400 } }""");
        Write("weak.json", """{ "type": "minimax", "name": "weak", "search": { "ttMegabytes": 1 }, "budget": { "maxDepth": 3 }, "epsilon": 0.2 }""");
        Write("random.json", """{ "type": "random", "name": "random" }""");
        Write("solver.json", """{ "type": "solver", "name": "solver", "randomTies": true }""");
    }

    public static SolverTable Table => Tables.GetOrBuild(new Rules(Six), null, TestContext.Current.CancellationToken);

    public string Folder { get; }

    public string Write(string name, string text)
    {
        string path = Path.Combine(Folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Writes an experiment spec with the given extra JSON members and loads it.</summary>
    public Experiment Spec(string mode, string agents, string extra = "", string positions = "\"openings\": \"openings.txt\"")
    {
        string json = $$"""
            { "format": "experiment/1", "id": "test-{{mode}}", "seed": 77, "ruleSet": "classic", "boardSize": 6,
              "mode": "{{mode}}", "agents": {{agents}}, {{positions}}{{(extra.Length > 0 ? ", " + extra : "")}} }
            """;
        return Experiment.Load(Write($"{mode}.json", json));
    }

    public RunResult Run(Experiment experiment, string folder, int workers, int? maxGames = null) =>
        RunUntil(experiment, folder, workers, maxGames, null, TestContext.Current.CancellationToken);

    public RunResult RunUntil(Experiment experiment, string folder, int workers, int? maxGames, Action<int>? afterWrite, CancellationToken ct) =>
        ExperimentRunner.Run(experiment, new RunOptions
        {
            Folder = Path.Combine(Folder, folder),
            Workers = workers,
            MaxGames = maxGames,
            Tables = Tables,
            Clock = new ManualClock(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero)),
            AfterWrite = afterWrite,
        }, ct);

    /// <summary>A run folder's files, with the wall-clock fields removed (decision D-20).</summary>
    public SortedDictionary<string, string> Files(string folder)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string root = Path.Combine(Folder, folder);
        foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(path);
            text = System.Text.RegularExpressions.Regex.Replace(text, "\"(ms|startedUtc)\": ?(\"[^\"]*\"|[0-9]+)", "\"$1\":0");
            result[Path.GetRelativePath(root, path)] = text;
        }

        return result;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
