using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoxHounds.Core;

/// <summary>
/// Game record files, format fhg/1 (spec 14.2): UTF-8 JSON, camelCase, positions in notation and plies as e1-d2.
/// Reading replays every ply with <see cref="Rules.ApplyChecked"/> and verifies the stored result; any mismatch
/// is a load error. Unknown fields are ignored; an unknown major format version is rejected.
/// </summary>
public static class GameRecordFile
{
    /// <summary>Format tag of files written by this version.</summary>
    public const string Format = "fhg/1";

    /// <summary>File extension of game records.</summary>
    public const string Extension = ".fhg";

    /// <summary>The application version written into records: assembly version plus a short commit id.</summary>
    public static string AppVersion { get; } = ReadAppVersion();

    /// <summary>Writes a record.</summary>
    public static void Write(GameRecord record, string path)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrEmpty(path);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, ToJson(record));
    }

    /// <summary>The record as fhg/1 JSON text.</summary>
    public static string ToJson(GameRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var rules = new Rules(record.RuleSet);
        var geo = rules.Geo;
        var dto = new RecordJson
        {
            Format = Format,
            AppVersion = AppVersion,
            StartedUtc = record.StartedUtc,
            RuleSet = RuleSetJson.From(record.RuleSet),
            Start = rules.ToNotation(record.Start),
            Players = new PlayersJson { Fox = PlayerJson.From(record.Fox), Hounds = PlayerJson.From(record.Hounds) },
            Seed = record.Seed,
            Plies = record.Plies.Select(p => new PlyJson
            {
                Move = geo.FormatMove(p.Move),
                Info = p.Info is null ? null : new InfoJson
                {
                    ScoreFox = p.Info.ScoreFox,
                    Depth = p.Info.DepthReached,
                    Nodes = p.Info.Nodes,
                    Ms = (long)p.Info.Elapsed.TotalMilliseconds,
                    Pv = p.Info.PrincipalVariation.Select(geo.FormatMove).ToList(),
                    Source = p.Info.Source,
                    Exact = p.Info.Exact ? true : null,
                },
            }).ToList(),
            Result = JsonNames.Of(record.Outcome.Result),
            Reason = JsonNames.Of(record.Outcome.Reason),
            Forfeit = record.Forfeit is null ? null : new ForfeitJson
            {
                Side = JsonNames.Of(record.Forfeit.Side),
                Cause = JsonNames.Of(record.Forfeit.Cause),
                Message = record.Forfeit.Message,
            },
        };
        return JsonSerializer.Serialize(dto, CoreJsonContext.Default.RecordJson) + "\n";
    }

    /// <summary>Reads and verifies a record file.</summary>
    /// <exception cref="InvalidDataException">The file is malformed, has an illegal ply, or its stored result does not
    /// match the replayed game.</exception>
    public static GameRecord Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>Parses and verifies fhg/1 JSON text.</summary>
    /// <exception cref="InvalidDataException">See <see cref="Read"/>.</exception>
    public static GameRecord Parse(string json, string source = "game record")
    {
        RecordJson? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, CoreJsonContext.Default.RecordJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{source}: {ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new InvalidDataException($"{source}: empty record.");
        }

        if (dto.Format is null || !dto.Format.StartsWith("fhg/", StringComparison.Ordinal) || dto.Format.Split('/')[1].Split('.')[0] != "1")
        {
            throw new InvalidDataException($"{source}: unsupported format '{dto.Format}' (expected {Format}).");
        }

        try
        {
            return Build(dto, source);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IllegalMoveException or InvalidPositionException)
        {
            throw new InvalidDataException($"{source}: {ex.Message}", ex);
        }
    }

    private static GameRecord Build(RecordJson dto, string source)
    {
        var ruleSet = (dto.RuleSet ?? throw new InvalidDataException($"{source}: missing ruleSet.")).ToRuleSet();
        var rules = new Rules(ruleSet);
        var geo = rules.Geo;
        var start = rules.FromNotation(dto.Start ?? throw new InvalidDataException($"{source}: missing start."));
        var plies = new List<PlyRecord>();
        GameState s = start;
        foreach (var ply in dto.Plies ?? [])
        {
            if (ply.Move is null || !geo.TryParseMove(ply.Move, out var move))
            {
                throw new InvalidDataException($"{source}: ply {plies.Count + 1} '{ply.Move}' is not a move.");
            }

            s = rules.ApplyChecked(s, move);
            plies.Add(new PlyRecord(move, ply.Info is null ? null : ToInfo(ply.Info, geo, source)));
        }

        var outcome = new Outcome(JsonNames.Parse<GameResult>(dto.Result, "result"), JsonNames.Parse<EndReason>(dto.Reason, "reason"));
        ForfeitInfo? forfeit = null;
        if (dto.Forfeit is not null)
        {
            forfeit = new ForfeitInfo(
                JsonNames.Parse<Side>(dto.Forfeit.Side, "forfeit side"),
                JsonNames.Parse<ForfeitCause>(dto.Forfeit.Cause, "forfeit cause"),
                dto.Forfeit.Message ?? "");
        }

        Verify(rules, s, outcome, forfeit, source);
        return new GameRecord
        {
            RuleSet = ruleSet,
            Start = start,
            Fox = dto.Players?.Fox?.ToPlayer() ?? PlayerInfo.Human,
            Hounds = dto.Players?.Hounds?.ToPlayer() ?? PlayerInfo.Human,
            Seed = dto.Seed,
            StartedUtc = dto.StartedUtc ?? default,
            Plies = plies,
            Outcome = outcome,
            Forfeit = forfeit,
        };
    }

    private static void Verify(Rules rules, in GameState final, Outcome stored, ForfeitInfo? forfeit, string source)
    {
        var actual = rules.Evaluate(final);
        if (stored.Reason == EndReason.Forfeit)
        {
            // A forfeit ends a game the rules consider unfinished; the forfeiting side loses.
            if (actual.IsOver || forfeit is null || stored.Result != Outcome.Win(forfeit.Side.Opponent(), EndReason.Forfeit).Result)
            {
                throw new InvalidDataException($"{source}: the stored forfeit does not match the game.");
            }

            return;
        }

        if (forfeit is not null || actual != stored)
        {
            throw new InvalidDataException(
                $"{source}: stored result {stored.Result} ({stored.Reason}) does not match the replayed game, which gives {actual.Result} ({actual.Reason}).");
        }
    }

    private static AgentInfo ToInfo(InfoJson info, Geometry geo, string source)
    {
        var pv = new List<Move>();
        foreach (string text in info.Pv ?? [])
        {
            if (!geo.TryParseMove(text, out var m))
            {
                throw new InvalidDataException($"{source}: '{text}' in a principal variation is not a move.");
            }

            pv.Add(m);
        }

        return new AgentInfo(info.ScoreFox, info.Depth, info.Nodes, TimeSpan.FromMilliseconds(info.Ms ?? 0), pv,
            info.Source ?? AgentInfo.SourceSearch, info.Exact ?? false);
    }

    private static string ReadAppVersion()
    {
        string? version = typeof(GameRecordFile).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (version is null)
        {
            return "0.0.0";
        }

        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
    }
}

