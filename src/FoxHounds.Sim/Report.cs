using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FoxHounds.Sim;

/// <summary>Text views of summary.json for <c>fh report</c> (spec 11.4).</summary>
public static class Report
{
    /// <summary>A run's summary as a table.</summary>
    public static string Format(JsonObject summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        summary = Normalize(summary);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{Str(summary, "id")}  mode {Str(summary, "mode")}  {Num(summary["done"])}/{Num(summary["planned"])} done{(summary["complete"]?.GetValue<bool>() == true ? "" : " (incomplete)")}\n");
        if (summary["agents"] is JsonObject agents)
        {
            foreach (var (role, node) in agents)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{role} ({Str(node, "name")})\n");
                foreach (string side in new[] { "fox", "hounds" })
                {
                    if (node?[side] is not JsonObject s)
                    {
                        continue;
                    }

                    sb.Append(CultureInfo.InvariantCulture, $"  as {side,-6} games {Num(s["games"])}  wins {Prop(s["wins"])}");
                    if (s["conversion"] is JsonObject conversion)
                    {
                        sb.Append("  conversion ").Append(Prop(conversion));
                    }

                    if (s["blunders"] is JsonObject blunders)
                    {
                        sb.Append("  blunders ").Append(Prop(blunders));
                    }

                    sb.Append(CultureInfo.InvariantCulture, $"  nodes/move {Num(s["nodesPerMove"])}");
                    if (s["forfeits"]?.GetValue<long>() is > 0 and var forfeits)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"  forfeits {forfeits}");
                    }

                    sb.Append('\n');
                }
            }
        }

        if (summary["headToHead"] is JsonArray matches)
        {
            foreach (var m in matches.OfType<JsonObject>())
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"{Str(m, "a")} vs {Str(m, "b")}: {Num(m["pairs"])} pairs  score {Num(m["score"])} [{Num(m["scoreLow"])}, {Num(m["scoreHigh"])}]  Elo {Num(m["elo"])} [{Num(m["eloLow"])}, {Num(m["eloHigh"])}]");
                if (m["sprt"] is JsonObject sprt)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  SPRT {Str(sprt, "decision")} (LLR {Num(sprt["llr"])}, bounds {Num(sprt["lower"])} / {Num(sprt["upper"])})");
                }

                sb.Append('\n');
            }
        }

        if (summary["grade"] is JsonObject grade)
        {
            foreach (var (side, node) in grade)
            {
                if (node is JsonObject g)
                {
                    sb.Append(CultureInfo.InvariantCulture,
                        $"grade {side,-6} positions {Num(g["positions"])}  accuracy {Prop(g["accuracy"])}  resistance {Prop(g["resistance"])}  nodes/position {Num(g["nodesPerPosition"])}\n");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>Two runs metric by metric: every proportion and number both summaries have, with the difference.</summary>
    public static string Compare(JsonObject a, JsonObject b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        a = Normalize(a);
        b = Normalize(b);
        var left = Flatten(a);
        var right = Flatten(b);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"a: {Str(a, "id")} ({Str(a, "mode")}, {Num(a["done"])} done)\nb: {Str(b, "id")} ({Str(b, "mode")}, {Num(b["done"])} done)\n");
        int width = left.Keys.Concat(right.Keys).Select(k => k.Length).DefaultIfEmpty(10).Max();
        foreach (string key in left.Keys.Union(right.Keys).Order(StringComparer.Ordinal))
        {
            left.TryGetValue(key, out var x);
            right.TryGetValue(key, out var y);
            string delta = x.Value is double u && y.Value is double v ? (v - u).ToString("+0.####;-0.####;0", CultureInfo.InvariantCulture) : "";
            sb.Append(key.PadRight(width)).Append("  ").Append((x.Text ?? "-").PadRight(34)).Append("  ").Append((y.Text ?? "-").PadRight(34)).Append("  ").Append(delta).Append('\n');
        }

        return sb.ToString();
    }

    private static Dictionary<string, (string? Text, double? Value)> Flatten(JsonObject summary)
    {
        var result = new Dictionary<string, (string?, double?)>(StringComparer.Ordinal);
        void Walk(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject o when o.ContainsKey("successes") && o.ContainsKey("trials"):
                    result[path] = (Prop(o), o["rate"]?.GetValue<double>());
                    break;
                case JsonObject o:
                    foreach (var (key, child) in o)
                    {
                        if (key is not ("sha256" or "id" or "format" or "name"))
                        {
                            Walk(child, path.Length == 0 ? key : $"{path}.{key}");
                        }
                    }

                    break;
                case JsonArray array:
                    for (int i = 0; i < array.Count; i++)
                    {
                        var item = array[i] as JsonObject;
                        string label = item is not null && item["b"] is JsonNode b ? $"{item["a"]}-vs-{b}" : i.ToString(CultureInfo.InvariantCulture);
                        Walk(array[i], $"{path}[{label}]");
                    }

                    break;
                case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                    double d = value.GetValue<double>();
                    result[path] = (d.ToString("0.####", CultureInfo.InvariantCulture), d);
                    break;
                case JsonValue value:
                    result[path] = (value.ToString(), null);
                    break;
            }
        }

        Walk(summary, "");
        return result;
    }

    /// <summary>Through text, so every number reads as a double, as it does in a summary loaded from a file.</summary>
    private static JsonObject Normalize(JsonObject summary) => JsonNode.Parse(summary.ToJsonString())!.AsObject();

    private static string Str(JsonNode? node, string key) => node?[key]?.ToString() ?? "?";

    private static string Num(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        ? v.GetValue<double>().ToString(Math.Abs(v.GetValue<double>()) >= 1000 ? "#,0" : "0.####", CultureInfo.InvariantCulture)
        : node?.ToString() ?? "?";

    private static string Prop(JsonNode? node) => node is JsonObject p
        ? string.Create(CultureInfo.InvariantCulture,
            $"{p["rate"]!.GetValue<double>() * 100:0.0#}% [{p["low"]!.GetValue<double>() * 100:0.0#}, {p["high"]!.GetValue<double>() * 100:0.0#}] ({p["successes"]}/{p["trials"]})")
        : "-";
}
