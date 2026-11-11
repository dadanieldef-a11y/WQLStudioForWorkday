using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExtendExplorer.Server.Services;

// Port of lib/workday/oauth.ts. OAuth helpers for a Workday public/confidential
// API client, Authorization Code grant with PKCE (S256) + state.
//
// The token endpoint authenticates the client with HTTP Basic
// (base64(clientId:clientSecret)) when a secret is supplied, else client_id in
// the body. redirect_uri is echoed per the OAuth spec.
public sealed class TokenResponse
{
    public string? access_token { get; set; }
    public string? token_type { get; set; }
    public int? expires_in { get; set; }
    public string? refresh_token { get; set; }
    public string? error { get; set; }
    public string? error_description { get; set; }
}

public readonly record struct DecodedToken(long? Exp, string? Sub);

public sealed class WorkdayAuthException : Exception
{
    public int? Status { get; }
    public string? Detail { get; }
    public WorkdayAuthException(string message, int? status = null, string? detail = null)
        : base(message)
    {
        Status = status;
        Detail = detail;
    }
}

public sealed class BuildAuthorizeOptions
{
    public string AuthorizeEndpoint { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string State { get; set; } = "";
    public string CodeChallenge { get; set; } = "";
    public string? Scope { get; set; }
    public string? TenantAlias { get; set; }
}

public sealed class ExchangeOptions
{
    public string TokenEndpoint { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string Code { get; set; } = "";
    public string CodeVerifier { get; set; } = "";
    public string? ClientSecret { get; set; }
    public string? TenantAlias { get; set; }
}

public sealed class OAuthService
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<OAuthService> _log;

    public OAuthService(IHttpClientFactory factory, ILogger<OAuthService> log)
    {
        _factory = factory;
        _log = log;
    }

    private static string Base64Url(byte[] buf) =>
        Convert.ToBase64String(buf).Replace("=", "").Replace("+", "-").Replace("/", "_");

    public static string GenerateState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    // ── authorize redirect ──────────────────────────────────────
    public static string BuildAuthorizeUrl(BuildAuthorizeOptions o)
    {
        var q = System.Web.HttpUtility.ParseQueryString(string.Empty);
        q["response_type"] = "code";
        q["client_id"] = o.ClientId; // raw client id, as shown
        q["redirect_uri"] = o.RedirectUri;
        q["state"] = o.State;
        q["code_challenge"] = o.CodeChallenge;
        q["code_challenge_method"] = "S256";
        if (!string.IsNullOrEmpty(o.Scope)) q["scope"] = o.Scope;
        // WCP central auth (auth.api.workday.com) takes the tenant as a param.
        if (!string.IsNullOrEmpty(o.TenantAlias)) q["tenantAlias"] = o.TenantAlias;

        var sep = o.AuthorizeEndpoint.Contains('?') ? "&" : "?";
        return $"{o.AuthorizeEndpoint}{sep}{q}";
    }

    // ── token exchange ──────────────────────────────────────────
    public async Task<TokenResponse> ExchangeCodeForTokensAsync(ExchangeOptions o)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = o.Code,
            ["redirect_uri"] = o.RedirectUri,
            ["code_verifier"] = o.CodeVerifier,
            ["client_id"] = o.ClientId,
        };
        // WCP central token endpoint is tenant-agnostic, so pass the alias when set.
        if (!string.IsNullOrEmpty(o.TenantAlias)) form["tenantAlias"] = o.TenantAlias;

        using var req = new HttpRequestMessage(HttpMethod.Post, o.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        // Confidential client: authenticate with HTTP Basic (clientId:secret).
        if (!string.IsNullOrEmpty(o.ClientSecret))
        {
            var basic = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{o.ClientId}:{o.ClientSecret}"));
            req.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}");
        }

        var client = _factory.CreateClient(WorkdayClient.HttpClientName);
        using var res = await client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            _log.LogError("[oauth] token exchange failed: {Status} {Reason}\n  body: {Body}",
                (int)res.StatusCode, res.ReasonPhrase,
                text.Length > 1000 ? text[..1000] : text);
        }

        TokenResponse json;
        try
        {
            json = string.IsNullOrEmpty(text)
                ? new TokenResponse()
                : JsonSerializer.Deserialize<TokenResponse>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                  ?? new TokenResponse();
        }
        catch
        {
            throw new WorkdayAuthException("Token endpoint returned non-JSON response",
                (int)res.StatusCode, text);
        }

        if (!res.IsSuccessStatusCode || string.IsNullOrEmpty(json.access_token))
        {
            var detail = json.error_description ?? json.error ?? $"HTTP {(int)res.StatusCode}";
            throw new WorkdayAuthException($"Token exchange failed: {detail}", (int)res.StatusCode, text);
        }
        return json;
    }

    // Decode (do NOT verify) the JWT to read exp + sub. Received over TLS
    // straight from the token endpoint, so there's nothing to verify here.
    public static DecodedToken DecodeToken(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length < 2) return new DecodedToken(null, null);
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var jsonBytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;
            long? exp = root.TryGetProperty("exp", out var e) && e.ValueKind == JsonValueKind.Number
                ? e.GetInt64() : null;
            string? sub = root.TryGetProperty("sub", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : null;
            return new DecodedToken(exp, sub);
        }
        catch
        {
            return new DecodedToken(null, null);
        }
    }
}
