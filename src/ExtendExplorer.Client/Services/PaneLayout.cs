namespace ExtendExplorer.Client.Services;

// Resizable workbench layout: the schema-tree width (column divider) and the
// editor height (row divider), with the drag state and localStorage
// persistence. Moved out of Explorer.razor; the component just forwards mouse
// events and reads TreeWidth / EditorHeight when rendering.
public sealed class PaneLayout
{
    private const int MinTree = 180;
    private const int MaxTree = 640;
    private const int MinEditor = 80;
    private const int MaxEditor = 640;
    private const string TreeKey = "wd_explorer_tree_width";
    private const string EditorKey = "wd_explorer_editor_height";

    private readonly Interop _interop;

    public PaneLayout(Interop interop) => _interop = interop;

    public int TreeWidth { get; private set; } = 288;
    public int EditorHeight { get; private set; } = 200;

    public bool DraggingColumn { get; private set; }
    public bool DraggingRow { get; private set; }
    public bool Dragging => DraggingColumn || DraggingRow;

    private double _startPos;
    private int _startSize;

    // Restore the last-used sizes.
    public async Task LoadAsync()
    {
        if (int.TryParse(await _interop.LocalGetAsync(TreeKey), out var w)) TreeWidth = ClampTree(w);
        if (int.TryParse(await _interop.LocalGetAsync(EditorKey), out var h)) EditorHeight = ClampEditor(h);
    }

    public void StartColumn(double clientX)
    {
        DraggingColumn = true;
        _startPos = clientX;
        _startSize = TreeWidth;
    }

    public void StartRow(double clientY)
    {
        DraggingRow = true;
        _startPos = clientY;
        _startSize = EditorHeight;
    }

    // One overlay serves whichever divider is active.
    public void Move(double clientX, double clientY)
    {
        if (DraggingColumn)
            TreeWidth = ClampTree((int)Math.Round(_startSize + (clientX - _startPos)));
        else if (DraggingRow)
            EditorHeight = ClampEditor((int)Math.Round(_startSize + (clientY - _startPos)));
    }

    // Finish the drag and persist the new size.
    public async Task EndAsync()
    {
        if (DraggingColumn)
        {
            DraggingColumn = false;
            await _interop.LocalSetAsync(TreeKey, TreeWidth.ToString());
        }
        else if (DraggingRow)
        {
            DraggingRow = false;
            await _interop.LocalSetAsync(EditorKey, EditorHeight.ToString());
        }
    }

    private static int ClampTree(int px) => Math.Min(MaxTree, Math.Max(MinTree, px));
    private static int ClampEditor(int px) => Math.Min(MaxEditor, Math.Max(MinEditor, px));
}
