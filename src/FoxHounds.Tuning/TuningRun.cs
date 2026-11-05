using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FoxHounds.Agents;
using FoxHounds.Core;
using FoxHounds.Engine.Evaluation;
using FoxHounds.Sim;
using FoxHounds.Solver;

namespace FoxHounds.Tuning;

/// <summary>A tuning spec as written (format tuning/1; spec 12, 12.1).</summary>
internal sealed class TuningJson
{
    public string? Format { get; set; }

    public string? Id { get; set; }

    public ulong? Seed { get; set; }

    public string? RuleSet { get; set; }

    public int? BoardSize { get; set; }

    public string? Tuner { get; set; }

    public string? Evaluator { get; set; }

    public string? Name { get; set; }

    public string? Train { get; set; }

    public string? Validation { get; set; }

    public string? Start { get; set; }

    public RegressionJson? Regression { get; set; }

    public SpsaJson? Spsa { get; set; }
}

internal sealed class RegressionJson
{
    public double? K { get; set; }

    public double? Lambda { get; set; }

    public double? LearningRate { get; set; }

    public int? MaxSteps { get; set; }

    public int? Patience { get; set; }
}

internal sealed class SpsaJson
{
    public int? Iterations { get; set; }

    public double? A { get; set; }

    public double? C { get; set; }

    public double? Stability { get; set; }

    public double? TargetStep { get; set; }

    public int? ValidateEvery { get; set; }

    public Dictionary<string, double>? Scales { get; set; }

    public string? Objective { get; set; }

    public int? Batch { get; set; }

    public BudgetSpec? Budget { get; set; }

    public string? Openings { get; set; }

    public int? Pairs { get; set; }

    public int? Workers { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(TuningJson))]
internal sealed partial class TuningJsonContext : JsonSerializerContext
{
}

/// <summary>What a tuning run produced.</summary>
/// <param name="Weights">The output weight file.</param>
/// <param name="Report">report.json's content.</param>
public sealed record TuningResult(WeightFile Weights, JsonObject Report);

