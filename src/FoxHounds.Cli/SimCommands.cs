using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using FoxHounds.Core;
using FoxHounds.Sim;
using FoxHounds.Solver;

namespace FoxHounds.Cli;

/// <summary><c>fh openings</c>: opening sets (spec 11.2, decision D-15).</summary>
internal static class OpeningsCommand
{
    public static Command Create()
    {
        var size = new Option<int>("--n") { Description = "Board size.", DefaultValueFactory = _ => 8 };
        var ruleSet = new Option<string>("--rules") { Description = "classic or gilith.", DefaultValueFactory = _ => "classic" };
        var choose = new Option<bool>("--choose-start") { Description = "Fox start ChooseOnBackRow: each start square in turn." };
        var plies = new Option<string>("--plies") { Description = "Plies per opening: one value or a range lo-hi.", DefaultValueFactory = _ => "6-16" };
        var count = new Option<int>("--count") { Description = "Openings wanted.", DefaultValueFactory = _ => 1000 };
        var seed = new Option<ulong>("--seed") { Description = "Seed.", DefaultValueFactory = _ => 7 };
        var label = new Option<bool>("--label") { Description = "Label each opening with its solver value." };
        var filter = new Option<OpeningFilter>("--filter") { Description = "Any, HoundsWon or FoxWon (needs --label).", DefaultValueFactory = _ => OpeningFilter.Any };
        var generator = new Option<OpeningPolicy>("--generator") { Description = "Uniform or NoisySolver.", DefaultValueFactory = _ => OpeningPolicy.Uniform };
        var epsilon = new Option<double>("--epsilon") { Description = "Random-ply probability of the noisy solver.", DefaultValueFactory = _ => 0.1 };
        var output = new Option<string>("--out", "-o") { Description = "Output file.", Required = true };

        var command = new Command("openings", "Generate an opening set: random openings, deduplicated, labelled by the solver and filtered.")
        {
            size, ruleSet, choose, plies, count, seed, label, filter, generator, epsilon, output,
        };
        command.SetAction(parse =>
        {
            var config = SimCommon.RuleSetFor(parse.GetValue(ruleSet)!, parse.GetValue(size)) with
            {
                FoxStart = parse.GetValue(choose) ? FoxStart.ChooseOnBackRow : FoxStart.Fixed,
            };
            var (lo, hi) = SimCommon.Range(parse.GetValue(plies)!);
            bool labels = parse.GetValue(label);
            var policy = parse.GetValue(generator);
            var table = labels || policy == OpeningPolicy.NoisySolver ? SimCommon.Table(config) : null;
            var options = new OpeningOptions
            {
                RuleSet = config,
                MinPlies = lo,
                MaxPlies = hi,
                Count = parse.GetValue(count),
                Seed = parse.GetValue(seed),
                Label = labels,
                Filter = parse.GetValue(filter),
                Policy = policy,
                Epsilon = parse.GetValue(epsilon),
            };
            var result = Openings.Generate(options, table);
            string acceptance = string.Create(CultureInfo.InvariantCulture,
                $"generator={JsonNames.Of(policy)} accepted={result.Positions.Count} attempts={result.Attempts} acceptance={result.AcceptanceRate * 100:0.0}% (terminal {result.Terminal}, duplicates {result.Duplicates}, filtered {result.Filtered})");
            var file = new PositionFile(config, [$"fh {string.Join(' ', parse.Tokens.Select(t => t.Value))}", acceptance], result.Positions);
            string path = parse.GetValue(output)!;
            file.Write(path);
            var writer = parse.InvocationConfiguration.Output;
            writer.WriteLine($"wrote {result.Positions.Count} openings to {path}");
            writer.WriteLine(acceptance);
            if (result.Positions.Count < options.Count)
            {
                writer.WriteLine($"warning: only {result.Positions.Count} of {options.Count} distinct openings exist within the attempt limit");
            }

            return 0;
        });
        return command;
    }
}

