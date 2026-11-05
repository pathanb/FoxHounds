namespace FoxHounds.Core;

/// <summary>How the fox's starting square is decided (spec 3.2).</summary>
public enum FoxStart : byte
{
    /// <summary>The default square of row n−1, or <see cref="RuleSet.FixedFoxColumn"/> when set.</summary>
    Fixed,

    /// <summary>The fox's agent picks any playable square of row n−1 before the first ply.</summary>
    ChooseOnBackRow,
}

/// <summary>What the fox must achieve to win, besides the hounds running out of plies (spec 3.4).</summary>
public enum FoxWinCondition : byte
{
    /// <summary>The fox wins on reaching row 0, the hounds' home row.</summary>
    ReachHomeRow,

    /// <summary>The fox wins only when the hounds have no legal ply (the gilith.com rules).</summary>
    HoundsImmobile,
}

/// <summary>
/// The immutable configuration of rule variants (spec 3.6). Two games are comparable only if their rulesets
/// are equal; records give value equality.
/// </summary>
public sealed record RuleSet
{
    /// <summary>Smallest supported board size.</summary>
    public const int MinBoardSize = 4;

    /// <summary>Largest supported board size: 128 playable squares fit one <see cref="UInt128"/> (spec 5.1).</summary>
    public const int MaxBoardSize = 16;

    private readonly int _boardSize = 8;

    /// <summary>Board side length n, 4…16. Out-of-range values are rejected when the ruleset is constructed.</summary>
    /// <exception cref="ArgumentOutOfRangeException">n is outside 4…16 (spec 3.1).</exception>
    public int BoardSize
    {
        get => _boardSize;
        init
        {
            if (value is < MinBoardSize or > MaxBoardSize)
            {
                throw new ArgumentOutOfRangeException(nameof(BoardSize), value, $"Board size must be {MinBoardSize}…{MaxBoardSize}.");
            }

            _boardSize = value;
        }
    }

    /// <summary>How the fox's start square is decided.</summary>
    public FoxStart FoxStart { get; init; } = FoxStart.Fixed;

    /// <summary>
    /// Overrides the default fox column under <see cref="FoxStart.Fixed"/>. Must be a playable column of row n−1;
    /// that is checked when <see cref="Rules"/> is constructed, because it depends on <see cref="BoardSize"/>.
    /// </summary>
    public int? FixedFoxColumn { get; init; }

    /// <summary>The side that makes the first ply.</summary>
    public Side FirstMover { get; init; } = Side.Fox;

    /// <summary>The fox's win condition.</summary>
    public FoxWinCondition FoxWinCondition { get; init; } = FoxWinCondition.ReachHomeRow;

    /// <summary>Declare a fox win as soon as no hound can ever block the fox again (spec 3.5).</summary>
    public bool AdjudicateEscape { get; init; } = true;

    /// <summary>The default ruleset: all defaults (spec 3.6, D-04).</summary>
    public static RuleSet Classic { get; } = new();

    /// <summary>HoundsImmobile with adjudication off; used to cross-check the solver against gilith.com.</summary>
    public static RuleSet Gilith { get; } = new()
    {
        FoxWinCondition = FoxWinCondition.HoundsImmobile,
        AdjudicateEscape = false,
    };

    /// <summary>The same ruleset for another board size.</summary>
    public RuleSet WithBoardSize(int n) => this with { BoardSize = n };
}