/// <summary>
/// Runs a tuning spec into a folder (spec 12): weights.json, report.json, run.json (spec, seed, commit and input
/// hashes, so a run can be repeated bit for bit; 12.1) and, for SPSA, tuning.csv. The test split is never read.
/// </summary>
public static class TuningRun
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Loads <paramref name="specPath"/> and runs it into <paramref name="folder"/>.</summary>
    /// <exception cref="InvalidDataException">The spec or one of its inputs is invalid.</exception>
    public static TuningResult Run(string specPath, string folder, Action<string>? log = null, SolverTableStore? tables = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(specPath);
        ArgumentException.ThrowIfNullOrEmpty(folder);
        string full = Path.GetFullPath(specPath);
        string baseDir = Path.GetDirectoryName(full) ?? "";
        byte[] specBytes = File.ReadAllBytes(full);
        TuningJson spec;
        try
        {
            spec = JsonSerializer.Deserialize(specBytes, TuningJsonContext.Default.TuningJson) ?? throw new InvalidDataException($"{full}: empty spec.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{full}: {ex.Message}", ex);
        }

        if (spec.Format is { } format && !(format.StartsWith("tuning/", StringComparison.Ordinal) && format["tuning/".Length..].Split('.')[0] == "1"))
        {
            throw new InvalidDataException($"{full}: unsupported format '{format}' (expected tuning/1).");
        }

        string id = spec.Id ?? throw new InvalidDataException($"{full}: id is required.");
        ulong seed = spec.Seed ?? throw new InvalidDataException($"{full}: seed is required.");
        var ruleSet = (spec.RuleSet ?? "classic") switch
        {
            "classic" => RuleSet.Classic,
            "gilith" => RuleSet.Gilith,
            var other => throw new InvalidDataException($"{full}: ruleSet '{other}' is not a preset."),
        };
        ruleSet = ruleSet with { BoardSize = spec.BoardSize ?? 8 };
        var rules = new Rules(ruleSet);
        bool tapered = (spec.Evaluator ?? WeightFile.Linear) switch
        {
            WeightFile.Linear => false,
            WeightFile.Tapered => true,
            var other => throw new InvalidDataException($"{full}: evaluator '{other}' is not linear or tapered."),
        };
        var inputs = new JsonObject();
        IReadOnlyList<LabelledPosition> Positions(string? path, string field)
        {
            string file = Resolve(baseDir, path ?? throw new InvalidDataException($"{full}: {field} is required."));
            byte[] bytes = File.ReadAllBytes(file);
            inputs[field] = new JsonObject { ["file"] = file, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
            return PositionFile.Parse(Encoding.UTF8.GetString(bytes), file, ruleSet).Positions;
        }

        var train = Positions(spec.Train, "train");
        var validation = Positions(spec.Validation, "validation");
        string name = spec.Name ?? id;
        Directory.CreateDirectory(folder);
        var report = new JsonObject
        {
            ["format"] = "tuning-report/1",
            ["id"] = id,
            ["tuner"] = spec.Tuner,
            ["evaluator"] = tapered ? WeightFile.Tapered : WeightFile.Linear,
            ["trainPositions"] = train.Count,
            ["validationPositions"] = validation.Count,
        };

        int[] weights;
        switch (spec.Tuner)
        {
            case "regression":
                weights = Regression(spec.Regression, train, validation, tapered, report, log, ct);
                break;
            case "spsa":
                var start = StartWeights(spec.Start, baseDir, tapered, inputs, rules.Geo.N);
                var table = (tables ?? SolverTableStore.Default).GetOrBuild(rules, null, ct);
                weights = RunSpsa(spec.Spsa ?? new SpsaJson(), seed, rules, table, train, validation, start, tapered, baseDir, inputs, folder, report, log, ct);
                break;
            default:
                throw new InvalidDataException($"{full}: tuner must be regression or spsa, not '{spec.Tuner}'.");
        }

        var file = WeightFileFor(name, weights, tapered, rules.Geo.N, id);
        file.Save(Path.Combine(folder, "weights.json"));
        report["weights"] = JsonNode.Parse(file.ToJson());
        File.WriteAllText(Path.Combine(folder, "report.json"), report.ToJsonString(Indented) + "\n");
        var run = new JsonObject
        {
            ["format"] = "tuning-run/1",
            ["id"] = id,
            ["seed"] = seed,
            ["commit"] = BuildInfo.Commit,
            ["version"] = BuildInfo.Version,
            ["spec"] = JsonNode.Parse(specBytes),
            ["specSha256"] = Convert.ToHexStringLower(SHA256.HashData(specBytes)),
            ["inputs"] = inputs,
        };
        File.WriteAllText(Path.Combine(folder, "run.json"), run.ToJsonString(Indented) + "\n");
        return new TuningResult(file, report);
    }

    /// <summary>The names of a weight vector's entries: features, or early.* then late.*.</summary>
    public static IReadOnlyList<string> WeightNames(bool tapered)
    {
        var names = Enum.GetNames<FeatureId>();
        return tapered ? [.. names.Select(n => "early." + n), .. names.Select(n => "late." + n)] : names;
    }

    /// <summary>A weight file for a vector (with its board size: tuned weights are not assumed to transfer, 9.3).</summary>
    public static WeightFile WeightFileFor(string name, int[] weights, bool tapered, int n, string provenance)
    {
        ArgumentNullException.ThrowIfNull(weights);
        int count = FeatureExtractor.Count;
        return tapered
            ? new WeightFile
            {
                Name = name,
                Evaluator = WeightFile.Tapered,
                BoardSize = n,
                Early = WeightFile.FromVector(weights.AsSpan(0, count)),
                Late = WeightFile.FromVector(weights.AsSpan(count, count)),
                Provenance = provenance,
            }
            : new WeightFile { Name = name, Evaluator = WeightFile.Linear, BoardSize = n, Weights = WeightFile.FromVector(weights), Provenance = provenance };
    }

    private static int[] Regression(RegressionJson? json, IReadOnlyList<LabelledPosition> train, IReadOnlyList<LabelledPosition> validation,
        bool tapered, JsonObject report, Action<string>? log, CancellationToken ct)
    {
        var defaults = new RegressionOptions();
        var options = new RegressionOptions
        {
            K = json?.K ?? defaults.K,
            Lambda = json?.Lambda ?? defaults.Lambda,
            LearningRate = json?.LearningRate ?? defaults.LearningRate,
            MaxSteps = json?.MaxSteps ?? defaults.MaxSteps,
            Patience = json?.Patience ?? defaults.Patience,
        };
        log?.Invoke($"regression: {train.Count} train, {validation.Count} validation positions, {(tapered ? 22 : 11)} weights");
        var result = RegressionTuner.Fit(FeatureSet.FromPositions(train, tapered), FeatureSet.FromPositions(validation, tapered), options, ct);
        report["regression"] = new JsonObject
        {
            ["steps"] = result.Steps,
            ["bestStep"] = result.BestStep,
            ["intercept"] = result.Intercept,
            ["trainLoss"] = result.TrainLoss,
            ["validationLoss"] = result.ValidationLoss,
            ["trainAccuracy"] = result.TrainAccuracy,
            ["validationAccuracy"] = result.ValidationAccuracy,
            ["validationLossWithoutIntercept"] = result.ValidationLossWithoutIntercept,
            ["lambda"] = options.Lambda,
            ["learningRate"] = options.LearningRate,
        };
        log?.Invoke(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"regression: best step {result.BestStep} of {result.Steps}; validation loss {result.ValidationLoss:0.0000} ({result.ValidationLossWithoutIntercept:0.0000} without the intercept), accuracy {result.ValidationAccuracy * 100:0.00}%"));
        return result.Weights;
    }

    private static int[] RunSpsa(SpsaJson json, ulong seed, Rules rules, SolverTable table, IReadOnlyList<LabelledPosition> train,
        IReadOnlyList<LabelledPosition> validation, int[] start, bool tapered, string baseDir, JsonObject inputs, string folder,
        JsonObject report, Action<string>? log, CancellationToken ct)
    {
        var budget = json.Budget?.ToBudget() ?? new SearchBudget(null, 20_000, null, null); // D-19
        if (budget.SoftTime is not null || budget.HardTime is not null)
        {
            throw new InvalidDataException("SPSA budgets are nodes or depth only, so a run can be repeated exactly.");
        }

        int workers = json.Workers is > 0 ? json.Workers.Value : Math.Max(1, Environment.ProcessorCount - 1);
        ISpsaObjective objective = (json.Objective ?? "gradeAccuracy") switch
        {
            "gradeAccuracy" => new GradeAccuracyObjective(rules, table, train, validation, budget, json.Batch ?? 2000, workers, tapered),
            "headToHead" => new HeadToHeadObjective(rules, Openings(json.Openings, baseDir, rules.Config, inputs), validation, start, budget, json.Pairs ?? 100, workers, tapered),
            var other => throw new InvalidDataException($"objective must be gradeAccuracy or headToHead, not '{other}'."),
        };
        var names = WeightNames(tapered);
        double[]? scales = null;
        if (json.Scales is { } given)
        {
            scales = start.Select(w => Math.Max(1, Math.Abs(w) / 10.0)).ToArray();
            foreach (var (key, value) in given)
            {
                int index = names.ToList().IndexOf(key);
                scales[index >= 0 ? index : throw new InvalidDataException($"scales: unknown weight '{key}'.")] = value;
            }
        }

        var options = new SpsaOptions
        {
            Iterations = json.Iterations ?? 500,
            Gain = json.A,
            C = json.C ?? 2.0,
            Stability = json.Stability,
            TargetStep = json.TargetStep ?? 1.0,
            ValidateEvery = json.ValidateEvery ?? 50,
            Seed = seed,
            Scales = scales,
        };
        log?.Invoke($"spsa: {options.Iterations} iterations, objective {json.Objective ?? "gradeAccuracy"}, {Describe(budget)} per position, {workers} worker(s)");
        using var csv = new StreamWriter(Path.Combine(folder, "tuning.csv"));
        var result = Spsa.Run(start.Select(w => (double)w).ToArray(), objective, options, step =>
        {
            if (step.Validation is { } v)
            {
                log?.Invoke($"spsa: iteration {step.Iteration + 1}: J+ {step.Plus:0.0000} J- {step.Minus:0.0000} validation {v:0.00000}");
            }
        }, ct);
        csv.Write(result.ToCsv(names));
        foreach (string warning in result.Warnings)
        {
            log?.Invoke("warning: " + warning);
        }

        report["spsa"] = new JsonObject
        {
            ["iterations"] = options.Iterations,
            ["gain"] = result.Gain,
            ["c"] = options.C,
            ["startValidation"] = result.StartValidation,
            ["bestValidation"] = result.BestValidation,
            ["budget"] = Describe(budget),
            ["warnings"] = new JsonArray([.. result.Warnings.Select(w => JsonValue.Create(w))]),
        };
        return result.Best;
    }

    /// <summary>"maxNodes 1000", "maxDepth 4, maxNodes 20000".</summary>
    private static string Describe(SearchBudget budget)
    {
        var parts = new List<string>();
        if (budget.MaxDepth is int depth)
        {
            parts.Add($"maxDepth {depth}");
        }

        if (budget.MaxNodes is long nodes)
        {
            parts.Add($"maxNodes {nodes}");
        }

        return parts.Count == 0 ? "no limit" : string.Join(", ", parts);
    }

    private static int[] StartWeights(string? path, string baseDir, bool tapered, JsonObject inputs, int n)
    {
        WeightFile file = WeightFile.LinV0;
        if (path is not null)
        {
            string full = Resolve(baseDir, path);
            inputs["start"] = new JsonObject { ["file"] = full, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))) };
            file = WeightFile.Load(full);
        }

        if (file.BoardSize is int size && size != n)
        {
            throw new InvalidDataException($"Start weights '{file.Name}' are for {size}x{size}, not {n}x{n}.");
        }

        if (file.Evaluator == WeightFile.Tapered)
        {
            return tapered
                ? [.. WeightFile.ToVector(file.Early, file.Name), .. WeightFile.ToVector(file.Late, file.Name)]
                : throw new InvalidDataException("A tapered start cannot seed a linear run.");
        }

        int[] linear = WeightFile.ToVector(file.Weights, file.Name);
        return tapered ? [.. linear, .. linear] : linear;
    }

    private static IReadOnlyList<LabelledPosition> Openings(string? path, string baseDir, RuleSet ruleSet, JsonObject inputs)
    {
        string full = Resolve(baseDir, path ?? throw new InvalidDataException("headToHead needs openings."));
        inputs["openings"] = new JsonObject { ["file"] = full, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(full))) };
        return PositionFile.Load(full, ruleSet).Positions;
    }

    private static string Resolve(string baseDir, string path) => Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path);
}
