using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FoxHounds.Core;

namespace FoxHounds.Solver;

/// <summary>
/// The exact value of every position for one board size and win condition, one byte per (hound set, fox square,
/// side to move) (spec 10). Built by a potential-ordered sweep with escape adjudication off. Immutable once built
/// and safe to share between threads.
/// </summary>
public sealed class SolverTable
{
    /// <summary>Largest board size the solver accepts (n = 11: 655 MB). n = 12 would need 22.5 GB (spec 10.1).</summary>
    public const int MaxBoardSize = 11;

    /// <summary>Table file format version (spec 10.6).</summary>
    public const ushort FormatVersion = 1;

    private const int HeaderSize = 8 + 2 + 4 + 8 + 32;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FHSOLV01");

    private readonly byte[] _values;
    private readonly HoundSetIndex _index;
    private readonly int _squareCount;
    private readonly int _houndCount;

    private SolverTable(Rules rules, byte[] values)
    {
        Rules = rules;
        _values = values;
        _squareCount = rules.Geo.SquareCount;
        _houndCount = rules.Geo.HoundCount;
        _index = new HoundSetIndex(_squareCount, _houndCount);
        Debug.Assert(values.LongLength == EntryCountFor(rules.Geo.N), "Table size does not match the board.");
    }

    /// <summary>
    /// The canonical rules the table was solved under: the table's board size and win condition, adjudication off.
    /// FoxStart, FixedFoxColumn and FirstMover do not affect the table (spec 10.1).
    /// </summary>
    public Rules Rules { get; }

    /// <summary>Board size n.</summary>
    public int BoardSize => Rules.Geo.N;

    /// <summary>The win condition the table was solved for.</summary>
    public FoxWinCondition WinCondition => Rules.Config.FoxWinCondition;

    /// <summary>Number of entries, C(P, H) · P · 2.</summary>
    public long EntryCount => _values.LongLength;

    /// <summary>Entries in the table for board size <paramref name="n"/>.</summary>
    public static long EntryCountFor(int n)
    {
        var geo = Geometry.For(n);
        return new HoundSetIndex(geo.SquareCount, geo.HoundCount).Count * geo.SquareCount * 2;
    }

    /// <summary>True when the solver can build a table for <paramref name="n"/> (4 ≤ n ≤ 11).</summary>
    public static bool IsSupported(int n) => n >= RuleSet.MinBoardSize && n <= MaxBoardSize;

    /// <summary>The ruleset a table for <paramref name="config"/> is solved under: same n and win condition,
    /// adjudication off, everything else default.</summary>
    public static RuleSet CanonicalRuleSet(RuleSet config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new RuleSet { BoardSize = config.BoardSize, FoxWinCondition = config.FoxWinCondition, AdjudicateEscape = false };
    }

    /// <summary>True when this table gives the values for games played under <paramref name="config"/>.</summary>
    public bool Covers(RuleSet config) =>
        config is not null && config.BoardSize == BoardSize && config.FoxWinCondition == WinCondition;

    /// <summary>Builds the table (spec 10.4) on all processors.</summary>
    public static SolverTable Build(Rules rules, IProgress<double>? progress, CancellationToken ct) =>
        Build(rules, Environment.ProcessorCount, progress, ct);

    /// <summary>
    /// Builds the table (spec 10.4): buckets hound sets by potential (sum of hound rows), then solves buckets from the
    /// highest potential down. Hounds-to-move entries of a set only read entries of the next bucket; fox-to-move
    /// entries only read the same set's hounds-to-move entries. Sets in one bucket are independent and may run in
    /// parallel; each writes only its own entries, so the result does not depend on scheduling.
    /// </summary>
    /// <exception cref="NotSupportedException">n &gt; 11: the table would not fit in memory.</exception>
    public static SolverTable Build(Rules rules, int maxDegreeOfParallelism, IProgress<double>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);
        int n = rules.Geo.N;
        if (!IsSupported(n))
        {
            double gb = EntryCountFor(n) / 1e9;
            throw new NotSupportedException(
                $"The solver supports boards up to {MaxBoardSize}×{MaxBoardSize}; a {n}×{n} table would need {gb:F1} GB.");
        }

