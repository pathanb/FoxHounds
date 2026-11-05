using System.Collections.Concurrent;
using FoxHounds.Core;

namespace FoxHounds.Solver;

/// <summary>
/// Solver tables cached on disk (spec 10.6): built on demand, saved in the user data folder, loaded on later use.
/// Tables are never committed to the repository. Thread-safe; concurrent requests for one table build it once.
/// </summary>
public sealed class SolverTableStore
{
    private readonly ConcurrentDictionary<string, Lazy<SolverTable>> _loaded = new(StringComparer.Ordinal);

    /// <summary>Creates a store in <paramref name="directory"/>.</summary>
    public SolverTableStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = directory;
    }

    /// <summary>The default store, in the user's local application data folder.</summary>
    public static SolverTableStore Default { get; } = new(DefaultDirectory);

    /// <summary>{LocalApplicationData}/FoxHounds/tables.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoxHounds", "tables");

    /// <summary>The folder holding the table files.</summary>
    public string Directory { get; }

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
    /// mismatched file is rebuilt.
    /// </summary>
    /// <exception cref="NotSupportedException">The board is too large for the solver.</exception>
    public SolverTable GetOrBuild(Rules rules, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);
        string path = PathFor(rules.Geo.N, rules.Config.FoxWinCondition);
        var lazy = _loaded.GetOrAdd(path, p => new Lazy<SolverTable>(
            () => LoadOrBuild(p, rules, progress, ct), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            _loaded.TryRemove(new KeyValuePair<string, Lazy<SolverTable>>(path, lazy));
            throw;
        }
    }

    private static SolverTable LoadOrBuild(string path, Rules rules, IProgress<double>? progress, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            try
            {
                return SolverTable.Load(path, rules.Config);
            }
            catch (InvalidDataException)
            {
                // Corrupt or mismatched: rebuild below and overwrite.
            }
        }

        var table = SolverTable.Build(rules, progress, ct);
        table.Save(path);
        return table;
    }
}
