using System.Diagnostics;
using System.Text.Json;
using ExtendExplorer.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Local desktop hosting ───────────────────────────────────────────────────
// The published, self-contained exe should behave like a double-click desktop
// app, so bind a fixed http port on localhost. Port 3000 is mandatory here, not
// just convenient: the Workday OAuth client's registered redirect URI is
// http://localhost:3000/api/auth/callback, and OAuth only returns to that exact
// URL. launchSettings.json pins this during development but is NOT used by a
// published exe, so it must be set here for the packaged build. An explicit
// ASPNETCORE_URLS (or --urls) still wins.
var configuredUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
var launchUrl = string.IsNullOrWhiteSpace(configuredUrls)
    ? "http://localhost:3000"
    : configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
if (string.IsNullOrWhiteSpace(configuredUrls))
    builder.WebHost.UseUrls(launchUrl);

// ── JSON: camelCase over the wire, matching the client + the Monaco JS ──────
builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

// ── Server-side session holds the Workday access token ──────────────────────
// This is the C# analogue of the original's iron-session cookie: the token
// lives on the server; the cookie carries only an opaque session id. The
// browser (and the WASM client) never see the token — it's attached as the
// Bearer only by the API controllers. SameSite=Lax lets the cookie ride the
// OAuth redirect back from Workday.
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "wd_explorer_session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.IdleTimeout = TimeSpan.FromHours(8);
});
builder.Services.AddHttpContextAccessor();

// Named HttpClient for all Workday/WCP calls. We surface upstream status codes
// ourselves (including 401), so auto-redirect is off.
builder.Services.AddHttpClient(WorkdayClient.HttpClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(100);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
});

builder.Services.AddScoped<WorkdaySessionStore>();
builder.Services.AddScoped<OAuthService>();
builder.Services.AddScoped<WorkdayClient>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

// Serve the Blazor WebAssembly client's framework files + static assets.
app.UseBlazorFrameworkFiles();
if (app.Environment.IsDevelopment())
{
    // In dev, tell the browser to revalidate our static assets (js/css) every
    // load, so edits to interop.js / monaco-wql.js / app.css show up on a normal
    // reload instead of being served from a stale cache.
    app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers["Cache-Control"] =
                "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"] = "no-cache";
            ctx.Context.Response.Headers["Expires"] = "0";
        },
    });
}
else
{
    app.UseStaticFiles();
}

app.UseRouting();
app.UseSession();

app.MapControllers();
// Any non-/api route falls through to the SPA shell so client-side routing +
// the post-login redirect to "/" both boot the WASM app.
app.MapFallbackToFile("index.html");

// Once Kestrel is actually listening, pop the tool open in the default browser
// so launching the exe just opens the app. Set WD_EXPLORER_NO_BROWSER=1 to
// suppress (headless / server use). Kestrel also prints the URL to the console,
// so manual open still works if this can't.
app.Lifetime.ApplicationStarted.Register(() =>
{
    if (Environment.GetEnvironmentVariable("WD_EXPLORER_NO_BROWSER") == "1") return;
    try
    {
        var open = launchUrl
            .Replace("0.0.0.0", "localhost")
            .Replace("[::]", "localhost")
            .Replace("+", "localhost");
        Process.Start(new ProcessStartInfo(open) { UseShellExecute = true });
    }
    catch
    {
        // No default browser / headless — ignore; the console shows the URL.
    }
});

app.Run();
