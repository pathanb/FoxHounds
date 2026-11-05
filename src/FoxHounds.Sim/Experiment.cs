using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FoxHounds.Agents;
using FoxHounds.Core;
using FoxHounds.Solver;

namespace FoxHounds.Sim;

/// <summary>The four experiment modes of spec 11.1.</summary>
public enum ExperimentMode
{
    /// <summary>The agent picks one move per suite position; no games.</summary>
    Grade,

    /// <summary>The candidate plays one side from labelled openings against a fixed reference.</summary>
    VsReference,

    /// <summary>A and B play each opening twice, swapping sides.</summary>
    HeadToHead,

    /// <summary>The candidate plays HeadToHead against each opponent.</summary>
    Gauntlet,
}

/// <summary>Which game records a run keeps (spec 11.4).</summary>
public enum RecordPolicy
{
    /// <summary>None.</summary>
    None,

    /// <summary>Every game with a blunder, and a 1% random sample.</summary>
    BlundersAndSample,

    /// <summary>Every game.</summary>
    All,
}

/// <summary>An agent of an experiment under its role ("candidate", "reference", "a", "b", "opponent1"…).</summary>
/// <param name="Role">The role key.</param>
/// <param name="Spec">The agent.</param>
public sealed record ExperimentAgent(string Role, AgentSpec Spec);

/// <summary>
/// A validated experiment spec (format experiment/1, spec 14.3) with its agents and positions loaded. Its
/// <see cref="Sha256"/> covers everything that decides the results (rules, seed, mode, agents and their weight
/// files, the positions, budgets), and not the worker count, game cap or memory limit, so a run can be resumed with
/// other values of those.
/// </summary>
public sealed class Experiment
{
    /// <summary>The format tag.</summary>
    public const string FormatTag = "experiment/1";

    private Experiment()
    {
    }

    /// <summary>The experiment's id.</summary>
    public required string Id { get; init; }

    /// <summary>The master seed; game i uses Rng.ForGame(seed, i) (spec 11.3).</summary>
    public required ulong Seed { get; init; }

    /// <summary>The rules every game uses.</summary>
    public required RuleSet RuleSet { get; init; }

    /// <summary>The mode.</summary>
    public required ExperimentMode Mode { get; init; }

    /// <summary>The agents: the candidate (or A) first, then the reference, B or the opponents.</summary>
    public required IReadOnlyList<ExperimentAgent> Agents { get; init; }

    /// <summary>VsReference: the candidate's side.</summary>
    public Side CandidateSide { get; init; } = Side.Hounds;

    /// <summary>The openings, or the suite in Grade mode.</summary>
    public required IReadOnlyList<LabelledPosition> Positions { get; init; }

    /// <summary>Where the positions came from.</summary>
    public required string PositionsPath { get; init; }

    /// <summary>Stop after this many games (positions in Grade mode); null for all.</summary>
    public int? MaxGames { get; init; }

    /// <summary>Workers; 0 means ProcessorCount − 1 (at least 1).</summary>
    public int Workers { get; init; }

    /// <summary>Refuse to start when the transposition tables and solver table would need more.</summary>
    public int MaxMemoryMb { get; init; } = 4096;

    /// <summary>Early stopping for HeadToHead, if set.</summary>
    public SprtSettings? Sprt { get; init; }

    /// <summary>Which game records to keep.</summary>
    public RecordPolicy SaveRecords { get; init; } = RecordPolicy.BlundersAndSample;

    /// <summary>Budget for whichever agent plays the fox, overriding its spec (D-22).</summary>
    public SearchBudget? FoxBudget { get; init; }

    /// <summary>Budget for whichever agent plays the hounds, overriding its spec (D-22).</summary>
    public SearchBudget? HoundsBudget { get; init; }

    /// <summary>True when time budgets and tables kept between games are allowed (results then depend on timing).</summary>
    public bool AllowNonDeterministic { get; init; }

    /// <summary>The canonical spec that <see cref="Sha256"/> is computed over.</summary>
    public required JsonObject Canonical { get; init; }