/// <summary><c>fh suite</c>: position suites (spec 11.2, decision D-28).</summary>
internal static class SuiteCommand
{
    public static Command Create()
    {
        var size = new Option<int>("--n") { Description = "Board size (the solver labels need n <= 11).", DefaultValueFactory = _ => 8 };
        var ruleSet = new Option<string>("--rules") { Description = "classic or gilith.", DefaultValueFactory = _ => "classic" };
        var seed = new Option<ulong>("--seed") { Description = "Seed.", DefaultValueFactory = _ => 11 };
        var train = new Option<int>("--train") { Description = "Training positions.", DefaultValueFactory = _ => 50_000 };
        var validation = new Option<int>("--validation") { Description = "Validation positions.", DefaultValueFactory = _ => 10_000 };
        var test = new Option<int>("--test") { Description = "Test positions.", DefaultValueFactory = _ => 20_000 };
        var workers = new Option<int>("--workers") { Description = "Games in parallel.", DefaultValueFactory = _ => Math.Max(1, Environment.ProcessorCount - 1) };
        var maxGames = new Option<long>("--max-games") { Description = "Give up after this many games (default 20 per position wanted)." };
        var output = new Option<string>("--out-dir", "-o") { Description = "Folder for train.txt, validation.txt and test.txt.", Required = true };
        var command = new Command("suite", "Generate a position suite from games between noisy depth-4 players, stratified by phase and solver winner.")
        {
            size, ruleSet, seed, train, validation, test, workers, maxGames, output,
        };
        command.SetAction(parse =>
        {
            var config = SimCommon.RuleSetFor(parse.GetValue(ruleSet)!, parse.GetValue(size));
            var options = new SuiteOptions
            {
                RuleSet = config,
                Seed = parse.GetValue(seed),
                Train = parse.GetValue(train),
                Validation = parse.GetValue(validation),
                Test = parse.GetValue(test),
                Workers = parse.GetValue(workers),
                MaxGames = parse.GetValue(maxGames),
            };
            var writer = parse.InvocationConfiguration.Output;
            var clock = SystemClock.Instance;
            var started = clock.Elapsed;
            var nextReport = started + TimeSpan.FromSeconds(10);
            var progress = new SyncProgress(games =>
            {
                if (clock.Elapsed >= nextReport)
                {
                    nextReport = clock.Elapsed + TimeSpan.FromSeconds(10);
                    writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{games} games, {games / (clock.Elapsed - started).TotalMinutes:0}/min"));
                }
            });
            var result = Suites.Generate(options, SimCommon.Table(config), progress);
            string dir = parse.GetValue(output)!;
            string command = $"fh {string.Join(' ', parse.Tokens.Select(t => t.Value))}";
            foreach (var (name, positions) in new[] { ("train", result.Train), ("validation", result.Validation), ("test", result.Test) })
            {
                string note = $"suite split {name}: {positions.Count} positions from {result.Games} games (decision D-28)";
                new PositionFile(config, [command, note], positions).Write(Path.Combine(dir, name + ".txt"));
                writer.WriteLine($"wrote {positions.Count} positions to {Path.Combine(dir, name + ".txt")}");
            }

            foreach (var (stratum, missing) in result.Shortfall)
            {
                writer.WriteLine($"warning: {stratum} is {missing} short after {result.Games} games");
            }

            return 0;
        });
        return command;
    }

