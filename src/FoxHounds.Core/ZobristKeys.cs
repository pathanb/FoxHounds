namespace FoxHounds.Core;

/// <summary>
/// Zobrist keys for one board size (spec 5.4): HoundKey[P], FoxKey[P] and SideKey, drawn in that order from
/// SplitMix64 seeded with 0x9E3779B97F4A7C15 ^ n, so hashes are identical across runs and machines.
/// </summary>
public sealed class ZobristKeys
{
    private readonly ulong[] _houndKeys;
    private readonly ulong[] _foxKeys;

    internal ZobristKeys(int boardSize, int squareCount)
    {
        ulong state = 0x9E3779B97F4A7C15UL ^ (ulong)boardSize;
        _houndKeys = new ulong[squareCount];
        _foxKeys = new ulong[squareCount];
        for (int i = 0; i < squareCount; i++)
        {
            _houndKeys[i] = SplitMix64.Next(ref state);
        }

        for (int i = 0; i < squareCount; i++)
        {
            _foxKeys[i] = SplitMix64.Next(ref state);
        }

        SideKey = SplitMix64.Next(ref state);
    }

    /// <summary>Key per square for a hound on it.</summary>
    public ReadOnlySpan<ulong> HoundKey => _houndKeys;

    /// <summary>Key per square for the fox on it.</summary>
    public ReadOnlySpan<ulong> FoxKey => _foxKeys;

    /// <summary>XORed in when the hounds are to move.</summary>
    public ulong SideKey { get; }

    internal ulong[] HoundKeyArray => _houndKeys;

    internal ulong[] FoxKeyArray => _foxKeys;

    /// <summary>Full recomputation of a position's hash: XOR of the hound keys, the fox key and the side key.</summary>
    public ulong Compute(UInt128 hounds, int fox, Side sideToMove)
    {
        ulong h = _foxKeys[fox];
        while (hounds != UInt128.Zero)
        {
            int sq = (int)UInt128.TrailingZeroCount(hounds);
            h ^= _houndKeys[sq];
            hounds &= hounds - UInt128.One;
        }

        if (sideToMove == Side.Hounds)
        {
            h ^= SideKey;
        }

        return h;
    }
}
