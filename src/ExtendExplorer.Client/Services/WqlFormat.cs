using System.Text;
using System.Text.Json;

namespace ExtendExplorer.Client.Services;

// Pure helpers for turning Workday WQL result values into display strings and
// CSV — the C# port of lib/wql-format.ts. WQL values arrive as JsonElement
// (string | number | bool | array | reference object | nested object), so the
// classification/rendering here keys off JsonElement.ValueKind. Dependency-free
// and side-effect-free, like the original.

public enum CellKind
{
    Empty,
    Reference,
    Multi,
    Scalar,
}

public static class WqlFormat
{
    // A "Single Instance" reference comes back as an object carrying a
    // descriptor (+ id). That's the thing rendered as a chip and drilled into.
    public static bool IsReference(JsonElement v) =>
        v.ValueKind == JsonValueKind.Object && v.TryGetProperty("descriptor", out _);

    public static string RefDescriptor(JsonElement v) =>
        v.ValueKind == JsonValueKind.Object
        && v.TryGetProperty("descriptor", out var d)
        && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? ""
            : "";

    public static string? RefId(JsonElement v) =>
        v.ValueKind == JsonValueKind.Object
        && v.TryGetProperty("id", out var id)
        && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    // Flat, human-readable string for any WQL value.
    public static string DisplayValue(JsonElement? ov)
    {
        if (ov is null) return "";
        var v = ov.Value;
        switch (v.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return "";
            case JsonValueKind.String:
                return v.GetString() ?? "";
            case JsonValueKind.Number:
                return v.GetRawText();
            case JsonValueKind.True:
                return "true";
            case JsonValueKind.False:
                return "false";
            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (var item in v.EnumerateArray())
                    parts.Add(DisplayValue(item));
                return parts.Count == 0 ? "" : string.Join("; ", parts);
            case JsonValueKind.Object:
                return IsReference(v) ? RefDescriptor(v) : v.GetRawText();
            default:
                return "";
        }
    }

    // Classify a cell so the grid can render it appropriately.
    public static CellKind Kind(JsonElement? ov)
    {
        if (ov is null) return CellKind.Empty;
        var v = ov.Value;
        if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return CellKind.Empty;
        if (v.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(v.GetString()))
            return CellKind.Empty;
        if (v.ValueKind == JsonValueKind.Array) return CellKind.Multi;
        if (IsReference(v)) return CellKind.Reference;
        return CellKind.Scalar;
    }

    private static string CsvEscape(string s)
    {
        if (s.IndexOfAny(new[] { '"', ',', '\n', '\r' }) >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    private static string CsvCell(JsonElement? v) => CsvEscape(DisplayValue(v));

    public static string ToCsv(
        IReadOnlyList<string> columns,
        IReadOnlyList<Dictionary<string, JsonElement>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", columns.Select(CsvEscape)));
        foreach (var r in rows)
        {
            sb.Append("\r\n");
            sb.Append(string.Join(",", columns.Select(c =>
                CsvCell(r.TryGetValue(c, out var el) ? el : (JsonElement?)null))));
        }
        return sb.ToString();
    }

    // ── FROM-clause prompt formatting ───────────────────────────
    // WQL prompt values are typed: dates and text are quoted; instances (WIDs),
    // booleans, and numbers are bare. Mirrors lib/wql-format.ts.
    public static string FormatPromptValue(string type, string value)
    {
        var t = (type ?? "").ToLowerInvariant();
        var v = value ?? "";
        if (t.Contains("instance"))
            return System.Text.RegularExpressions.Regex.Replace(v, @"[()\s]", "");
        if (t == "boolean") return v == "true" ? "true" : "false";
        if (t is "numeric" or "currency") return v;
        return "\"" + v.Replace("\"", "\\\"") + "\"";
    }

    // Build a FROM clause with an optional data source filter and prompt values:
    //   sourceAlias(dataSourceFilter = filterAlias, prompt1 = v1, ...)
    // Empty prompt values are skipped.
    public static string BuildFromClause(
        string sourceAlias,
        string? filterAlias,
        IEnumerable<FilledPrompt> prompts)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(filterAlias))
            parts.Add($"dataSourceFilter = {filterAlias}");
        foreach (var p in prompts)
        {
            if (string.IsNullOrEmpty(p.Value)) continue;
            parts.Add($"{p.Alias} = {FormatPromptValue(p.Type, p.Value)}");
        }
        return parts.Count > 0 ? $"{sourceAlias}({string.Join(", ", parts)})" : sourceAlias;
    }
}

public sealed class FilledPrompt
{
    public string Alias { get; set; } = "";
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";
}
