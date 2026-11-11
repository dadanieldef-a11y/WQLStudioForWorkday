using System.Text.RegularExpressions;

namespace ExtendExplorer.Server.Services;

// Port of lib/wcp-app.ts. WCP Extend App REST helpers.
//
// Custom business objects embed the tenant's "BO company id" between the app
// name and the resource:
//   {app}_{companyId}_{resource}
//   e.g. acme_abcdef_loanProducts  (companyId = abcdef)
//
// The Extend App REST endpoint for such a BO is:
//   {apiBase}/apps/{app}_{companyId}/v1/{resource}
//
// WQL is read-only; this REST API is how custom BO rows are created, updated,
// and deleted. Only custom BOs (those carrying the _{companyId}_ marker) are
// writable — delivered/standard BOs are not.
public static class WcpApp
{
    public static string NormalizeApiBase(string raw) =>
        Regex.Replace((raw ?? "").Trim(), @"/+$", "");

    // A BO belongs to this tenant's custom apps when its name carries the
    // _{companyId}_ marker. Delivered BOs won't.
    public static bool IsCustomBo(string boName, string? boCompanyId)
    {
        if (string.IsNullOrEmpty(boCompanyId)) return false;
        return boName.Contains($"_{boCompanyId}_");
    }

    // Split a custom BO name into its app segment (up to and including the
    // company id) and its resource segment, or null if it isn't a custom BO.
    public static (string App, string Resource)? SplitCustomBo(string boName, string? boCompanyId)
    {
        if (string.IsNullOrEmpty(boCompanyId)) return null;
        var marker = $"_{boCompanyId}_";
        var i = boName.IndexOf(marker, StringComparison.Ordinal);
        if (i == -1) return null;
        var app = boName.Substring(0, i + 1 + boCompanyId.Length); // {app}_{companyId}
        var resource = boName.Substring(i + marker.Length);        // {resource}
        if (string.IsNullOrEmpty(app) || string.IsNullOrEmpty(resource)) return null;
        return (app, resource);
    }

    // Build the WCP Extend App REST URL for a custom BO, or null if the name
    // doesn't carry the company-id marker (i.e. it isn't a writable custom BO).
    public static string? WcpAppUrl(string? apiBase, string? boCompanyId, string boName)
    {
        if (string.IsNullOrEmpty(apiBase)) return null;
        var parts = SplitCustomBo(boName, boCompanyId);
        if (parts is null) return null;
        return $"{NormalizeApiBase(apiBase)}/apps/{parts.Value.App}/v1/{parts.Value.Resource}";
    }
}
