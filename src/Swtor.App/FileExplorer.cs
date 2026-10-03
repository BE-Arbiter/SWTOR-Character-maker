using System.Numerics;
using ImGuiNET;

namespace Swtor.App;

/// <summary>
/// Lazy file tree drawn with ImGui. The visible rows are kept in one flat list,
/// so only the rows in view are drawn, even for folders with 30,000 files.
/// </summary>
public sealed class FileExplorer
{
    private sealed record Row(string Path, string Name, bool IsDir, int Depth)
    {
        public bool Expanded;
    }

    private readonly List<Row> _rows = [];
    private List<Row> _shown = [];
    private string _filter = "";
    private string _lastFilter = "";
    private string? _selected;

    public string? Root { get; private set; }

    /// <summary>Raised when the user selects a file (single click).</summary>
    public event Action<string>? FileSelected;

    /// <summary>Raised when the user opens a file (double click).</summary>
    public event Action<string>? FileOpened;

    /// <summary>Replaces the tree with the content of <paramref name="root"/>. Returns false if the folder is missing.</summary>
    public bool SetRoot(string root)
    {
        if (!Directory.Exists(root)) return false;
        Root = root;
        _rows.Clear();
        _selected = null;
        AddChildren(0, root, 0);
        Refilter();
        return true;
    }

    /// <summary>Draws the tree inside the current ImGui window.</summary>
    public void Draw()
    {
        if (Root is null)
        {
            ImGui.TextWrapped("No asset folder. Use File > Open asset folder.");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter files...", ref _filter, 128);
        if (_filter != _lastFilter) Refilter();

        ImGui.BeginChild("##tree", Vector2.Zero, false);
        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        float top = ImGui.GetCursorPosY();
        int first = Math.Max(0, (int)(ImGui.GetScrollY() / rowHeight) - 1);
        int last = Math.Min(_shown.Count, first + (int)(ImGui.GetWindowHeight() / rowHeight) + 3);

        for (int i = first; i < last; i++)
        {
            var row = _shown[i];
            ImGui.SetCursorPos(new Vector2(ImGui.GetStyle().WindowPadding.X + row.Depth * 14, top + i * rowHeight));
            string label = row.IsDir ? (row.Expanded ? "[-] " : "[+] ") + row.Name : row.Name;
            if (ImGui.Selectable($"{label}##{i}", row.Path == _selected, ImGuiSelectableFlags.AllowDoubleClick))
            {
                if (row.IsDir)
                {
                    Toggle(row);
                    break; // The row list changed. Draw it again on the next frame.
                }
                _selected = row.Path;
                FileSelected?.Invoke(row.Path);
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) FileOpened?.Invoke(row.Path);
            }
        }

        // Set the full height so the scroll bar matches the whole list.
        ImGui.SetCursorPos(new Vector2(0, top + _shown.Count * rowHeight));
        ImGui.Dummy(Vector2.Zero);
        ImGui.EndChild();
    }

    private void Toggle(Row row)
    {
        int index = _rows.IndexOf(row);
        if (row.Expanded)
        {
            int end = index + 1;
            while (end < _rows.Count && _rows[end].Depth > row.Depth) end++;
            _rows.RemoveRange(index + 1, end - index - 1);
            row.Expanded = false;
        }
        else
        {
            AddChildren(index + 1, row.Path, row.Depth + 1);
            row.Expanded = true;
        }
        Refilter();
    }

    // Inserts the folders first, then the files, both sorted by name.
    private void AddChildren(int at, string dir, int depth)
    {
        var children = new List<Row>();
        try
        {
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                children.Add(new Row(entry.FullName, entry.Name, entry is DirectoryInfo, depth));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        children.Sort((a, b) => a.IsDir != b.IsDir ? (a.IsDir ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _rows.InsertRange(at, children);
    }

    private void Refilter()
    {
        _lastFilter = _filter;
        _shown = _filter.Length == 0
            ? _rows
            : _rows.Where(r => r.IsDir || r.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
