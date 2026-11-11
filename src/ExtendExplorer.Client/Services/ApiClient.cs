using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace ExtendExplorer.Client.Services;

// Thrown when a request comes back 401: the session is gone/expired. Callers
// that use the auto-redirecting helpers catch this and stop quietly — the
// browser is already navigating back to the Connect screen at "/". Mirrors the
// original's `throw new Error("redirecting_to_login")` sentinel.
public sealed class RedirectingToLoginException : Exception
{
    public RedirectingToLoginException() : base("redirecting_to_login") { }
}

// The client's HTTP layer. Same-origin calls to /api/* carry the httpOnly
// session cookie automatically, exactly like the original React app's fetch()
// calls — the browser (and this WASM client) never sees the Workday token.
public sealed class ApiClient
{
    private readonly HttpClient _http;
    private readonly NavigationManager _nav;

    public static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web);

    public ApiClient(HttpClient http, NavigationManager nav)
    {
        _http = http;
        _nav = nav;
    }

    public HttpClient Http => _http;

    // Session gone or expired — return to the Connect screen. Login now needs
    // the browser's saved connection, so we can't deep-link a login route; the
    // gate at "/" handles reconnection.
    public void RedirectToLogin() => _nav.NavigateTo("/", forceLoad: true);

    // GET + deserialize, auto-redirecting on 401 (metadata fetchers use this).
    public async Task<T?> GetJsonAsync<T>(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Accept", "application/json");
        using var res = await _http.SendAsync(req);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            RedirectToLogin();
            throw new RedirectingToLoginException();
        }
        if (!res.IsSuccessStatusCode)
        {
            var detail = await SafeBodyAsync(res);
            throw new Exception($"Request failed ({(int)res.StatusCode}) {detail}");
        }
        return await res.Content.ReadFromJsonAsync<T>(JsonOpts);
    }

    // Raw GET returning status + body, WITHOUT auto-redirect. Dialogs use this
    // and handle 401 themselves (to match the original per-call behaviour).
    public async Task<(int Status, string Body)> GetRawAsync(string url)
    {
        using var res = await _http.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        return ((int)res.StatusCode, body);
    }

    // Raw POST of a JSON payload returning status + body, WITHOUT auto-redirect.
    public async Task<(int Status, string Body)> PostRawAsync(string url, object payload)
    {
        using var res = await _http.PostAsJsonAsync(url, payload, JsonOpts);
        var body = await res.Content.ReadAsStringAsync();
        return ((int)res.StatusCode, body);
    }

    private static async Task<string> SafeBodyAsync(HttpResponseMessage res)
    {
        try { return await res.Content.ReadAsStringAsync(); }
        catch { return ""; }
    }
}
