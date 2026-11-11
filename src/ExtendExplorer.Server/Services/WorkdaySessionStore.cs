using System.Text.Json;

namespace ExtendExplorer.Server.Services;

// The Workday access token is held server-side in ISession (the C# analogue of
// the original's iron-session cookie). The tenant's wqlBase + display name are
// pinned here at login so every later call targets the same tenant the token
// came from.
public sealed class WorkdaySessionData
{
    public string? Token { get; set; }
    public long? TokenExp { get; set; }     // seconds since epoch (JWT `exp`)
    public string? User { get; set; }        // worker id (JWT `sub`)
    public string? WqlBase { get; set; }     // tenant WQL base, pinned at login
    public string? TenantName { get; set; }  // connection name, for display
    public string? BoCompanyId { get; set; } // custom-BO marker for the Extend API
    public string? ApiBase { get; set; }     // Extend API base, for custom-BO writes
    public long? CreatedAt { get; set; }
}

// Short-lived transaction data carrying CSRF `state` + PKCE verifier across the
// redirect to Workday and back, plus the chosen connection's token context.
public sealed class OAuthTxData
{
    public string? State { get; set; }
    public string? CodeVerifier { get; set; }
    public string? ReturnTo { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? WqlBase { get; set; }
    public string? TenantName { get; set; }
    public string? TenantAlias { get; set; }
    public string? BoCompanyId { get; set; }
    public string? ApiBase { get; set; }
}

// Reads/writes the session + tx blobs from HttpContext.Session as JSON.
public sealed class WorkdaySessionStore
{
    private const string SessionKey = "wd_session";
    private const string TxKey = "wd_oauth_tx";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpContextAccessor _http;
    public WorkdaySessionStore(IHttpContextAccessor http) => _http = http;

    private ISession Session =>
        _http.HttpContext?.Session
        ?? throw new InvalidOperationException("No session on the current request.");

    public WorkdaySessionData GetSession()
    {
        var raw = Session.GetString(SessionKey);
        return raw is null
            ? new WorkdaySessionData()
            : JsonSerializer.Deserialize<WorkdaySessionData>(raw, Json) ?? new WorkdaySessionData();
    }

    public void SaveSession(WorkdaySessionData data) =>
        Session.SetString(SessionKey, JsonSerializer.Serialize(data, Json));

    public void DestroySession() => Session.Remove(SessionKey);

    public OAuthTxData GetTx()
    {
        var raw = Session.GetString(TxKey);
        return raw is null
            ? new OAuthTxData()
            : JsonSerializer.Deserialize<OAuthTxData>(raw, Json) ?? new OAuthTxData();
    }

    public void SaveTx(OAuthTxData tx) =>
        Session.SetString(TxKey, JsonSerializer.Serialize(tx, Json));

    public void DestroyTx() => Session.Remove(TxKey);

    // Treat a token that expires within 30s as already expired — avoids handing
    // off a token that dies mid-request. Mirrors wcpAuth.getValidToken.
    public static bool TokenIsExpired(WorkdaySessionData s)
    {
        if (s.TokenExp is null) return false; // no exp claim → let Workday decide
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return s.TokenExp.Value - 30 <= now;
    }
}
