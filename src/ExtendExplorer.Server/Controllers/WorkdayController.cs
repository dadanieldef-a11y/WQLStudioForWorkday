using System.Text.Json;
using ExtendExplorer.Server.Services;
using ExtendExplorer.Shared;
using Microsoft.AspNetCore.Mvc;

namespace ExtendExplorer.Server.Controllers;

[ApiController]
[Route("api/workday")]
public sealed class WorkdayController : ControllerBase
{
    private readonly WorkdaySessionStore _store;
    private readonly WorkdayClient _wd;
    private readonly ILogger<WorkdayController> _log;

    public WorkdayController(WorkdaySessionStore store, WorkdayClient wd, ILogger<WorkdayController> log)
    {
        _store = store;
        _wd = wd;
        _log = log;
    }

    // Return upstream JSON text verbatim with its status code.
    private ContentResult Passthrough(HttpResponseMessage res, string text) =>
        new() { Content = text, ContentType = "application/json", StatusCode = (int)res.StatusCode };

    private IActionResult NotAuthed() => StatusCode(401, new { error = "not_authenticated" });

    // ── JsonElement helpers ────────────────────────────────────────────────
    private static string? Str(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        return null;
    }

    private static JsonElement? Obj(JsonElement parent, string name)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.Object)
            return v;
        return null;
    }

    private static IEnumerable<JsonElement> DataRows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray();
        return Array.Empty<JsonElement>();
    }

    private static int? Total(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetInt32() : null;

    // ─────────────────────────────────────────────────────────────────────
    // POST /api/workday/wql
    // ─────────────────────────────────────────────────────────────────────
    [HttpPost("wql")]
    public async Task<IActionResult> Wql([FromBody] WqlRequest body)
    {
        var s = _store.GetSession();
        if (string.IsNullOrEmpty(s.Token) || string.IsNullOrEmpty(s.WqlBase)) return NotAuthed();
        if (string.IsNullOrWhiteSpace(body.Wql)) return BadRequest(new { error = "missing_wql" });

        var url = new UriBuilder(WqlUrls.WqlDataUrl(s.WqlBase!));
        var q = System.Web.HttpUtility.ParseQueryString(url.Query);
        if (body.Limit is int lim) q["limit"] = lim.ToString();
        if (body.Offset is int off && off != 0) q["offset"] = off.ToString();
        url.Query = q.ToString();

        try
        {
            var payload = JsonSerializer.Serialize(new { query = body.Wql });
            using var res = await _wd.FetchAsync(s, url.ToString(), HttpMethod.Post, "application/json", payload);
            var text = await res.Content.ReadAsStringAsync();
            return Passthrough(res, text);
        }
        catch (NotAuthenticatedException)
        {
            return StatusCode(401, new { error = "session_expired" });
        }
        catch (Exception e)
        {
            _log.LogError(e, "WQL proxy error");
            return StatusCode(502, new { error = "upstream_error" });
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET /api/workday/datasources
    // ─────────────────────────────────────────────────────────────────────
    [HttpGet("datasources")]
    public async Task<IActionResult> DataSources()
    {
        var s = _store.GetSession();
        if (string.IsNullOrEmpty(s.Token) || string.IsNullOrEmpty(s.WqlBase)) return NotAuthed();

        // The WCP API Gateway caps `limit` at 100 per request (the 10,000 in the
        // WQL docs applies to the tenanted host), so paging is 100 at a time with
        // 5 requests in flight.
        const int pageSize = 100;
        const int concurrency = 5;

        // One upstream page → (rows, total) or an upstream error to pass through.
        async Task<(List<DataSourceDto> Rows, int Total, int? ErrStatus, string? ErrText)> FetchPage(int off)
        {
            var url = $"{WqlUrls.WqlDataSourcesUrl(s.WqlBase!)}?limit={pageSize}&offset={off}";
            using var res = await _wd.FetchAsync(s, url, HttpMethod.Get);
            if (!res.IsSuccessStatusCode)
                return (new(), 0, (int)res.StatusCode, await res.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var rows = new List<DataSourceDto>();
            foreach (var r in DataRows(doc.RootElement))
            {
                var n = NormalizeDataSource(r);
                if (n is not null) rows.Add(n);
            }
            return (rows, Total(doc.RootElement) ?? rows.Count, null, null);
        }

        ContentResult Upstream(int status, string? text) =>
            new() { Content = text ?? "", ContentType = "application/json", StatusCode = status };

        try
        {
            var all = new List<DataSourceDto>();
            // First page reports the real total; the remaining pages are fetched
            // in parallel batches (5 at a time) instead of one-at-a-time.
            var first = await FetchPage(0);
            if (first.ErrStatus is int es) return Upstream(es, first.ErrText);
            all.AddRange(first.Rows);

            var total = first.Total;
            var offsets = new List<int>();
            for (var o = pageSize; o < total; o += pageSize) offsets.Add(o);

            for (var i = 0; i < offsets.Count; i += concurrency)
            {
                var batch = offsets.Skip(i).Take(concurrency).ToList();
                var results = await Task.WhenAll(batch.Select(FetchPage));
                foreach (var r in results)
                {
                    if (r.ErrStatus is int e) return Upstream(e, r.ErrText);
                    all.AddRange(r.Rows);
                }
            }

            all.Sort((a, b) => string.CompareOrdinal(a.Alias, b.Alias));
            return Ok(new DataSourcesResponse { DataSources = all, Total = all.Count });
        }
        catch (NotAuthenticatedException) { return StatusCode(401, new { error = "session_expired" }); }
        catch (Exception e) { _log.LogError(e, "dataSources proxy error"); return StatusCode(502, new { error = "upstream_error" }); }
    }

    private static DataSourceDto? NormalizeDataSource(JsonElement raw)
    {
        var inner = Obj(raw, "dataSource") ?? raw;
        var id = Str(inner, "id") ?? Str(raw, "id") ?? "";
        var alias = Str(raw, "alias") ?? Str(inner, "alias") ?? "";
        var descriptor = Str(inner, "descriptor") ?? Str(raw, "descriptor") ?? alias;
        if (string.IsNullOrEmpty(alias) && string.IsNullOrEmpty(id)) return null;
        return new DataSourceDto { Id = id, Alias = alias, Descriptor = descriptor };
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET /api/workday/datasources/{id}/fields
    // ─────────────────────────────────────────────────────────────────────
    [HttpGet("datasources/{id}/fields")]
    public async Task<IActionResult> Fields(string id, [FromQuery] int? offset, [FromQuery] int? limit)
    {
        var s = _store.GetSession();
        if (string.IsNullOrEmpty(s.Token) || string.IsNullOrEmpty(s.WqlBase)) return NotAuthed();
        if (string.IsNullOrEmpty(id)) return BadRequest(new { error = "missing_datasource_id" });

        // Optional window: ?offset=&limit= lets the tree load a first batch fast
        // for huge BOs (e.g. allWorkers, thousands of fields) and pull the rest
        // on demand. With no params the whole set is returned (what the write
        // dialogs need). Pages are fetched with bounded concurrency to stay well
        // under upstream/host timeouts.
        // 100 is the WCP API Gateway's maximum page size; 5 requests in flight.
        const int pageSize = 100;
        const int concurrency = 5;
        var startOffset = Math.Max(0, offset ?? 0);
        int? windowLimit = limit is int l && l >= 0 ? l : null;

        // One upstream page → (rows, total) or an upstream error to pass through.
        async Task<(List<FieldDto> Rows, int Total, int? ErrStatus, string? ErrText)> FetchPage(int off)
        {
            var url = $"{WqlUrls.WqlFieldsUrl(s.WqlBase!, id)}?limit={pageSize}&offset={off}";
            using var res = await _wd.FetchAsync(s, url, HttpMethod.Get);
            if (!res.IsSuccessStatusCode)
                return (new(), 0, (int)res.StatusCode, await res.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var rows = new List<FieldDto>();
            foreach (var r in DataRows(doc.RootElement))
            {
                var n = NormalizeField(r);
                if (n is not null) rows.Add(n);
            }
            return (rows, Total(doc.RootElement) ?? rows.Count, null, null);
        }

        ContentResult Upstream(int status, string? text) =>
            new() { Content = text ?? "", ContentType = "application/json", StatusCode = status };

        try
        {
            var all = new List<FieldDto>();
            // First page at the start offset reports the real total.
            var first = await FetchPage(startOffset);
            if (first.ErrStatus is int es) return Upstream(es, first.ErrText);
            all.AddRange(first.Rows);

            var total = first.Total;
            var end = windowLimit is int wl ? Math.Min(startOffset + wl, total) : total;

            var offsets = new List<int>();
            for (var o = startOffset + pageSize; o < end; o += pageSize) offsets.Add(o);

            for (var i = 0; i < offsets.Count; i += concurrency)
            {
                var batch = offsets.Skip(i).Take(concurrency).ToList();
                var results = await Task.WhenAll(batch.Select(FetchPage));
                foreach (var r in results)
                {
                    if (r.ErrStatus is int e) return Upstream(e, r.ErrText);
                    all.AddRange(r.Rows);
                }
            }

            // Sort only the full, unwindowed response (callers that fetch
            // everything expect alphabetical); windowed responses stay in native
            // order and the client sorts the accumulated set.
            if (startOffset == 0 && windowLimit is null)
                all.Sort((a, b) => string.CompareOrdinal(a.Alias, b.Alias));
            return Ok(new FieldsResponse { Fields = all, Total = total });
        }
        catch (NotAuthenticatedException) { return StatusCode(401, new { error = "session_expired" }); }
        catch (Exception e) { _log.LogError(e, "fields proxy error"); return StatusCode(502, new { error = "upstream_error" }); }
    }

    private static FieldDto? NormalizeField(JsonElement raw)
    {
        var alias = Str(raw, "alias");
        if (string.IsNullOrEmpty(alias)) return null;
        var bo = Obj(raw, "businessObject") ?? Obj(raw, "relatedBusinessObject");
        return new FieldDto
        {
            Alias = alias,
            Descriptor = Str(raw, "descriptor", "description") ?? alias,
            Type = Str(raw, "type") ?? "Text",
            BusinessObject = bo is JsonElement b
                ? new BusinessObjectRef { Descriptor = Str(b, "descriptor"), Id = Str(b, "id") }
                : null,
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET /api/workday/datasources/{id}/filters
    // ─────────────────────────────────────────────────────────────────────
    [HttpGet("datasources/{id}/filters")]
    public async Task<IActionResult> Filters(string id)
    {
        var s = _store.GetSession();
        if (string.IsNullOrEmpty(s.Token) || string.IsNullOrEmpty(s.WqlBase)) return NotAuthed();
        if (string.IsNullOrEmpty(id)) return BadRequest(new { error = "missing_datasource_id" });

        try
        {
            var url = $"{WqlUrls.WqlFiltersUrl(s.WqlBase!, id)}?limit=100";
            using var res = await _wd.FetchAsync(s, url, HttpMethod.Get);
            if (!res.IsSuccessStatusCode)
                return Passthrough(res, await res.Content.ReadAsStringAsync());

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var filters = new List<FilterDto>();
            foreach (var r in DataRows(doc.RootElement))
            {
                var n = NormalizeFilter(r);
                if (n is not null) filters.Add(n);
            }
            filters.Sort((a, b) => string.CompareOrdinal(a.Alias, b.Alias));
            return Ok(new FiltersResponse { Filters = filters, Total = filters.Count });
        }
        catch (NotAuthenticatedException) { return StatusCode(401, new { error = "session_expired" }); }
        catch (Exception e) { _log.LogError(e, "filters proxy error"); return StatusCode(502, new { error = "upstream_error" }); }
    }

    private static PromptDto? NormalizePrompt(JsonElement raw)
    {
        var alias = Str(raw, "alias", "parameterAlias", "promptAlias");
        if (string.IsNullOrEmpty(alias)) return null;
        var required = (raw.TryGetProperty("required", out var rq) && rq.ValueKind == JsonValueKind.True)
                       || (raw.TryGetProperty("optional", out var op) && op.ValueKind == JsonValueKind.False);
        return new PromptDto
        {
            Alias = alias,
            Descriptor = Str(raw, "descriptor", "description") ?? alias,
            Type = Str(raw, "type", "dataType") ?? "Text",
            Required = required,
        };
    }

    private static FilterDto? NormalizeFilter(JsonElement raw)
    {
        var alias = Str(raw, "alias");
        if (string.IsNullOrEmpty(alias)) return null;

        JsonElement promptsEl = default;
        foreach (var key in new[] { "prompts", "parameters", "promptParameters", "dataSourceFilterParameters" })
        {
            if (raw.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array)
            {
                promptsEl = v;
                break;
            }
        }

        var prompts = new List<PromptDto>();
        if (promptsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in promptsEl.EnumerateArray())
            {
                var n = NormalizePrompt(p);
                if (n is not null) prompts.Add(n);
            }
        }

        return new FilterDto
        {
            Alias = alias,
            Descriptor = Str(raw, "descriptor", "description") ?? alias,
            Prompts = prompts,
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET /api/workday/record?boName=&id=
    // ─────────────────────────────────────────────────────────────────────
    [HttpGet("record")]
    public async Task<IActionResult> Record([FromQuery] string? boName, [FromQuery] string? id)
    {
        var s = _store.GetSession();
        if (string.IsNullOrEmpty(s.Token)) return NotAuthed();
        if (string.IsNullOrEmpty(s.ApiBase) || string.IsNullOrEmpty(s.BoCompanyId))
            return BadRequest(new { error = "missing_app_api_context" });

        boName = (boName ?? "").Trim();
        id = (id ?? "").Trim();
        if (string.IsNullOrEmpty(boName) || !WcpApp.IsCustomBo(boName, s.BoCompanyId))
            return BadRequest(new { error = "not_a_custom_bo" });
        if (string.IsNullOrEmpty(id)) return BadRequest(new { error = "missing_id" });

        var collectionUrl = WcpApp.WcpAppUrl(s.ApiBase, s.BoCompanyId, boName);
        if (collectionUrl is null) return BadRequest(new { error = "cannot_form_url" });
        var url = $"{collectionUrl}/{Uri.EscapeDataString(id)}";

        try
        {
            using var res = await _wd.FetchAsync(s, url, HttpMethod.Get);
            var text = await res.Content.ReadAsStringAsync();
            return new ContentResult
            {
                Content = string.IsNullOrEmpty(text) ? "{}" : text,
                ContentType = "application/json",
                StatusCode = (int)res.StatusCode,
            };
        }
        catch (NotAuthenticatedException) { return StatusCode(401, new { error = "session_expired" }); }
        catch (Exception e) { _log.LogError(e, "[record] upstream error"); return StatusCode(502, new { error = "upstream_error" }); }
    }

    // ─────────────────────────────────────────────────────────────────────
    // POST /api/workday/insert   Body: { boName, record }
    // ─────────────────────────────────────────────────────────────────────
    [HttpPost("insert")]
    public async Task<IActionResult> Insert([FromBody] JsonElement payload)
    {
        var s = _store.GetSession();
        if (string.IsNullOrEmpty(s.Token)) return NotAuthed();
        if (string.IsNullOrEmpty(s.ApiBase) || string.IsNullOrEmpty(s.BoCompanyId))
            return BadRequest(new { error = "missing_app_api_context" });

        var boName = (Str(payload, "boName") ?? "").Trim();
        if (string.IsNullOrEmpty(boName) || !WcpApp.IsCustomBo(boName, s.BoCompanyId))
            return BadRequest(new { error = "not_a_custom_bo" });

        if (!payload.TryGetProperty("record", out var record) ||
            record.ValueKind != JsonValueKind.Object ||
            !record.EnumerateObject().Any())
            return BadRequest(new { error = "empty_record" });

        var collectionUrl = WcpApp.WcpAppUrl(s.ApiBase, s.BoCompanyId, boName);
        if (collectionUrl is null) return BadRequest(new { error = "cannot_form_url" });

        try
        {
            using var res = await _wd.FetchAsync(s, collectionUrl, HttpMethod.Post, "application/json", record.GetRawText());
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                _log.LogError("[insert] POST failed: {Status} {Body}", (int)res.StatusCode, text.Length > 500 ? text[..500] : text);
            return new ContentResult
            {
                Content = string.IsNullOrEmpty(text) ? JsonSerializer.Serialize(new { ok = res.IsSuccessStatusCode }) : text,
                ContentType = "application/json",
                StatusCode = (int)res.StatusCode,
            };
        }
        catch (NotAuthenticatedException) { return StatusCode(401, new { error = "session_expired" }); }
        catch (Exception e) { _log.LogError(e, "[insert] upstream error"); return StatusCode(502, new { error = "upstream_error" }); }
    }

    // ─────────────────────────────────────────────────────────────────────
    // POST /api/workday/insert-bulk   Body: { boName, records[] }
    // Inserts many custom-BO instances via the WCP bulk create (?bulk=true,
    // ≤100 per call), auto-dropping Workday read-only/derived fields (A13) and
    // reporting each row's outcome.
    // ─────────────────────────────────────────────────────────────────────
    private const int InsertBulkMaxRows = 200;

    [HttpPost("insert-bulk")]
    public async Task<IActionResult> InsertBulk([FromBody] JsonElement payload)
    {
        var s = _store.GetSession();
        if (string.IsNullOrEmpty(s.Token)) return NotAuthed();
        if (string.IsNullOrEmpty(s.ApiBase) || string.IsNullOrEmpty(s.BoCompanyId))
            return BadRequest(new { error = "missing_app_api_context" });

        var boName = (Str(payload, "boName") ?? "").Trim();
        if (string.IsNullOrEmpty(boName) || !WcpApp.IsCustomBo(boName, s.BoCompanyId))
            return BadRequest(new { error = "not_a_custom_bo" });

        if (!payload.TryGetProperty("records", out var recordsEl) || recordsEl.ValueKind != JsonValueKind.Array)
            return BadRequest(new { error = "no_records" });
        var records = recordsEl.EnumerateArray().ToList();
        if (records.Count == 0) return BadRequest(new { error = "no_records" });
        if (records.Count > InsertBulkMaxRows)
            return BadRequest(new { error = $"too_many_records (max {InsertBulkMaxRows})" });

        var url = WcpApp.WcpAppUrl(s.ApiBase, s.BoCompanyId, boName);
        if (url is null) return BadRequest(new { error = "cannot_form_url" });
        var bulkUrl = url + (url.Contains('?') ? "&" : "?") + "bulk=true";

        var results = new List<InsertResultRow>();
        var okRows = 0;
        var failRows = 0;

        try
        {
            // The WCP App API accepts a native bulk create: POST { "data": [...] }
            // to the entity URL with ?bulk=true, up to 100 records per call. We
            // sub-batch by 100 (the client already chunks; this is a safety net).
            const int bulkSize = 100;
            for (var start = 0; start < records.Count; start += bulkSize)
            {
                var slice = records.Skip(start).Take(bulkSize).ToList();
                foreach (var r in await InsertBulkBatch(s, bulkUrl, slice, start))
                {
                    results.Add(r);
                    if (r.Ok) okRows++; else failRows++;
                }
            }
        }
        catch (NotAuthenticatedException)
        {
            return StatusCode(401, new { error = "not_authenticated" });
        }
        catch (Exception e)
        {
            _log.LogError(e, "[insert-bulk] upstream error");
            return StatusCode(502, new { error = "upstream_error", results, okRows, failRows });
        }

        return Ok(new InsertBulkResponse { Results = results, OkRows = okRows, FailRows = failRows });
    }

    // POST one batch (≤100 records) as { "data": [...] } to the ?bulk=true
    // endpoint, auto-dropping read-only/derived fields (A13) from every record and
    // retrying the batch (read-only fields are schema-consistent, so dropping them
    // across the whole batch is safe).
    private async Task<List<InsertResultRow>> InsertBulkBatch(
        WorkdaySessionData s, string bulkUrl, List<JsonElement> slice, int indexOffset)
    {
        var current = slice.Select(rec =>
        {
            var d = new Dictionary<string, JsonElement>();
            if (rec.ValueKind == JsonValueKind.Object)
                foreach (var p in rec.EnumerateObject()) d[p.Name] = p.Value.Clone();
            return d;
        }).ToList();
        var dropped = new List<string>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["data"] = current });
            using var res = await _wd.FetchAsync(s, bulkUrl, HttpMethod.Post, "application/json", body);
            var text = await res.Content.ReadAsStringAsync();

            if (res.IsSuccessStatusCode)
                return MapBulkSuccess(current.Count, text, indexOffset, dropped);

            var errs = ParseErrors(text);
            var ro = errs.Where(IsReadOnly)
                .Select(e => e.Field)
                .Where(f => !string.IsNullOrEmpty(f) && current.Any(d => d.ContainsKey(f!)))
                .Select(f => f!)
                .Distinct()
                .ToList();
            if (ro.Count > 0 && attempt < 2)
            {
                foreach (var f in ro)
                    foreach (var d in current) d.Remove(f);
                dropped.AddRange(ro);
                continue;
            }

            var msg = errs.Count > 0
                ? string.Join("; ", errs.Select(e => $"{(string.IsNullOrEmpty(e.Field) ? "" : $"{e.Field}: ")}{e.Message}"))
                : (text.Length > 300 ? text[..300] : text);
            var status = (int)res.StatusCode;
            var dl = dropped.Count > 0 ? dropped.Distinct().ToList() : null;
            return Enumerable.Range(0, current.Count).Select(i => new InsertResultRow
            {
                Index = indexOffset + i,
                Ok = false,
                Status = status,
                Error = msg,
                Dropped = dl,
            }).ToList();
        }

        return Enumerable.Range(0, current.Count).Select(i => new InsertResultRow
        {
            Index = indexOffset + i,
            Ok = false,
            Status = 0,
            Error = "too many read-only retries",
        }).ToList();
    }

    // Map a successful bulk response to per-record results. The response is
    // expected to carry a "data" array of created instances in submitted order; we
    // pull an id per row where present and surface any per-row error the API gives.
    private static List<InsertResultRow> MapBulkSuccess(
        int count, string text, int indexOffset, List<string> dropped)
    {
        var ids = new string?[count];
        var errors = new string?[count];
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            JsonElement arr = default;
            var haveArr = false;
            if (root.ValueKind == JsonValueKind.Array) { arr = root; haveArr = true; }
            else if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
            { arr = d; haveArr = true; }

            if (haveArr)
            {
                var i = 0;
                foreach (var item in arr.EnumerateArray())
                {
                    if (i >= count) break;
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        if (item.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String)
                            ids[i] = idv.GetString();
                        else if (item.TryGetProperty("workdayID", out var wid) && wid.ValueKind == JsonValueKind.Object
                            && wid.TryGetProperty("id", out var widId) && widId.ValueKind == JsonValueKind.String)
                            ids[i] = widId.GetString();

                        if (item.TryGetProperty("errors", out var ie) && ie.ValueKind == JsonValueKind.Array && ie.GetArrayLength() > 0)
                            errors[i] = ie[0].TryGetProperty("error", out var em) && em.ValueKind == JsonValueKind.String ? em.GetString() : "error";
                        else if (item.TryGetProperty("error", out var single) && single.ValueKind == JsonValueKind.String)
                            errors[i] = single.GetString();
                    }
                    i++;
                }
            }
        }
        catch { /* unexpected shape — treat all as created */ }

        var dl = dropped.Count > 0 ? dropped.Distinct().ToList() : null;
        return Enumerable.Range(0, count).Select(i => new InsertResultRow
        {
            Index = indexOffset + i,
            Ok = errors[i] is null,
            Status = 200,
            Id = ids[i],
            Error = errors[i],
            Dropped = dl,
        }).ToList();
    }

    private sealed record ValErr(string? Field, string Message, string? Code);

    private static List<ValErr> ParseErrors(string text)
    {
        var list = new List<ValErr>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("errors", out var errs)
                && errs.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in errs.EnumerateArray())
                    list.Add(new ValErr(Str(e, "field") ?? Str(e, "path"), Str(e, "error") ?? "error", Str(e, "code")));
            }
        }
        catch { /* not JSON */ }
        return list;
    }

    private static bool IsReadOnly(ValErr e) =>
        e.Code == "A13"
        || System.Text.RegularExpressions.Regex.IsMatch(e.Message, "read.?only", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // ─────────────────────────────────────────────────────────────────────
    // POST /api/workday/update   Body: { boName, id, body }
    // ─────────────────────────────────────────────────────────────────────
    [HttpPost("update")]
    public async Task<IActionResult> Update([FromBody] JsonElement payload)
    {
        var s = _store.GetSession();
        if (string.IsNullOrEmpty(s.Token)) return NotAuthed();
        if (string.IsNullOrEmpty(s.ApiBase) || string.IsNullOrEmpty(s.BoCompanyId))
            return BadRequest(new { error = "missing_app_api_context" });

        var boName = (Str(payload, "boName") ?? "").Trim();
        var id = (Str(payload, "id") ?? "").Trim();

        if (string.IsNullOrEmpty(boName) || !WcpApp.IsCustomBo(boName, s.BoCompanyId))
            return BadRequest(new { error = "not_a_custom_bo" });
        if (string.IsNullOrEmpty(id)) return BadRequest(new { error = "missing_id" });

        if (!payload.TryGetProperty("body", out var patch) || patch.ValueKind != JsonValueKind.Object)
            return BadRequest(new { error = "invalid_body" });
        if (!patch.EnumerateObject().Any())
            return BadRequest(new { error = "no_changes" });

        var collectionUrl = WcpApp.WcpAppUrl(s.ApiBase, s.BoCompanyId, boName);
        if (collectionUrl is null) return BadRequest(new { error = "cannot_form_url" });
        var url = $"{collectionUrl}/{Uri.EscapeDataString(id)}";

        try
        {
            using var res = await _wd.FetchAsync(s, url, HttpMethod.Patch, "application/json", patch.GetRawText());
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                _log.LogError("[update] PATCH failed: {Status} {Body}", (int)res.StatusCode, text.Length > 500 ? text[..500] : text);
            return new ContentResult
            {
                Content = string.IsNullOrEmpty(text) ? JsonSerializer.Serialize(new { ok = res.IsSuccessStatusCode }) : text,
                ContentType = "application/json",
                StatusCode = (int)res.StatusCode,
            };
        }
        catch (NotAuthenticatedException) { return StatusCode(401, new { error = "session_expired" }); }
        catch (Exception e) { _log.LogError(e, "[update] upstream error"); return StatusCode(502, new { error = "upstream_error" }); }
    }

    // ─────────────────────────────────────────────────────────────────────
    // POST /api/workday/update-bulk   Body: { boName, ids[], body }
    // ─────────────────────────────────────────────────────────────────────
    private const int BulkMaxIds = 1000;
    private const int BatchSize = 100;

    [HttpPost("update-bulk")]
    public async Task<IActionResult> UpdateBulk([FromBody] JsonElement payload)
    {
        var s = _store.GetSession();
        if (string.IsNullOrEmpty(s.Token)) return NotAuthed();
        if (string.IsNullOrEmpty(s.ApiBase) || string.IsNullOrEmpty(s.BoCompanyId))
            return BadRequest(new { error = "missing_app_api_context" });

        var boName = (Str(payload, "boName") ?? "").Trim();
        if (string.IsNullOrEmpty(boName) || !WcpApp.IsCustomBo(boName, s.BoCompanyId))
            return BadRequest(new { error = "not_a_custom_bo" });

        var ids = ReadStringArray(payload, "ids");
        if (ids.Count == 0) return BadRequest(new { error = "no_ids" });
        if (ids.Count > BulkMaxIds) return BadRequest(new { error = $"too_many_ids (max {BulkMaxIds})" });

        if (!payload.TryGetProperty("body", out var bodyEl) ||
            bodyEl.ValueKind != JsonValueKind.Object ||
            !bodyEl.EnumerateObject().Any())
            return BadRequest(new { error = "invalid_body" });

        var collectionUrl = WcpApp.WcpAppUrl(s.ApiBase, s.BoCompanyId, boName);
        if (collectionUrl is null) return BadRequest(new { error = "cannot_form_url" });
        var bulkUrl = $"{collectionUrl}?bulk=true";

        // Each row becomes { id, ...changed fields }.
        var bodyFields = bodyEl.EnumerateObject().ToList();

        var batches = new List<BatchResultRow>();
        var okRows = 0;
        var failRows = 0;

        for (var i = 0; i < ids.Count; i += BatchSize)
        {
            var chunk = ids.Skip(i).Take(BatchSize).ToList();
            var index = batches.Count;

            // Build { data: [ { id, ...fields }, ... ] } with raw upstream field values.
            var records = chunk.Select(rawId =>
            {
                var dict = new Dictionary<string, object?> { ["id"] = rawId };
                foreach (var f in bodyFields)
                    dict[f.Name] = JsonSerializer.Deserialize<object?>(f.Value.GetRawText());
                return dict;
            }).ToList();
            var batchBody = JsonSerializer.Serialize(new { data = records });

            try
            {
                using var res = await _wd.FetchAsync(s, bulkUrl, HttpMethod.Patch, "application/json", batchBody);
                if (res.IsSuccessStatusCode)
                {
                    batches.Add(new BatchResultRow { Index = index, Count = chunk.Count, Ok = true, Status = (int)res.StatusCode });
                    okRows += chunk.Count;
                }
                else
                {
                    var detail = "";
                    try { detail = await res.Content.ReadAsStringAsync(); if (detail.Length > 400) detail = detail[..400]; } catch { }
                    _log.LogError("[update-bulk] batch failed: {Status} {Detail}", (int)res.StatusCode, detail);
                    batches.Add(new BatchResultRow { Index = index, Count = chunk.Count, Ok = false, Status = (int)res.StatusCode, Error = string.IsNullOrEmpty(detail) ? res.ReasonPhrase : detail });
                    failRows += chunk.Count;
                }
            }
            catch (NotAuthenticatedException)
            {
                return StatusCode(401, new { error = "not_authenticated" });
            }
            catch (Exception e)
            {
                batches.Add(new BatchResultRow { Index = index, Count = chunk.Count, Ok = false, Status = 0, Error = e.Message });
                failRows += chunk.Count;
            }
        }

        return Ok(new BulkUpdateResponse { Batches = batches, OkRows = okRows, FailRows = failRows, Target = bulkUrl });
    }

    // ─────────────────────────────────────────────────────────────────────
    // POST /api/workday/delete   Body: { boName, ids[] }
    // Deletes via the WCP App API's native bulk delete: DELETE {entity}?bulk=true
    // with { "data": [ { "id": wid }, … ] }, up to 100 ids per call. Each id
    // still gets its own result row so the dialog can report per-record outcomes.
    // ─────────────────────────────────────────────────────────────────────
    // Matches the client's Delete all cap (1000); sent as 10 bulk calls max.
    private const int DeleteMaxIds = 1000;
    private const int DeleteBatchSize = 100;

    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromBody] JsonElement payload)
    {
        var s = _store.GetSession();
        if (string.IsNullOrEmpty(s.Token)) return NotAuthed();
        if (string.IsNullOrEmpty(s.ApiBase) || string.IsNullOrEmpty(s.BoCompanyId))
            return BadRequest(new { error = "missing_app_api_context" });

        var boName = (Str(payload, "boName") ?? "").Trim();
        if (string.IsNullOrEmpty(boName) || !WcpApp.IsCustomBo(boName, s.BoCompanyId))
            return BadRequest(new { error = "not_a_custom_bo" });

        var ids = ReadStringArray(payload, "ids");
        if (ids.Count == 0) return BadRequest(new { error = "no_ids" });
        if (ids.Count > DeleteMaxIds) return BadRequest(new { error = $"too_many_ids (max {DeleteMaxIds})" });

        var collectionUrl = WcpApp.WcpAppUrl(s.ApiBase, s.BoCompanyId, boName);
        if (collectionUrl is null) return BadRequest(new { error = "cannot_form_url" });
        var bulkUrl = collectionUrl + (collectionUrl.Contains('?') ? "&" : "?") + "bulk=true";

        var results = new List<DeleteResultRow>();

        // Blank ids never reach Workday.
        foreach (var blank in ids.Where(string.IsNullOrEmpty))
            results.Add(new DeleteResultRow { Id = blank, Ok = false, Status = 0, Error = "empty id" });
        var valid = ids.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();

        for (var start = 0; start < valid.Count; start += DeleteBatchSize)
        {
            var chunk = valid.Skip(start).Take(DeleteBatchSize).ToList();
            var body = JsonSerializer.Serialize(new { data = chunk.Select(id => new { id }) });
            try
            {
                using var res = await _wd.FetchAsync(s, bulkUrl, HttpMethod.Delete, "application/json", body);
                var text = await res.Content.ReadAsStringAsync();
                if (res.IsSuccessStatusCode)
                {
                    results.AddRange(MapBulkDelete(chunk, text, (int)res.StatusCode));
                }
                else
                {
                    var errs = ParseErrors(text);
                    var msg = errs.Count > 0
                        ? string.Join("; ", errs.Select(e => e.Message))
                        : string.IsNullOrEmpty(text) ? res.ReasonPhrase : (text.Length > 300 ? text[..300] : text);
                    _log.LogError("[delete] bulk batch failed: {Status} {Detail}", (int)res.StatusCode, msg);
                    results.AddRange(chunk.Select(id => new DeleteResultRow
                    {
                        Id = id, Ok = false, Status = (int)res.StatusCode, Error = msg,
                    }));
                }
            }
            catch (NotAuthenticatedException)
            {
                // Token is dead — stop and let the client bounce to re-login.
                return StatusCode(401, new { error = "not_authenticated" });
            }
            catch (Exception e)
            {
                results.AddRange(chunk.Select(id => new DeleteResultRow
                {
                    Id = id, Ok = false, Status = 0, Error = e.Message,
                }));
            }
        }

        return Ok(new DeleteResponse { Results = results, Target = bulkUrl });
    }

    // Map a successful bulk-delete response to per-id results. If the API returns
    // a "data" array with per-item errors (matched by id, else by position), those
    // ids are reported as failed; otherwise every id in the batch is deleted.
    private static List<DeleteResultRow> MapBulkDelete(List<string> chunk, string text, int status)
    {
        var errorsById = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                JsonElement arr = default;
                var haveArr = false;
                if (root.ValueKind == JsonValueKind.Array) { arr = root; haveArr = true; }
                else if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
                { arr = d; haveArr = true; }

                if (haveArr)
                {
                    var i = 0;
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            string? err = null;
                            if (item.TryGetProperty("errors", out var ie) && ie.ValueKind == JsonValueKind.Array && ie.GetArrayLength() > 0)
                                err = Str(ie[0], "error") ?? "error";
                            else
                                err = Str(item, "error");

                            if (err is not null)
                            {
                                var id = Str(item, "id") ?? (i < chunk.Count ? chunk[i] : null);
                                if (id is not null) errorsById[id] = err;
                            }
                        }
                        i++;
                    }
                }
            }
        }
        catch { /* empty or unexpected body — treat the batch as deleted */ }

        return chunk.Select(id => errorsById.TryGetValue(id, out var err)
            ? new DeleteResultRow { Id = id, Ok = false, Status = status, Error = err }
            : new DeleteResultRow { Id = id, Ok = true, Status = status }).ToList();
    }

    private static List<string> ReadStringArray(JsonElement payload, string name)
    {
        var list = new List<string>();
        if (payload.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                list.Add(el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.ToString());
            }
        }
        return list;
    }
}
