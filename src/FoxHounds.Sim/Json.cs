using System.Text.Json;
using System.Text.Json.Serialization;
using FoxHounds.Agents;
using FoxHounds.Core;

namespace FoxHounds.Sim;

// JSON shapes of the simulator's files (spec 14.3, 14.4, 11.4). Set accessors, not init: the source generator fills
// every init-only property, which would replace defaults with default(T) (decision D-52).

/// <summary>An experiment spec as written (spec 14.3).</summary>
internal sealed class ExperimentJson
{
    public string? Format { get; set; }

    public string? Id { get; set; }

    public ulong? Seed { get; set; }

    /// <summary>A preset name ("classic", "gilith") or a ruleset object as in 14.2.</summary>
    public JsonElement? RuleSet { get; set; }

    public int? BoardSize { get; set; }

    public string? Mode { get; set; }

    public AgentsJson? Agents { get; set; }

    public string? CandidateSide { get; set; }

    public string? Openings { get; set; }

    public string? Suite { get; set; }

    public int? MaxGames { get; set; }

    public int? Workers { get; set; }

    public int? MaxMemoryMb { get; set; }

    public SprtJson? Sprt { get; set; }

    public string? SaveRecords { get; set; }

    public BudgetsJson? Budgets { get; set; }

    public bool? AllowNonDeterministic { get; set; }
}

/// <summary>Agents by role; each entry is a path to an agent/1 file or an inline spec object.</summary>
internal sealed class AgentsJson
{
    public JsonElement? Candidate { get; set; }

    public JsonElement? Reference { get; set; }

    public JsonElement? A { get; set; }

    public JsonElement? B { get; set; }

    public List<JsonElement>? Opponents { get; set; }
}

internal sealed class SprtJson
{
    public double? Elo0 { get; set; }

    public double? Elo1 { get; set; }

    public double? Alpha { get; set; }

    public double? Beta { get; set; }
}

/// <summary>Budget overrides by side (decision D-22).</summary>
internal sealed class BudgetsJson
{
    public BudgetSpec? Fox { get; set; }

    public BudgetSpec? Hounds { get; set; }
}

/// <summary>spec.json in a run folder (spec 11.4).</summary>
internal sealed class RunSpecJson
{
    public string? Format { get; set; }

    public string? Sha256 { get; set; }

    public string? Commit { get; set; }

    public string? Version { get; set; }

    public JsonElement? Spec { get; set; }
}

/// <summary>A count per side; null sides are left out.</summary>
internal sealed class SidesJson
{
    public long? Fox { get; set; }

    public long? Hounds { get; set; }
}

/// <summary>One games.jsonl line (spec 14.4), plus the roles and plies in won positions the summary needs.</summary>
internal sealed class GameLineJson
{
    public int Game { get; set; }

    public int Opening { get; set; }

    public string? Fox { get; set; }

    public string? Hounds { get; set; }

    public string? FoxRole { get; set; }

    public string? HoundsRole { get; set; }

    public string? Result { get; set; }

    public string? Reason { get; set; }

    public int Plies { get; set; }

    public string? OpeningLabel { get; set; }

    public SidesJson? Blunders { get; set; }

    public SidesJson? WonPlies { get; set; }

    /// <summary>Plies each side made.</summary>
    public SidesJson? Moves { get; set; }

    public SidesJson? Nodes { get; set; }

    public string? Forfeit { get; set; }

    public long Ms { get; set; }
}

/// <summary>One games.jsonl line in Grade mode: the move an agent chose in a suite position.</summary>
internal sealed class GradeLineJson
{
    public int Position { get; set; }

    public string? Agent { get; set; }

    public string? Side { get; set; }

    public string? Label { get; set; }

    public string? Move { get; set; }

    /// <summary>Won positions: the move keeps the win.</summary>
    public bool? Kept { get; set; }

    /// <summary>Lost positions: the move resists as long as any move can.</summary>
    public bool? Longest { get; set; }

    public long Nodes { get; set; }

    public long Ms { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(ExperimentJson))]
[JsonSerializable(typeof(RunSpecJson))]
[JsonSerializable(typeof(GameLineJson))]
[JsonSerializable(typeof(GradeLineJson))]
[JsonSerializable(typeof(RuleSetJson))]
[JsonSerializable(typeof(BudgetSpec))]
internal sealed partial class SimJsonContext : JsonSerializerContext
{
}
