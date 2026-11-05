using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FoxHounds.Agents;
using FoxHounds.Core;
using FoxHounds.Solver;

namespace FoxHounds.Sim;

/// <summary>How to run an experiment.</summary>
public sealed record RunOptions
{
    /// <summary>The run folder: spec.json, games.jsonl, summary.json, records/ (spec 11.4).</summary>
    public required string Folder { get; init; }

    /// <summary>Overrides the spec's worker count (0: ProcessorCount − 1).</summary>
    public int? Workers { get; init; }

    /// <summary>Overrides the spec's game cap; neither changes the spec hash, so a run can grow by resuming.</summary>
    public int? MaxGames { get; init; }

    /// <summary>Clock for watchdog time limits, game timings and progress (never for results).</summary>
    public IClock Clock { get; init; } = SystemClock.Instance;

    /// <summary>Where solver tables come from (default: the user data folder).</summary>
    public SolverTableStore? Tables { get; init; }

    /// <summary>Called on the calling thread about every <see cref="ProgressInterval"/>.</summary>
    public Action<RunProgress>? Progress { get; init; }

    /// <summary>How often <see cref="Progress"/> is called (spec 11.3: every 2 s).</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Messages worth showing: resumed games, memory use.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Called after each games.jsonl line is written, with the number written so far (tests stop runs with it).</summary>
    internal Action<int>? AfterWrite { get; init; }
}

/// <summary>A progress report (spec 11.3).</summary>
/// <param name="Done">Games (or positions) finished, including those of earlier sessions.</param>
/// <param name="Total">Games planned.</param>
/// <param name="PerMinute">Games per minute in this session.</param>
/// <param name="Finish">Projected finish, if the rate is known.</param>
/// <param name="Headline">The mode's main metric with its interval.</param>
public sealed record RunProgress(int Done, int Total, double PerMinute, DateTimeOffset? Finish, string Headline);

/// <summary>What a run left behind.</summary>
/// <param name="Done">Games (or positions) in games.jsonl.</param>
/// <param name="Total">Games planned.</param>
/// <param name="Complete">True when every planned game is done, or the sequential test stopped the run.</param>
/// <param name="Summary">summary.json's content.</param>
public sealed record RunResult(int Done, int Total, bool Complete, JsonObject Summary);

/// <summary>An experiment cannot run, or had to stop, for a reason other than cancellation.</summary>
public sealed class ExperimentException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ExperimentException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ExperimentException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ExperimentException()
    {
    }
}

/// <summary>
/// Runs experiments (spec 11.3): a deterministic work list, game i seeded with Rng.ForGame(seed, i), workers that
/// each own their agents, output written in game order so that any worker count and any resume give the same
/// files, and the summary of 11.5.
/// </summary>
public static class ExperimentRunner
{
    private const string GamesFile = "games.jsonl";
    private const string SpecFile = "spec.json";
    private const string SummaryFile = "summary.json";
    private const ulong SampleSalt = 0x5EC0_4D5A_11E5_7A1EUL;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Runs (or resumes) <paramref name="experiment"/> into <see cref="RunOptions.Folder"/>. Games already in its
    /// games.jsonl are skipped; a folder holding a different spec is an error. Cancelling stops after writing the
    /// games finished in order so far, and the run can be resumed later.
    /// </summary>
    /// <exception cref="ExperimentException">The folder holds another spec, the memory limit is exceeded, or a
    /// game hit its watchdog time limit (D-21).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static RunResult Run(Experiment experiment, RunOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ArgumentNullException.ThrowIfNull(options);
        var rules = new Rules(experiment.RuleSet);
        Directory.CreateDirectory(options.Folder);
        CheckOrWriteSpec(experiment, options.Folder);
        var tables = options.Tables ?? SolverTableStore.Default;
        var factory = new AgentFactory(tables, options.Clock) { DefaultTtMegabytes = 16 };
        var table = SolverTable.IsSupported(rules.Geo.N) ? tables.GetOrBuild(rules, null, ct) : null;
        var work = WorkList(experiment, options.MaxGames ?? experiment.MaxGames);
        int workers = Math.Max(1, options.Workers ?? (experiment.Workers > 0 ? experiment.Workers : Environment.ProcessorCount - 1));
        CheckMemory(experiment, factory, workers, table, options.Log);

