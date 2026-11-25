using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExtendExplorer.Shared;

namespace ExtendExplorer.Client.Services;

// Client-side metadata layer + WQL execution — the C# port of
// lib/wql-metadata.ts. Data sources and their fields are near-static and
// security-scoped to the logged-in user, so they're cached for the session
// (this scoped service lives for the app's lifetime in WASM) rather than
// re-fetched on every tree expand or keystroke.
public sealed class WqlMetadata
{
    private readonly HttpClient _http;
    private readonly ApiClient _api;
    private readonly Interop _interop;

    private List<DataSourceDto>? _dataSourceCache;
    private readonly Dictionary<string, List<FieldDto>> _fieldCache = new();
    private readonly Dictionary<string, List<FilterDto>> _filterCache = new();
    private readonly Dictionary<string, Dictionary<string, string>> _typeCache = new();

    public WqlMetadata(HttpClient http, ApiClient api, Interop interop)
    {
        _http = http;
        _api = api;
        _interop = interop;
    }

    // Rows per browser→server step when loading the data-source list or a
    // source's fields with progress; the bar updates after each step. The
    // server fetches a step from Workday as 100-row pages, 5 at a time. The
    // first step learns the total (one page on its own, then the other four);
    // every later step passes that total back so all five pages go at once —
    // one round per 500 rows. 2,000 fields: 5 rounds, 4 progress updates.
    private const int LoadStep = 500;

    // onProgress(loaded, total) is called after each step; total is Workday's
    // count once the first step has reported it.
    public async Task<List<DataSourceDto>> FetchDataSourcesAsync(
        bool force = false, Action<int, int?>? onProgress = null)
    {
        if (_dataSourceCache is not null && !force) return _dataSourceCache;

        var all = new List<DataSourceDto>();
        int? total = null;
        var offset = 0;
        while (true)
        {
            var url = $"/api/workday/datasources?offset={offset}&limit={LoadStep}";
            if (total is int known) url += $"&total={known}";
            var json = await _api.GetJsonAsync<DataSourcesResponse>(url);
            var got = json?.DataSources ?? new();
            total ??= json?.Total;
            all.AddRange(got);
            offset += LoadStep;
            onProgress?.Invoke(all.Count, total);
            if (got.Count == 0 || total is null || offset >= total.Value) break;
        }
        all.Sort((a, b) => string.CompareOrdinal(a.Alias, b.Alias));
        _dataSourceCache = all;
        // Share with the editor's completion provider so autocomplete doesn't
        // have to re-fetch this (slow, on a large tenant) list itself.
        await _interop.PrimeDataSourcesAsync(_dataSourceCache);
        return _dataSourceCache;
    }

    public async Task<List<FieldDto>> FetchFieldsAsync(string dataSourceId, bool force = false)
    {
        if (!force && _fieldCache.TryGetValue(dataSourceId, out var cached)) return cached;
        var json = await _api.GetJsonAsync<FieldsResponse>(
            $"/api/workday/datasources/{Uri.EscapeDataString(dataSourceId)}/fields");
        var fields = json?.Fields ?? new();
        _fieldCache[dataSourceId] = fields;
        // Share with the editor's completion provider (keyed by data-source id).
        await _interop.PrimeFieldsAsync(dataSourceId, fields);
        return fields;
    }

    // Fetch a window of a source's fields (offset/limit). The tree uses this to
    // load a first batch fast for very wide BOs, then the rest on demand. Not
    // cached — the tree accumulates and calls PrimeFieldsAsync once the full set
    // is in. Returns the window plus the source's full field count.
    // Fetch a window of a source's fields in LoadStep-sized steps, reporting
    // progress after each. `limit` null means "to the end".
    public async Task<(List<FieldDto> Fields, int Total)> FetchFieldsWithProgressAsync(
        string dataSourceId, int offset, int? limit, Action<int, int?>? onProgress = null)
    {
        var fields = new List<FieldDto>();
        int? total = null;
        var pos = offset;
        while (true)
        {
            var want = limit is int lim ? Math.Min(LoadStep, offset + lim - pos) : LoadStep;
            if (want <= 0) break;
            var (got, t) = await FetchFieldsPageAsync(dataSourceId, pos, want, total);
            total ??= t;
            fields.AddRange(got);
            pos += want;
            onProgress?.Invoke(fields.Count, limit is int l2 ? Math.Min(l2, Math.Max(0, t - offset)) : Math.Max(0, t - offset));
            if (got.Count == 0 || pos >= t) break;
        }
        return (fields, total ?? fields.Count);
    }

