namespace FoxHounds.Solver;

/// <summary>
/// Ranks hound sets with the combinatorial number system (spec 10.2): for squares s₀ &lt; s₁ &lt; … &lt; s_{H−1},
/// rank = Σ C(sᵢ, i + 1), a bijection onto 0 … C(P, H) − 1. Rank order is colexicographic order.
/// </summary>
public sealed class HoundSetIndex
{
    private readonly long[,] _binomial;

    /// <summary>Creates the index for <paramref name="squareCount"/> squares and <paramref name="houndCount"/> hounds.</summary>
    public HoundSetIndex(int squareCount, int houndCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(squareCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(squareCount, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(houndCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(houndCount, squareCount);
        SquareCount = squareCount;
        HoundCount = houndCount;
        _binomial = new long[squareCount + 1, houndCount + 1];
        for (int p = 0; p <= squareCount; p++)
        {
            _binomial[p, 0] = 1;
            for (int k = 1; k <= Math.Min(p, houndCount); k++)
            {
                _binomial[p, k] = _binomial[p - 1, k - 1] + (k <= p - 1 ? _binomial[p - 1, k] : 0);
            }
        }

        Count = _binomial[squareCount, houndCount];
    }

    /// <summary>Number of squares P.</summary>
    public int SquareCount { get; }

    /// <summary>Number of hounds H.</summary>
    public int HoundCount { get; }

    /// <summary>Number of hound sets, C(P, H).</summary>
    public long Count { get; }

    /// <summary>C(p, k) for p ≤ P and k ≤ H.</summary>
    public long Binomial(int p, int k) => k > p ? 0 : _binomial[p, k];

    /// <summary>Rank of a hound set. The set must have exactly H bits, all below P.</summary>
    public long Rank(UInt128 hounds)
    {
        long rank = 0;
        int i = 0;
        while (hounds != UInt128.Zero)
        {
            int sq = (int)UInt128.TrailingZeroCount(hounds);
            rank += _binomial[sq, ++i];
            hounds &= hounds - UInt128.One;
        }

        return rank;
    }

    /// <summary>The hound set with the given rank, found greedily from the largest square down.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rank"/> is outside 0 … C(P, H) − 1.</exception>
    public UInt128 Unrank(long rank)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rank);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(rank, Count);
        UInt128 set = UInt128.Zero;
        int sq = SquareCount;
        for (int k = HoundCount; k >= 1; k--)
        {
            do
            {
                sq--;
            }
            while (Binomial(sq, k) > rank);

            rank -= Binomial(sq, k);
            set |= UInt128.One << sq;
        }

        return set;
    }

    /// <summary>Every hound set in rank order: element r has rank r.</summary>
    public UInt128[] EnumerateAll()
    {
        var sets = new UInt128[Count];
        Span<int> c = stackalloc int[HoundCount + 1];
        for (int i = 0; i < HoundCount; i++)
        {
            c[i] = i;
        }

        c[HoundCount] = SquareCount; // sentinel
        for (long r = 0; r < Count; r++)
        {
            UInt128 set = UInt128.Zero;
            for (int i = 0; i < HoundCount; i++)
            {
                set |= UInt128.One << c[i];
            }

            sets[r] = set;

            // Next combination in colex order: bump the lowest element that can move up, reset those below it.
            int j = 0;
            while (j < HoundCount && c[j] + 1 == c[j + 1])
            {
                j++;
            }

            if (j < HoundCount)
            {
                c[j]++;
                for (int i = 0; i < j; i++)
                {
                    c[i] = i;
                }
            }
        }

        return sets;
    }
}
