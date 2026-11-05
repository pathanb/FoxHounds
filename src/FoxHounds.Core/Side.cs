namespace FoxHounds.Core;

/// <summary>The two sides. The byte values are part of the solver table layout (spec 10.2).</summary>
public enum Side : byte
{
    /// <summary>The single fox.</summary>
    Fox = 0,

    /// <summary>The hounds, moved one at a time.</summary>
    Hounds = 1,
}

/// <summary>Helpers for <see cref="Side"/>.</summary>
public static class SideExtensions
{
    /// <summary>The other side.</summary>
    public static Side Opponent(this Side side) => side == Side.Fox ? Side.Hounds : Side.Fox;

    /// <summary>The notation letter for the side to move: F or H (spec 5.6).</summary>
    public static char Letter(this Side side) => side == Side.Fox ? 'F' : 'H';
}
