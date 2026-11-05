using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoxHounds.Engine.Evaluation;

/// <summary>
/// A weight file (spec 9.3): a named weighting for a linear or tapered evaluator. A missing feature has weight 0;
/// an unknown feature name is a load error. A file with a <see cref="BoardSize"/> may only be used for that size.
/// </summary>
public sealed record WeightFile
{
    /// <summary>Format tag written to new files; files without one are read as this version (decision D-36).</summary>
    public const string CurrentFormat = "weights/1";

    /// <summary>Evaluator type of a linear weight file.</summary>
    public const string Linear = "linear";

    /// <summary>Evaluator type of a tapered weight file.</summary>
    public const string Tapered = "tapered";

    /// <summary>Name of the weighting, used as the evaluator's name (e.g. lin-v0).</summary>
    public required string Name { get; init; }

    /// <summary><see cref="Linear"/> or <see cref="Tapered"/>.</summary>
    public required string Evaluator { get; init; }

    /// <summary>The board size the weights were tuned for, or null for any size.</summary>
    public int? BoardSize { get; init; }

    /// <summary>Linear weights by feature name.</summary>
    public IReadOnlyDictionary<string, int>? Weights { get; init; }

    /// <summary>Tapered weights for the early phase, by feature name.</summary>
    public IReadOnlyDictionary<string, int>? Early { get; init; }

    /// <summary>Tapered weights for the late phase, by feature name.</summary>
    public IReadOnlyDictionary<string, int>? Late { get; init; }

    /// <summary>Where the weights came from: "hand-set", or the tuning experiment id.</summary>
    public string? Provenance { get; init; }

