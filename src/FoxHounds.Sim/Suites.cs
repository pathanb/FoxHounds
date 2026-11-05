using FoxHounds.Core;
using FoxHounds.Engine;
using FoxHounds.Engine.Evaluation;
using FoxHounds.Solver;

namespace FoxHounds.Sim;

/// <summary>Settings for <see cref="Suites.Generate"/> (spec 11.2, decision D-28).</summary>
public sealed record SuiteOptions
{
    /// <summary>The rules.</summary>
    public required RuleSet RuleSet { get; init; }

    /// <summary>Seed; game g uses Rng.ForGame(seed, g).</summary>
    public ulong Seed { get; init; } = 11;

    /// <summary>Training positions wanted.</summary>
    public int Train { get; init; } = 50_000;

    /// <summary>Validation positions wanted.</summary>
    public int Validation { get; init; } = 10_000;

    /// <summary>Test positions wanted.</summary>
    public int Test { get; init; } = 20_000;

    /// <summary>Random-ply probability of the players.</summary>
    public double Epsilon { get; init; } = 0.15;

    /// <summary>Search depth of the players.</summary>
    public int Depth { get; init; } = 4;

    /// <summary>Fewest random opening plies before the players take over (D-15).</summary>
    public int MinOpeningPlies { get; init; } = 6;

    /// <summary>Most random opening plies.</summary>
    public int MaxOpeningPlies { get; init; } = 16;

    /// <summary>Games played in parallel.</summary>
    public int Workers { get; init; } = 1;

    /// <summary>Give up after this many games (0: 20 per position wanted).</summary>
    public long MaxGames { get; init; }
}

/// <summary>A generated suite: the three splits and how full each stratum got.</summary>
/// <param name="Train">Training positions.</param>
/// <param name="Validation">Validation positions.</param>
/// <param name="Test">Test positions.</param>
/// <param name="Games">Games played.</param>
/// <param name="Shortfall">Positions missing, by "split phase winner" stratum, when the games ran out first.</param>
public sealed record SuiteResult(IReadOnlyList<LabelledPosition> Train, IReadOnlyList<LabelledPosition> Validation,
    IReadOnlyList<LabelledPosition> Test, long Games, IReadOnlyDictionary<string, int> Shortfall);

/// <summary>
/// The position suite generator (spec 11.2 with decision D-28): one position sampled uniformly from each game between
/// EpsilonAgent(ε) around MinimaxAgent(lin-v0, fixed depth) on both sides, started from a random D-15 opening.
/// Positions are spread over equal quotas per phase (HoundMovesLeft in four equal buckets) × solver winner, and
/// split into train, validation and test by their hash, so no position appears in two splits.
/// </summary>
public static class Suites
{
    private const ulong SampleSalt = 0x5017_E5A3_9C2B_D00DUL;
    private static readonly string[] SplitNames = ["train", "validation", "test"];

