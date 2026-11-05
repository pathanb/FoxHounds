namespace FoxHounds.Core.Tests;

/// <summary>T-CORE-03: perft equals spec 15.2 exactly (Classic, fox first, default start, adjudication off).</summary>
public sealed class PerftTests
{
    public static TheoryData<int, string, long[]> Vectors => new()
    {
        { 4, "c1", [2, 6, 13, 27, 30, 60, 126, 170, 266, 110] },
        { 6, "c1", [2, 10, 40, 168, 418, 1_730, 5_917, 23_818, 56_283, 228_556] },
        { 8, "e1", [2, 14, 56, 344, 1_032, 6_264, 23_387, 141_427, 442_493, 2_674_123] },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Perft_matches_spec_15_2(int n, string foxStart, long[] expected)
    {
        var rules = TestPositions.Classic(n);
        var start = rules.Initial();
        Assert.Equal(foxStart, rules.Geo.SquareName(start.Fox));
        for (int d = 1; d <= expected.Length; d++)
        {
            Assert.Equal(expected[d - 1], Perft.Count(rules, start, d));
        }
    }

    [Fact]
    public void Perft_zero_is_one_and_divide_sums_to_perft()
    {
        var rules = TestPositions.Classic(8);
        var start = rules.Initial();
        Assert.Equal(1, Perft.Count(rules, start, 0));
        var divide = Perft.Divide(rules, start, 7);
        Assert.Equal(Perft.Count(rules, start, 7), divide.Sum(d => d.Count));
        Assert.Equal(2, divide.Count);
    }

    [Fact]
    public void Perft_of_a_finished_position_is_zero_beyond_depth_zero()
    {
        var rules = TestPositions.Classic(8);
        var trapped = rules.FromNotation("8/a1/b2,b8,d8,f8/F");
        Assert.Equal(1, Perft.Count(rules, trapped, 0));
        Assert.Equal(0, Perft.Count(rules, trapped, 1));
        Assert.Empty(Perft.Divide(rules, trapped, 3));
    }
}