    // knownTotal: the source's field count, if an earlier call reported it —
    // lets the server fetch every page of the window at once.
    public async Task<(List<FieldDto> Fields, int Total)> FetchFieldsPageAsync(
        string dataSourceId, int offset, int? limit, int? knownTotal = null)
    {
        var url = $"/api/workday/datasources/{Uri.EscapeDataString(dataSourceId)}/fields?offset={offset}";
        if (limit is int lim) url += $"&limit={lim}";
        if (knownTotal is int kt && limit is not null) url += $"&total={kt}";
        var json = await _api.GetJsonAsync<FieldsResponse>(url);
        var fields = json?.Fields ?? new();
        return (fields, json?.Total ?? fields.Count);
    }

    // Seed the field cache with a fully-loaded set and share it with the editor's
    // completion provider (the C# port of primeFieldCache).
    public async Task PrimeFieldsAsync(string dataSourceId, List<FieldDto> fields)
    {
        _fieldCache[dataSourceId] = fields;
        await _interop.PrimeFieldsAsync(dataSourceId, fields);
    }

    // alias → WQL type ("Boolean", "Numeric", "Date", "Single Instance", …) for a
    // data source, so the results grid can offer the right column-menu filters
    // even when a query returns no rows to infer from. Uses whatever the schema
    // tree already loaded; otherwise fetches a window of fields once and caches
    // the map.
    public async Task<Dictionary<string, string>> FetchFieldTypesAsync(string dataSourceId)
    {
        if (_typeCache.TryGetValue(dataSourceId, out var cachedMap)) return cachedMap;
        if (_fieldCache.TryGetValue(dataSourceId, out var cachedFields))
            return _typeCache[dataSourceId] = BuildTypeMap(cachedFields);

        var (fields, _) = await FetchFieldsPageAsync(dataSourceId, 0, 2000);
        return _typeCache[dataSourceId] = BuildTypeMap(fields);
    }

