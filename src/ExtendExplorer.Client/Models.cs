namespace ExtendExplorer.Client;

// Query mode: Select (any BO, read-only) and Export (one custom BO, read-only,
// whole object to a file) vs Insert/Update/Delete (custom BOs only). Mirrors the
// QueryMode union in components/Toolbar.tsx.
public enum QueryMode
{
    Select,
    Insert,
    Update,
    Delete,
    Export,
}

// Header-menu actions in the results grid. Mirrors the ColumnAction union in
// components/ResultsGrid.tsx.
public enum ColumnAction
{
    SortAsc,
    SortDesc,
    Contains,
    Blank,
    NotBlank,
    // Boolean columns filter with `= true` / `= false` (WQL uses `=` for
    // booleans) rather than contains/blank, which don't apply to them.
    BoolEquals,
    // Numeric / date columns filter with a comparison operator; the action's
    // value carries "<op> <value>", e.g. ">= 5000".
    Compare,
}

public static class QueryModeExtensions
{
    // "select" | "insert" | "update" | "delete" — the lowercase wire/label form.
    public static string ToWire(this QueryMode m) => m.ToString().ToLowerInvariant();

    // "Select" | "Insert" | … — the button label form. Export mode also imports
    // rows back from a file it wrote, so it's labelled for both.
    public static string ToLabel(this QueryMode m) => m switch
    {
        QueryMode.Export => "Export/Import",
        _ => m.ToString(),
    };
}
