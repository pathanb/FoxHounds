using FoxHounds.Core;

namespace FoxHounds.Engine;

/// <summary>Plays a uniformly random legal ply, drawn from its game <see cref="Rng"/> (spec 7.2). Baseline and smoke
/// tests.</summary>
public sealed class RandomAgent : IAgent
{
    private Rules? _rules;
    private Rng? _rng;

    /// <inheritdoc />
    public string Name => "random";

    /// <inheritdoc />
    public void NewGame(Rules rules, Side side, Rng rng)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(rng);
        _rules = rules;
        _rng = rng;
    }

    /// <inheritdoc />
    public byte ChooseFoxStart(Rules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var rng = _rng ?? throw new InvalidOperationException("Call NewGame first.");
        return rules.FoxStartSquares[rng.NextInt(rules.FoxStartSquares.Count)];
    }

    /// <inheritdoc />
    public AgentDecision ChooseMove(in GameState state, IReadOnlyList<Move> history, SearchBudget budget, CancellationToken ct)
    {
        var rules = _rules ?? throw new InvalidOperationException("Call NewGame first.");
        var rng = _rng!;
        Span<Move> moves = stackalloc Move[Rules.MaxMoves];
        int count = rules.GenerateMoves(state, moves);
        if (count == 0)
        {
            throw new InvalidOperationException($"No legal ply in {state}.");
        }

        return new AgentDecision(moves[rng.NextInt(count)], AgentInfo.Random);
    }
}
