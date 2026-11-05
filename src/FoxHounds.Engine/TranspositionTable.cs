using System.Runtime.InteropServices;
using FoxHounds.Core;

namespace FoxHounds.Engine;

/// <summary>What a stored score means relative to the search window.</summary>
public enum Bound : byte
{
    /// <summary>Empty slot.</summary>
    None = 0,

    /// <summary>The exact value.</summary>
    Exact = 1,

    /// <summary>A lower bound (the search failed high).</summary>
    Lower = 2,

    /// <summary>An upper bound (the search failed low).</summary>
    Upper = 3,
}

/// <summary>One 16-byte table entry (spec 8.4).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TtEntry
{
    /// <summary>Upper 32 bits of the position hash.</summary>
    public uint KeyCheck;

    /// <summary>Score, with proven scores stored relative to the node (spec 8.3).</summary>
    public int Score;

    /// <summary>Best or refutation move.</summary>
    public Move Move;

    /// <summary>Remaining depth the score was searched to.</summary>
    public byte Depth;

    /// <summary>Meaning of <see cref="Score"/>.</summary>
    public Bound Bound;

    /// <summary>Search generation that stored the entry.</summary>
    public byte Age;
}

/// <summary>
/// A fixed-size, power-of-two transposition table indexed by the low hash bits (spec 8.4). Replacement: overwrite
/// when the new depth is at least the stored depth, or the stored entry is from an older search. One table per agent,
/// never shared between threads.
/// </summary>
public sealed class TranspositionTable
{
    private readonly TtEntry[] _entries;
    private readonly ulong _mask;
    private byte _age;

    /// <summary>Creates a table of the largest power-of-two entry count that fits <paramref name="megabytes"/>;
    /// 0 disables it.</summary>
    public TranspositionTable(int megabytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(megabytes);
        if (megabytes == 0)
        {
            _entries = [];
            return;
        }

        long count = (long)megabytes * 1024 * 1024 / 16;
        long size = 1L << (63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)count));
        _entries = new TtEntry[size];
        _mask = (ulong)(size - 1);
    }

    /// <summary>False when the table was created with 0 MB.</summary>
    public bool Enabled => _entries.Length > 0;

    /// <summary>Number of entries.</summary>
    public int Capacity => _entries.Length;

    /// <summary>Empties the table.</summary>
    public void Clear()
    {
        Array.Clear(_entries);
        _age = 0;
    }

    /// <summary>Starts a new search generation (older entries become replaceable).</summary>
    public void NewSearch() => _age = unchecked((byte)(_age + 1));

    /// <summary>Looks up a position; a hit requires the upper hash bits to match.</summary>
    public bool Probe(ulong hash, out TtEntry entry)
    {
        if (_entries.Length == 0)
        {
            entry = default;
            return false;
        }

        entry = _entries[(int)(hash & _mask)];
        return entry.Bound != Bound.None && entry.KeyCheck == (uint)(hash >> 32);
    }

    /// <summary>Stores a result, subject to the replacement rule.</summary>
    public void Store(ulong hash, int depth, int score, Bound bound, Move move)
    {
        if (_entries.Length == 0)
        {
            return;
        }

        ref TtEntry slot = ref _entries[(int)(hash & _mask)];
        if (slot.Bound != Bound.None && depth < slot.Depth && slot.Age == _age)
        {
            return;
        }

        slot.KeyCheck = (uint)(hash >> 32);
        slot.Score = score;
        slot.Move = move;
        slot.Depth = (byte)depth;
        slot.Bound = bound;
        slot.Age = _age;
    }
}