    /// <summary>Generates a suite. Deterministic for a seed, whatever the worker count.</summary>
    public static SuiteResult Generate(SuiteOptions options, SolverTable table, IProgress<long>? gamesPlayed = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(table);
        var rules = new Rules(options.RuleSet);
        if (!table.Covers(rules.Config))
        {
            throw new ArgumentException("The table is for other rules.", nameof(table));
        }

        int[] sizes = [options.Train, options.Validation, options.Test];
        if (sizes.Any(s => s < 0) || sizes.Sum() == 0 || options.MinOpeningPlies < 0 || options.MaxOpeningPlies < options.MinOpeningPlies)
        {
            throw new ArgumentException("Split sizes must be 0 or more with a positive total, and the opening plies a range.", nameof(options));
        }

        // Quota per split and stratum (4 phases × 2 winners); the remainder goes to the first strata.
        var quota = new int[3, 8];
        for (int split = 0; split < 3; split++)
        {
            for (int k = 0; k < 8; k++)
            {
                quota[split, k] = (sizes[split] / 8) + (k < sizes[split] % 8 ? 1 : 0);
            }
        }

        int maxMovesLeft = MovesLeft(rules.Initial());
        int total = sizes.Sum();
        long maxGames = options.MaxGames > 0 ? options.MaxGames : 20L * total;
        var splits = new[] { new List<LabelledPosition>(), new List<LabelledPosition>(), new List<LabelledPosition>() };
        var seen = new HashSet<GameState>();
        int workers = Math.Max(1, options.Workers);
        using var players = new ThreadLocal<IAgent[]>(() => [Player(options, rules), Player(options, rules)]);
        long game = 0;
        int open = total;
        while (open > 0 && game < maxGames)
        {
            ct.ThrowIfCancellationRequested();
            int batch = (int)Math.Min(64L * workers, maxGames - game);
            var samples = new LabelledPosition?[batch];
            long first = game;
            Parallel.For(0, batch, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, i =>
            {
                samples[i] = Sample(rules, table, options, players.Value!, first + i, ct);
            });
            game += batch;
            foreach (var sample in samples)
            {
                // In game order, so the result does not depend on how the batch was scheduled.
                if (sample is not { } p || !seen.Add(p.Position))
                {
                    continue;
                }

                int split = Split(p.Position, sizes);
                int stratum = (Phase(p.Position, maxMovesLeft) * 2) + (p.Winner == Side.Fox ? 0 : 1);
                if (quota[split, stratum] > 0)
                {
                    quota[split, stratum]--;
                    splits[split].Add(p);
                    open--;
                }
            }

            gamesPlayed?.Report(game);
        }

        var shortfall = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int split = 0; split < 3; split++)
        {
            for (int k = 0; k < 8; k++)
            {
                if (quota[split, k] > 0)
                {
                    shortfall[$"{SplitNames[split]} phase{k / 2} {(k % 2 == 0 ? "foxWon" : "houndsWon")}"] = quota[split, k];
                }
            }
        }

        return new SuiteResult(splits[0], splits[1], splits[2], game, shortfall);
    }

    /// <summary>HoundMovesLeft in four equal buckets, 0 (late) to 3 (early).</summary>
    public static int Phase(in GameState s, int maxMovesLeft) => Math.Min(3, 4 * MovesLeft(s) / (maxMovesLeft + 1));

    private static int MovesLeft(in GameState s)
    {
        Span<int> features = stackalloc int[FeatureExtractor.Count];
        FeatureExtractor.Extract(s, features);
        return features[(int)FeatureId.HoundMovesLeft];
    }

    private static int Split(in GameState s, int[] sizes)
    {
        // The Zobrist hash spreads positions evenly; a position always lands in the same split.
        ulong bucket = (s.Hash ^ (s.Hash >> 29)) % (ulong)sizes.Sum();
        return bucket < (ulong)sizes[0] ? 0 : bucket < (ulong)(sizes[0] + sizes[1]) ? 1 : 2;
    }

    private static EpsilonAgent Player(SuiteOptions options, Rules rules) =>
        new(new MinimaxAgent(WeightFile.LinV0.CreateEvaluator(rules.Geo.N), new SearchOptions { TtMegabytes = 1 }, SystemClock.Instance), options.Epsilon);

    private static LabelledPosition? Sample(Rules rules, SolverTable table, SuiteOptions options, IAgent[] players, long game, CancellationToken ct)
    {
        var rng = Rng.ForGame(options.Seed, game);
        var starts = rules.FoxStartSquares;
        var s = rules.Config.FoxStart == FoxStart.ChooseOnBackRow ? rules.Initial(starts[rng.NextInt(starts.Count)]) : rules.Initial();
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        int plies = options.MinOpeningPlies + rng.NextInt(options.MaxOpeningPlies - options.MinOpeningPlies + 1);
        for (int i = 0; i < plies; i++)
        {
            if (rules.Evaluate(s).IsOver)
            {
                return null;
            }

            int count = rules.GenerateMoves(s, moves);
            s = rules.Apply(s, moves[rng.NextInt(count)]);
        }

        if (rules.Evaluate(s).IsOver)
        {
            return null;
        }

        var budget = SearchBudget.Depth(options.Depth);
        var record = GameRunner.Play(rules, players[0], players[1],
            new GameOptions { FoxBudget = budget, HoundsBudget = budget, Start = s, Clock = SystemClock.Instance }, rng.Split(), ct);
        var positions = record.Replay(rules);

        // The final position is over; every earlier one is a candidate.
        int pick = Rng.ForGame(options.Seed ^ SampleSalt, game).NextInt(positions.Count - 1);
        var chosen = positions[pick];
        return new LabelledPosition(chosen, table.Get(chosen));
    }
}
