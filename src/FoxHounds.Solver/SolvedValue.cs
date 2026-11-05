using FoxHounds.Core;

namespace FoxHounds.Solver;

/// <summary>The true result of a position with perfect play: whether the side to move wins, and in how many plies.
/// The winner plays the shortest win, the loser the longest resistance.</summary>
/// <param name="SideToMoveWins">True when the side to move wins.</param>
/// <param name="Plies">Plies to the end of the game with perfect play.</param>
public readonly record struct SolvedValue(bool SideToMoveWins, int Plies)
{
    /// <summary>The winner, given the side to move of the position this value belongs to.</summary>
    public Side Winner(Side sideToMove) => SideToMoveWins ? sideToMove : sideToMove.Opponent();

    /// <summary>Negamax score (side to move's perspective).</summary>
    public int Score => Scores.Proven(SideToMoveWins, Plies);

    /// <summary>"Fox wins in 7" style text, given the side to move.</summary>
    public string Describe(Side sideToMove) =>
        Winner(sideToMove) == Side.Fox ? $"Fox wins in {Plies}" : $"Hounds win in {Plies}";

    /// <summary>Compact label used in opening files (spec 11.2): F7, H44.</summary>
    public string Label(Side sideToMove) => $"{Winner(sideToMove).Letter()}{Plies}";

    /// <summary>Decodes a table byte (spec 10.3). Byte 0 has no value.</summary>
    internal static SolvedValue Decode(byte b) => b >= 128 ? new SolvedValue(true, b - 128) : new SolvedValue(false, b - 1);
}

/// <summary>A legal ply rated by the solver.</summary>
/// <param name="Move">The ply.</param>
/// <param name="ValueForMover">The value after the ply, from the perspective of the side that made it, counting the
/// ply itself.</param>
public readonly record struct RatedMove(Move Move, SolvedValue ValueForMover);
