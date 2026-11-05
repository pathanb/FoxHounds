using FoxHounds.Core;
using FoxHounds.Engine;
using FoxHounds.Solver;

namespace FoxHounds.Sim;

/// <summary>Which positions an opening generator keeps (spec 11.2).</summary>
public enum OpeningFilter
{
    /// <summary>Every position.</summary>
    Any,

    /// <summary>Positions the solver rates won for the hounds.</summary>
    HoundsWon,

    /// <summary>Positions the solver rates won for the fox.</summary>
    FoxWon,
}

/// <summary>How the plies of an opening are chosen (spec 11.2).</summary>
public enum OpeningPolicy
{
    /// <summary>Uniformly random legal plies.</summary>
    Uniform,

    /// <summary>EpsilonAgent around SolverAgent for both sides.</summary>
    NoisySolver,
}

/// <summary>Settings for <see cref="Openings.Generate"/>.</summary>
public sealed record OpeningOptions
{
    /// <summary>The rules; under ChooseOnBackRow each fox start square is used in turn.</summary>
    public required RuleSet RuleSet { get; init; }

    /// <summary>Fewest plies (decision D-15: each opening's length is uniform in the range).</summary>
    public int MinPlies { get; init; } = 6;

    /// <summary>Most plies.</summary>
    public int MaxPlies { get; init; } = 6;

    /// <summary>Openings wanted.</summary>
    public int Count { get; init; } = 1000;

    /// <summary>Seed of the generator's random stream.</summary>
    public ulong Seed { get; init; } = 7;

    /// <summary>Label each position with its solver value (needs a table).</summary>
    public bool Label { get; init; } = true;

    /// <summary>Which positions to keep; anything but Any needs labels.</summary>
    public OpeningFilter Filter { get; init; } = OpeningFilter.Any;

    /// <summary>How plies are chosen.</summary>
    public OpeningPolicy Policy { get; init; } = OpeningPolicy.Uniform;

    /// <summary>Random-ply probability of the noisy solver.</summary>
    public double Epsilon { get; init; } = 0.1;

    /// <summary>Give up after this many attempts (0: 1,000 per opening wanted).</summary>
    public long MaxAttempts { get; init; }
}

/// <summary>Generated openings and how many attempts were spent on them.</summary>
/// <param name="Positions">The openings, in generation order.</param>
/// <param name="Attempts">Random openings played.</param>
/// <param name="Terminal">Attempts dropped because the game ended.</param>
/// <param name="Duplicates">Attempts dropped as repeats of an earlier position.</param>
/// <param name="Filtered">Attempts dropped by the filter.</param>
public sealed record OpeningResult(IReadOnlyList<LabelledPosition> Positions, long Attempts, long Terminal, long Duplicates, long Filtered)
{
    /// <summary>Accepted / attempts.</summary>
    public double AcceptanceRate => Attempts == 0 ? 0 : (double)Positions.Count / Attempts;
}

/// <summary>The opening set generator of spec 11.2 with decision D-15.</summary>
public static class Openings
{
    /// <summary>
    /// Plays random openings from the initial position until <see cref="OpeningOptions.Count"/> distinct, unfinished
    /// positions pass the filter, or the attempts run out. Deterministic for a seed.
    /// </summary>
    /// <param name="options">What to generate.</param>
    /// <param name="table">The solver table for the rules; required for labels, filters and the noisy solver.</param>
    /// <param name="ct">Cancels the generation.</param>
    /// <exception cref="ArgumentException">Inconsistent options, or a table is needed but missing.</exception>
    public static OpeningResult Generate(OpeningOptions options, SolverTable? table, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var rules = new Rules(options.RuleSet);
        if (options.MinPlies < 1 || options.MaxPlies < options.MinPlies)
        {
            throw new ArgumentException($"Plies must be 1 or more, low before high: {options.MinPlies}-{options.MaxPlies}.", nameof(options));
        }

        if (options.Count < 1)
        {
            throw new ArgumentException("Count must be positive.", nameof(options));
        }

        bool needTable = options.Label || options.Filter != OpeningFilter.Any || options.Policy == OpeningPolicy.NoisySolver;
        if (needTable && table is null)
        {
            throw new ArgumentException(SolverTable.IsSupported(rules.Geo.N)
                ? "Labels, filters and the noisy solver need the solver table."
                : $"No solver on {rules.Geo.N}x{rules.Geo.N}: labels and filters other than any are not available (spec 11.2).", nameof(table));
        }

        if (options.Filter != OpeningFilter.Any && !options.Label)
        {
            throw new ArgumentException("A filter needs labels.", nameof(options));
        }

        if (table is not null && !table.Covers(rules.Config))
        {
            throw new ArgumentException("The table is for other rules.", nameof(table));
        }

        var starts = rules.Config.FoxStart == FoxStart.ChooseOnBackRow
            ? rules.FoxStartSquares.Select(sq => rules.Initial(sq)).ToArray()
            : [rules.Initial()];
        long maxAttempts = options.MaxAttempts > 0 ? options.MaxAttempts : 1000L * options.Count;
        var rng = new Rng(options.Seed);
        var seen = new HashSet<GameState>();
        var kept = new List<LabelledPosition>(options.Count);
        var noisy = options.Policy == OpeningPolicy.NoisySolver
            ? new[] { new EpsilonAgent(new SolverAgent(table!, randomTies: true), options.Epsilon), new EpsilonAgent(new SolverAgent(table!, randomTies: true), options.Epsilon) }
            : null;
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        var history = new List<Move>();
        long attempts = 0, terminal = 0, duplicates = 0, filtered = 0;
        while (kept.Count < options.Count && attempts < maxAttempts)
        {
            ct.ThrowIfCancellationRequested();
            var s = starts[attempts % starts.Length];
            attempts++;
            int plies = options.MinPlies + rng.NextInt(options.MaxPlies - options.MinPlies + 1);
            if (noisy is not null)
            {
                noisy[0].NewGame(rules, Side.Fox, rng.Split());
                noisy[1].NewGame(rules, Side.Hounds, rng.Split());
                history.Clear();
            }

            bool ended = false;
            for (int ply = 0; ply < plies; ply++)
            {
                if (rules.Evaluate(s).IsOver)
                {
                    ended = true;
                    break;
                }

                Move move;
                if (noisy is not null)
                {
                    move = noisy[(int)s.SideToMove].ChooseMove(s, history, SearchBudget.Unlimited, ct).Move;
                    history.Add(move);
                }
                else
                {
                    int count = rules.GenerateMoves(s, moves);
                    move = moves[rng.NextInt(count)];
                }

                s = rules.Apply(s, move);
            }

            if (ended || rules.Evaluate(s).IsOver)
            {
                terminal++;
                continue;
            }

            if (!seen.Add(s))
            {
                duplicates++;
                continue;
            }

            SolvedValue? value = options.Label ? table!.Get(s) : null;
            var winner = value?.Winner(s.SideToMove);
            bool keep = options.Filter switch
            {
                OpeningFilter.HoundsWon => winner == Side.Hounds,
                OpeningFilter.FoxWon => winner == Side.Fox,
                _ => true,
            };
            if (!keep)
            {
                filtered++;
                continue;
            }

            kept.Add(new LabelledPosition(s, value));
        }

        return new OpeningResult(kept, attempts, terminal, duplicates, filtered);
    }
}
