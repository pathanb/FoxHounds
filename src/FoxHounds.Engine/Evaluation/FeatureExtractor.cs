using FoxHounds.Core;

namespace FoxHounds.Engine.Evaluation;

/// <summary>The evaluation features of spec 9.1, in this order. Append new ones; never renumber.</summary>
public enum FeatureId
{
    /// <summary>(n − 1) − foxRow.</summary>
    FoxAdvance,

    /// <summary>Empty diagonal neighbours of the fox, whoever is to move.</summary>
    FoxMobility,

    /// <summary>Legal hound plies as if the hounds were to move.</summary>
    HoundMobility,

    /// <summary>Empty squares reachable by fox steps with the hounds frozen, excluding the fox's own square.</summary>
    FoxRegion,

    /// <summary>1 if that region contains a row-0 square (or the fox is on row 0), else 0.</summary>
    GoalReachable,

    /// <summary>n − (distance to the nearest row-0 square) if reachable, else 0.</summary>
    GoalCloseness,

    /// <summary>Hounds with row &gt; foxRow.</summary>
    HoundsPassed,

    /// <summary>maxHoundRow − minHoundRow.</summary>
    HoundSpread,

    /// <summary>Sum over hounds of (n − 1 − row): an upper bound on the hound plies left.</summary>
    HoundMovesLeft,

    /// <summary>max(0, foxRow − minHoundRow).</summary>
    EscapeDistance,

    /// <summary>1 if the fox is to move, else 0.</summary>
    FoxToMove,
}

/// <summary>
/// Extracts the raw feature counts of spec 9.1. Whether a feature helps the fox or the hounds is decided only by the
/// sign of its weight. Allocation-free; one flood fill serves features 4 to 6. Boards up to 11×11 (P ≤ 64) take a
/// 64-bit path, because UInt128 arithmetic is emulated in software and the flood fill dominates the cost (spec 9.4).
/// </summary>
public static class FeatureExtractor
{
    /// <summary>Number of features.</summary>
    public const int Count = 11;

    private static readonly string[] Names = Enum.GetNames<FeatureId>();
    private static readonly Tables64?[] Small = BuildSmallTables();

    /// <summary>Feature names in <see cref="FeatureId"/> order, as used in weight files.</summary>
    public static IReadOnlyList<string> FeatureNames => Names;

    /// <summary>Fills <paramref name="values"/> (at least <see cref="Count"/> long) in <see cref="FeatureId"/> order.</summary>
    public static void Extract(in GameState s, Span<int> values)
    {
        var small = Small[s.N];
        if (small is not null)
        {
            Extract64(s, small, values);
        }
        else
        {
            Extract128(s, values);
        }
    }

    /// <summary>The general path on UInt128 bitboards, for every board size.</summary>
    internal static void Extract128(in GameState s, Span<int> values)
    {
        var geo = Geometry.For(s.N);
        int n = geo.N;
        var row = geo.Row;
        var foxSteps = geo.FoxSteps;
        var houndSteps = geo.HoundSteps;
        UInt128 hounds = s.Hounds;
        UInt128 foxBit = UInt128.One << s.Fox;
        UInt128 empty = geo.AllSquares & ~(hounds | foxBit);
        int foxRow = row[s.Fox];

        // Hound features in one pass.
        int houndMobility = 0;
        int passed = 0;
        int minRow = int.MaxValue;
        int maxRow = -1;
        int movesLeft = 0;
        UInt128 h = hounds;
        while (h != UInt128.Zero)
        {
            int sq = (int)UInt128.TrailingZeroCount(h);
            int r = row[sq];
            houndMobility += (int)UInt128.PopCount(houndSteps[sq] & empty);
            passed += r > foxRow ? 1 : 0;
            minRow = Math.Min(minRow, r);
            maxRow = Math.Max(maxRow, r);
            movesLeft += n - 1 - r;
            h &= h - UInt128.One;
        }

        // Breadth-first flood fill from the fox through hound-free squares, one layer per step.
        UInt128 passable = geo.AllSquares & ~hounds;
        UInt128 visited = foxBit;
        UInt128 frontier = foxBit;
        UInt128 homeRow = geo.RowMask[0];
        int goalDistance = foxRow == 0 ? 0 : -1;
        int distance = 0;
        while (frontier != UInt128.Zero)
        {
            UInt128 next = UInt128.Zero;
            UInt128 f = frontier;
            while (f != UInt128.Zero)
            {
                next |= foxSteps[(int)UInt128.TrailingZeroCount(f)];
                f &= f - UInt128.One;
            }

            next &= passable & ~visited;
            if (next == UInt128.Zero)
            {
                break;
            }

            distance++;
            if (goalDistance < 0 && (next & homeRow) != UInt128.Zero)
            {
                goalDistance = distance;
            }

            visited |= next;
            frontier = next;
        }

        Store(values, n, foxRow, (int)UInt128.PopCount(foxSteps[s.Fox] & empty), houndMobility, (int)UInt128.PopCount(visited) - 1,
            goalDistance, passed, minRow, maxRow, movesLeft, s.SideToMove);
    }

