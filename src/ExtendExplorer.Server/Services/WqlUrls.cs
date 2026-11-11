using System.Text.RegularExpressions;

namespace ExtendExplorer.Server.Services;

// Port of lib/workday/config.ts. Every tenant's WQL endpoints arrive per-request
// from the browser at login and are pinned into the session, so these builders
// take the tenant's wqlBase explicitly rather than reading global config.
public static class WqlUrls
{
    // Accept either the WQL base or a full /data URL; normalise to the base
    // (no trailing slash, no trailing /data).
    public static string NormalizeWqlBase(string raw)
    {
        var trimmed = (raw ?? "").Trim();
        trimmed = Regex.Replace(trimmed, @"/+$", "");
        trimmed = Regex.Replace(trimmed, @"/data$", "", RegexOptions.IgnoreCase);
        return trimmed;
    }

    // This app's own OAuth callback, derived from the incoming request origin so
    // it tracks dev vs prod automatically. Every tenant's API client must
    // register this exact redirect URI.
    public static string RedirectUriFromRequest(HttpRequest req)
    {
        var origin = $"{req.Scheme}://{req.Host.Value}";
        return $"{origin}/api/auth/callback";
    }

    public static string WqlDataUrl(string wqlBase) => $"{wqlBase}/data";
    public static string WqlDataSourcesUrl(string wqlBase) => $"{wqlBase}/dataSources";

    public static string WqlFieldsUrl(string wqlBase, string dataSourceId) =>
        $"{wqlBase}/dataSources/{Uri.EscapeDataString(dataSourceId)}/fields";

    public static string WqlFiltersUrl(string wqlBase, string dataSourceId) =>
        $"{wqlBase}/dataSources/{Uri.EscapeDataString(dataSourceId)}/dataSourceFilters";
}
