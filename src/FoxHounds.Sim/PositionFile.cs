using System.Globalization;
using System.Text;
using System.Text.Json;
using FoxHounds.Core;
using FoxHounds.Solver;

namespace FoxHounds.Sim;

/// <summary>A position from an opening set or a position suite, with its solver value when known.</summary>
/// <param name="Position">The position.</param>
/// <param name="Value">The solver's value for the side to move; null without a solver (n ≥ 12).</param>
public readonly record struct LabelledPosition(GameState Position, SolvedValue? Value)
{
    /// <summary>The winner with perfect play, when known.</summary>
    public Side? Winner => Value?.Winner(Position.SideToMove);
}

/// <summary>
/// An opening set or position suite file (spec 11.2, 14.5): comment lines starting with '#', then one position per
/// line in 5.6 notation, a tab, and the label (H or F for the winner, then the plies; empty without a solver).
/// A comment line <c># ruleSet={…}</c> records the ruleset the labels were computed for.
/// </summary>
public sealed class PositionFile
{
    private const string RuleSetPrefix = "ruleSet=";

    /// <summary>Creates a file's content.</summary>
    public PositionFile(RuleSet ruleSet, IReadOnlyList<string> comments, IReadOnlyList<LabelledPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentNullException.ThrowIfNull(positions);
        RuleSet = ruleSet;
        Comments = comments;
        Positions = positions;
    }

    /// <summary>The ruleset of the positions and labels.</summary>
    public RuleSet RuleSet { get; }

    /// <summary>Comment lines other than the ruleset line, without the leading "# ".</summary>
    public IReadOnlyList<string> Comments { get; }

    /// <summary>The positions, in file order.</summary>
    public IReadOnlyList<LabelledPosition> Positions { get; }

    /// <summary>The label of <paramref name="value"/> for a position with <paramref name="sideToMove"/> to move: "H44".</summary>
    public static string Label(SolvedValue value, Side sideToMove) => value.Label(sideToMove);

