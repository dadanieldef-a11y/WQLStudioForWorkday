namespace ExtendExplorer.Client.Services;

// Client-side slice of lib/wcp-app.ts. The Explorer and schema tree only need
// to know which BOs are writable: a BO belongs to this tenant's custom apps
// when its name carries the _{companyId}_ marker (delivered/standard BOs
// don't). The REST URL construction lives server-side (ExtendExplorer.Server).
public static class Wcp
{
    public static bool IsCustomBo(string boName, string? boCompanyId) =>
        !string.IsNullOrEmpty(boCompanyId) && boName.Contains($"_{boCompanyId}_");
}
