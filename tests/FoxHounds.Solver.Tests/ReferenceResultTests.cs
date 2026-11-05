using FoxHounds.Core;

namespace FoxHounds.Solver.Tests;

/// <summary>T-SOL-01 and T-SOL-02: the reference results of spec 10.7, and the prototype's values for positions
/// that are not reachable from the start.</summary>
public sealed class ReferenceResultTests
{
    // Spec 10.7, plus the 5×5 and 7×7 Gilith rows that the prototype prints (reference/fh_proto_all.txt).
    public static TheoryData<string, int, string, string> Rows => new()
    {
        { "classic", 4, "Hounds win in 8", "Hounds win in 7" },
        { "classic", 5, "Fox wins in 7", "Fox wins in 8" },
        { "classic", 6, "Fox wins in 25", "Hounds win in 21" },
        { "classic", 7, "Fox wins in 15", "Fox wins in 12" },
        { "classic", 8, "Hounds win in 44", "Hounds win in 43" },
        { "gilith", 4, "Hounds win in 8", "Hounds win in 7" },
        { "gilith", 5, "Fox wins in 17", "Fox wins in 16" },
        { "gilith", 6, "Fox wins in 31", "Hounds win in 21" },
        { "gilith", 7, "Fox wins in 37", "Fox wins in 36" },
        { "gilith", 8, "Hounds win in 44", "Hounds win in 43" },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Every_reference_row_holds_from_every_fox_start(string ruleset, int n, string foxFirst, string houndsFirst)
    {
        var table = TestTables.Get(n, TestTables.ParseCondition(ruleset));
        foreach (var (first, expected) in new[] { (Side.Fox, foxFirst), (Side.Hounds, houndsFirst) })
        {
            var rules = TestTables.Rules(n, table.WinCondition, first);
            Assert.Equal(expected, table.Get(rules.Initial()).Describe(first));

            // Spec 10.7: on 4×4, 6×6 and 8×8 every start square gives the same result (the prototype shows it for 5 and 7 too).
            foreach (byte start in rules.FoxStartSquares)
            {
                Assert.Equal(expected, table.Get(rules.Initial(start)).Describe(first));
            }
        }
    }

    [Fact]
    public void Published_gilith_solve_agrees_on_winner_and_distance()
    {
        // T-SOL-01: gilith.com: hounds win in 8 on 4×4 and in 44 on 8×8; the fox wins on 6×6.
        Assert.Equal("Hounds win in 8", Initial(TestTables.Gilith(4)));
        Assert.Equal("Hounds win in 44", Initial(TestTables.Gilith(8)));
        Assert.StartsWith("Fox wins", Initial(TestTables.Gilith(6)), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Published_gilith_solve_agrees_on_10x10()
    {
        // T-SOL-01 stretch: a 212 MB table, about 10 s in Release.
        var table = SolverTable.Build(TestTables.Rules(10, FoxWinCondition.HoundsImmobile), null, TestContext.Current.CancellationToken);
        Assert.Equal("Hounds win in 72", Initial(table));
    }

    [Theory]
    [InlineData("8/e1/b8,d8,f8,h8/F", "Hounds win in 44")]
    [InlineData("8/d4/a7,c7,e7,f6/H", "Hounds win in 33")]
    [InlineData("8/c5/b6,d6,f6,h6/F", "Hounds win in 28")]
    [InlineData("8/g3/b6,d6,e5,g5/F", "Hounds win in 22")]
    [InlineData("8/b4/b8,h8,e7,d6/F", "Fox wins in 7")]
    [InlineData("8/f2/f8,h8,e7,d6/F", "Fox wins in 15")]
    [InlineData("8/d2/b8,f8,e7,h6/F", "Fox wins in 11")]
    public void Feature_and_opening_positions_of_the_spec(string notation, string expected)
    {
        // The solver row of spec 9.5 and the labelled openings of spec 14.5.
        var table = TestTables.Classic(8);
        var s = table.Rules.FromNotation(notation);
        Assert.Equal(expected, table.Get(s).Describe(s.SideToMove));
    }

    [Fact]
    public void Prototype_values_of_800_random_positions()
    {
        // reference/solver_vectors.txt: random legal positions on 4×4…8×8, most of them unreachable in play.
        int checkedCount = 0;
        foreach (string line in File.ReadLines(RepoRoot.File("reference/solver_vectors.txt")))
        {
            if (line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split(' ');
            int n = int.Parse(parts[1].AsSpan(0, parts[1].IndexOf('/')), System.Globalization.CultureInfo.InvariantCulture);
            var table = TestTables.Get(n, TestTables.ParseCondition(parts[0]));
            var s = table.Rules.FromNotation(parts[1]);
            Assert.Equal(parts[2], table.Get(s).Label(s.SideToMove));
            checkedCount++;
        }

        Assert.Equal(800, checkedCount);
    }

    private static string Initial(SolverTable table) => table.Get(table.Rules.Initial()).Describe(Side.Fox);
}