        var state = new RunState(experiment, rules, table, options.Folder);
        state.LoadExisting(options.Log);
        var remaining = work.Where(w => !state.Done.Contains(w.Index)).ToArray();
        if (remaining.Length < work.Count - state.Done.Count)
        {
            options.Log?.Invoke($"games.jsonl holds games outside this run's list; they are kept but not counted.");
        }

        try
        {
            if (remaining.Length > 0 && !state.Stopped)
            {
                options.Log?.Invoke(state.Done.Count > 0
                    ? $"resuming: {state.Done.Count} of {work.Count} done, {remaining.Length} to go on {workers} worker(s)"
                    : $"{work.Count} {(experiment.Mode == ExperimentMode.Grade ? "positions" : "games")} on {workers} worker(s)");
                Execute(state, remaining, work.Count, factory, workers, options, ct);
            }
        }
        finally
        {
            // Also after a cancellation or failure, so the folder always describes what it holds.
            WriteSummary(state, work, options.Folder);
        }

        var summary = Summary.Build(experiment, state.GameLines, state.GradeLines, work, state.Stopped);
        int done = state.Count(work);
        return new RunResult(done, work.Count, done == work.Count || state.Stopped, summary);
    }

    /// <summary>Recomputes the summary from a run folder's games.jsonl.</summary>
    public static JsonObject Summarize(Experiment experiment, string folder, int? maxGames = null)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        var state = new RunState(experiment, new Rules(experiment.RuleSet), null, folder);
        state.LoadExisting(null);
        var work = WorkList(experiment, maxGames ?? experiment.MaxGames);
        return Summary.Build(experiment, state.GameLines, state.GradeLines, work, state.Stopped);
    }

    private static void WriteSummary(RunState state, IReadOnlyList<WorkItem> work, string folder)
    {
        var summary = Summary.Build(state.Experiment, state.GameLines, state.GradeLines, work, state.Stopped);
        File.WriteAllText(Path.Combine(folder, SummaryFile), summary.ToJsonString(Indented) + "\n");
    }

    /// <summary>The deterministic work list (spec 11.3): game index = position in the list.</summary>
    internal static IReadOnlyList<WorkItem> WorkList(Experiment experiment, int? maxGames)
    {
        var list = new List<WorkItem>();
        int positions = experiment.Positions.Count;
        switch (experiment.Mode)
        {
            case ExperimentMode.Grade:
                for (int i = 0; i < positions; i++)
                {
                    list.Add(new WorkItem(i, i, 0, -1));
                }

                break;
            case ExperimentMode.VsReference:
                for (int i = 0; i < positions; i++)
                {
                    list.Add(experiment.CandidateSide == Side.Fox ? new WorkItem(i, i, 0, 1) : new WorkItem(i, i, 1, 0));
                }

                break;
            case ExperimentMode.HeadToHead:
                for (int i = 0; i < positions; i++)
                {
                    list.Add(new WorkItem(list.Count, i, 0, 1));
                    list.Add(new WorkItem(list.Count, i, 1, 0));
                }

                break;
            default:
                for (int i = 0; i < positions; i++)
                {
                    for (int j = 1; j < experiment.Agents.Count; j++)
                    {
                        list.Add(new WorkItem(list.Count, i, 0, j));
                        list.Add(new WorkItem(list.Count, i, j, 0));
                    }
                }

                break;
        }

        if (maxGames is int cap && cap < list.Count)
        {
            // Keep whole pairs so head-to-head scores stay paired.
            int keep = experiment.Mode is ExperimentMode.HeadToHead or ExperimentMode.Gauntlet ? cap - (cap % 2) : cap;
            list.RemoveRange(keep, list.Count - keep);
        }

        return list;
    }

    private static void CheckOrWriteSpec(Experiment experiment, string folder)
    {
        string path = Path.Combine(folder, SpecFile);
        if (File.Exists(path))
        {
            RunSpecJson? existing;
            try
            {
                existing = JsonSerializer.Deserialize(File.ReadAllText(path), SimJsonContext.Default.RunSpecJson);
            }
            catch (JsonException ex)
            {
                throw new ExperimentException($"{path} is not a run's spec.json: {ex.Message}", ex);
            }

            if (existing?.Sha256 != experiment.Sha256)
            {
                throw new ExperimentException($"{folder} holds a run of a different spec (sha256 {existing?.Sha256}, this spec {experiment.Sha256}).");
            }

            return;
        }

        var run = new JsonObject
        {
            ["format"] = "run/1",
            ["sha256"] = experiment.Sha256,
            ["commit"] = BuildInfo.Commit,
            ["version"] = BuildInfo.Version,
            ["positionsFile"] = experiment.PositionsPath,
            ["spec"] = experiment.Canonical.DeepClone(),
        };
        File.WriteAllText(path, run.ToJsonString(Indented) + "\n");
    }

    private static void CheckMemory(Experiment experiment, AgentFactory factory, int workers, SolverTable? table, Action<string>? log)
    {
        long perWorker = experiment.Agents.Sum(a => (long)factory.TtMegabytesOf(a.Spec));
        long tableMb = table is null ? 0 : (table.EntryCount + (1 << 20) - 1) >> 20;
        long total = (perWorker * workers) + tableMb;
        log?.Invoke($"memory: {perWorker} MB of transposition tables per worker x {workers} + {tableMb} MB solver table = {total} MB (limit {experiment.MaxMemoryMb} MB)");
        if (total > experiment.MaxMemoryMb)
        {
            throw new ExperimentException($"The run needs about {total} MB, over maxMemoryMb = {experiment.MaxMemoryMb}: use fewer workers or smaller tables.");
        }
    }

    private static void Execute(RunState state, WorkItem[] remaining, int total, AgentFactory factory, int workerCount,
        RunOptions options, CancellationToken ct)
    {
        var queue = new ConcurrentQueue<WorkItem>(remaining);
        using var results = new BlockingCollection<Completed>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? failure = null;
        var tasks = new Task[workerCount];
        for (int w = 0; w < workerCount; w++)
        {
            tasks[w] = Task.Run(() =>
            {
                try
                {
                    var worker = new Worker(state, factory, options.Clock);
                    while (!stop.IsCancellationRequested && queue.TryDequeue(out var item))
                    {
                        results.Add(worker.Run(item, stop.Token), CancellationToken.None);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
#pragma warning disable CA1031 // The first failure stops the run and is rethrown on the calling thread.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                    stop.Cancel();
                }
            }, CancellationToken.None);
        }

        var all = Task.WhenAll(tasks).ContinueWith(_ => results.CompleteAdding(), TaskScheduler.Default);
        var clock = options.Clock;
        var started = clock.Elapsed;
        var nextReport = started + options.ProgressInterval;
        int doneAtStart = state.Done.Count;
        var pending = new Dictionary<int, Completed>();
        int next = 0; // next index into remaining to write
        while (!results.IsCompleted)
        {
            // Not the caller's token: after a cancellation the finished games are still collected and written.
            if (results.TryTake(out var completed, 200, CancellationToken.None))
            {
                pending[completed.Item.Index] = completed;
                while (next < remaining.Length && !state.Stopped && pending.Remove(remaining[next].Index, out var ready))
                {
                    state.Write(ready);
                    next++;
                    options.AfterWrite?.Invoke(state.Done.Count);
                    if (state.Stopped)
                    {
                        stop.Cancel(); // the sequential test decided: games beyond this point are not counted
                    }
                }
            }

            var now = clock.Elapsed;
            if (options.Progress is not null && now >= nextReport)
            {
                nextReport = now + options.ProgressInterval;
                double minutes = (now - started).TotalMinutes;
                int doneHere = state.Done.Count - doneAtStart;
                double rate = minutes > 0 ? doneHere / minutes : 0;
                DateTimeOffset? finish = rate > 0 ? clock.UtcNow.AddMinutes((total - state.Done.Count) / rate) : null;
                options.Progress(new RunProgress(state.Done.Count, total, rate, finish, Summary.Headline(state.Experiment, state.GameLines, state.GradeLines)));
            }
        }

        all.Wait(CancellationToken.None);
        if (failure is not null)
        {
            throw failure is ExperimentException ? failure : new ExperimentException($"A worker failed: {failure.Message}", failure);
        }

        ct.ThrowIfCancellationRequested();
    }

    /// <summary>A planned game (or graded position): index, position index, and agent indices per side (-1: none).</summary>
    internal readonly record struct WorkItem(int Index, int Position, int FoxAgent, int HoundsAgent);

    /// <summary>A finished work item: its line, and its game record when one is to be kept.</summary>
    private sealed record Completed(WorkItem Item, GameLineJson? Game, GradeLineJson? Grade, GameRecord? Record);

    /// <summary>One worker's agents, created once and reset by NewGame before each game (spec 11.3).</summary>
    private sealed class Worker(RunState state, AgentFactory factory, IClock clock)
    {
        private readonly IAgent?[] _agents = new IAgent?[state.Experiment.Agents.Count];

        public Completed Run(WorkItem item, CancellationToken ct) =>
            item.HoundsAgent < 0 ? Grade(item, ct) : Play(item, ct);

        private IAgent Agent(int k, CancellationToken ct) =>
            _agents[k] ??= factory.Create(state.Experiment.Agents[k].Spec, state.Rules, null, ct);

        private Completed Play(WorkItem item, CancellationToken ct)
        {
            var e = state.Experiment;
            var opening = e.Positions[item.Position];
            var fox = e.Agents[item.FoxAgent];
            var hounds = e.Agents[item.HoundsAgent];
            var foxAgent = Agent(item.FoxAgent, ct);
            var houndsAgent = Agent(item.HoundsAgent, ct);
            var options = new GameOptions
            {
                FoxBudget = e.BudgetFor(Side.Fox, fox.Spec),
                HoundsBudget = e.BudgetFor(Side.Hounds, hounds.Spec),
                Start = opening.Position,
                Clock = clock,
                FoxPlayer = PlayerInfo.ForAgent(foxAgent, fox.Spec.WithAbsolutePaths().ToJson()),
                HoundsPlayer = PlayerInfo.ForAgent(houndsAgent, hounds.Spec.WithAbsolutePaths().ToJson()),
            };
            var before = clock.Elapsed;
            var record = GameRunner.Play(state.Rules, foxAgent, houndsAgent, options, Rng.ForGame(e.Seed, item.Index), ct);
            long ms = (long)(clock.Elapsed - before).TotalMilliseconds;
            if (record.Forfeit is { Cause: ForfeitCause.TimeLimit } timeout)
            {
                // D-21: the hard limit is a watchdog; a run with a timeout would not be reproducible.
                throw new ExperimentException($"Game {item.Index}: the {JsonNames.Of(timeout.Side)} agent hit its hard time limit ({timeout.Message}); the run is stopped.");
            }

            var line = GameMetrics.Line(state.Rules, state.Table, item, opening, fox, hounds, record, ms);
            bool blunder = (line.Blunders?.Fox ?? 0) + (line.Blunders?.Hounds ?? 0) > 0;
            bool keep = e.SaveRecords switch
            {
                RecordPolicy.All => true,
                RecordPolicy.BlundersAndSample => blunder || Rng.ForGame(e.Seed ^ SampleSalt, item.Index).NextDouble() < 0.01,
                _ => false,
            };
            return new Completed(item, line, null, keep ? record : null);
        }

        private Completed Grade(WorkItem item, CancellationToken ct)
        {
            var e = state.Experiment;
            var p = e.Positions[item.Position];
            var candidate = e.Agents[0];
            var agent = Agent(0, ct);
            var side = p.Position.SideToMove;
            agent.NewGame(state.Rules, side, Rng.ForGame(e.Seed, item.Index));
            var before = clock.Elapsed;
            var decision = agent.ChooseMove(p.Position, [], e.BudgetFor(side, candidate.Spec), ct);
            long ms = (long)(clock.Elapsed - before).TotalMilliseconds;
            return new Completed(item, null, GameMetrics.Grade(state.Rules, state.Table!, item, p, candidate, decision, ms), null);
        }
    }

    /// <summary>The run's lines so far, the writer, and the sequential test (calling thread only).</summary>
    private sealed class RunState(Experiment experiment, Rules rules, SolverTable? table, string folder)
    {
        private readonly List<double> _pairScores = [];
        private readonly Dictionary<int, GameLineJson> _byIndex = [];

        public Experiment Experiment { get; } = experiment;

        public Rules Rules { get; } = rules;

        public SolverTable? Table { get; } = table;

        public HashSet<int> Done { get; } = [];

        public List<GameLineJson> GameLines { get; } = [];

        public List<GradeLineJson> GradeLines { get; } = [];

        /// <summary>True once the sequential test has decided.</summary>
        public bool Stopped { get; private set; }

        private string GamesPath => Path.Combine(folder, GamesFile);

        public int Count(IReadOnlyList<WorkItem> work) => work.Count(w => Done.Contains(w.Index));

        /// <summary>Reads games.jsonl; a last line cut short by a crash is dropped from the file.</summary>
        public void LoadExisting(Action<string>? log)
        {
            if (!File.Exists(GamesPath))
            {
                return;
            }

            string text = File.ReadAllText(GamesPath);
            int good = 0;
            int lineStart = 0;
            while (lineStart < text.Length)
            {
                int end = text.IndexOf('\n', lineStart);
                if (end < 0 || !TryAdd(text[lineStart..end]))
                {
                    break; // an unfinished or broken line: everything from here is redone
                }

                lineStart = end + 1;
                good = lineStart;
            }

            if (good < text.Length)
            {
                log?.Invoke($"dropping a damaged last line of {GamesFile}");
                using var stream = new FileStream(GamesPath, FileMode.Open, FileAccess.Write);
                stream.SetLength(Encoding.UTF8.GetByteCount(text.AsSpan(0, good)));
            }
        }

        public void Write(Completed c)
        {
            string json = c.Game is not null
                ? JsonSerializer.Serialize(c.Game, SimJsonContext.Default.GameLineJson)
                : JsonSerializer.Serialize(c.Grade!, SimJsonContext.Default.GradeLineJson);
            if (c.Record is not null)
            {
                GameRecordFile.Write(c.Record, Path.Combine(folder, "records", $"game-{c.Item.Index:D6}{GameRecordFile.Extension}"));
            }

            File.AppendAllText(GamesPath, json + "\n");
            Add(c.Game, c.Grade);
        }

        private bool TryAdd(string line)
        {
            try
            {
                if (Experiment.Mode == ExperimentMode.Grade)
                {
                    var grade = JsonSerializer.Deserialize(line, SimJsonContext.Default.GradeLineJson);
                    if (grade is null)
                    {
                        return false;
                    }

                    Add(null, grade);
                }
                else
                {
                    var game = JsonSerializer.Deserialize(line, SimJsonContext.Default.GameLineJson);
                    if (game?.Result is null)
                    {
                        return false;
                    }

                    Add(game, null);
                }

                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private void Add(GameLineJson? game, GradeLineJson? grade)
        {
            if (grade is not null)
            {
                Done.Add(grade.Position);
                GradeLines.Add(grade);
                return;
            }

            Done.Add(game!.Game);
            GameLines.Add(game);
            _byIndex[game.Game] = game;
            if (Experiment.Sprt is { } sprt && !Stopped && game.Game % 2 == 1 && _byIndex.TryGetValue(game.Game - 1, out var first))
            {
                // Pair i is games 2i and 2i + 1; lines arrive in order, so pairs are tested in order too.
                _pairScores.Add(Summary.PairScore(first, game, "a"));
                Stopped = Stats.Sprt(_pairScores.ToArray(), sprt).Decision != SprtDecision.Continue;
            }
        }
    }
}

/// <summary>The build's version and git commit, from the assembly's informational version.</summary>
public static class BuildInfo
{
    /// <summary>"0.1.0+abcdef…".</summary>
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";

    /// <summary>The git commit the build came from, or "unknown".</summary>
    public static string Commit { get; } = Version.Contains('+', StringComparison.Ordinal) ? Version[(Version.IndexOf('+', StringComparison.Ordinal) + 1)..] : "unknown";

    internal static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
