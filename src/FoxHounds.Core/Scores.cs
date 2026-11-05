namespace FoxHounds.Core;

/// <summary>
/// Score constants and conversions (spec 2, 8.3). Internally scores are negamax: positive is good for the side to
/// move. Externally they are always reported as scoreFox. A proven result "side to move wins in d plies" scores
/// <see cref="Win"/> − d; heuristic evaluations are clamped to ±<see cref="EvalMax"/>, so the ranges never overlap.
/// </summary>
public static class Scores
{
    /// <summary>Score of a win in 0 plies for the side to move.</summary>
    public const int Win = 1_000_000;

    /// <summary>Bound on every heuristic evaluation.</summary>
    public const int EvalMax = 100_000;

    /// <summary>Larger than any score; the search's initial window.</summary>
    public const int Infinity = Win + 1;

    /// <summary>The longest game on any supported board (n = 16): 2·8·15 + 1 plies.</summary>
    public const int MaxPliesAnyBoard = 241;

    /// <summary>Score of a proven result from the side to move's perspective.</summary>
    public static int Proven(bool sideToMoveWins, int plies) => sideToMoveWins ? Win - plies : -(Win - plies);

    /// <summary>True when |score| ≥ Win − MaxPlies, i.e. the score is a proven result (spec 8.3).</summary>
    public static bool IsProven(int score) => Math.Abs(score) >= Win - MaxPliesAnyBoard;

    /// <summary>Distance in plies to the end of the game for a proven score.</summary>
    public static int PliesToEnd(int provenScore) => Win - Math.Abs(provenScore);

    /// <summary>Converts a side-to-move score to the fox's perspective (and back: the conversion is its own inverse).</summary>
    public static int ToFox(int score, Side sideToMove) => sideToMove == Side.Fox ? score : -score;

    /// <summary>Clamps a heuristic evaluation to ±<see cref="EvalMax"/>.</summary>
    public static int ClampEval(long value) => (int)Math.Clamp(value, -EvalMax, EvalMax);

    /// <summary>Human-readable form of a scoreFox: "Fox wins in 13", "Hounds win in 44", or a signed number.</summary>
    public static string DescribeFox(int scoreFox)
    {
        if (!IsProven(scoreFox))
        {
            return scoreFox.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture);
        }

        int plies = PliesToEnd(scoreFox);
        return scoreFox > 0 ? $"Fox wins in {plies}" : $"Hounds win in {plies}";
    }
}
