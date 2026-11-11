using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ExtendExplorer.Server.Services;

public sealed class NotAuthenticatedException : Exception
{
    public NotAuthenticatedException(string message = "Not authenticated") : base(message) { }
}

// Port of lib/workday/client.ts. Authenticated call to any WQL/metadata/App URL.
// Attaches the session's access token as the Bearer. Refresh tokens aren't
// used, so an expired token or a 401 surfaces as NotAuthenticatedException →
// the frontend redirects to the Connect screen.
public sealed class WorkdayClient
{
    public const string HttpClientName = "workday";

    private readonly IHttpClientFactory _factory;
    public WorkdayClient(IHttpClientFactory factory) => _factory = factory;

    public async Task<HttpResponseMessage> FetchAsync(
        WorkdaySessionData session,
        string url,
        HttpMethod method,
        string? contentType = null,
        string? body = null)
    {
        if (string.IsNullOrEmpty(session.Token)) throw new NotAuthenticatedException();
        if (WorkdaySessionStore.TokenIsExpired(session)) throw new NotAuthenticatedException("token_expired");

        using var req = new HttpRequestMessage(method, url);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        if (body is not null)
        {
            req.Content = new StringContent(body, Encoding.UTF8,
                contentType ?? "application/json");
        }

        var client = _factory.CreateClient(HttpClientName);
        var res = await client.SendAsync(req);

        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            res.Dispose();
            throw new NotAuthenticatedException("workday_401");
        }
        return res;
    }
}
