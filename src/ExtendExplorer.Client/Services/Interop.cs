using ExtendExplorer.Shared;
using Microsoft.JSInterop;

namespace ExtendExplorer.Client.Services;

// Thin C# wrapper over wwwroot/js/interop.js and wwwroot/js/monaco-wql.js.
// Everything the browser-only parts of the original React app did through DOM
// APIs (localStorage, window.confirm, a CSV blob download) and the Monaco
// editor lives behind these calls.
public sealed class Interop
{
    private readonly IJSRuntime _js;

    public Interop(IJSRuntime js) => _js = js;

    // ── localStorage (connections + tree width) ──────────────────────────
    public async ValueTask<string?> LocalGetAsync(string key)
    {
        try { return await _js.InvokeAsync<string?>("appInterop.localGet", key); }
        catch { return null; }
    }

    public async ValueTask LocalSetAsync(string key, string value)
    {
        try { await _js.InvokeVoidAsync("appInterop.localSet", key, value); }
        catch { /* storage disabled / quota — best effort */ }
    }

    public async ValueTask LocalRemoveAsync(string key)
    {
        try { await _js.InvokeVoidAsync("appInterop.localRemove", key); }
        catch { /* ignore */ }
    }

    public ValueTask<bool> ConfirmAsync(string message) =>
        _js.InvokeAsync<bool>("appInterop.confirm", message);

    public async ValueTask<int> InnerWidthAsync()
    {
        try { return await _js.InvokeAsync<int>("appInterop.innerWidth"); }
        catch { return 1024; }
    }

    public ValueTask DownloadCsvAsync(string filename, string csv) =>
        _js.InvokeVoidAsync("appInterop.downloadCsv", filename, csv);

    public ValueTask DownloadJsonAsync(string filename, string json) =>
        _js.InvokeVoidAsync("appInterop.downloadText", filename, json, "application/json");

    public ValueTask DownloadTextAsync(string filename, string text) =>
        _js.InvokeVoidAsync("appInterop.downloadText", filename, text, "text/plain;charset=utf-8");

    // Native "Save As" dialog (choose name/location, overwrite). Returns false if
    // the user cancelled; falls back to a download where unsupported.
    public ValueTask<bool> SaveTextAsAsync(string suggestedName, string text) =>
        _js.InvokeAsync<bool>("appInterop.saveTextAs", suggestedName, text, "text/plain;charset=utf-8");

    // ── Streaming file export (wwwroot/js/interop.js: exportOpen/Write/Close) ──
    // Open a file to stream an export into. Shows the browser's Save dialog
    // where supported (otherwise the parts are collected and downloaded on
    // close). Returns a handle id, or null if the user cancelled the dialog.
    public ValueTask<string?> ExportOpenAsync(string suggestedName, string description, string mime, string extension) =>
        _js.InvokeAsync<string?>("appInterop.exportOpen", suggestedName, description, mime, extension);

    public ValueTask ExportWriteAsync(string id, string text) =>
        _js.InvokeVoidAsync("appInterop.exportWrite", id, text);

    // Finish the file (this is what actually commits it to disk).
    public ValueTask ExportCloseAsync(string id) =>
        _js.InvokeVoidAsync("appInterop.exportClose", id);

    // Discard an unfinished export; nothing is saved.
    public async ValueTask ExportAbortAsync(string id)
    {
        try { await _js.InvokeVoidAsync("appInterop.exportAbort", id); }
        catch { /* best effort */ }
    }

    public ValueTask FocusByIdAsync(string id) =>
        _js.InvokeVoidAsync("appInterop.focusById", id);

    public async ValueTask SetIndeterminateAsync(string id, bool value)
    {
        try { await _js.InvokeVoidAsync("appInterop.setIndeterminate", id, value); }
        catch { /* element may not be mounted yet */ }
    }

    // ── Monaco WQL editor (wwwroot/js/monaco-wql.js) ─────────────────────
    // Returns true when Monaco loaded and the editor mounted; the caller falls
    // back to a plain <textarea> if this throws or returns false.
    public ValueTask<bool> EditorCreateAsync<T>(
        string elementId, string initialValue, DotNetObjectReference<T> dotnetRef)
        where T : class =>
        _js.InvokeAsync<bool>("wqlEditor.create", elementId, initialValue, dotnetRef);

    public ValueTask<string> EditorGetValueAsync(string elementId) =>
        _js.InvokeAsync<string>("wqlEditor.getValue", elementId);

    // What Run should execute (selection, or the statement around the cursor),
    // with the offsets of the query text inside it. null if no editor.
    public ValueTask<EditorRunText?> EditorGetRunTextAsync(string elementId) =>
        _js.InvokeAsync<EditorRunText?>("wqlEditor.getRunText", elementId);

    // Add text as a new statement after the one the cursor is in (or fill an
    // empty editor), placing the cursor `cursor` characters into it.
    public ValueTask<int> EditorInsertStatementAsync(string elementId, string value, int cursor) =>
        _js.InvokeAsync<int>("wqlEditor.insertStatement", elementId, value, cursor);

    // Replace the text between two offsets as one undoable edit.
    public ValueTask EditorReplaceRangeAsync(string elementId, int start, int end, string value) =>
        _js.InvokeVoidAsync("wqlEditor.replaceRange", elementId, start, end, value);

    public ValueTask EditorSetValueAsync(string elementId, string value) =>
        _js.InvokeVoidAsync("wqlEditor.setValue", elementId, value);

    public ValueTask EditorSetPositionAsync(string elementId, int line, int column) =>
        _js.InvokeVoidAsync("wqlEditor.setPosition", elementId, line, column);

    public ValueTask EditorInsertAtCursorAsync(string elementId, string text) =>
        _js.InvokeVoidAsync("wqlEditor.insertAtCursor", elementId, text);

    public ValueTask EditorFocusAsync(string elementId) =>
        _js.InvokeVoidAsync("wqlEditor.focus", elementId);

    public ValueTask EditorDisposeAsync(string elementId) =>
        _js.InvokeVoidAsync("wqlEditor.dispose", elementId);

    // Prime the editor's completion caches with data the app already loaded, so
    // autocomplete is instant instead of re-fetching. Best-effort: the editor
    // JS may not be present yet (or Monaco not loaded), which is fine.
    public async ValueTask PrimeDataSourcesAsync(IEnumerable<DataSourceDto> dataSources)
    {
        try { await _js.InvokeVoidAsync("wqlEditor.setDataSources", dataSources); }
        catch { /* editor JS not ready */ }
    }

    public async ValueTask PrimeFieldsAsync(string dataSourceId, IEnumerable<FieldDto> fields)
    {
        try { await _js.InvokeVoidAsync("wqlEditor.setFields", dataSourceId, fields); }
        catch { /* editor JS not ready */ }
    }
}

// What the editor says Run should execute. Text is the full statement (with any
// header comment and the ending ";"); CodeStart/CodeEnd are the offsets of just
// the query, which is what a column sort or drill-down rewrites in place.
public sealed class EditorRunText
{
    public string Text { get; set; } = "";
    public int CodeStart { get; set; }
    public int CodeEnd { get; set; }
}