    /// <summary>Reads a file. <paramref name="expected"/>, when given, must agree on board size and win condition.</summary>
    /// <exception cref="InvalidDataException">The file is malformed or was made for other rules.</exception>
    public static PositionFile Load(string path, RuleSet? expected = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllText(path), path, expected);
    }

    /// <summary>Parses file text; see <see cref="Load"/>.</summary>
    public static PositionFile Parse(string text, string source = "position file", RuleSet? expected = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var comments = new List<string>();
        var lines = new List<(int Number, string Text)>();
        RuleSet? recorded = null;
        int number = 0;
        foreach (string raw in text.Split('\n'))
        {
            number++;
            string line = raw.TrimEnd('\r');
            if (line.StartsWith('#'))
            {
                string comment = line[1..].TrimStart();
                if (comment.StartsWith(RuleSetPrefix, StringComparison.Ordinal))
                {
                    recorded = ParseRuleSet(comment[RuleSetPrefix.Length..], source);
                }
                else
                {
                    comments.Add(comment);
                }
            }
            else if (line.Trim().Length > 0)
            {
                lines.Add((number, line));
            }
        }

        var ruleSet = recorded ?? expected ?? throw new InvalidDataException($"{source}: no ruleSet line and no rules given.");
        if (expected is not null && (expected.BoardSize != ruleSet.BoardSize || expected.FoxWinCondition != ruleSet.FoxWinCondition))
        {
            throw new InvalidDataException(
                $"{source}: made for {ruleSet.BoardSize}x{ruleSet.BoardSize} {ruleSet.FoxWinCondition}, not {expected.BoardSize}x{expected.BoardSize} {expected.FoxWinCondition}.");
        }

        var rules = new Rules(ruleSet);
        var positions = new List<LabelledPosition>(lines.Count);
        foreach (var (line, content) in lines)
        {
            string[] parts = content.Split('\t');
            GameState s;
            try
            {
                s = rules.FromNotation(parts[0].Trim());
            }
            catch (Exception ex) when (ex is FormatException or InvalidPositionException or ArgumentException)
            {
                throw new InvalidDataException($"{source}:{line}: {ex.Message}", ex);
            }

            string label = parts.Length > 1 ? parts[1].Trim() : "";
            positions.Add(new LabelledPosition(s, label.Length == 0 ? null : ParseLabel(label, s.SideToMove, $"{source}:{line}")));
        }

        return new PositionFile(ruleSet, comments, positions);
    }

    /// <summary>Decodes a label such as "H44" for a position with <paramref name="sideToMove"/> to move.</summary>
    public static SolvedValue ParseLabel(string label, Side sideToMove, string source = "label")
    {
        ArgumentNullException.ThrowIfNull(label);
        if (label.Length < 2 || label[0] is not ('H' or 'F')
            || !int.TryParse(label.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int plies) || plies < 1)
        {
            throw new InvalidDataException($"{source}: '{label}' is not a label (H or F, then the plies).");
        }

        var winner = label[0] == 'F' ? Side.Fox : Side.Hounds;
        return new SolvedValue(winner == sideToMove, plies);
    }

    /// <summary>The file text: comments, the ruleset line, then the positions.</summary>
    public string ToText()
    {
        var rules = new Rules(RuleSet);
        var sb = new StringBuilder();
        foreach (string comment in Comments)
        {
            sb.Append("# ").Append(comment).Append('\n');
        }

        sb.Append("# ").Append(RuleSetPrefix).Append(JsonSerializer.Serialize(RuleSetJson.From(RuleSet), SimJsonContext.Default.RuleSetJson)).Append('\n');
        foreach (var p in Positions)
        {
            sb.Append(rules.ToNotation(p.Position)).Append('\t');
            if (p.Value is { } v)
            {
                sb.Append(v.Label(p.Position.SideToMove));
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Writes the file, creating its folder.</summary>
    public void Write(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, ToText());
    }

    /// <summary>
    /// The ruleset line: JSON as written by <see cref="ToText"/>, or the form of the spec 14.5 example, a preset name
    /// followed by field=value pairs ("classic adjudicateEscape=false").
    /// </summary>
    private static RuleSet ParseRuleSet(string text, string source)
    {
        try
        {
            if (text.TrimStart().StartsWith('{'))
            {
                var dto = JsonSerializer.Deserialize(text, SimJsonContext.Default.RuleSetJson)
                    ?? throw new InvalidDataException($"{source}: empty ruleSet line.");
                return dto.ToRuleSet();
            }

            string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var ruleSet = words.Length == 0 ? throw new FormatException("no preset") : words[0] switch
            {
                "classic" => RuleSet.Classic,
                "gilith" => RuleSet.Gilith,
                var other => throw new FormatException($"'{other}' is not a preset (classic, gilith)"),
            };
            foreach (string word in words.Skip(1))
            {
                string[] pair = word.Split('=', 2);
                string value = pair.Length == 2 ? pair[1] : throw new FormatException($"'{word}' is not field=value");
                ruleSet = pair[0] switch
                {
                    "boardSize" => ruleSet with { BoardSize = int.Parse(value, CultureInfo.InvariantCulture) },
                    "adjudicateEscape" => ruleSet with { AdjudicateEscape = bool.Parse(value) },
                    "foxStart" => ruleSet with { FoxStart = JsonNames.Parse<FoxStart>(value, "foxStart") },
                    "firstMover" => ruleSet with { FirstMover = JsonNames.Parse<Side>(value, "firstMover") },
                    "foxWinCondition" => ruleSet with { FoxWinCondition = JsonNames.Parse<FoxWinCondition>(value, "foxWinCondition") },
                    "fixedFoxColumn" => ruleSet with { FixedFoxColumn = int.Parse(value, CultureInfo.InvariantCulture) },
                    var field => throw new FormatException($"unknown ruleset field '{field}'"),
                };
            }

            return ruleSet;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
        {
            throw new InvalidDataException($"{source}: bad ruleSet line: {ex.Message}", ex);
        }
    }
}