    /// <summary>
    /// The hand-set starting point of spec 9.3. It carries no board size (decision D-18): it is not tuned, so it
    /// applies to every n.
    /// </summary>
    public static WeightFile LinV0 { get; } = new()
    {
        Name = "lin-v0",
        Evaluator = Linear,
        Weights = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["FoxAdvance"] = 8,
            ["FoxMobility"] = 12,
            ["HoundMobility"] = -4,
            ["FoxRegion"] = 3,
            ["GoalReachable"] = 250,
            ["GoalCloseness"] = 25,
            ["HoundsPassed"] = 80,
            ["HoundSpread"] = 15,
            ["HoundMovesLeft"] = -2,
            ["EscapeDistance"] = -10,
            ["FoxToMove"] = 5,
        },
        Provenance = "hand-set starting point, not tuned",
    };

    /// <summary>Reads a weight file.</summary>
    /// <exception cref="InvalidDataException">The file is malformed, names an unknown feature or evaluator, or uses an
    /// unsupported format version.</exception>
    public static WeightFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>Parses weight file JSON.</summary>
    /// <exception cref="InvalidDataException">See <see cref="Load"/>.</exception>
    public static WeightFile Parse(string json, string source = "weight file")
    {
        WeightFileJson? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, EngineJsonContext.Default.WeightFileJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{source}: {ex.Message}", ex);
        }

        if (dto is null || string.IsNullOrEmpty(dto.Name) || string.IsNullOrEmpty(dto.Evaluator))
        {
            throw new InvalidDataException($"{source}: name and evaluator are required.");
        }

        if (dto.Format is not null && dto.Format != CurrentFormat)
        {
            throw new InvalidDataException($"{source}: unsupported format '{dto.Format}' (expected {CurrentFormat}).");
        }

        var file = new WeightFile
        {
            Name = dto.Name,
            Evaluator = dto.Evaluator,
            BoardSize = dto.BoardSize,
            Weights = dto.Weights,
            Early = dto.Early,
            Late = dto.Late,
            Provenance = dto.Provenance,
        };
        file.Validate(source);
        return file;
    }

    /// <summary>Writes the file as indented JSON with a format tag.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllText(path, ToJson());
    }

    /// <summary>The file's JSON text.</summary>
    public string ToJson()
    {
        var dto = new WeightFileJson
        {
            Format = CurrentFormat,
            Name = Name,
            Evaluator = Evaluator,
            BoardSize = BoardSize,
            Weights = Ordered(Weights),
            Early = Ordered(Early),
            Late = Ordered(Late),
            Provenance = Provenance,
        };
        return JsonSerializer.Serialize(dto, EngineJsonContext.Default.WeightFileJson) + "\n";
    }

    /// <summary>Creates the evaluator for board size <paramref name="n"/>.</summary>
    /// <exception cref="InvalidDataException">The file is for another board size.</exception>
    public IEvaluator CreateEvaluator(int n)
    {
        if (BoardSize is int size && size != n)
        {
            throw new InvalidDataException($"Weights '{Name}' are for {size}×{size}, not {n}×{n}; weights are not assumed to transfer.");
        }

        Validate(Name);
        return Evaluator == Linear
            ? new LinearEvaluator(Name, ToVector(Weights, Name))
            : new TaperedEvaluator(Name, ToVector(Early, Name), ToVector(Late, Name));
    }

    /// <summary>Weights in <see cref="FeatureId"/> order; missing features are 0.</summary>
    /// <exception cref="InvalidDataException">A feature name is unknown.</exception>
    public static int[] ToVector(IReadOnlyDictionary<string, int>? weights, string source)
    {
        var vector = new int[FeatureExtractor.Count];
        if (weights is null)
        {
            return vector;
        }

        foreach (var (name, weight) in weights)
        {
            // Exact, case-sensitive names only, so a typo can never silently disable a feature (spec 9.3).
            int id = IndexOfFeature(name);
            if (id < 0)
            {
                throw new InvalidDataException($"{source}: unknown feature '{name}'.");
            }

            vector[id] = weight;
        }

        return vector;
    }

    /// <summary>The same weights as a dictionary in <see cref="FeatureId"/> order, with zeros included.</summary>
    public static IReadOnlyDictionary<string, int> FromVector(ReadOnlySpan<int> vector)
    {
        if (vector.Length != FeatureExtractor.Count)
        {
            throw new ArgumentException($"Expected {FeatureExtractor.Count} weights.", nameof(vector));
        }

        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < vector.Length; i++)
        {
            dict[FeatureExtractor.FeatureNames[i]] = vector[i];
        }

        return dict;
    }

    private static int IndexOfFeature(string name)
    {
        var names = FeatureExtractor.FeatureNames;
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static Dictionary<string, int>? Ordered(IReadOnlyDictionary<string, int>? weights)
    {
        if (weights is null)
        {
            return null;
        }

        var ordered = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string name in FeatureExtractor.FeatureNames)
        {
            if (weights.TryGetValue(name, out int w))
            {
                ordered[name] = w;
            }
        }

        foreach (var (name, w) in weights)
        {
            ordered.TryAdd(name, w);
        }

        return ordered;
    }

    private void Validate(string source)
    {
        if (Evaluator == Linear)
        {
            if (Weights is null || Early is not null || Late is not null)
            {
                throw new InvalidDataException($"{source}: a linear weight file has \"weights\" and no \"early\"/\"late\".");
            }

            ToVector(Weights, source);
        }
        else if (Evaluator == Tapered)
        {
            if (Early is null || Late is null || Weights is not null)
            {
                throw new InvalidDataException($"{source}: a tapered weight file has \"early\" and \"late\" instead of \"weights\".");
            }

            ToVector(Early, source);
            ToVector(Late, source);
        }
        else
        {
            throw new InvalidDataException($"{source}: unknown evaluator '{Evaluator}' (expected {Linear} or {Tapered}).");
        }

        if (BoardSize is int n && (n < Core.RuleSet.MinBoardSize || n > Core.RuleSet.MaxBoardSize))
        {
            throw new InvalidDataException($"{source}: board size {n} is outside 4…16.");
        }
    }
}

/// <summary>JSON shape of a weight file.</summary>
internal sealed class WeightFileJson
{
    public string? Format { get; set; }

    public string? Name { get; set; }

    public string? Evaluator { get; set; }

    public int? BoardSize { get; set; }

    public Dictionary<string, int>? Weights { get; set; }

    public Dictionary<string, int>? Early { get; set; }

    public Dictionary<string, int>? Late { get; set; }

    public string? Provenance { get; set; }
}

/// <summary>Source-generated JSON metadata for the Engine's files (spec 4.1: no reflection).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(WeightFileJson))]
internal sealed partial class EngineJsonContext : JsonSerializerContext
{
}
