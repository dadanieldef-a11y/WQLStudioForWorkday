using ExtendExplorer.Server.Services;
using ExtendExplorer.Shared;
using Microsoft.AspNetCore.Mvc;

namespace ExtendExplorer.Server.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly WorkdaySessionStore _store;
    private readonly OAuthService _oauth;
    private readonly ILogger<AuthController> _log;

    public AuthController(WorkdaySessionStore store, OAuthService oauth, ILogger<AuthController> log)
    {
        _store = store;
        _oauth = oauth;
        _log = log;
    }

    private static bool IsHttpUrl(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        return Uri.TryCreate(v, UriKind.Absolute, out var u)
               && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
    }

    // POST /api/auth/login
    // The chosen tenant connection comes from the browser (localStorage) — the
    // server holds no tenant config. We generate state + PKCE, pin the token
    // context the callback will need into the session tx slot, and hand back the
    // authorize URL for the client to navigate to.
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest body)
    {
        if (!IsHttpUrl(body.AuthorizeEndpoint) ||
            !IsHttpUrl(body.TokenEndpoint) ||
            !IsHttpUrl(body.WqlBase))
        {
            return BadRequest(new { error = "invalid_endpoints" });
        }
        if (string.IsNullOrWhiteSpace(body.ClientId))
        {
            return BadRequest(new { error = "missing_client_id" });
        }

        var state = OAuthService.GenerateState();
        var (verifier, challenge) = OAuthService.GeneratePkce();
        var redirectUri = WqlUrls.RedirectUriFromRequest(Request);

        var tx = new OAuthTxData
        {
            State = state,
            CodeVerifier = verifier,
            ReturnTo = body.ReturnTo is not null && body.ReturnTo.StartsWith('/') ? body.ReturnTo : "/",
            TokenEndpoint = body.TokenEndpoint!.Trim(),
            ClientId = body.ClientId!.Trim(),
            ClientSecret = string.IsNullOrWhiteSpace(body.ClientSecret) ? null : body.ClientSecret!.Trim(),
            WqlBase = WqlUrls.NormalizeWqlBase(body.WqlBase!),
            TenantName = body.Name,
            // Custom-BO REST context is optional — a connection can be read-only.
            BoCompanyId = string.IsNullOrWhiteSpace(body.BoCompanyId) ? null : body.BoCompanyId!.Trim(),
            ApiBase = IsHttpUrl(body.ApiBase) ? WcpApp.NormalizeApiBase(body.ApiBase!) : null,
            TenantAlias = string.IsNullOrWhiteSpace(body.TenantAlias) ? null : body.TenantAlias!.Trim(),
        };
        _store.SaveTx(tx);

        var redirectUrl = OAuthService.BuildAuthorizeUrl(new BuildAuthorizeOptions
        {
            AuthorizeEndpoint = body.AuthorizeEndpoint!.Trim(),
            ClientId = body.ClientId!.Trim(),
            RedirectUri = redirectUri,
            State = state,
            CodeChallenge = challenge,
            TenantAlias = tx.TenantAlias,
        });

        return Ok(new LoginResponse { RedirectUrl = redirectUrl });
    }

    // GET /api/auth/callback?code=...&state=...
    // Validate state, exchange the code using the pinned tenant context, and
    // store the token + tenant in the session — that token becomes the Bearer
    // for every WQL/metadata call, always against the tenant it came from.
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription)
    {
        var origin = $"{Request.Scheme}://{Request.Host.Value}";

        var tx = _store.GetTx();
        _store.DestroyTx(); // consume the transaction either way

        IActionResult Fail(string reason) =>
            Redirect($"{origin}/?auth_error={Uri.EscapeDataString(reason)}");

        if (!string.IsNullOrEmpty(error)) return Fail(errorDescription ?? error);
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state)) return Fail("missing_code_or_state");
        if (string.IsNullOrEmpty(tx.State) || state != tx.State) return Fail("state_mismatch");
        if (string.IsNullOrEmpty(tx.CodeVerifier)) return Fail("missing_pkce_verifier");
        if (string.IsNullOrEmpty(tx.TokenEndpoint) || string.IsNullOrEmpty(tx.ClientId) || string.IsNullOrEmpty(tx.WqlBase))
            return Fail("missing_connection_context");

        try
        {
            var tokens = await _oauth.ExchangeCodeForTokensAsync(new ExchangeOptions
            {
                TokenEndpoint = tx.TokenEndpoint!,
                ClientId = tx.ClientId!,
                ClientSecret = tx.ClientSecret,
                RedirectUri = WqlUrls.RedirectUriFromRequest(Request),
                Code = code!,
                CodeVerifier = tx.CodeVerifier!,
                TenantAlias = tx.TenantAlias,
            });

            var decoded = OAuthService.DecodeToken(tokens.access_token!);

            var session = new WorkdaySessionData
            {
                Token = tokens.access_token,
                TokenExp = decoded.Exp ??
                    (tokens.expires_in is int secs
                        ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + secs
                        : null),
                User = decoded.Sub,
                WqlBase = tx.WqlBase,
                TenantName = tx.TenantName,
                BoCompanyId = tx.BoCompanyId,
                ApiBase = tx.ApiBase,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            _store.SaveSession(session);

            // Resolve returnTo against our own origin — no open redirect.
            var returnTo = string.IsNullOrEmpty(tx.ReturnTo) ? "/" : tx.ReturnTo!;
            if (!returnTo.StartsWith('/')) returnTo = "/";
            return Redirect($"{origin}{returnTo}");
        }
        catch (WorkdayAuthException e)
        {
            _log.LogError("[callback] token exchange failed: {Status}\n  detail: {Detail}", e.Status, e.Detail);
            return Fail(e.Message);
        }
        catch (Exception e)
        {
            _log.LogError(e, "[callback] unexpected token-exchange error");
            return Fail(e.Message);
        }
    }

    // POST /api/auth/logout — clears the local session (and the token it holds).
    [HttpPost("logout")]
    public IActionResult LogoutPost()
    {
        _store.DestroySession();
        return Ok(new { ok = true });
    }

    // GET /api/auth/logout — convenience: logout then redirect home.
    [HttpGet("logout")]
    public IActionResult LogoutGet()
    {
        _store.DestroySession();
        return Redirect($"{Request.Scheme}://{Request.Host.Value}/");
    }

    // GET /api/auth/session — lightweight status check. Never returns the token.
    [HttpGet("session")]
    public ActionResult<SessionStatus> Session()
    {
        var s = _store.GetSession();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expired = s.TokenExp is long exp && exp - 30 <= now;
        var authenticated = !string.IsNullOrEmpty(s.Token) && !expired;
        return new SessionStatus
        {
            Authenticated = authenticated,
            User = authenticated ? s.User : null,
            Tenant = authenticated ? s.TenantName : null,
            BoCompanyId = authenticated ? s.BoCompanyId : null,
            ExpiresAt = authenticated && s.TokenExp is long e ? e * 1000 : null,
        };
    }
}