    /// <summary>Reports on the generating thread (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}

/// <summary><c>fh match</c> and <c>fh grade</c>: run or resume an experiment (spec 11.1, 11.3).</summary>
internal static class MatchCommand
{
    public static Command Create(bool grade)
    {
        var spec = new Argument<string>("spec") { Description = "Experiment spec (experiment/1 JSON)." };
        var output = new Option<string>("--out", "-o") { Description = "Run folder; rerunning into it resumes.", Required = true };
        var workers = new Option<int?>("--workers") { Description = "Workers (default: the spec's, else processors − 1)." };
        var maxGames = new Option<int?>("--max-games") { Description = "Stop after this many games; a later run resumes." };
        var quiet = new Option<bool>("--quiet") { Description = "No progress lines." };
        var command = grade
            ? new Command("grade", "Grade an agent's moves on a position suite against the solver (mode grade).") { spec, output, workers, maxGames, quiet }
            : new Command("match", "Play an experiment's games (modes vsReference, headToHead, gauntlet).") { spec, output, workers, maxGames, quiet };
        command.SetAction(parse =>
        {
            var experiment = Experiment.Load(parse.GetValue(spec)!);
            if ((experiment.Mode == ExperimentMode.Grade) != grade)
            {
                throw new ArgumentException(grade
                    ? $"fh grade runs mode grade; this spec is {JsonNames.Of(experiment.Mode)}: use fh match."
                    : "This spec is mode grade: use fh grade.");
            }

            var writer = parse.InvocationConfiguration.Output;
            using var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                e.Cancel = true;
                cancel.Cancel();
            };
            Console.CancelKeyPress += onCancel;
            string folder = parse.GetValue(output)!;
            try
            {
                var options = new RunOptions
                {
                    Folder = folder,
                    Workers = parse.GetValue(workers),
                    MaxGames = parse.GetValue(maxGames),
                    Log = message => writer.WriteLine(message),
                    Progress = parse.GetValue(quiet) ? null : p => writer.WriteLine(SimCommon.Describe(p)),
                    Tables = SimCommon.Store,
                };
                var result = ExperimentRunner.Run(experiment, options, cancel.Token);
                writer.WriteLine(result.Complete ? "done" : $"{result.Done} of {result.Total} done; run again to continue");
                writer.Write(Report.Format(result.Summary));
                return 0;
            }
            catch (OperationCanceledException)
            {
                writer.WriteLine($"stopped; run the same command again to resume into {folder}");
                return 3;
            }
            catch (ExperimentException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 2;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        });
        return command;
    }
}

/// <summary><c>fh report</c>: a run's summary as a table, or two runs side by side (spec 11.4).</summary>
internal static class ReportCommand
{
    public static Command Create()
    {
        var first = new Argument<string>("folder") { Description = "Run folder." };
        var second = new Argument<string?>("other") { Description = "A second run folder to compare with.", Arity = ArgumentArity.ZeroOrOne };
        var command = new Command("report", "Print a run's summary, or compare two runs metric by metric.") { first, second };
        command.SetAction(parse =>
        {
            var a = Load(parse.GetValue(first)!);
            var writer = parse.InvocationConfiguration.Output;
            writer.Write(parse.GetValue(second) is { } other ? Report.Compare(a, Load(other)) : Report.Format(a));
            return 0;
        });
        return command;
    }

    private static JsonObject Load(string folder)
    {
        string path = Path.Combine(folder, "summary.json");
        if (!File.Exists(path))
        {
            throw new ArgumentException($"{folder} has no summary.json.");
        }

        return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new ArgumentException($"{path} is empty.");
    }
}

/// <summary>Helpers shared by the simulator commands.</summary>
internal static class SimCommon
{
    public static SolverTableStore Store { get; } = new(SolverTableStore.DefaultDirectory)
    {
        Warn = message => Console.Error.WriteLine($"warning: {message}"),
    };

    public static RuleSet RuleSetFor(string name, int n)
    {
        var preset = name.ToLowerInvariant() switch
        {
            "classic" => RuleSet.Classic,
            "gilith" => RuleSet.Gilith,
            _ => throw new ArgumentException($"--rules must be classic or gilith, not '{name}'."),
        };
        return preset with { BoardSize = n };
    }

    public static (int Lo, int Hi) Range(string text)
    {
        string[] parts = text.Split('-');
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out int lo)
            || !int.TryParse(parts[^1], CultureInfo.InvariantCulture, out int hi))
        {
            throw new ArgumentException($"'{text}' is not a number or a range lo-hi.");
        }

        return (lo, hi);
    }

    public static SolverTable Table(RuleSet config)
    {
        var rules = new Rules(config);
        if (!SolverTable.IsSupported(rules.Geo.N))
        {
            throw new ArgumentException($"No solver on {rules.Geo.N}x{rules.Geo.N}: labels, filters and the noisy solver are not available.");
        }

        return Store.GetOrBuild(rules, null, CancellationToken.None);
    }

    public static string Describe(RunProgress p)
    {
        string finish = p.Finish is { } f ? f.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "?";
        return string.Create(CultureInfo.InvariantCulture, $"{p.Done}/{p.Total}  {p.PerMinute:0.0}/min  finish {finish}  {p.Headline}");
    }
}
