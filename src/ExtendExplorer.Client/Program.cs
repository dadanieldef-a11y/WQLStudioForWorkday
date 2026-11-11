using ExtendExplorer.Client;
using ExtendExplorer.Client.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Same-origin HttpClient: /api/* calls carry the httpOnly session cookie
// automatically, exactly like the original React app's fetch() calls. The
// client never sees the Workday token — it only ever hits our BFF.
builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});

builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<WqlMetadata>();
builder.Services.AddScoped<BoFields>();
builder.Services.AddScoped<Connections>();
builder.Services.AddScoped<Interop>();

await builder.Build().RunAsync();