        var canonical = new Rules(CanonicalRuleSet(rules.Config));
        var builder = new Builder(canonical);
        builder.Run(maxDegreeOfParallelism, progress, ct);
        return new SolverTable(canonical, builder.Values);
    }

    /// <summary>The value of a legal position.</summary>
    /// <exception cref="ArgumentException">The position is malformed or belongs to another board size.</exception>
    /// <exception cref="InvalidOperationException">The table holds no value for it: under ReachHomeRow, the fox on
    /// row 0 with the fox to move, which cannot arise in play (spec 10.3).</exception>
    public SolvedValue Get(in GameState s)
    {
        byte b = _values[IndexOf(s)];
        if (b == 0)
        {
            throw new InvalidOperationException($"The solver table holds no value for {s}.");
        }

        return SolvedValue.Decode(b);
    }

    /// <summary>The value of a legal position, or false when the table holds none.</summary>
    public bool TryGet(in GameState s, out SolvedValue value)
    {
        byte b = _values[IndexOf(s)];
        value = b == 0 ? default : SolvedValue.Decode(b);
        return b != 0;
    }

    /// <summary>
    /// Rates every legal ply of a non-terminal position, in normative move order: the value after the ply from the
    /// mover's perspective, counting the ply. Returns 0 for a finished position.
    /// </summary>
    /// <param name="s">The position.</param>
    /// <param name="buffer">At least <see cref="Core.Rules.MaxMoves"/> long.</param>
    /// <exception cref="ArgumentException">The position is malformed or belongs to another board size.</exception>
    public int RateMoves(in GameState s, Span<RatedMove> buffer)
    {
        _ = IndexOf(s); // validates the position against this table before the rules touch it
        if (Rules.Evaluate(s).IsOver)
        {
            return 0;
        }

        Span<Move> moves = stackalloc Move[Core.Rules.MaxMoves];
        int count = Rules.GenerateMoves(s, moves);
        for (int i = 0; i < count; i++)
        {
            var child = Get(Rules.Apply(s, moves[i]));
            buffer[i] = new RatedMove(moves[i], new SolvedValue(!child.SideToMoveWins, child.Plies + 1));
        }

        return count;
    }

    /// <summary>The raw byte of an entry (spec 10.3), for consistency tests and tools.</summary>
    public byte RawValue(long entryIndex) => _values[entryIndex];

    /// <summary>Entry index of a position: (rank · P + fox) · 2 + side (spec 10.2).</summary>
    /// <exception cref="ArgumentException">The position is malformed or belongs to another board size.</exception>
    public long IndexOf(in GameState s)
    {
        if (s.N != BoardSize || s.Fox >= _squareCount || (int)UInt128.PopCount(s.Hounds) != _houndCount
            || (s.Hounds & ~Rules.Geo.AllSquares) != UInt128.Zero)
        {
            throw new ArgumentException($"Not a legal position of the {BoardSize}×{BoardSize} table: {s}", nameof(s));
        }

        return (((_index.Rank(s.Hounds) * _squareCount) + s.Fox) * 2) + (int)s.SideToMove;
    }

    /// <summary>The position stored at an entry index (the inverse of <see cref="IndexOf"/>).</summary>
    /// <returns>False when the entry is not a position (fox on a hound square).</returns>
    public bool TryGetPosition(long entryIndex, out GameState position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, EntryCount);
        var side = (Side)(entryIndex & 1);
        long rest = entryIndex >> 1;
        int fox = (int)(rest % _squareCount);
        UInt128 hounds = _index.Unrank(rest / _squareCount);
        if (((hounds >> fox) & UInt128.One) != UInt128.Zero)
        {
            position = default;
            return false;
        }

        position = Rules.Create(hounds, (byte)fox, side);
        return true;
    }

    /// <summary>
    /// Writes the table file (spec 10.6): header with a SHA-256 of the payload, then the raw bytes. The bytes go to a
    /// uniquely named temporary file, are flushed to disk, and only then is the file renamed into place; on failure the
    /// temporary file is deleted. A reader therefore sees either the old file or the complete new one, and a damaged
    /// file is caught by its hash in any case.
    /// </summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            WriteFile(temp);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private void WriteFile(string temp)
    {
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], FormatVersion);
            header[10] = (byte)BoardSize;
            header[11] = (byte)_houndCount;
            header[12] = (byte)_squareCount;
            header[13] = (byte)WinCondition;
            BinaryPrimitives.WriteUInt64LittleEndian(header[14..], (ulong)EntryCount);
            SHA256.HashData(_values, header[22..]);
            stream.Write(header);
            stream.Write(_values);
            stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>Reads a table file, verifying magic, version, sizes and the payload hash.</summary>
    /// <exception cref="InvalidDataException">The file is not a valid table.</exception>
    public static SolverTable Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[HeaderSize];
        if (stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) != HeaderSize)
        {
            throw new InvalidDataException($"{path}: too short to be a solver table.");
        }

        if (!header[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException($"{path}: not a solver table (bad magic).");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"{path}: table format version {version} is not supported (expected {FormatVersion}).");
        }

        int n = header[10];
        var win = (FoxWinCondition)header[13];
        if (!IsSupported(n) || !Enum.IsDefined(win))
        {
            throw new InvalidDataException($"{path}: unsupported board size {n} or win condition {(int)win}.");
        }

        var geo = Geometry.For(n);
        ulong entries = BinaryPrimitives.ReadUInt64LittleEndian(header[14..]);
        if (header[11] != geo.HoundCount || header[12] != geo.SquareCount || entries != (ulong)EntryCountFor(n))
        {
            throw new InvalidDataException($"{path}: header sizes do not match a {n}×{n} table.");
        }

        if (stream.Length - HeaderSize != (long)entries)
        {
            throw new InvalidDataException($"{path}: payload is {stream.Length - HeaderSize} bytes, expected {entries}.");
        }

        var values = GC.AllocateUninitializedArray<byte>((int)entries);
        stream.ReadExactly(values);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(values, hash);
        if (!hash.SequenceEqual(header[22..]))
        {
            throw new InvalidDataException($"{path}: payload hash does not match; the file is corrupt.");
        }

        var rules = new Rules(new RuleSet { BoardSize = n, FoxWinCondition = win, AdjudicateEscape = false });
        var table = new SolverTable(rules, values);
        if (!table.PayloadMatchesWinCondition())
        {
            // The hash covers only the payload (spec 10.6), so check the header's win-condition byte against the data.
            throw new InvalidDataException($"{path}: the header says {win}, but the payload belongs to the other win condition.");
        }

        return table;
    }

    /// <summary>
    /// The payload's own signature of its win condition (decision D-32): with all hounds on the back row and the fox
    /// on row 0 with the fox to move, ReachHomeRow stores 0 (spec 10.3) while HoundsImmobile stores a value.
    /// </summary>
    private bool PayloadMatchesWinCondition()
    {
        bool reachHome = WinCondition == FoxWinCondition.ReachHomeRow;
        long backRowRank = _index.Count - 1; // the highest squares are exactly the back row
        for (int fox = 0; fox < _houndCount; fox++) // the lowest squares are exactly row 0
        {
            byte b = _values[((backRowRank * _squareCount) + fox) * 2];
            if ((b == 0) != reachHome)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads a table file and refuses one built for another board size or win condition.</summary>
    /// <exception cref="InvalidDataException">The file is invalid or was built for another ruleset.</exception>
    public static SolverTable Load(string path, RuleSet expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var table = Load(path);
        if (!table.Covers(expected))
        {
            throw new InvalidDataException(
                $"{path}: built for {table.BoardSize}×{table.BoardSize} {table.WinCondition}, not {expected.BoardSize}×{expected.BoardSize} {expected.FoxWinCondition}.");
        }

        return table;
    }

    /// <summary>The sweep of spec 10.4.</summary>
    private sealed class Builder
    {
        private readonly Rules _rules;
        private readonly HoundSetIndex _index;
        private readonly int _p;
        private readonly bool _reachHome;
        private readonly UInt128 _homeRow;
        private UInt128[] _sets = [];

        public Builder(Rules rules)
        {
            _rules = rules;
            _p = rules.Geo.SquareCount;
            _index = new HoundSetIndex(_p, rules.Geo.HoundCount);
            _reachHome = rules.Config.FoxWinCondition == FoxWinCondition.ReachHomeRow;
            _homeRow = rules.Geo.RowMask[0];
            Values = new byte[_index.Count * _p * 2];
        }

        public byte[] Values { get; }

        public void Run(int parallelism, IProgress<double>? progress, CancellationToken ct)
        {
            _sets = _index.EnumerateAll();
            int setCount = _sets.Length;
            var geo = _rules.Geo;
            int maxPotential = geo.HoundCount * (geo.N - 1);

            // 1. Counting sort of set ranks by potential.
            var potential = new int[setCount];
            var bucketStart = new int[maxPotential + 2];
            for (int r = 0; r < setCount; r++)
            {
                int sum = 0;
                UInt128 h = _sets[r];
                while (h != UInt128.Zero)
                {
                    sum += geo.Row[(int)UInt128.TrailingZeroCount(h)];
                    h &= h - UInt128.One;
                }

                potential[r] = sum;
                bucketStart[sum + 1]++;
            }

            for (int i = 1; i < bucketStart.Length; i++)
            {
                bucketStart[i] += bucketStart[i - 1];
            }

            var order = new int[setCount];
            var fill = (int[])bucketStart.Clone();
            for (int r = 0; r < setCount; r++)
            {
                order[fill[potential[r]]++] = r;
            }

            // 2. Buckets from the highest potential down.
            var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = parallelism };
            int done = 0;
            for (int pot = maxPotential; pot >= 0; pot--)
            {
                ct.ThrowIfCancellationRequested();
                int start = bucketStart[pot];
                int end = bucketStart[pot + 1];
                if (parallelism == 1 || end - start < 64)
                {
                    for (int i = start; i < end; i++)
                    {
                        SolveSet(order[i]);
                    }
                }
                else
                {
                    Parallel.For(start, end, options, i => SolveSet(order[i]));
                }

                done += end - start;
                progress?.Report((double)done / setCount);
            }
        }

        private void SolveSet(int rank)
        {
            UInt128 hounds = _sets[rank];
            Span<Move> moves = stackalloc Move[Rules.MaxMoves];
            long rowBase = (long)rank * _p;

            // 2.1 Hounds to move: children are (S′, f, Fox) in the next bucket, already solved.
            for (int f = 0; f < _p; f++)
            {
                if (((hounds >> f) & UInt128.One) != UInt128.Zero)
                {
                    continue; // fox on a hound square: stays 0
                }

                var s = _rules.Create(hounds, (byte)f, Side.Hounds);
                long entry = ((rowBase + f) * 2) + 1;
                var outcome = _rules.Evaluate(s);
                if (outcome.IsOver)
                {
                    Debug.Assert(outcome.Result == GameResult.FoxWins, "A terminal hounds-to-move position is a loss for the hounds.");
                    Values[entry] = 1; // side to move loses in 0
                    continue;
                }

                Values[entry] = Combine(s, moves);
            }

            // 2.2 Fox to move: children are (S, f′, Hounds), solved in 2.1 just above.
            for (int f = 0; f < _p; f++)
            {
                if (((hounds >> f) & UInt128.One) != UInt128.Zero)
                {
                    continue;
                }

                long entry = (rowBase + f) * 2;
                if (_reachHome && ((_homeRow >> f) & UInt128.One) != UInt128.Zero)
                {
                    Values[entry] = 0; // cannot arise in play: the game ended when the fox arrived (spec 10.3)
                    continue;
                }

                var s = _rules.Create(hounds, (byte)f, Side.Fox);
                var outcome = _rules.Evaluate(s);
                if (outcome.IsOver)
                {
                    Debug.Assert(outcome.Reason == EndReason.FoxTrapped, "A terminal fox-to-move position is a trapped fox.");
                    Values[entry] = 1;
                    continue;
                }

                Values[entry] = Combine(s, moves);
            }
        }

        // 3. A win in 1 + the smallest d if any child is a loss for its side to move, else a loss in 1 + the largest d.
        private byte Combine(in GameState s, Span<Move> moves)
        {
            int count = _rules.GenerateMoves(s, moves);
            Debug.Assert(count > 0, "A non-terminal position has a legal ply.");
            int shortestWin = int.MaxValue;
            int longestLoss = -1;
            for (int i = 0; i < count; i++)
            {
                var child = _rules.Apply(s, moves[i]);
                byte v = Values[(((_index.Rank(child.Hounds) * _p) + child.Fox) * 2) + (int)child.SideToMove];
                Debug.Assert(v != 0, "Children are solved before their parents.");
                if (v < 128)
                {
                    shortestWin = Math.Min(shortestWin, v - 1);
                }
                else
                {
                    longestLoss = Math.Max(longestLoss, v - 128);
                }
            }

            if (shortestWin != int.MaxValue)
            {
                int d = shortestWin + 1;
                if (d > 127)
                {
                    throw new InvariantViolationException($"Win distance {d} does not fit a table byte.");
                }

                return (byte)(128 + d);
            }

            int loss = longestLoss + 1;
            if (loss > 126)
            {
                throw new InvariantViolationException($"Loss distance {loss} does not fit a table byte.");
            }

            return (byte)(1 + loss);
        }
    }
}
