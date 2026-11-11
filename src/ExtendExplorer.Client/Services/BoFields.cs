using System.Net;
using System.Text.Json;
using ExtendExplorer.Shared;

namespace ExtendExplorer.Client.Services;

// Authoritative writable-field metadata for a custom BO — the C# port of
// lib/bo-fields.ts. Fetched by data-source WID from the (fully paged) WQL
// fields endpoint, with system + self-referencing fields filtered out here.
// Returns null on any failure so callers fall back to inferring fields from
// the result columns / record shape.
public sealed class BoFields
{
    private readonly ApiClient _api;

    public BoFields(ApiClient api) => _api = api;

    // Workday-generated system fields, and a field *type* (a BO's own self-
    // reference) that's never editable — excluded from the writable set.
    private static readonly HashSet<string> SystemFields = new(StringComparer.Ordinal)
    {
        "workdayID", "classOfInstance1", "referenceID1", "createdMoment",
        "lastFunctionallyUpdated", "currentOMSVersion", "standardWorkdayID",
    };
    private static readonly HashSet<string> SystemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "self referencing instance",
    };

    private static (string Kind, string Type) KindAndType(string? t)
    {
        var s = (t ?? "").ToLowerInvariant();
        if (s.Contains("boolean")) return ("scalar", "boolean");
        if (s.Contains("numeric") || s.Contains("decimal") || s.Contains("integer")) return ("scalar", "number");
        if (s.Contains("instance")) return ("reference", "string");
        return ("scalar", "string");
    }

    // Fetch writable fields by data-source WID (known from the tree's paged
    // load), filtering system + self-referencing fields client-side. The C# port
    // of fetchBoFieldsById. null on failure so callers fall back.
    public async Task<List<BoFieldDto>?> FetchByIdAsync(string dataSourceId)
    {
        try
        {
            var (status, body) = await _api.GetRawAsync(
                $"/api/workday/datasources/{Uri.EscapeDataString(dataSourceId)}/fields");
            if (status == (int)HttpStatusCode.Unauthorized)
            {
                _api.RedirectToLogin();
                return null;
            }
            if (status < 200 || status >= 300) return null;
            var parsed = JsonSerializer.Deserialize<FieldsResponse>(body, ApiClient.JsonOpts);
            var rows = parsed?.Fields ?? new();
            var result = new List<BoFieldDto>();
            foreach (var f in rows)
            {
                if (string.IsNullOrEmpty(f.Alias) || SystemFields.Contains(f.Alias)) continue;
                if (SystemTypes.Contains(f.Type)) continue;
                var (kind, type) = KindAndType(f.Type);
                result.Add(new BoFieldDto { Key = f.Alias, Label = f.Descriptor, Kind = kind, Type = type });
            }
            return result;
        }
        catch
        {
            return null;
        }
    }
}
