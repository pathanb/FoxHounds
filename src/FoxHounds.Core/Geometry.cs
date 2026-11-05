using System.Globalization;

namespace FoxHounds.Core;

/// <summary>
/// Board geometry for one size n, immutable and cached: <see cref="For"/> returns the same instance every time
/// (spec 5.1). Playable squares are those with (r + c) odd; square index = (r·n + c) &gt;&gt; 1. Row 0 is the
/// top of the screen and the hounds' home row.
/// </summary>
public sealed class Geometry
{
    /// <summary>Sentinel for "no square" in <see cref="SquareAt"/>.</summary>
    public const int NoSquare = -1;

    private static readonly Lazy<Geometry>[] Cache = CreateCache();

    // Exposed read-only (D-25). Rules and other Core types use the arrays directly on the hot path.
    internal readonly byte[] RowOf;
    internal readonly byte[] ColOf;
    internal readonly UInt128[] FoxStepMask;
    internal readonly UInt128[] HoundStepMask;
    internal readonly byte[] FoxStepTo;      // 4 slots per square, in FoxStepList order
    internal readonly byte[] FoxStepCount;
    internal readonly byte[] HoundStepTo;    // 2 slots per square, in HoundStepList order
    internal readonly byte[] HoundStepCount;
    private readonly short[] _squareAt;
    private readonly UInt128[] _rowMask;
    private readonly byte[] _backRow;

