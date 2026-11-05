using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace FoxHounds.Core;

/// <summary>
/// A position: hound set, fox square and side to move, plus its Zobrist hash (spec 5.2). An immutable 32-byte
/// struct; search copies it instead of mutating it. Only <see cref="Rules"/> creates states. The ply count is
/// deliberately not part of the state, so transpositions are identical.
/// </summary>
[DebuggerDisplay("{ToString(),nq}")]
[SuppressMessage("Design", "CA1051:Do not declare visible instance fields",
    Justification = "Spec 5.2 prescribes public readonly fields; the struct is immutable (decision D-26).")]
public readonly struct GameState : IEquatable<GameState>
{
    /// <summary>Bit i set = a hound on square i.</summary>
    public readonly UInt128 Hounds;

    /// <summary>Zobrist hash (spec 5.4). Not part of equality, but equal states have equal hashes.</summary>
    public readonly ulong Hash;

    /// <summary>Square index of the fox.</summary>
    public readonly byte Fox;

    /// <summary>The side to move.</summary>
    public readonly Side SideToMove;

    /// <summary>Board size; guards against mixing positions of different sizes.</summary>
    public readonly byte N;

    internal GameState(UInt128 hounds, byte fox, Side sideToMove, byte n, ulong hash)
    {
        Hounds = hounds;
        Fox = fox;
        SideToMove = sideToMove;
        N = n;
        Hash = hash;
    }

    /// <summary>Equality of <see cref="Hounds"/>, <see cref="Fox"/>, <see cref="SideToMove"/> and <see cref="N"/>.</summary>
    public static bool operator ==(GameState left, GameState right) => left.Equals(right);

    /// <summary>Inequality, the negation of <see cref="op_Equality"/>.</summary>
    public static bool operator !=(GameState left, GameState right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(GameState other)
    {
        bool equal = Hounds == other.Hounds && Fox == other.Fox && SideToMove == other.SideToMove && N == other.N;
        Debug.Assert(!equal || Hash == other.Hash, "Equal states must have equal hashes.");
        return equal;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GameState other && Equals(other);

    /// <inheritdoc />
    /// <remarks>Folds the Zobrist hash, which equal states share (spec 5.2). Unlike HashCode.Combine it is the same in
    /// every process, so the iteration order of hash sets of positions is reproducible.</remarks>
    public override int GetHashCode() => (int)Hash ^ (int)(Hash >> 32);

    /// <summary>Position notation (spec 5.6), for example 8/e1/b8,d8,f8,h8/F.</summary>
    public override string ToString() =>
        N is >= RuleSet.MinBoardSize and <= RuleSet.MaxBoardSize ? Notation.Format(Geometry.For(N), this) : "(no position)";
}