/// <summary>camelCase names of enum values in files (spec 14).</summary>
public static class JsonNames
{
    /// <summary>The camelCase name of an enum value, e.g. reachHomeRow.</summary>
    public static string Of<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        string name = value.ToString();
        return string.Concat(char.ToLowerInvariant(name[0]).ToString(), name[1..]);
    }

    /// <summary>Parses a camelCase enum name; the PascalCase form is accepted too.</summary>
    /// <exception cref="FormatException">The text names no value of <typeparamref name="TEnum"/>.</exception>
    public static TEnum Parse<TEnum>(string? text, string what)
        where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            string name = value.ToString();
            if (string.Equals(text, Of(value), StringComparison.Ordinal) || string.Equals(text, name, StringComparison.Ordinal))
            {
                return value;
            }
        }

        throw new FormatException($"'{text}' is not a valid {what}.");
    }
}

/// <summary>JSON shape of a ruleset (spec 14.2), shared by records and experiment specs.</summary>
public sealed class RuleSetJson
{
    /// <summary>n.</summary>
    public int BoardSize { get; set; } = 8;

    /// <summary>fixed or chooseOnBackRow.</summary>
    public string? FoxStart { get; set; }

    /// <summary>Optional fixed fox column.</summary>
    public int? FixedFoxColumn { get; set; }

