using System.Numerics;

namespace FoxHounds.Core;

/// <summary>SplitMix64 (Steele, Lea, Flood 2014). Seeds <see cref="Rng"/> and draws the Zobrist keys.</summary>
public static class SplitMix64
{
    /// <summary>Advances <paramref name="state"/> and returns the next output.</summary>
    public static ulong Next(ref ulong state)
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}

/// <summary>
/// The only source of randomness in the solution (spec 4.3, 5.5): xoshiro256** with its state filled by
/// SplitMix64 from a 64-bit seed. Mutable and not thread-safe: give each thread or agent its own instance.
/// </summary>
public sealed class Rng
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    /// <summary>Creates a generator whose whole output stream is a pure function of <paramref name="seed"/>.</summary>
    public Rng(ulong seed)
    {
        Seed = seed;
        ulong state = seed;
        _s0 = SplitMix64.Next(ref state);
        _s1 = SplitMix64.Next(ref state);
        _s2 = SplitMix64.Next(ref state);
        _s3 = SplitMix64.Next(ref state);
    }

    /// <summary>The seed this generator was created from. Recreating it from this seed replays the stream.</summary>
    public ulong Seed { get; }

    /// <summary>
    /// The generator for one game of an experiment: a pure function of the experiment seed and the game index,
    /// so results never depend on which thread ran which game (spec 5.5).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gameIndex"/> is negative.</exception>
    public static Rng ForGame(ulong experimentSeed, long gameIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gameIndex);
        return new Rng(experimentSeed ^ unchecked((ulong)gameIndex * 0xD1B54A32D192ED03UL));
    }

    /// <summary>Next 64 random bits.</summary>
    public ulong NextU64()
    {
        unchecked
        {
            ulong result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
            ulong t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = BitOperations.RotateLeft(_s3, 45);
            return result;
        }
    }

    /// <summary>
    /// Uniform integer in [0, <paramref name="maxExclusive"/>), unbiased: Lemire's multiply-shift with rejection
    /// (decision D-24). The exact algorithm is fixed because results must reproduce bit for bit.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxExclusive"/> is not positive.</exception>
    public int NextInt(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        ulong s = (ulong)maxExclusive;
        ulong high = Math.BigMul(NextU64(), s, out ulong low);
        if (low < s)
        {
            ulong threshold = unchecked(0UL - s) % s;
            while (low < threshold)
            {
                high = Math.BigMul(NextU64(), s, out low);
            }
        }

        return (int)high;
    }

    /// <summary>Uniform double in [0, 1) from the top 53 bits of one draw (D-24).</summary>
    public double NextDouble() => (NextU64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>A new independent generator seeded from one draw of this one (D-24).</summary>
    public Rng Split() => new(NextU64());
}