    private Geometry(int n)
    {
        N = n;
        HoundCount = n / 2;
        SquareCount = n * n / 2;
        int p = SquareCount;

        RowOf = new byte[p];
        ColOf = new byte[p];
        _squareAt = new short[n * n];
        Array.Fill(_squareAt, (short)NoSquare);
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                if (((r + c) & 1) == 1)
                {
                    int index = ((r * n) + c) >> 1;
                    RowOf[index] = (byte)r;
                    ColOf[index] = (byte)c;
                    _squareAt[(r * n) + c] = (short)index;
                }
            }
        }

        FoxStepMask = new UInt128[p];
        HoundStepMask = new UInt128[p];
        FoxStepTo = new byte[p * 4];
        FoxStepCount = new byte[p];
        HoundStepTo = new byte[p * 2];
        HoundStepCount = new byte[p];
        ReadOnlySpan<(int Dr, int Dc)> foxDirections = [(-1, -1), (-1, 1), (1, -1), (1, 1)];
        ReadOnlySpan<int> houndColumnSteps = [-1, 1];
        for (int sq = 0; sq < p; sq++)
        {
            int r = RowOf[sq];
            int c = ColOf[sq];
            foreach (var (dr, dc) in foxDirections)
            {
                int to = SquareAt(r + dr, c + dc);
                if (to != NoSquare)
                {
                    FoxStepTo[(sq * 4) + FoxStepCount[sq]++] = (byte)to;
                    FoxStepMask[sq] |= UInt128.One << to;
                }
            }

            foreach (int dc in houndColumnSteps)
            {
                int to = SquareAt(r + 1, c + dc);
                if (to != NoSquare)
                {
                    HoundStepTo[(sq * 2) + HoundStepCount[sq]++] = (byte)to;
                    HoundStepMask[sq] |= UInt128.One << to;
                }
            }
        }

        _rowMask = new UInt128[n];
        for (int sq = 0; sq < p; sq++)
        {
            _rowMask[RowOf[sq]] |= UInt128.One << sq;
            AllSquares |= UInt128.One << sq;
        }

        var back = new List<byte>();
        for (int c = 0; c < n; c++)
        {
            int sq = SquareAt(n - 1, c);
            if (sq != NoSquare)
            {
                back.Add((byte)sq);
            }
        }

        _backRow = [.. back];
        Zobrist = new ZobristKeys(n, p);
    }

    /// <summary>Board side length n.</summary>
    public int N { get; }

    /// <summary>Number of hounds H = ⌊n/2⌋.</summary>
    public int HoundCount { get; }

    /// <summary>Number of playable squares P = ⌊n²/2⌋.</summary>
    public int SquareCount { get; }

    /// <summary>Mask of all P playable squares.</summary>
    public UInt128 AllSquares { get; }

    /// <summary>Row of each square.</summary>
    public ReadOnlySpan<byte> Row => RowOf;

    /// <summary>Column of each square.</summary>
    public ReadOnlySpan<byte> Col => ColOf;

    /// <summary>Mask of the up-to-4 diagonal neighbours of each square.</summary>
    public ReadOnlySpan<UInt128> FoxSteps => FoxStepMask;

    /// <summary>Mask of the up-to-2 forward (row + 1) diagonal squares of each square.</summary>
    public ReadOnlySpan<UInt128> HoundSteps => HoundStepMask;

    /// <summary>All playable squares of each row, indexed by row.</summary>
    public ReadOnlySpan<UInt128> RowMask => _rowMask;

    /// <summary>Playable squares of row n−1 (the fox's back row), ascending.</summary>
    public ReadOnlySpan<byte> BackRowSquares => _backRow;

    /// <summary>Zobrist keys for this board size (spec 5.4).</summary>
    public ZobristKeys Zobrist { get; }

    /// <summary>The cached geometry for board size <paramref name="n"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">n is outside 4…16.</exception>
    public static Geometry For(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, RuleSet.MinBoardSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(n, RuleSet.MaxBoardSize);
        return Cache[n].Value;
    }

    /// <summary>Square index of (r, c), or <see cref="NoSquare"/> for an unplayable or off-board square.</summary>
    public int SquareAt(int r, int c)
    {
        if ((uint)r >= (uint)N || (uint)c >= (uint)N)
        {
            return NoSquare;
        }

        return _squareAt[(r * N) + c];
    }

    /// <summary>The diagonal neighbours of a square in fixed order: up-left, up-right, down-left, down-right.</summary>
    public ReadOnlySpan<byte> FoxStepList(int square) => FoxStepTo.AsSpan(square * 4, FoxStepCount[square]);

    /// <summary>The forward squares of a square in fixed order: forward-left, forward-right.</summary>
    public ReadOnlySpan<byte> HoundStepList(int square) => HoundStepTo.AsSpan(square * 2, HoundStepCount[square]);

    /// <summary>Algebraic name of a square: file letter a + c, rank number n − r (for example e1).</summary>
    public string SquareName(int square)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(square);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(square, SquareCount);
        return string.Create(CultureInfo.InvariantCulture, $"{(char)('a' + ColOf[square])}{N - RowOf[square]}");
    }

    /// <summary>
    /// Parses an algebraic name into board coordinates without checking playability. Accepts exactly a lowercase
    /// file letter inside the board followed by a rank 1…n without leading zeros.
    /// </summary>
    public bool TryParseCoordinates(ReadOnlySpan<char> name, out int row, out int col)
    {
        row = col = -1;
        if (name.Length is < 2 or > 3)
        {
            return false;
        }

        int file = name[0] - 'a';
        if (file < 0 || file >= N || name[1] == '0')
        {
            return false;
        }

        int rank = 0;
        foreach (char ch in name[1..])
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }

            rank = (rank * 10) + (ch - '0');
        }

        if (rank < 1 || rank > N)
        {
            return false;
        }

        row = N - rank;
        col = file;
        return true;
    }

    /// <summary>Parses an algebraic name of a playable square.</summary>
    public bool TryParseSquare(ReadOnlySpan<char> name, out int square)
    {
        square = NoSquare;
        if (!TryParseCoordinates(name, out int r, out int c))
        {
            return false;
        }

        square = SquareAt(r, c);
        return square != NoSquare;
    }

    /// <summary>Text form of a move: from-to in algebraic names, for example e1-d2 (spec 5.3).</summary>
    public string FormatMove(Move move) =>
        move.IsNone ? "none" : string.Concat(SquareName(move.From), "-", SquareName(move.To));

    /// <summary>Parses the text form of a move between two playable squares. Does not check legality.</summary>
    public bool TryParseMove(ReadOnlySpan<char> text, out Move move)
    {
        move = Move.None;
        int dash = text.IndexOf('-');
        if (dash < 0 || !TryParseSquare(text[..dash], out int from) || !TryParseSquare(text[(dash + 1)..], out int to))
        {
            return false;
        }

        move = new Move((byte)from, (byte)to);
        return true;
    }

    private static Lazy<Geometry>[] CreateCache()
    {
        var cache = new Lazy<Geometry>[RuleSet.MaxBoardSize + 1];
        for (int n = RuleSet.MinBoardSize; n <= RuleSet.MaxBoardSize; n++)
        {
            int size = n;
            cache[n] = new Lazy<Geometry>(() => new Geometry(size), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        return cache;
    }
}
