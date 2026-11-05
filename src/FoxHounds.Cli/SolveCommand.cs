using System.CommandLine;
using System.Globalization;
using FoxHounds.Core;
using FoxHounds.Solver;

namespace FoxHounds.Cli;

/// <summary><c>fh solve</c>: builds exact solver tables and prints the results of spec 10.7.</summary>
internal static class SolveCommand
{
    private enum WinChoice
    {
        Both,
        ReachHomeRow,
        HoundsImmobile,
    }

    public static Command Create()
    {
        var size = new Option<int>("--n") { Description = "Board size n, 4…11.", DefaultValueFactory = _ => 8 };
        var win = new Option<WinChoice>("--win") { Description = "Win condition to solve (Classic = ReachHomeRow, Gilith = HoundsImmobile).", DefaultValueFactory = _ => WinChoice.Both };
        var threads = new Option<int>("--threads") { Description = "Worker threads for the build.", DefaultValueFactory = _ => Environment.ProcessorCount };
        var position = new Option<string?>("--position", "-p") { Description = "Also print the value of this position (notation, spec 5.6)." };
        var save = new Option<string?>("--save") { Description = "Save the table to this file (one win condition only)." };
        var cache = new Option<bool>("--cache") { Description = "Load from / save to the user data folder instead of building fresh." };

        var command = new Command("solve", "Solve every position of a board exactly and print the results from each fox start.")
        {
            size, win, threads, position, save, cache,
        };

        command.SetAction(parse =>
        {
            var output = parse.InvocationConfiguration.Output;
            int n = parse.GetValue(size);
            var choice = parse.GetValue(win);
            string? savePath = parse.GetValue(save);
            if (savePath is not null && choice == WinChoice.Both)
            {
                throw new ArgumentException("--save needs one win condition: add --win ReachHomeRow or --win HoundsImmobile.");
            }

            var conditions = choice switch
            {
                WinChoice.ReachHomeRow => [FoxWinCondition.ReachHomeRow],
                WinChoice.HoundsImmobile => [FoxWinCondition.HoundsImmobile],
                _ => new[] { FoxWinCondition.ReachHomeRow, FoxWinCondition.HoundsImmobile },
            };

            foreach (var condition in conditions)
            {
                var rules = new Rules(new RuleSet { BoardSize = n, FoxWinCondition = condition, AdjudicateEscape = false });
                var clock = SystemClock.Instance;
                TimeSpan t0 = clock.Elapsed;
                var table = parse.GetValue(cache)
                    ? SolverTableStore.Default.GetOrBuild(rules, null, CancellationToken.None)
                    : SolverTable.Build(rules, parse.GetValue(threads), null, CancellationToken.None);
                TimeSpan spent = clock.Elapsed - t0;
                string label = condition == FoxWinCondition.ReachHomeRow ? "Classic" : "Gilith";
                foreach (var first in new[] { Side.Fox, Side.Hounds })
                {
                    var r = new Rules(rules.Config with { FirstMover = first });
                    var results = r.FoxStartSquares.Select(sq => $"{r.Geo.SquareName(sq)}: {table.Get(r.Initial(sq)).Describe(first)}");
                    output.WriteLine($"{label,-8} n={n} {(first == Side.Fox ? "fox" : "hounds")} first: {string.Join("; ", results)}");
                }

                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"         {table.EntryCount:N0} entries ({table.EntryCount / 1e6:F1} MB) in {spent.TotalSeconds:F2} s"));

                if (parse.GetValue(position) is { } text)
                {
                    var s = rules.FromNotation(text);
                    output.WriteLine($"         {text}: {table.Get(s).Describe(s.SideToMove)}");
                }

                if (savePath is not null)
                {
                    table.Save(savePath);
                    output.WriteLine($"         saved to {savePath}");
                }
            }

            return 0;
        });

        return command;
    }
}
