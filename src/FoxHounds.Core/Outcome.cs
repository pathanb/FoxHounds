namespace FoxHounds.Core;

/// <summary>Result of a position or game.</summary>
public enum GameResult : byte
{
    /// <summary>The game continues.</summary>
    Ongoing,

    /// <summary>The fox has won.</summary>
    FoxWins,

    /// <summary>The hounds have won.</summary>
    HoundsWin,
}

/// <summary>Why a game ended (spec 3.4). Forfeit is decided by the game controller, not by the rules.</summary>
public enum EndReason : byte
{
    /// <summary>The game is not over.</summary>
    None,

    /// <summary>The fox stands on row 0 under <see cref="FoxWinCondition.ReachHomeRow"/>.</summary>
    FoxReachedHome,

    /// <summary>The fox is to move and has no legal ply.</summary>
    FoxTrapped,

    /// <summary>The hounds are to move and have no legal ply.</summary>
    HoundsImmobile,

    /// <summary>Escape adjudication fired (spec 3.5).</summary>
    FoxEscaped,

    /// <summary>An agent threw, returned an illegal ply or overran its hard time limit.</summary>
    Forfeit,
}

/// <summary>A result with its reason.</summary>
/// <param name="Result">Who won, or <see cref="GameResult.Ongoing"/>.</param>
/// <param name="Reason">Why the game ended.</param>
public readonly record struct Outcome(GameResult Result, EndReason Reason)
{
    /// <summary>The game continues.</summary>
    public static readonly Outcome Ongoing = new(GameResult.Ongoing, EndReason.None);

    /// <summary>True when a side has won.</summary>
    public bool IsOver => Result != GameResult.Ongoing;

    /// <summary>The winning side. Only meaningful when <see cref="IsOver"/>.</summary>
    public Side Winner => Result == GameResult.FoxWins ? Side.Fox : Side.Hounds;

    /// <summary>The outcome in which <paramref name="winner"/> wins for <paramref name="reason"/>.</summary>
    public static Outcome Win(Side winner, EndReason reason) =>
        new(winner == Side.Fox ? GameResult.FoxWins : GameResult.HoundsWin, reason);
}
