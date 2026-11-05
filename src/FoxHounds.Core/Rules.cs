using System.Collections.ObjectModel;
using System.Diagnostics;

namespace FoxHounds.Core;

/// <summary>
/// Owns every rule (spec 6). Nothing else in the solution decides legality or game end: the UI, search, solver
/// and simulator all call this class. Immutable and thread-safe.
/// </summary>
public sealed class Rules
{
    /// <summary>Upper bound on legal plies in any position: fox ≤ 4, hounds ≤ 2H ≤ 16 (spec 5.3).</summary>
    public const int MaxMoves = 16;

    private readonly byte _n;
    private readonly int _houndCount;
    private readonly int _squareCount;
    private readonly bool _reachHome;
    private readonly bool _adjudicate;
    private readonly byte[] _row;
    private readonly UInt128[] _foxStepMask;
    private readonly UInt128[] _houndStepMask;
    private readonly byte[] _foxStepTo;
    private readonly byte[] _foxStepCount;
    private readonly byte[] _houndStepTo;
    private readonly byte[] _houndStepCount;
    private readonly ulong[] _houndKey;
    private readonly ulong[] _foxKey;
    private readonly ulong _sideKey;
    private readonly UInt128 _allSquares;
    private readonly UInt128 _backRowMask;

    /// <summary>Creates the rules for a ruleset.</summary>
    /// <exception cref="ArgumentException">An enum field is undefined, or <see cref="RuleSet.FixedFoxColumn"/> is not a
    /// playable column of row n−1 (spec 3.2).</exception>
    public Rules(RuleSet config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Enum.IsDefined(config.FoxStart) || !Enum.IsDefined(config.FirstMover) || !Enum.IsDefined(config.FoxWinCondition))
        {
            throw new ArgumentException("The ruleset has an undefined enum value.", nameof(config));
        }

        Config = config;
        Geo = Geometry.For(config.BoardSize);
        _n = (byte)Geo.N;
        _houndCount = Geo.HoundCount;
        _squareCount = Geo.SquareCount;
        _reachHome = config.FoxWinCondition == FoxWinCondition.ReachHomeRow;
        _adjudicate = config.AdjudicateEscape;
        _row = Geo.RowOf;
        _foxStepMask = Geo.FoxStepMask;
        _houndStepMask = Geo.HoundStepMask;
        _foxStepTo = Geo.FoxStepTo;
        _foxStepCount = Geo.FoxStepCount;
        _houndStepTo = Geo.HoundStepTo;
        _houndStepCount = Geo.HoundStepCount;
        _houndKey = Geo.Zobrist.HoundKeyArray;
        _foxKey = Geo.Zobrist.FoxKeyArray;
        _sideKey = Geo.Zobrist.SideKey;
        _allSquares = Geo.AllSquares;
        _backRowMask = Geo.RowMask[_n - 1];

        byte[] back = Geo.BackRowSquares.ToArray();
        FoxStartSquares = Array.AsReadOnly(back);
        if (config.FixedFoxColumn is int column)
        {
            int sq = Geo.SquareAt(_n - 1, column);
            if (sq == Geometry.NoSquare)
            {
                throw new ArgumentException(
                    $"FixedFoxColumn {column} is not a playable column of row {_n - 1} on the {_n}×{_n} board.", nameof(config));
            }

            DefaultFoxStart = (byte)sq;
        }
        else
        {
            // The playable square of row n−1 with the largest column ≤ ⌊n/2⌋ (spec 3.2): e1 on 8×8.
            DefaultFoxStart = back.Last(sq => Geo.ColOf[sq] <= _n / 2);
        }