    /// <summary>SHA-256 of the canonical spec, lowercase hex.</summary>
    public required string Sha256 { get; init; }

    /// <summary>The budget an agent gets on <paramref name="side"/>: the experiment's override, else its spec's.</summary>
    public SearchBudget BudgetFor(Side side, AgentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return (side == Side.Fox ? FoxBudget : HoundsBudget) ?? spec.ToBudget();
    }

    /// <summary>Reads and validates an experiment spec; paths in it are relative to its folder.</summary>
    /// <exception cref="InvalidDataException">The spec, an agent or the positions are invalid.</exception>
    public static Experiment Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        return Parse(File.ReadAllText(full), Path.GetDirectoryName(full) ?? "", full);
    }

    /// <summary>Parses and validates experiment JSON; see <see cref="Load"/>.</summary>
    public static Experiment Parse(string json, string baseDirectory, string source = "experiment")
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ExperimentJson dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, SimJsonContext.Default.ExperimentJson)
                ?? throw new InvalidDataException($"{source}: empty spec.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{source}: {ex.Message}", ex);
        }

        try
        {
            return Build(dto, baseDirectory, source);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException)
        {
            throw new InvalidDataException($"{source}: {ex.Message}", ex);
        }
    }

    private static Experiment Build(ExperimentJson dto, string baseDirectory, string source)
    {
        if (dto.Format is { } format && !(format.StartsWith("experiment/", StringComparison.Ordinal)
            && format["experiment/".Length..].Split('.')[0] == "1"))
        {
            throw new InvalidDataException($"{source}: unsupported format '{format}' (expected {FormatTag}).");
        }

        string id = string.IsNullOrWhiteSpace(dto.Id) ? throw new InvalidDataException($"{source}: id is required.") : dto.Id;
        ulong seed = dto.Seed ?? throw new InvalidDataException($"{source}: seed is required.");
        var ruleSet = ParseRuleSet(dto.RuleSet, source);
        if (dto.BoardSize is int n)
        {
            ruleSet = ruleSet with { BoardSize = n, FixedFoxColumn = ruleSet.BoardSize == n ? ruleSet.FixedFoxColumn : null };
        }

        _ = new Rules(ruleSet); // validates the ruleset
        var mode = JsonNames.Parse<ExperimentMode>(dto.Mode ?? throw new InvalidDataException($"{source}: mode is required."), "mode");
        bool solvable = SolverTable.IsSupported(ruleSet.BoardSize);
        if (!solvable && mode is ExperimentMode.Grade or ExperimentMode.VsReference)
        {
            throw new InvalidDataException($"{source}: {mode} needs solver labels, and there is no solver on {ruleSet.BoardSize}x{ruleSet.BoardSize} (spec 11.2).");
        }

        var agents = LoadAgents(dto.Agents, mode, baseDirectory, source);
        var candidateSide = dto.CandidateSide is null ? Side.Hounds : JsonNames.Parse<Side>(dto.CandidateSide, "candidateSide");
        string? positionsField = mode == ExperimentMode.Grade ? dto.Suite ?? dto.Openings : dto.Openings;
        string positionsPath = positionsField is null
            ? throw new InvalidDataException($"{source}: {(mode == ExperimentMode.Grade ? "suite" : "openings")} is required.")
            : Resolve(baseDirectory, positionsField);
        byte[] positionsBytes = File.ReadAllBytes(positionsPath);
        var positions = PositionFile.Parse(Encoding.UTF8.GetString(positionsBytes), positionsPath, ruleSet).Positions;
        if (positions.Count == 0)
        {
            throw new InvalidDataException($"{source}: {positionsPath} holds no positions.");
        }

        if (dto.MaxGames is < 1 || dto.Workers is < 0 || dto.MaxMemoryMb is < 1)
        {
            throw new InvalidDataException($"{source}: maxGames and maxMemoryMb must be positive, workers 0 or more.");
        }

        SprtSettings? sprt = null;
        if (dto.Sprt is { } s)
        {
            if (mode != ExperimentMode.HeadToHead)
            {
                throw new InvalidDataException($"{source}: sprt is for headToHead only.");
            }

            sprt = new SprtSettings(s.Elo0 ?? 0, s.Elo1 ?? 10, s.Alpha ?? 0.05, s.Beta ?? 0.05);
            if (sprt.Alpha is <= 0 or >= 1 || sprt.Beta is <= 0 or >= 1 || sprt.Elo1 <= sprt.Elo0)
            {
                throw new InvalidDataException($"{source}: sprt needs 0 < alpha, beta < 1 and elo1 > elo0.");
            }
        }

        var records = dto.SaveRecords is null ? RecordPolicy.BlundersAndSample : JsonNames.Parse<RecordPolicy>(dto.SaveRecords, "saveRecords");
        SearchBudget? foxBudget = ToBudget(dto.Budgets?.Fox, source);
        SearchBudget? houndsBudget = ToBudget(dto.Budgets?.Hounds, source);
        bool allowNonDeterministic = dto.AllowNonDeterministic ?? false;
        if (!allowNonDeterministic)
        {
            foreach (var agent in agents)
            {
                // A hard limit is only a watchdog in an experiment (D-21); a soft one changes the results with timing.
                foreach (var side in new[] { Side.Fox, Side.Hounds })
                {
                    var budget = (side == Side.Fox ? foxBudget : houndsBudget) ?? agent.Spec.ToBudget();
                    if (budget.SoftTime is not null)
                    {
                        throw new InvalidDataException($"{source}: {agent.Role} has a soft time limit; set allowNonDeterministic to run it.");
                    }
                }

                if (agent.Spec.Search?.KeepTtBetweenGames == true)
                {
                    throw new InvalidDataException($"{source}: {agent.Role} keeps its table between games, so results would depend on which worker played which game; set allowNonDeterministic to run it.");
                }
            }
        }

        var canonical = BuildCanonical(dto, id, seed, ruleSet, mode, agents, candidateSide, positionsBytes, positions.Count,
            sprt, records, foxBudget, houndsBudget, allowNonDeterministic);
        return new Experiment
        {
            Id = id,
            Seed = seed,
            RuleSet = ruleSet,
            Mode = mode,
            Agents = agents,
            CandidateSide = candidateSide,
            Positions = positions,
            PositionsPath = positionsPath,
            MaxGames = dto.MaxGames,
            Workers = dto.Workers ?? 0,
            MaxMemoryMb = dto.MaxMemoryMb ?? 4096,
            Sprt = sprt,
            SaveRecords = records,
            FoxBudget = foxBudget,
            HoundsBudget = houndsBudget,
            AllowNonDeterministic = allowNonDeterministic,
            Canonical = canonical,
            Sha256 = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToJsonString()))),
        };
    }

    private static RuleSet ParseRuleSet(JsonElement? element, string source)
    {
        if (element is not { } e || e.ValueKind == JsonValueKind.Null)
        {
            return RuleSet.Classic;
        }

        if (e.ValueKind == JsonValueKind.String)
        {
            return e.GetString() switch
            {
                "classic" => RuleSet.Classic,
                "gilith" => RuleSet.Gilith,
                var other => throw new InvalidDataException($"{source}: ruleSet '{other}' is not a preset (classic, gilith)."),
            };
        }

        var dto = e.Deserialize(SimJsonContext.Default.RuleSetJson) ?? throw new InvalidDataException($"{source}: empty ruleSet.");
        return dto.ToRuleSet();
    }

    private static List<ExperimentAgent> LoadAgents(AgentsJson? dto, ExperimentMode mode, string baseDirectory, string source)
    {
        if (dto is null)
        {
            throw new InvalidDataException($"{source}: agents are required.");
        }

        ExperimentAgent Required(JsonElement? entry, string role) => entry is { ValueKind: not JsonValueKind.Null } e
            ? new ExperimentAgent(role, LoadAgent(e, baseDirectory, $"{source}: agents.{role}"))
            : throw new InvalidDataException($"{source}: {mode} needs agents.{role}.");

        var list = mode switch
        {
            ExperimentMode.Grade => [Required(dto.Candidate, "candidate")],
            ExperimentMode.VsReference => [Required(dto.Candidate, "candidate"), Required(dto.Reference, "reference")],
            ExperimentMode.HeadToHead => [Required(dto.A, "a"), Required(dto.B, "b")],
            _ => new List<ExperimentAgent> { Required(dto.Candidate, "candidate") },
        };
        if (mode == ExperimentMode.Gauntlet)
        {
            if (dto.Opponents is not { Count: > 0 } opponents)
            {
                throw new InvalidDataException($"{source}: gauntlet needs agents.opponents.");
            }

            for (int i = 0; i < opponents.Count; i++)
            {
                list.Add(new ExperimentAgent($"opponent{i + 1}", LoadAgent(opponents[i], baseDirectory, $"{source}: agents.opponents[{i}]")));
            }
        }

        return list;
    }

    private static AgentSpec LoadAgent(JsonElement entry, string baseDirectory, string source) => entry.ValueKind switch
    {
        JsonValueKind.String => AgentSpec.Load(Resolve(baseDirectory, entry.GetString()!)),
        JsonValueKind.Object => AgentSpec.Parse(entry.GetRawText(), baseDirectory, source),
        _ => throw new InvalidDataException($"{source}: an agent is a path or an agent/1 object."),
    };

    private static SearchBudget? ToBudget(BudgetSpec? spec, string source)
    {
        try
        {
            return spec?.ToBudget();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException($"{source}: budgets: {ex.Message}", ex);
        }
    }

    private static string Resolve(string baseDirectory, string path) =>
        Path.IsPathRooted(path) || baseDirectory.Length == 0 ? path : Path.Combine(baseDirectory, path);

    private static JsonObject BuildCanonical(ExperimentJson dto, string id, ulong seed, RuleSet ruleSet, ExperimentMode mode,
        List<ExperimentAgent> agents, Side candidateSide, byte[] positionsBytes, int positionCount, SprtSettings? sprt,
        RecordPolicy records, SearchBudget? foxBudget, SearchBudget? houndsBudget, bool allowNonDeterministic)
    {
        var agentsNode = new JsonObject();
        foreach (var agent in agents)
        {
            var node = JsonNode.Parse(agent.Spec.ToJson())!.AsObject();
            if (agent.Spec.Evaluator?.WeightsFile is { } weights)
            {
                node["weightsSha256"] = Hex(SHA256.HashData(File.ReadAllBytes(agent.Spec.Resolve(weights))));
            }

            agentsNode[agent.Role] = node;
        }

        JsonNode? Budget(BudgetSpec? b) => b is null ? null : JsonSerializer.SerializeToNode(b, SimJsonContext.Default.BudgetSpec);
        return new JsonObject
        {
            ["format"] = FormatTag,
            ["id"] = id,
            ["seed"] = seed,
            ["ruleSet"] = JsonSerializer.SerializeToNode(RuleSetJson.From(ruleSet), SimJsonContext.Default.RuleSetJson),
            ["mode"] = JsonNames.Of(mode),
            ["agents"] = agentsNode,
            ["candidateSide"] = mode == ExperimentMode.VsReference ? JsonNames.Of(candidateSide) : null,
            ["positions"] = new JsonObject { ["sha256"] = Hex(SHA256.HashData(positionsBytes)), ["count"] = positionCount },
            ["sprt"] = sprt is null ? null : new JsonObject { ["elo0"] = sprt.Elo0, ["elo1"] = sprt.Elo1, ["alpha"] = sprt.Alpha, ["beta"] = sprt.Beta },
            ["saveRecords"] = JsonNames.Of(records),
            ["budgets"] = foxBudget is null && houndsBudget is null ? null
                : new JsonObject { ["fox"] = Budget(dto.Budgets?.Fox), ["hounds"] = Budget(dto.Budgets?.Hounds) },
            ["allowNonDeterministic"] = allowNonDeterministic,
        };
    }

    internal static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
