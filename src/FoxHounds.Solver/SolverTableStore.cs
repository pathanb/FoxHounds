using FoxHounds.Core;

namespace FoxHounds.Solver;

/// <summary>
/// Solver tables cached on disk (spec 10.6): built on demand, saved in the user data folder, loaded on later use.
/// Tables are never committed to the repository.
/// </summary>
/// <remarks>
/// Thread-safe. Concurrent requests for one table share a single build. Each caller waits with its own cancellation
/// token and receives progress through its own callback; the shared build is cancelled only when every waiting caller
/// has given up. A table that was built but could not be saved is still returned (see <see cref="Warn"/>).
/// </remarks>
public sealed class SolverTableStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, BuildEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>Creates a store in <paramref name="directory"/>.</summary>
    public SolverTableStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
    }

    /// <summary>The default store, in the user's local application data folder.</summary>
    public static SolverTableStore Default { get; } = new(DefaultDirectory);

    /// <summary>
    /// {LocalApplicationData}/FoxHounds/tables. When the platform reports no usable data folder (for example a
    /// headless Linux account without a home directory), {temp}/FoxHounds/tables, so the path is always absolute.
    /// </summary>
    public static string DefaultDirectory
    {
        get
        {
            string data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
            string root = !string.IsNullOrEmpty(data) && Path.IsPathRooted(data) ? data : Path.GetTempPath();
            return Path.Combine(root, "FoxHounds", "tables");
        }
    }

    /// <summary>The folder holding the table files.</summary>
    public string Directory { get; }

    /// <summary>Worker threads for builds; defaults to the processor count.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    /// <summary>Receives non-fatal problems, such as a table that could not be saved. Called on the building thread.</summary>
    public Action<string>? Warn { get; init; }

    /// <summary>File name of the table for a board size and win condition, e.g. n8-reachHomeRow.fhsolv.</summary>
    public string PathFor(int n, FoxWinCondition winCondition)
    {
        string condition = winCondition == FoxWinCondition.ReachHomeRow ? "reachHomeRow" : "houndsImmobile";
        return Path.Combine(Directory, $"n{n}-{condition}.fhsolv");
    }

    /// <summary>True when a table file for <paramref name="config"/> exists (it is not opened).</summary>
    public bool Exists(RuleSet config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return File.Exists(PathFor(config.BoardSize, config.FoxWinCondition));
    }

    /// <summary>
    /// The table for <paramref name="rules"/>: from memory, else from disk, else built and saved. A corrupt or
    /// mismatched file is rebuilt. Blocks until the table is ready or <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="NotSupportedException">The board is too large for the solver.</exception>
    public SolverTable GetOrBuild(Rules rules, IProgress<double>? progress, CancellationToken ct) =>
        GetOrBuildAsync(rules, progress, ct).GetAwaiter().GetResult();

    /// <summary>Asynchronous form of <see cref="GetOrBuild"/>; the build runs on the thread pool.</summary>
    public async Task<SolverTable> GetOrBuildAsync(Rules rules, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!SolverTable.IsSupported(rules.Geo.N))
        {
            // Fail fast with the solver's own message instead of starting a doomed build.
            return SolverTable.Build(rules, progress, ct);
        }

        string path = PathFor(rules.Geo.N, rules.Config.FoxWinCondition);
        BuildEntry entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(path, out entry!) || entry.Task.IsFaulted || entry.Task.IsCanceled)
            {
                entry = new BuildEntry();
                var started = entry;
                entry.Task = Task.Run(() => LoadOrBuild(path, rules, started), CancellationToken.None);
                _entries[path] = entry;
            }

            entry.Waiters++;
            entry.AddListener(progress);
        }

        try
        {
            return await entry.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                entry.Waiters--;
                entry.RemoveListener(progress);

                bool abandoned = entry.Waiters == 0 && !entry.Task.IsCompleted;
                if (abandoned)
                {
                    entry.Cancellation.Cancel();
                }

                if ((abandoned || entry.Task.IsFaulted || entry.Task.IsCanceled)
                    && _entries.TryGetValue(path, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(path); // the next request starts afresh
                }
            }
        }
    }

    private SolverTable LoadOrBuild(string path, Rules rules, BuildEntry entry)
    {
        var ct = entry.Cancellation.Token;
        if (File.Exists(path))
        {
            try
            {
                return SolverTable.Load(path, rules.Config);
            }
            catch (InvalidDataException ex)
            {
                Warn?.Invoke($"{ex.Message} Rebuilding it.");
            }
            catch (IOException ex)
            {
                Warn?.Invoke($"{path}: could not read ({ex.Message}). Rebuilding it.");
            }
        }

        var table = SolverTable.Build(rules, MaxDegreeOfParallelism, entry, ct);
        try
        {
            table.Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Caching is a side effect: a table that took seconds to build is still returned.
            Warn?.Invoke($"{path}: could not save the solver table ({ex.Message}); it will be rebuilt next time.");
        }

        return table;
    }

    /// <summary>One shared build: its task, the callers waiting for it, and their progress callbacks.</summary>
    private sealed class BuildEntry : IProgress<double>
    {
        private readonly Lock _listenersLock = new();
        private readonly List<IProgress<double>> _listeners = [];

        public CancellationTokenSource Cancellation { get; } = new();

        public Task<SolverTable> Task { get; set; } = null!;

        public int Waiters { get; set; }

        public void AddListener(IProgress<double>? progress)
        {
            if (progress is not null)
            {
                lock (_listenersLock)
                {
                    _listeners.Add(progress);
                }
            }
        }

        public void RemoveListener(IProgress<double>? progress)
        {
            if (progress is not null)
            {
                lock (_listenersLock)
                {
                    _listeners.Remove(progress);
                }
            }
        }

        public void Report(double value)
        {
            IProgress<double>[] listeners;
            lock (_listenersLock)
            {
                listeners = [.. _listeners];
            }

            foreach (var listener in listeners)
            {
                listener.Report(value);
            }
        }
    }
}