        MaxPlies = (2 * _houndCount * (_n - 1)) + 1;
    }

    /// <summary>The ruleset these rules implement.</summary>
    public RuleSet Config { get; }

    /// <summary>The board geometry.</summary>
    public Geometry Geo { get; }

    /// <summary>The longest possible game: 2·H·(n−1) + 1 plies (spec 3.7); 57 on 8×8.</summary>
    public int MaxPlies { get; }

    /// <summary>Playable squares of row n−1, ascending: where the fox may start.</summary>
    public IReadOnlyList<byte> FoxStartSquares { get; }

    /// <summary>The fox's start square under <see cref="FoxStart.Fixed"/> (spec 3.2), honouring FixedFoxColumn.</summary>
    public byte DefaultFoxStart { get; }

    /// <summary>
    /// Upper bound on the plies left from <paramref name="s"/>: 2·(H·(n−1) − sum of hound rows) + 1. Each hound ply
    /// raises the sum of hound rows by exactly one.
    /// </summary>
    public int MaxPliesFrom(in GameState s) => (2 * ((_houndCount * (_n - 1)) - HoundRowSum(s))) + 1;

    /// <summary>Sum of the hounds' rows: the potential that orders the solver's sweep (spec 10.4).</summary>
    public int HoundRowSum(in GameState s)
    {
        Debug.Assert(s.N == _n, "Position belongs to another board size.");
        int sum = 0;
        UInt128 h = s.Hounds;
        while (h != UInt128.Zero)
        {
            sum += _row[(int)UInt128.TrailingZeroCount(h)];
            h &= h - UInt128.One;
        }

        return sum;
    }

    /// <summary>The initial position (spec 3.2): a hound on every playable square of row 0, the fox on
    /// <paramref name="foxSquare"/> or <see cref="DefaultFoxStart"/>, <see cref="RuleSet.FirstMover"/> to move.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="foxSquare"/> is not a playable square of row n−1.</exception>
    public GameState Initial(byte? foxSquare = null)
    {
        byte fox = foxSquare ?? DefaultFoxStart;
        if (fox >= _squareCount || ((_backRowMask >> fox) & UInt128.One) == UInt128.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(foxSquare), fox, "The fox must start on a playable square of row n−1.");
        }

        return Create(Geo.RowMask[0], fox, Config.FirstMover);
    }

    /// <summary>
    /// The outcome of a position, applying spec 3.4 in order: FoxReachedHome, FoxTrapped, HoundsImmobile,
    /// FoxEscaped. Call it before <see cref="GenerateMoves"/>. With adjudication on it can return a win for the
    /// side to move (FoxEscaped with the fox to move).
    /// </summary>
    public Outcome Evaluate(in GameState s)
    {
        Debug.Assert(s.N == _n, "Position belongs to another board size.");
        if (_reachHome && _row[s.Fox] == 0)
        {
            return new Outcome(GameResult.FoxWins, EndReason.FoxReachedHome);
        }

        if (!HasAnyMove(s))
        {
            return s.SideToMove == Side.Fox
                ? new Outcome(GameResult.HoundsWin, EndReason.FoxTrapped)
                : new Outcome(GameResult.FoxWins, EndReason.HoundsImmobile);
        }

        if (_adjudicate && IsEscaped(s))
        {
            return new Outcome(GameResult.FoxWins, EndReason.FoxEscaped);
        }

        return Outcome.Ongoing;
    }

    /// <summary>
    /// Writes the legal plies into <paramref name="buffer"/> and returns their count, in the normative order
    /// (spec 6.1): fox steps in FoxStepList order; hounds in ascending square index, each forward-left then
    /// forward-right. Does not check whether the game is over. Never allocates.
    /// </summary>
    /// <param name="s">The position.</param>
    /// <param name="buffer">At least <see cref="MaxMoves"/> long.</param>
    public int GenerateMoves(in GameState s, Span<Move> buffer)
    {
        Debug.Assert(s.N == _n, "Position belongs to another board size.");
        UInt128 occupied = s.Hounds | (UInt128.One << s.Fox);
        int count = 0;
        if (s.SideToMove == Side.Fox)
        {
            int from = s.Fox;
            int slot = from * 4;
            int end = slot + _foxStepCount[from];
            for (; slot < end; slot++)
            {
                byte to = _foxStepTo[slot];
                if (((occupied >> to) & UInt128.One) == UInt128.Zero)
                {
                    buffer[count++] = new Move((byte)from, to);
                }
            }

            return count;
        }

        UInt128 hounds = s.Hounds;
        while (hounds != UInt128.Zero)
        {
            int from = (int)UInt128.TrailingZeroCount(hounds);
            int slot = from * 2;
            int end = slot + _houndStepCount[from];
            for (; slot < end; slot++)
            {
                byte to = _houndStepTo[slot];
                if (((occupied >> to) & UInt128.One) == UInt128.Zero)
                {
                    buffer[count++] = new Move((byte)from, to);
                }
            }

            hounds &= hounds - UInt128.One;
        }

        return count;
    }

    /// <summary>True when the side to move has at least one legal ply. Does not check whether the game is over.</summary>
    public bool HasAnyMove(in GameState s)
    {
        Debug.Assert(s.N == _n, "Position belongs to another board size.");
        UInt128 empty = ~(s.Hounds | (UInt128.One << s.Fox));
        if (s.SideToMove == Side.Fox)
        {
            return (_foxStepMask[s.Fox] & empty) != UInt128.Zero;
        }

        UInt128 hounds = s.Hounds;
        while (hounds != UInt128.Zero)
        {
            if ((_houndStepMask[(int)UInt128.TrailingZeroCount(hounds)] & empty) != UInt128.Zero)
            {
                return true;
            }

            hounds &= hounds - UInt128.One;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="m"/> is a legal ply in <paramref name="s"/>. Every ply of a finished position is
    /// illegal (decision D-31), so plies after the end of a game are rejected.
    /// </summary>
    public bool IsLegal(in GameState s, Move m)
    {
        if (s.N != _n || m.From >= _squareCount || m.To >= _squareCount)
        {
            return false;
        }

        UInt128 occupied = s.Hounds | (UInt128.One << s.Fox);
        if (((occupied >> m.To) & UInt128.One) != UInt128.Zero)
        {
            return false;
        }

        bool shapeOk = s.SideToMove == Side.Fox
            ? m.From == s.Fox && ((_foxStepMask[m.From] >> m.To) & UInt128.One) != UInt128.Zero
            : ((s.Hounds >> m.From) & UInt128.One) != UInt128.Zero && ((_houndStepMask[m.From] >> m.To) & UInt128.One) != UInt128.Zero;
        return shapeOk && !Evaluate(s).IsOver;
    }

    /// <summary>Plays a legal ply. Undefined for an illegal ply in Release (asserted in Debug): untrusted input goes
    /// through <see cref="ApplyChecked"/>. Updates the hash incrementally (spec 5.4).</summary>
    public GameState Apply(in GameState s, Move m)
    {
        Debug.Assert(IsLegal(s, m), "Apply called with an illegal ply.");
        if (s.SideToMove == Side.Fox)
        {
            return new GameState(s.Hounds, m.To, Side.Hounds, _n, s.Hash ^ _foxKey[m.From] ^ _foxKey[m.To] ^ _sideKey);
        }

        UInt128 hounds = s.Hounds ^ ((UInt128.One << m.From) | (UInt128.One << m.To));
        return new GameState(hounds, s.Fox, Side.Fox, _n, s.Hash ^ _houndKey[m.From] ^ _houndKey[m.To] ^ _sideKey);
    }

    /// <summary>Plays a ply from an untrusted source (a human, a file, another process).</summary>
    /// <exception cref="IllegalMoveException">The ply is not legal in <paramref name="s"/>.</exception>
    public GameState ApplyChecked(in GameState s, Move m)
    {
        if (!IsLegal(s, m))
        {
            throw new IllegalMoveException($"Ply {DescribeMove(m)} is not legal in {s}.");
        }

        return Apply(s, m);
    }

    /// <summary>
    /// The escape test of spec 3.5, regardless of <see cref="RuleSet.AdjudicateEscape"/>. ReachHomeRow:
    /// fox.row ≤ minHoundRow. HoundsImmobile: fox.row &lt; minHoundRow and minHoundRow ≥ 2.
    /// </summary>
    public bool IsEscaped(in GameState s)
    {
        Debug.Assert(s.N == _n, "Position belongs to another board size.");
        int foxRow = _row[s.Fox];
        int minHoundRow = MinHoundRow(s);
        return _reachHome ? foxRow <= minHoundRow : foxRow < minHoundRow && minHoundRow >= 2;
    }

    /// <summary>The smallest row occupied by a hound, in O(1): indices increase in row-major order (spec 6.2).</summary>
    public int MinHoundRow(in GameState s)
    {
        Debug.Assert(s.N == _n, "Position belongs to another board size.");
        return _row[(int)UInt128.TrailingZeroCount(s.Hounds)];
    }

    /// <summary>Checks the invariants of spec 5.2: board size, exactly H hounds on playable squares, the fox on a
    /// playable square not shared with a hound, a defined side to move, and a hash equal to a full recomputation.</summary>
    /// <exception cref="InvalidPositionException">An invariant does not hold.</exception>
    public void Validate(in GameState s)
    {
        string? problem = FindProblem(s.Hounds, s.Fox, s.SideToMove, s.N);
        if (problem is null && s.Hash != Geo.Zobrist.Compute(s.Hounds, s.Fox, s.SideToMove))
        {
            problem = "the hash does not match a full recomputation";
        }

        if (problem is not null)
        {
            throw new InvalidPositionException($"Invalid position: {problem}.");
        }
    }

    /// <summary>
    /// Creates a position from its parts (decision D-30), computing the hash. The parts must be valid; that is
    /// asserted in Debug only. Use <see cref="CreateChecked"/> for untrusted parts.
    /// </summary>
    public GameState Create(UInt128 hounds, byte fox, Side sideToMove)
    {
        Debug.Assert(FindProblem(hounds, fox, sideToMove, _n) is null, "Create called with invalid parts.");
        return new GameState(hounds, fox, sideToMove, _n, Geo.Zobrist.Compute(hounds, fox, sideToMove));
    }

    /// <summary>Creates a position from untrusted parts.</summary>
    /// <exception cref="InvalidPositionException">The parts break an invariant of spec 5.2.</exception>
    public GameState CreateChecked(UInt128 hounds, byte fox, Side sideToMove)
    {
        string? problem = FindProblem(hounds, fox, sideToMove, _n);
        if (problem is not null)
        {
            throw new InvalidPositionException($"Invalid position: {problem}.");
        }

        return new GameState(hounds, fox, sideToMove, _n, Geo.Zobrist.Compute(hounds, fox, sideToMove));
    }

    /// <summary>
    /// Parses position notation (spec 5.6). Hounds may appear in any order. Rejects a board size other than the
    /// ruleset's, unplayable or unknown squares, duplicate hounds, a wrong hound count, the fox on a hound square
    /// and anything malformed.
    /// </summary>
    /// <exception cref="FormatException">The text is not a valid position for these rules.</exception>
    public GameState FromNotation(string text) => Notation.Parse(this, text);

    /// <summary>The canonical notation of a position: hounds in ascending square index.</summary>
    /// <exception cref="ArgumentException">The position belongs to another board size.</exception>
    public string ToNotation(in GameState s)
    {
        if (s.N != _n)
        {
            throw new ArgumentException($"A {s.N}×{s.N} position cannot be written with {_n}×{_n} rules.", nameof(s));
        }

        return Notation.Format(Geo, s);
    }

    private string? FindProblem(UInt128 hounds, byte fox, Side sideToMove, byte n)
    {
        if (n != _n)
        {
            return $"board size {n} differs from the ruleset's {_n}";
        }

        if ((hounds & ~_allSquares) != UInt128.Zero)
        {
            return "a hound bit is set at an index ≥ P";
        }

        int count = (int)UInt128.PopCount(hounds);
        if (count != _houndCount)
        {
            return $"{count} hounds instead of {_houndCount}";
        }

        if (fox >= _squareCount)
        {
            return $"fox square {fox} is not below P = {_squareCount}";
        }

        if (((hounds >> fox) & UInt128.One) != UInt128.Zero)
        {
            return "the fox stands on a hound square";
        }

        if (!Enum.IsDefined(sideToMove))
        {
            return $"undefined side to move {(byte)sideToMove}";
        }

        return null;
    }

    private string DescribeMove(Move m) =>
        m.From < _squareCount && m.To < _squareCount ? Geo.FormatMove(m) : $"({m.From}, {m.To})";
}
