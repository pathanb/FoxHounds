using System.Globalization;
using System.Text;
using FoxHounds.Core;
using FoxHounds.Engine.Evaluation;
using FoxHounds.TestSupport;

namespace FoxHounds.Engine.Tests;

/// <summary>
/// T-ENG-05: on 50 fixed positions the search's move, score and node count equal a committed golden file, so any
/// change to the search's behaviour shows up, on Windows and Linux alike. Regenerate the file deliberately, after a
/// change that is meant to alter the search, with FH_WRITE_GOLDEN=1 and say so in the commit.
/// </summary>
public sealed class GoldenSearchTests
{
    private const string GoldenPath = "tests/FoxHounds.Engine.Tests/golden/search-50.txt";
    private static readonly ManualClock Clock = new(DateTimeOffset.UnixEpoch);

    [Fact]
    public void Fifty_fixed_positions_give_the_committed_moves_scores_and_node_counts()
    {
        string path = RepoRoot.File(GoldenPath);
        if (Environment.GetEnvironmentVariable("FH_WRITE_GOLDEN") == "1")
        {
            Write(path);
        }

        Assert.True(File.Exists(path), $"{GoldenPath} is missing; generate it with FH_WRITE_GOLDEN=1.");
        int count = 0;
        foreach (string line in File.ReadLines(path).Where(l => l.Length > 0 && l[0] != '#'))
        {
            // position, depth, move, score, nodes
            string[] f = line.Split('\t');
            var rules = new Rules(RuleSet.Classic with { BoardSize = int.Parse(f[0].Split('/')[0], CultureInfo.InvariantCulture) });
            var s = rules.FromNotation(f[0]);
            var result = Search(rules, s, int.Parse(f[1], CultureInfo.InvariantCulture));
            Assert.Equal(line, Line(rules, s, result));
            count++;
        }

        Assert.Equal(50, count);
    }

    private static SearchResult Search(Rules rules, in GameState s, int depth)
    {
        var search = new Search(rules, WeightFile.LinV0.CreateEvaluator(rules.Geo.N), new SearchOptions { TtMegabytes = 4 }, Clock);
        return search.Run(s, SearchBudget.Depth(depth), TestContext.Current.CancellationToken);
    }

    private static string Line(Rules rules, in GameState s, SearchResult r) => string.Create(CultureInfo.InvariantCulture,
        $"{rules.ToNotation(s)}\t{r.Depth}\t{rules.Geo.FormatMove(r.Move)}\t{r.Score}\t{r.Nodes}");

    private static void Write(string path)
    {
        var sb = new StringBuilder();
        sb.Append("# T-ENG-05 golden search results: position, depth, move, score, nodes (lin-v0, 4 MB table, fixed depth)\n");
        foreach (var (n, depth, count, seed) in new[] { (8, 10, 30, 805UL), (10, 8, 10, 1007UL), (16, 7, 10, 1606UL) })
        {
            // Positions from random play whose result is not yet proven at this depth, so the full depth is searched.
            var rules = new Rules(RuleSet.Classic with { BoardSize = n });
            var rng = new Rng(seed);
            for (int kept = 0; kept < count;)
            {
                var s = Positions.RandomPlayed(rules, rng);
                if (rules.Evaluate(s).IsOver)
                {
                    continue;
                }

                var result = Search(rules, s, depth);
                if (!Scores.IsProven(result.Score))
                {
                    sb.Append(Line(rules, s, result)).Append('\n');
                    kept++;
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString());
    }
}
