using System.Numerics;
using ImGuiNET;
using Swtor.Assets;

namespace Swtor.App;

/// <summary>
/// Tree of the models in the asset index, drawn with ImGui. Folders without a model are not shown.
/// The visible rows are kept in one flat list, so only the rows in view are drawn,
/// even for folders with 30,000 files.
/// </summary>
public sealed class FileExplorer
{
    private sealed class Node(string name, string path)
    {
        public string Name { get; } = name;

        /// <summary>Path relative to the asset root, '/' separators.</summary>
        public string Path { get; } = path;
        public SortedDictionary<string, Node>? Children { get; set; }
        public bool IsDir => Children is not null;
    }

    private sealed record Row(Node Node, int Depth)
    {
        public bool Expanded;
    }

    private readonly List<Row> _rows = [];
    private List<string> _matches = [];
    private Node? _tree;
    private string _filter = "";
    private string _lastFilter = "";
    private string? _selected;

    /// <summary>Raised when the user selects a model (single click). The argument is a relative path.</summary>
    public event Action<string>? FileSelected;

    /// <summary>Raised when the user opens a model (double click).</summary>
    public event Action<string>? FileOpened;

    private AssetIndex? _index;

    /// <summary>Shown instead of the tree while the index is not ready.</summary>
    public string? Status { get; set; }

    /// <summary>Builds the tree from the models of <paramref name="index"/>.</summary>
    public void SetIndex(AssetIndex index)
    {
        _index = index;
        _tree = new Node("", "") { Children = new(StringComparer.OrdinalIgnoreCase) };
        foreach (var model in index.Models)
        {
            var node = _tree;
            string[] parts = model.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                bool isFile = i == parts.Length - 1;
                if (!node.Children!.TryGetValue(parts[i], out var child))
                {
                    child = new Node(parts[i], string.Join('/', parts[..(i + 1)])) { Children = isFile ? null : new(StringComparer.OrdinalIgnoreCase) };
                    node.Children[parts[i]] = child;
                }
                node = child;
            }
        }
        _rows.Clear();
        _selected = null;
        AddChildren(0, _tree, 0);
        Status = null;
        _lastFilter = "\0"; // Forces the filter to refresh.
    }

    /// <summary>Draws the tree inside the current ImGui window.</summary>
    public void Draw()
    {
        if (_index is null)
        {
            ImGui.TextWrapped(Status ?? "No asset folder. Use File > Open asset folder.");
            return;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", $"Filter {_index.Models.Count} models...", ref _filter, 128);
        if (_filter != _lastFilter) Refilter();

        ImGui.BeginChild("##tree", Vector2.Zero, false);
        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        float top = ImGui.GetCursorPosY();
        int first = Math.Max(0, (int)(ImGui.GetScrollY() / rowHeight) - 1);
        int count = _filter.Length == 0 ? _rows.Count : _matches.Count;
        int last = Math.Min(count, first + (int)(ImGui.GetWindowHeight() / rowHeight) + 3);

        for (int i = first; i < last; i++)
        {
            float x = ImGui.GetStyle().WindowPadding.X;
            if (_filter.Length == 0)
            {
                var row = _rows[i];
                ImGui.SetCursorPos(new Vector2(x + row.Depth * 14, top + i * rowHeight));
                string label = row.Node.IsDir ? (row.Expanded ? "[-] " : "[+] ") + row.Node.Name : row.Node.Name;
                if (ImGui.Selectable($"{label}##{i}", row.Node.Path == _selected, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    if (row.Node.IsDir)
                    {
                        Toggle(row);
                        break; // The row list changed. Draw it again on the next frame.
                    }
                    Choose(row.Node.Path);
                }
            }
            else
            {
                ImGui.SetCursorPos(new Vector2(x, top + i * rowHeight));
                if (ImGui.Selectable($"{_matches[i]}##{i}", _matches[i] == _selected, ImGuiSelectableFlags.AllowDoubleClick))
                    Choose(_matches[i]);
            }
        }

        // Set the full height so the scroll bar matches the whole list.
        ImGui.SetCursorPos(new Vector2(0, top + count * rowHeight));
        ImGui.Dummy(Vector2.Zero);
        ImGui.EndChild();
    }

    private void Choose(string path)
    {
        _selected = path;
        FileSelected?.Invoke(path);
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) FileOpened?.Invoke(path);
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
            AddChildren(index + 1, row.Node, row.Depth + 1);
            row.Expanded = true;
        }
    }

    // Inserts the folders first, then the files, both sorted by name.
    private void AddChildren(int at, Node parent, int depth)
    {
        var ordered = parent.Children!.Values.OrderBy(n => !n.IsDir).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
        _rows.InsertRange(at, ordered.Select(n => new Row(n, depth)));
    }

    private void Refilter()
    {
        _lastFilter = _filter;
        _matches = _filter.Length == 0 || _index is null
            ? []
            : _index.Models.Where(m => m.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
