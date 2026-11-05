using System.Text.Json;
using System.Text.Json.Serialization;
using FoxHounds.Core;

namespace FoxHounds.Agents;

/// <summary>
/// An agent described in JSON, format agent/1 (spec 14.1). One <see cref="AgentFactory"/> builds every agent from
/// such a spec, so an agent described in a file plays identically in the app, the CLI and experiments.
/// </summary>
public sealed record AgentSpec
{
    /// <summary>Format tag of files written by this version.</summary>
    public const string Format = "agent/1";

    /// <summary>random, minimax or solver.</summary>
    public required string Type { get; init; }

    /// <summary>Stable name, logged and recorded (e.g. hard-lin-v0).</summary>
    public required string Name { get; init; }

    /// <summary>Evaluator for minimax: a registered type and an optional weight file (default: the built-in lin-v0).</summary>
    public EvaluatorSpec? Evaluator { get; init; }

    /// <summary>Search options for minimax.</summary>
    public SearchSpec? Search { get; init; }

    /// <summary>The budget passed to the agent on every ply (D-22).</summary>
    public BudgetSpec? Budget { get; init; }

    /// <summary>With probability ε play a random legal ply instead (wraps the agent in an EpsilonAgent when &gt; 0).</summary>
    public double Epsilon { get; init; }

    /// <summary>For solver agents: break ties randomly.</summary>
    public bool RandomTies { get; init; }

    /// <summary>Folder against which relative paths resolve: the spec file's folder, or the current folder.</summary>
    [JsonIgnore]
    public string BaseDirectory { get; init; } = "";

    /// <summary>Reads a spec file; relative paths in it resolve against its folder.</summary>
    /// <exception cref="InvalidDataException">The file is not a valid agent spec.</exception>
    public static AgentSpec Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        return Parse(File.ReadAllText(full), Path.GetDirectoryName(full) ?? "", full);
    }

    /// <summary>Parses agent/1 JSON.</summary>
    /// <exception cref="InvalidDataException">The text is not a valid agent spec.</exception>
    public static AgentSpec Parse(string json, string baseDirectory = "", string source = "agent spec")
    {
        AgentSpec? spec;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{source}: an agent spec is a JSON object.");
            }

            if (doc.RootElement.TryGetProperty("format", out var format) && format.GetString() is { } f
                && !(f.StartsWith("agent/", StringComparison.Ordinal) && f["agent/".Length..].Split('.')[0] == "1"))
            {
                throw new InvalidDataException($"{source}: unsupported format '{f}' (expected {Format}).");
            }

            spec = JsonSerializer.Deserialize(json, AgentsJsonContext.Default.AgentSpec);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{source}: {ex.Message}", ex);
        }

        if (spec is null)
        {
            throw new InvalidDataException($"{source}: empty agent spec.");
        }

        spec = spec with { BaseDirectory = baseDirectory };
        spec.Validate(source);
        return spec;
    }

    /// <summary>The spec as agent/1 JSON (compact), as stored in game records.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, AgentsJsonContext.Default.AgentSpec);

    /// <summary>The budget this spec asks for; no fields means unlimited.</summary>
    public SearchBudget ToBudget() => Budget?.ToBudget() ?? SearchBudget.Unlimited;

    /// <summary>Resolves a path from the spec against <see cref="BaseDirectory"/>.</summary>
    public string Resolve(string path) =>
        Path.IsPathRooted(path) || string.IsNullOrEmpty(BaseDirectory) ? path : Path.Combine(BaseDirectory, path);

    private void Validate(string source)
    {
        if (Type is not (AgentFactory.Random or AgentFactory.Minimax or AgentFactory.Solver))
        {
            throw new InvalidDataException($"{source}: type must be random, minimax or solver, not '{Type}'.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidDataException($"{source}: name is required.");
        }

        if (Epsilon is < 0 or > 1 || double.IsNaN(Epsilon))
        {
            throw new InvalidDataException($"{source}: epsilon must be in [0, 1].");
        }

        if (Search is { } search)
        {
            if (search.RootSelection is not ("best" or "randomAmongBest"))
            {
                throw new InvalidDataException($"{source}: rootSelection must be best or randomAmongBest, not '{search.RootSelection}'.");
            }

            if (search.TtMegabytes is < 0 || search.Margin < 0)
            {
                throw new InvalidDataException($"{source}: ttMegabytes and margin cannot be negative.");
            }
        }

        if (Evaluator is { } evaluator && string.IsNullOrEmpty(evaluator.Type))
        {
            throw new InvalidDataException($"{source}: the evaluator needs a type.");
        }

        try
        {
            _ = ToBudget();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException($"{source}: {ex.Message}", ex);
        }
    }
}

// The parts below use set accessors on purpose: System.Text.Json's source generator fills every init-only property
// of a type, so a field missing from the JSON would get default(T) instead of its initializer ("best" became null).

/// <summary>Evaluator part of an agent spec.</summary>
public sealed record EvaluatorSpec
{
    /// <summary>Registered evaluator type: zero, linear, tapered, solver.</summary>
    public string Type { get; set; } = "linear";

    /// <summary>Weight file, relative to the spec's folder; null means the built-in lin-v0.</summary>
    public string? WeightsFile { get; set; }
}

/// <summary>Search part of an agent spec.</summary>
public sealed record SearchSpec
{
    /// <summary>Transposition table size; null means the factory's default (64 MB in the app, 16 MB in experiments).</summary>
    public int? TtMegabytes { get; set; }

    /// <summary>best or randomAmongBest.</summary>
    public string RootSelection { get; set; } = "best";

    /// <summary>Margin for randomAmongBest.</summary>
    public int Margin { get; set; }

    /// <summary>Keep the table between games.</summary>
    public bool KeepTtBetweenGames { get; set; }
}

/// <summary>Budget part of an agent spec; times in milliseconds.</summary>
public sealed record BudgetSpec
{
    /// <summary>Depth limit.</summary>
    public int? MaxDepth { get; set; }

    /// <summary>Node limit.</summary>
    public long? MaxNodes { get; set; }

    /// <summary>Soft time limit.</summary>
    public int? SoftTimeMs { get; set; }

    /// <summary>Hard time limit.</summary>
    public int? HardTimeMs { get; set; }

    /// <summary>The <see cref="SearchBudget"/> this describes.</summary>
    public SearchBudget ToBudget() => new(
        MaxDepth,
        MaxNodes,
        SoftTimeMs is int soft ? TimeSpan.FromMilliseconds(soft) : null,
        HardTimeMs is int hard ? TimeSpan.FromMilliseconds(hard) : null);
}

/// <summary>Source-generated JSON metadata for agent specs (spec 4.1: no reflection).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(AgentSpec))]
internal sealed partial class AgentsJsonContext : JsonSerializerContext
{
}
