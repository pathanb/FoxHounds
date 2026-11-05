using System.CommandLine;
using System.Globalization;
using FoxHounds.Core;

namespace FoxHounds.Cli;

/// <summary><c>fh perft</c>: ply-sequence counts for checking the rules (spec 15.2).</summary>
internal static class PerftCommand
{
    public static Command Create()
    {
        var size = new Option<int?>("--n") { Description = "Board size n, 4…16 (default 8, or the size of --position)." };
        var depth = new Option<int>("--depth", "-d") { Description = "Deepest perft depth in plies.", DefaultValueFactory = _ => 10 };
        var position = new Option<string?>("--position", "-p") { Description = "Start position in notation (spec 5.6); default: the initial position." };
        var fox = new Option<string?>("--fox") { Description = "Fox start square for the initial position, e.g. e1." };
        var first = new Option<Side>("--first") { Description = "Side that moves first.", DefaultValueFactory = _ => Side.Fox };
        var win = new Option<FoxWinCondition>("--win") { Description = "Fox win condition.", DefaultValueFactory = _ => FoxWinCondition.ReachHomeRow };
        var adjudicate = new Option<bool>("--adjudicate") { Description = "Turn escape adjudication on (off by default, as in spec 15.2)." };
        var divide = new Option<bool>("--divide") { Description = "Also print the count below each root ply at the deepest depth." };

        var command = new Command("perft", "Count ply sequences of exactly d plies (games that end earlier count 0).")
        {
            size, depth, position, fox, first, win, adjudicate, divide,
        };

        command.SetAction(parse =>
        {
            var output = parse.InvocationConfiguration.Output;
            string? notation = parse.GetValue(position);
            if (notation is not null && parse.GetValue(fox) is not null)
            {
                throw new ArgumentException("--fox applies to the initial position only; it cannot be combined with --position.");
            }

            var ruleSet = new RuleSet
            {
                BoardSize = parse.GetValue(size) ?? BoardSizeOf(notation) ?? 8,
                FirstMover = parse.GetValue(first),
                FoxWinCondition = parse.GetValue(win),
                AdjudicateEscape = parse.GetValue(adjudicate),
            };
            var rules = new Rules(ruleSet);
            GameState start = StartPosition(rules, notation, parse.GetValue(fox));
            int maxDepth = parse.GetValue(depth);

            output.WriteLine($"perft {rules.ToNotation(start)}  ({Describe(ruleSet)})");
            var clock = SystemClock.Instance;
            for (int d = 1; d <= maxDepth; d++)
            {
                TimeSpan t0 = clock.Elapsed;
                long count = Perft.Count(rules, start, d);
                TimeSpan spent = clock.Elapsed - t0;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{d,3} {count,18:N0} {spent.TotalMilliseconds,10:F1} ms"));
            }

            if (parse.GetValue(divide) && maxDepth >= 1)
            {
                foreach (var (move, count) in Perft.Divide(rules, start, maxDepth))
                {
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {rules.Geo.FormatMove(move),-8} {count:N0}"));
                }
            }

            return 0;
        });

        return command;
    }

    private static GameState StartPosition(Rules rules, string? notation, string? foxSquare)
    {
        if (notation is not null)
        {
            return rules.FromNotation(notation);
        }

        if (foxSquare is null)
        {
            return rules.Initial();
        }

        if (!rules.Geo.TryParseSquare(foxSquare, out int sq))
        {
            throw new ArgumentException($"'{foxSquare}' is not a playable square on the {rules.Geo.N}×{rules.Geo.N} board.");
        }

        return rules.Initial((byte)sq);
    }

    private static int? BoardSizeOf(string? notation)
    {
        if (notation is null)
        {
            return null;
        }

        int slash = notation.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && int.TryParse(notation.AsSpan(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    private static string Describe(RuleSet r) =>
        $"{r.FoxWinCondition}, {r.FirstMover} first, adjudication {(r.AdjudicateEscape ? "on" : "off")}";
}