    /// <summary>fox or hounds.</summary>
    public string? FirstMover { get; set; }

    /// <summary>reachHomeRow or houndsImmobile.</summary>
    public string? FoxWinCondition { get; set; }

    /// <summary>Escape adjudication.</summary>
    public bool? AdjudicateEscape { get; set; }

    /// <summary>The JSON shape of a ruleset.</summary>
    public static RuleSetJson From(RuleSet ruleSet)
    {
        ArgumentNullException.ThrowIfNull(ruleSet);
        return new RuleSetJson
        {
            BoardSize = ruleSet.BoardSize,
            FoxStart = JsonNames.Of(ruleSet.FoxStart),
            FixedFoxColumn = ruleSet.FixedFoxColumn,
            FirstMover = JsonNames.Of(ruleSet.FirstMover),
            FoxWinCondition = JsonNames.Of(ruleSet.FoxWinCondition),
            AdjudicateEscape = ruleSet.AdjudicateEscape,
        };
    }

    /// <summary>The ruleset; missing fields take their defaults (spec 3.6).</summary>
    /// <exception cref="FormatException">A field has an invalid value.</exception>
    public RuleSet ToRuleSet() => new()
    {
        BoardSize = BoardSize,
        FoxStart = FoxStart is null ? Core.FoxStart.Fixed : JsonNames.Parse<FoxStart>(FoxStart, "foxStart"),
        FixedFoxColumn = FixedFoxColumn,
        FirstMover = FirstMover is null ? Side.Fox : JsonNames.Parse<Side>(FirstMover, "firstMover"),
        FoxWinCondition = FoxWinCondition is null ? Core.FoxWinCondition.ReachHomeRow : JsonNames.Parse<FoxWinCondition>(FoxWinCondition, "foxWinCondition"),
        AdjudicateEscape = AdjudicateEscape ?? true,
    };
}

#pragma warning disable CA1812 // Instantiated by the source-generated serializer.
internal sealed class RecordJson
{
    public string? Format { get; set; }

    public string? AppVersion { get; set; }

    public DateTimeOffset? StartedUtc { get; set; }

    public RuleSetJson? RuleSet { get; set; }

    public string? Start { get; set; }

    public PlayersJson? Players { get; set; }

    public ulong? Seed { get; set; }

    public List<PlyJson>? Plies { get; set; }

    public string? Result { get; set; }

    public string? Reason { get; set; }

    public ForfeitJson? Forfeit { get; set; }
}

internal sealed class PlayersJson
{
    public PlayerJson? Fox { get; set; }

    public PlayerJson? Hounds { get; set; }
}

internal sealed class PlayerJson
{
    public string? Kind { get; set; }

    public string? Name { get; set; }

    public JsonElement? Spec { get; set; }

    public static PlayerJson From(PlayerInfo player) => new()
    {
        Kind = player.Kind,
        Name = player.Name,
        Spec = player.SpecJson is null ? null : JsonDocument.Parse(player.SpecJson).RootElement.Clone(),
    };

    public PlayerInfo ToPlayer() =>
        new(Kind ?? PlayerInfo.HumanKind, Name, Spec is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } spec ? spec.GetRawText() : null);
}

internal sealed class PlyJson
{
    public string? Move { get; set; }

    public InfoJson? Info { get; set; }
}

internal sealed class InfoJson
{
    public int? ScoreFox { get; set; }

    public int Depth { get; set; }

    public long Nodes { get; set; }

    public long? Ms { get; set; }

    public List<string>? Pv { get; set; }

    public string? Source { get; set; }

    /// <summary>Written only when true (decision D-41); older readers ignore it (spec 14).</summary>
    public bool? Exact { get; set; }
}

internal sealed class ForfeitJson
{
    public string? Side { get; set; }

    public string? Cause { get; set; }

    public string? Message { get; set; }
}
#pragma warning restore CA1812

/// <summary>Source-generated JSON metadata for Core's files (spec 4.1: no reflection).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    AllowDuplicateProperties = false)]
[JsonSerializable(typeof(RecordJson))]
[JsonSerializable(typeof(RuleSetJson))]
internal sealed partial class CoreJsonContext : JsonSerializerContext
{
}
