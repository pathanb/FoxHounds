using System.CommandLine;
using System.Globalization;
using FoxHounds.Core;
using FoxHounds.Engine;
using FoxHounds.Engine.Evaluation;
using FoxHounds.Solver;

namespace FoxHounds.Cli;

/// <summary><c>fh analyse</c>: searches one position and, where a solver table exists, prints the true value.</summary>
internal static class AnalyseCommand
{
    public static Command Create()
    {
        var position = new Option<string?>("--position", "-p") { Description = "Position in notation (spec 5.6); default: the initial position." };
        var size = new Option<int?>("--n") { Description = "Board size for the initial position (default 8, or the size of --position)." };
        var depth = new Option<int?>("--depth", "-d") { Description = "Search depth in plies." };
        var nodes = new Option<long?>("--nodes") { Description = "Node budget." };
        var weights = new Option<string?>("--weights", "-w") { Description = "Weight file (default: the built-in lin-v0)." };
        var ttMb = new Option<int>("--tt-mb") { Description = "Transposition table size in MB.", DefaultValueFactory = _ => 64 };
        var adjudicate = new Option<bool>("--adjudicate") { Description = "Turn escape adjudication on (Classic default) for the search." };
        var solver = new Option<bool>("--solver") { Description = "Also print the exact value (builds or loads a table, n ≤ 11)." };

        var command = new Command("analyse", "Search a position with the minimax AI and print score, depth, nodes and line.")
        {
            position, size, depth, nodes, weights, ttMb, adjudicate, solver,
        };

        command.SetAction(parse =>
        {
            var output = parse.InvocationConfiguration.Output;
            string? notation = parse.GetValue(position);
            int n = parse.GetValue(size) ?? (notation is null ? 8 : int.Parse(notation.AsSpan(0, notation.IndexOf('/', StringComparison.Ordinal)), CultureInfo.InvariantCulture));
            var rules = new Rules(RuleSet.Classic with { BoardSize = n, AdjudicateEscape = parse.GetValue(adjudicate) });
            var s = notation is null ? rules.Initial() : rules.FromNotation(notation);
            var outcome = rules.Evaluate(s);
            output.WriteLine($"position {rules.ToNotation(s)}");
            if (outcome.IsOver)
            {
                output.WriteLine($"game over: {outcome.Result} ({outcome.Reason})");
                return 0;
            }

            var weightFile = parse.GetValue(weights) is { } path ? WeightFile.Load(path) : WeightFile.LinV0;
            var evaluator = weightFile.CreateEvaluator(n);
            var budget = new SearchBudget(parse.GetValue(depth) ?? (parse.GetValue(nodes) is null ? 12 : null), parse.GetValue(nodes), null, null);
            var search = new Search(rules, evaluator, new SearchOptions { TtMegabytes = parse.GetValue(ttMb) }, SystemClock.Instance);
            var result = search.Run(s, budget, CancellationToken.None);
            int scoreFox = Scores.ToFox(result.Score, s.SideToMove);
            double seconds = Math.Max(result.Elapsed.TotalSeconds, 1e-9);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"best {rules.Geo.FormatMove(result.Move)}  scoreFox {Scores.DescribeFox(scoreFox)}  depth {result.Depth}{(result.Exact ? " (exact)" : "")}  nodes {result.Nodes:N0}  {result.Nodes / seconds / 1e6:F2} M nodes/s  {result.Elapsed.TotalMilliseconds:F0} ms"));
            output.WriteLine("line " + string.Join(' ', search.PrincipalVariation.ToArray().Select(rules.Geo.FormatMove)));

            if (parse.GetValue(solver))
            {
                var table = SolverTableStore.Default.GetOrBuild(rules, null, CancellationToken.None);
                output.WriteLine($"solver {table.Get(s).Describe(s.SideToMove)}");
            }

            return 0;
        });

        return command;
    }
}