    /// <summary>The same computation on 64-bit bitboards, for P ≤ 64 (n ≤ 11).</summary>
    private static void Extract64(in GameState s, Tables64 t, Span<int> values)
    {
        int n = t.N;
        byte[] row = t.Row;
        ulong[] foxSteps = t.FoxSteps;
        ulong[] houndSteps = t.HoundSteps;
        ulong hounds = (ulong)s.Hounds;
        ulong foxBit = 1UL << s.Fox;
        ulong empty = t.All & ~(hounds | foxBit);
        int foxRow = row[s.Fox];

        int houndMobility = 0;
        int passed = 0;
        int minRow = int.MaxValue;
        int maxRow = -1;
        int movesLeft = 0;
        ulong h = hounds;
        while (h != 0)
        {
            int sq = System.Numerics.BitOperations.TrailingZeroCount(h);
            int r = row[sq];
            houndMobility += System.Numerics.BitOperations.PopCount(houndSteps[sq] & empty);
            passed += r > foxRow ? 1 : 0;
            minRow = Math.Min(minRow, r);
            maxRow = Math.Max(maxRow, r);
            movesLeft += n - 1 - r;
            h &= h - 1;
        }

        ulong passable = t.All & ~hounds;
        ulong visited = foxBit;
        ulong frontier = foxBit;
        int goalDistance = foxRow == 0 ? 0 : -1;
        int distance = 0;
        while (frontier != 0)
        {
            ulong next = 0;
            ulong f = frontier;
            while (f != 0)
            {
                next |= foxSteps[System.Numerics.BitOperations.TrailingZeroCount(f)];
                f &= f - 1;
            }

            next &= passable & ~visited;
            if (next == 0)
            {
                break;
            }

            distance++;
            if (goalDistance < 0 && (next & t.Home) != 0)
            {
                goalDistance = distance;
            }

            visited |= next;
            frontier = next;
        }

        Store(values, n, foxRow, System.Numerics.BitOperations.PopCount(foxSteps[s.Fox] & empty), houndMobility,
            System.Numerics.BitOperations.PopCount(visited) - 1, goalDistance, passed, minRow, maxRow, movesLeft, s.SideToMove);
    }

    private static void Store(Span<int> values, int n, int foxRow, int foxMobility, int houndMobility, int region, int goalDistance,
        int passed, int minRow, int maxRow, int movesLeft, Side sideToMove)
    {
        values[(int)FeatureId.FoxAdvance] = n - 1 - foxRow;
        values[(int)FeatureId.FoxMobility] = foxMobility;
        values[(int)FeatureId.HoundMobility] = houndMobility;
        values[(int)FeatureId.FoxRegion] = region;
        values[(int)FeatureId.GoalReachable] = goalDistance >= 0 ? 1 : 0;
        values[(int)FeatureId.GoalCloseness] = goalDistance >= 0 ? n - goalDistance : 0;
        values[(int)FeatureId.HoundsPassed] = passed;
        values[(int)FeatureId.HoundSpread] = maxRow - minRow;
        values[(int)FeatureId.HoundMovesLeft] = movesLeft;
        values[(int)FeatureId.EscapeDistance] = Math.Max(0, foxRow - minRow);
        values[(int)FeatureId.FoxToMove] = sideToMove == Side.Fox ? 1 : 0;
    }

    private static Tables64?[] BuildSmallTables()
    {
        var tables = new Tables64?[RuleSet.MaxBoardSize + 1];
        for (int n = RuleSet.MinBoardSize; n <= RuleSet.MaxBoardSize; n++)
        {
            var geo = Geometry.For(n);
            if (geo.SquareCount > 64)
            {
                continue;
            }

            var fox = new ulong[geo.SquareCount];
            var hound = new ulong[geo.SquareCount];
            for (int sq = 0; sq < geo.SquareCount; sq++)
            {
                fox[sq] = (ulong)geo.FoxSteps[sq];
                hound[sq] = (ulong)geo.HoundSteps[sq];
            }

            tables[n] = new Tables64(n, geo.Row.ToArray(), fox, hound, (ulong)geo.AllSquares, (ulong)geo.RowMask[0]);
        }

        return tables;
    }

    /// <summary>Immutable 64-bit copies of the geometry tables for one small board.</summary>
    private sealed record Tables64(int N, byte[] Row, ulong[] FoxSteps, ulong[] HoundSteps, ulong All, ulong Home);
}
