// Wire contracts shared by the ASP.NET Core BFF (ExtendExplorer.Server) and the
// Blazor WebAssembly client (ExtendExplorer.Client). JSON is serialized
// camelCase on both ends (see each project's JsonSerializerOptions), so a
// property such as DataSources goes over the wire as "dataSources" — matching
// what the Monaco JS in wwwroot/js/monaco-wql.js also expects.

namespace ExtendExplorer.Shared;

// ── auth ──────────────────────────────────────────────────────────────────

// POST body the Connect screen sends to /api/auth/login. Mirrors the browser's
// saved connection (endpoints + API client id, optional client secret for a
// confidential client, optional custom-BO REST context). The server holds no
// tenant config.
public sealed class LoginRequest
{
    public string? Name { get; set; }
    public string? AuthorizeEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? WqlBase { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? TenantAlias { get; set; }
    public string? BoCompanyId { get; set; }
    public string? ApiBase { get; set; }
    public string? ReturnTo { get; set; }
}

public sealed class LoginResponse
{
    public string RedirectUrl { get; set; } = "";
}

// GET /api/auth/session — lightweight status for the gate. Never returns the token.
public sealed class SessionStatus
{
    public bool Authenticated { get; set; }
    public string? User { get; set; }
    public string? Tenant { get; set; }
    public string? BoCompanyId { get; set; }
    public long? ExpiresAt { get; set; } // ms since epoch
}

// ── WQL metadata ──────────────────────────────────────────────────────────

public sealed class DataSourceDto
{
    public string Id { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Descriptor { get; set; } = "";
}

public sealed class DataSourcesResponse
{
    public List<DataSourceDto> DataSources { get; set; } = new();
    public int Total { get; set; }
}

public sealed class BusinessObjectRef
{
    public string? Descriptor { get; set; }
    public string? Id { get; set; }
}

public sealed class FieldDto
{
    public string Alias { get; set; } = "";
    public string Descriptor { get; set; } = "";
    // Text | Date | Numeric | Currency | Boolean | Single Instance | Multi Instance
    public string Type { get; set; } = "Text";
    public BusinessObjectRef? BusinessObject { get; set; }
}

public sealed class FieldsResponse
{
    public List<FieldDto> Fields { get; set; } = new();
    public int Total { get; set; }
}

public sealed class PromptDto
{
    public string Alias { get; set; } = "";
    public string Descriptor { get; set; } = "";
    public string Type { get; set; } = "Text";
    public bool Required { get; set; }
}

public sealed class FilterDto
{
    public string Alias { get; set; } = "";
    public string Descriptor { get; set; } = "";
    public List<PromptDto> Prompts { get; set; } = new();
}

public sealed class FiltersResponse
{
    public List<FilterDto> Filters { get; set; } = new();
    public int Total { get; set; }
}

// A writable field on a custom BO, derived client-side from FieldDto by
// BoFields.FetchByIdAsync (system + self-referencing fields excluded).
public sealed class BoFieldDto
{
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "scalar";  // scalar | reference
    public string Type { get; set; } = "string";  // string | number | boolean
    public string? Label { get; set; }
}

// ── WQL execution ─────────────────────────────────────────────────────────

public sealed class WqlRequest
{
    public string? Wql { get; set; }
    public int? Limit { get; set; }
    public int? Offset { get; set; }
}

// ── custom-BO CRUD ────────────────────────────────────────────────────────

public sealed class DeleteRequest
{
    public string BoName { get; set; } = "";
    public List<string> Ids { get; set; } = new();
}

public sealed class DeleteResultRow
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public int Status { get; set; }
    public string? Error { get; set; }
}

public sealed class DeleteResponse
{
    public List<DeleteResultRow> Results { get; set; } = new();
    public string? Target { get; set; }
}

public sealed class BatchResultRow
{
    public int Index { get; set; }
    public int Count { get; set; }
    public bool Ok { get; set; }
    public int Status { get; set; }
    public string? Error { get; set; }
}

public sealed class BulkUpdateResponse
{
    public List<BatchResultRow> Batches { get; set; } = new();
    public int OkRows { get; set; }
    public int FailRows { get; set; }
    public string? Target { get; set; }
}

// One row's outcome from a bulk insert (sent via the WCP ?bulk=true endpoint,
// ≤100 records per call; results are reported per record).
public sealed class InsertResultRow
{
    public int Index { get; set; }
    public bool Ok { get; set; }
    public int Status { get; set; }
    public string? Id { get; set; }        // created instance id, when returned
    public string? Error { get; set; }
    public List<string>? Dropped { get; set; } // read-only fields Workday rejected
}

public sealed class InsertBulkResponse
{
    public List<InsertResultRow> Results { get; set; } = new();
    public int OkRows { get; set; }
    public int FailRows { get; set; }
}
