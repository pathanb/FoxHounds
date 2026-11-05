using FoxHounds.Core;

namespace FoxHounds.Solver;

/// <summary>How a ply compares with perfect play: the colours of the analysis viewer (spec 13.5).</summary>
public enum MoveQuality : byte
{
    /// <summary>Keeps the win at the optimal distance (green).</summary>
    Fastest,

    /// <summary>Keeps the win, but more slowly than the best ply (yellow).</summary>
    Slower,

    /// <summary>Turns a won position into a lost one (red): a blunder (spec 11.5).</summary>
    Blunder,

    /// <summary>The position was already lost for the mover, so no ply can lose anything (grey).</summary>
    AlreadyLost,
}

/// <summary>A legal ply with the solver's value after it and its <see cref="MoveQuality"/>.</summary>
/// <param name="Move">The ply.</param>
/// <param name="ValueForMover">The value after the ply from the mover's perspective, counting the ply itself, so
/// the fastest winning ply has the same distance as the position.</param>
/// <param name="Quality">Its class.</param>
public readonly record struct GradedMove(Move Move, SolvedValue ValueForMover, MoveQuality Quality);

/// <summary>The solver's verdict on a ply that was played.</summary>
/// <param name="MoverWasWinning">The position before the ply was won for the side that made it.</param>
/// <param name="Blunder">The ply turned that win into a loss (spec 11.5).</param>
public readonly record struct PlyJudgement(bool MoverWasWinning, bool Blunder);

/// <summary>
/// Grades plies against a solver table: the analysis viewer's colours (spec 13.5) and the harness's blunders
/// (spec 11.5) come from here, so the two always agree.
/// </summary>
public static class MoveGrading
{
    /// <summary>
    /// Grades every legal ply of a position, in normative move order. Distances are the table's, which is solved
    /// without escape adjudication (spec 10.1); adjudication only ends games the fox has already won, so it never
    /// changes a winner. Returns 0 when the table's rules call the position finished; a game under adjudication can be
    /// over earlier (the fox has escaped), so callers check their own rules first.
    /// </summary>
    /// <param name="table">A table for the position's board size and fox win condition.</param>
    /// <param name="s">The position.</param>
    /// <param name="buffer">At least <see cref="Rules.MaxMoves"/> long.</param>
    /// <exception cref="ArgumentException">The position is malformed or belongs to another board size.</exception>
    public static int Grade(SolverTable table, in GameState s, Span<GradedMove> buffer)
    {
        ArgumentNullException.ThrowIfNull(table);
        Span<RatedMove> rated = stackalloc RatedMove[Rules.MaxMoves];
        int count = table.RateMoves(s, rated);
        int fastest = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (rated[i].ValueForMover.SideToMoveWins)
            {
                fastest = Math.Min(fastest, rated[i].ValueForMover.Plies);
            }
        }

        bool won = fastest != int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var value = rated[i].ValueForMover;
            var quality = !won ? MoveQuality.AlreadyLost
                : !value.SideToMoveWins ? MoveQuality.Blunder
                : value.Plies == fastest ? MoveQuality.Fastest
                : MoveQuality.Slower;
            buffer[i] = new GradedMove(rated[i].Move, value, quality);
        }

        return count;
    }

    /// <summary>
    /// Judges a ply that was played (spec 11.5): was the mover winning, and did the ply throw the win away? The game's
    /// <paramref name="rules"/> decide whether the ply ended the game; otherwise the table rates the new position.
    /// </summary>
    /// <param name="rules">The rules the game is played under.</param>
    /// <param name="table">A table for the rules' board size and fox win condition.</param>
    /// <param name="before">A position where the game is not over.</param>
    /// <param name="move">A legal ply in it.</param>
    public static PlyJudgement Judge(Rules rules, SolverTable table, in GameState before, Move move)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(table);
        if (!table.Get(before).SideToMoveWins)
        {
            return new PlyJudgement(false, false);
        }

        var mover = before.SideToMove;
        var next = rules.Apply(before, move);
        var outcome = rules.Evaluate(next);
        bool stillWon = outcome.IsOver ? outcome.Winner == mover : table.Get(next).Winner(next.SideToMove) == mover;
        return new PlyJudgement(true, !stillWon);
    }

    /// <summary>Judges every ply of a game record in order (for the move list's blunder markers).</summary>
    /// <param name="rules">Rules for the record's ruleset.</param>
    /// <param name="table">A table for the record's board size and fox win condition.</param>
    /// <param name="start">The position before the first ply.</param>
    /// <param name="plies">The plies.</param>
    public static PlyJudgement[] JudgeGame(Rules rules, SolverTable table, GameState start, IReadOnlyList<Move> plies)
    {
        ArgumentNullException.ThrowIfNull(plies);
        var result = new PlyJudgement[plies.Count];
        var s = start;
        for (int i = 0; i < plies.Count; i++)
        {
            result[i] = Judge(rules, table, s, plies[i]);
            s = rules.Apply(s, plies[i]);
        }

        return result;
    }
}