    private static Dictionary<string, string> BuildTypeMap(List<FieldDto> fields)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fields)
            if (!string.IsNullOrEmpty(f.Alias)) map[f.Alias] = f.Type ?? "";
        return map;
    }

    // Run a query and page through the WHOLE result set, handing each page to
    // `onPage` (in row order) as it arrives instead of collecting the rows — so
    // an export of any size never has to fit in memory. Used by the Export
    // dialog, where the toolbar's Limit shouldn't decide how much data comes out.
    //
    // The query is paged exactly as written. Each page is a separate run of it,
    // so the result reports what a caller needs to judge whether the export is
    // complete: Workday's total, the rows actually fetched, and how many
    // workdayIDs came back more than once (a sign rows shifted between pages).
    //
    // The first page is fetched on its own: it warms WQL's per-session result
    // cache and reports `total`, which tells us how many more pages to ask for.
    // Paging stops on a short page as well, so an unknown total still terminates.
    // Cancellation is checked before each batch of pages is requested.
    public async Task<StreamRowsResult> StreamAllRowsAsync(
        string wql,
        Func<WqlResult, Task> onPage,
        Action<int, long?>? onProgress = null,
        CancellationToken cancel = default)
    {
        const int pageSize = 100;   // WCP API Gateway caps `limit` at 100
        const int concurrency = 5;
        // Duplicate tracking keeps one entry per row; past this many it stops
        // (and the result says the ids weren't fully checked) rather than
        // letting the set grow without bound.
        const int maxTrackedIds = 2_000_000;

        var first = await RunWqlAsync(wql, pageSize, 0);

        var total = first.Total;
        var count = 0;
        var idCount = 0;
        var duplicateIds = 0;
        var idOverflow = false;
        // Workday ids are 32 hex digits, so they're tracked compactly as Guids;
        // anything else falls back to the string itself.
        var guidIds = new HashSet<Guid>();
        var otherIds = new HashSet<string>(StringComparer.Ordinal);

        void TrackIds(WqlResult page)
        {
            foreach (var row in page.Rows)
            {
                var key = row.Keys.FirstOrDefault(k => k.Equals("workdayID", StringComparison.OrdinalIgnoreCase));
                if (key is null) continue;
                var v = row[key];
                var id = v.ValueKind == JsonValueKind.String ? v.GetString() : WqlFormat.RefId(v);
                if (string.IsNullOrEmpty(id)) continue;
                idCount++;
                if (guidIds.Count + otherIds.Count >= maxTrackedIds)
                {
                    idOverflow = true;
                    continue;
                }
                var isNew = Guid.TryParseExact(id, "N", out var g) ? guidIds.Add(g) : otherIds.Add(id);
                if (!isNew) duplicateIds++;
            }
        }

        async Task TakeAsync(WqlResult page)
        {
            TrackIds(page);
            count += page.Rows.Count;
            await onPage(page);
            onProgress?.Invoke(count, total);
        }

        await TakeAsync(first);

        if (first.Rows.Count >= pageSize)
        {
            var offset = pageSize;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();

                var offsets = new List<int>();
                while (offsets.Count < concurrency && (!total.HasValue || offset < total.Value))
                {
                    offsets.Add(offset);
                    offset += pageSize;
                }
                if (offsets.Count == 0) break;

                var results = await Task.WhenAll(offsets.Select(o => RunWqlAsync(wql, pageSize, o)));
                var shortPage = false;
                foreach (var r in results) // Task.WhenAll keeps input order, so rows stay in offset order
                {
                    await TakeAsync(r);
                    if (r.Rows.Count < pageSize) shortPage = true;
                }
                if (shortPage) break;
            }
        }

        return new StreamRowsResult
        {
            Total = total,
            Count = count,
            IdsChecked = idCount > 0 && !idOverflow,
            DuplicateIds = duplicateIds,
        };
    }

    // Page through a query collecting just the id column, bailing out once past
    // the cap so a huge match is detected in ~cap rows, not by pulling them all.
    // Used by Bulk edit to gather the WIDs it will PATCH.
    public async Task<(List<string> Ids, bool Overflow)> CollectIdsAsync(string wql, string keyColumn, int cap)
    {
        var ids = new List<string>();
        const int page = 100;   // WCP API Gateway caps `limit` at 100
        var off = 0;
        while (true)
        {
            var res = await RunWqlAsync(wql, page, off);
            foreach (var r in res.Rows)
            {
                var id = "";
                if (r.TryGetValue(keyColumn, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Object)
                        id = WqlFormat.RefId(v) ?? "";
                    else if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        id = "";
                    else
                        id = v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.GetRawText();
                }
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
                if (ids.Count > cap) return (ids.Take(cap).ToList(), true);
            }
            if (res.Rows.Count < page) break;
            off += page;
        }
        return (ids, false);
    }

    // A data source's filters + prompts. Cached per source — an empty list means
    // "we checked, this source has no filters".
    public async Task<List<FilterDto>> FetchFiltersAsync(string dataSourceId, bool force = false)
    {
        if (!force && _filterCache.TryGetValue(dataSourceId, out var cached)) return cached;
        var json = await _api.GetJsonAsync<FiltersResponse>(
            $"/api/workday/datasources/{Uri.EscapeDataString(dataSourceId)}/filters");
        var filters = json?.Filters ?? new();
        _filterCache[dataSourceId] = filters;
        return filters;
    }

    // Parse the SELECT list into the column names WQL will key results by, so
    // the grid can show requested columns even when they come back empty (WQL
    // omits value-less fields from the row objects entirely). Respects commas
    // inside ()/{} and honours `AS` aliases.
    private static List<string> ParseSelectColumns(string wql)
    {
        var m = Regex.Match(wql, @"\bselect\b([\s\S]*?)\bfrom\b", RegexOptions.IgnoreCase);
        if (!m.Success) return new();
        var body = m.Groups[1].Value;
        var parts = new List<string>();
        var depth = 0;
        var cur = new System.Text.StringBuilder();
        foreach (var ch in body)
        {
            if (ch is '(' or '{') depth++;
            else if (ch is ')' or '}') depth = Math.Max(0, depth - 1);
            if (ch == ',' && depth == 0)
            {
                parts.Add(cur.ToString());
                cur.Clear();
            }
            else
            {
                cur.Append(ch);
            }
        }
        if (cur.ToString().Trim().Length > 0) parts.Add(cur.ToString());

        var cols = new List<string>();
        foreach (var raw in parts)
        {
            var p = raw.Trim();
            if (p.Length == 0) continue;
            var asM = Regex.Match(p, @"\bas\s+([a-zA-Z_]\w*)\s*$", RegexOptions.IgnoreCase);
            if (asM.Success)
            {
                cols.Add(asM.Groups[1].Value);
                continue;
            }
            var baseName = p.Split('(', '{')[0].Trim();
            cols.Add(Regex.IsMatch(baseName, @"^[a-zA-Z_][\w.]*$") ? baseName : p);
        }
        return cols;
    }

    // Run a WQL query through the backend proxy. Returns normalized columns +
    // rows, or throws with the tenant's error message so the UI can show it.
    public async Task<WqlResult> RunWqlAsync(string wql, int limit, int offset = 0)
    {
        using var res = await _http.PostAsJsonAsync("/api/workday/wql",
            new WqlRequest { Wql = wql, Limit = limit, Offset = offset }, ApiClient.JsonOpts);
        var text = await res.Content.ReadAsStringAsync();

        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            _api.RedirectToLogin();
            throw new RedirectingToLoginException();
        }

        JsonDocument doc;
        try
        {
            doc = string.IsNullOrEmpty(text)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(text);
        }
        catch
        {
            throw new Exception(string.IsNullOrEmpty(text)
                ? $"Query failed ({(int)res.StatusCode})"
                : text);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!res.IsSuccessStatusCode)
            {
                throw new Exception(ExtractError(root, (int)res.StatusCode));
            }

            var rows = new List<Dictionary<string, JsonElement>>();
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in data.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var row = new Dictionary<string, JsonElement>();
                    foreach (var prop in el.EnumerateObject())
                        row[prop.Name] = prop.Value.Clone(); // detach from doc
                    rows.Add(row);
                }
            }

            // Seed columns from the SELECT list so requested-but-empty fields
            // still show, then append any extra keys the rows actually returned.
            var columns = new List<string>();
            var seen = new HashSet<string>();
            foreach (var c in ParseSelectColumns(wql))
                if (seen.Add(c)) columns.Add(c);
            foreach (var row in rows)
                foreach (var k in row.Keys)
                    if (seen.Add(k)) columns.Add(k);

            long? total = null;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("total", out var t)
                && t.ValueKind == JsonValueKind.Number
                && t.TryGetInt64(out var tv))
                total = tv;

            return new WqlResult { Columns = columns, Rows = rows, Total = total };
        }
    }

    // Workday's WQL errors carry the useful message; surface it verbatim:
    // errors[0].error ?? error ?? message ?? "Query failed (status)".
    private static string ExtractError(JsonElement root, int status)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("errors", out var errs)
                && errs.ValueKind == JsonValueKind.Array
                && errs.GetArrayLength() > 0)
            {
                var first = errs[0];
                if (first.ValueKind == JsonValueKind.Object
                    && first.TryGetProperty("error", out var e)
                    && e.ValueKind == JsonValueKind.String)
                    return e.GetString() ?? $"Query failed ({status})";
            }
            if (root.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String)
                return er.GetString() ?? $"Query failed ({status})";
            if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                return msg.GetString() ?? $"Query failed ({status})";
        }
        return $"Query failed ({status})";
    }
}

// What StreamAllRowsAsync paged through, with what's needed to judge whether it
// was the complete result set.
public sealed class StreamRowsResult
{
    // The total Workday reported on the first page, when it gave one.
    public long? Total { get; init; }
    // Rows actually fetched (and handed to onPage).
    public int Count { get; init; }
    // Every row's workdayID was tracked, so DuplicateIds means something.
    public bool IdsChecked { get; init; }
    // workdayIDs seen more than once across the pages.
    public int DuplicateIds { get; init; }

    // Fetched a different number of rows than Workday said the query matches.
    public bool CountMismatch => Total.HasValue && Total.Value != Count;
}

// Normalized WQL result: the SELECT-derived column order, the row objects (each
// a name→JsonElement map), and the tenant-reported total when present.
public sealed class WqlResult
{
    public List<string> Columns { get; init; } = new();
    public List<Dictionary<string, JsonElement>> Rows { get; init; } = new();
    public long? Total { get; init; }
}
