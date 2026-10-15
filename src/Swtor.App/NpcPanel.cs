using System.Numerics;
using System.Text;
using ImGuiNET;
using Swtor.Assets;

namespace Swtor.App;

/// <summary>
/// The "NPC" tab: every humanoid NPC look of the game, with a filter. A click shows the NPC in the preview
/// (through <see cref="CharacterPanel.ShowNpc"/>). One button exports the NPC to a Jedi Academy model, another exports all NPCs of the list.
/// </summary>
public sealed class NpcPanel(CharacterPanel character, JkaPanel jka)
{
    private const int MaxBatch = 500;

    private NpcCatalog? _catalog;
    private List<NpcLook> _shown = [];
    private string _filter = "", _shownFilter = "\0";
    private bool _namedOnly = true;
    private int _gender; // 0 all, 1 female, 2 male
    private bool _listDirty = true;
    private NpcLook? _selected;
    private Dictionary<string, int> _slugCounts = [];
    private string? _status;

    // Batch export: one NPC at a time, because the preview builds the textures and the export reads them.
    private List<NpcLook>? _batch;
    private int _batchNext, _batchDone, _batchFailed;
    private NpcLook? _batchCurrent;
    private string? _firstFailure;

    /// <summary>Gives the panel its data. <paramref name="catalog"/> is null until the game database is loaded.</summary>
    public void SetData(NpcCatalog? catalog)
    {
        _catalog = catalog;
        _listDirty = true;
        _slugCounts = [];
        if (catalog is null) return;
        foreach (var look in catalog.Looks)
            _slugCounts[Slug(look)] = _slugCounts.GetValueOrDefault(Slug(look)) + 1;
    }

    /// <summary>Shows the first NPC whose name or key contains <paramref name="text"/>. For tests.</summary>
    public void SelectByText(string text) => _pendingText = text;

    private string? _pendingText;

    /// <summary>Shows the chosen NPC again. Call when the tab becomes active.</summary>
    public void Activate()
    {
        if (_selected is not null) character.ShowNpc(_selected);
    }

    public void Draw()
    {
        jka.Poll();
        if (_catalog is null || !character.CanShowNpc)
        {
            ImGui.TextDisabled("Loading the game database...");
            return;
        }
        if (_pendingText is { } text)
        {
            _pendingText = null;
            if (_catalog.Looks.FirstOrDefault(l => l.SearchText.Contains(text.ToLowerInvariant(), StringComparison.Ordinal)) is { } look) Select(look);
        }
        ContinueBatch();

        ImGui.TextWrapped("Humanoid NPCs of the game. Click one to see it. The look comes from the game data: head, hair, colors and equipment.");
        DrawFilter();
        DrawList();
        DrawSelected();
        DrawExport();
    }

    private void DrawFilter()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##npcfilter", "name or key (for example lana, theron, sith_warrior)", ref _filter, 64);
        bool changed = ImGui.Checkbox("Named only", ref _namedOnly);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        changed |= ImGui.Combo("##npcgender", ref _gender, "Any gender\0Female\0Male\0");
        if (changed || _filter != _shownFilter || _listDirty) Refilter();
        ImGui.SameLine();
        ImGui.TextDisabled($"{_shown.Count} of {_catalog!.Looks.Count}");
    }

    private void Refilter()
    {
        _shownFilter = _filter;
        _listDirty = false;
        string filter = _filter.Trim().ToLowerInvariant();
        _shown = _catalog!.Looks.Where(l =>
            (!_namedOnly || l.Names.Count > 0)
            && (_gender == 0 || l.Gender == (_gender == 1 ? 'f' : 'm'))
            && (filter.Length == 0 || l.SearchText.Contains(filter, StringComparison.Ordinal))).ToList();
    }

    // The list is drawn by hand, so only the rows in view cost time.
    private void DrawList()
    {
        ImGui.BeginChild("##npclist", new Vector2(0, 280), true);
        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        float top = ImGui.GetCursorPosY();
        int first = Math.Max(0, (int)(ImGui.GetScrollY() / rowHeight) - 1);
        int last = Math.Min(_shown.Count, first + (int)(ImGui.GetWindowHeight() / rowHeight) + 3);
        for (int i = first; i < last; i++)
        {
            ImGui.SetCursorPos(new Vector2(ImGui.GetStyle().WindowPadding.X, top + i * rowHeight));
            var look = _shown[i];
            if (ImGui.Selectable($"{Label(look)}##{i}", look == _selected) && _batch is null) Select(look);
        }
        ImGui.SetCursorPos(new Vector2(0, top + _shown.Count * rowHeight));
        ImGui.Dummy(Vector2.Zero);
        ImGui.EndChild();
    }

    private static string Label(NpcLook look) =>
        look.Names.Count == 0 ? look.ShortKey : $"{look.Label}  ({look.Bodytype})";

    private void Select(NpcLook look)
    {
        _selected = look;
        _status = null;
        character.ShowNpc(look);
    }

    private void Step(int delta)
    {
        if (_shown.Count == 0 || _batch is not null) return;
        int at = _selected is null ? -1 : _shown.IndexOf(_selected);
        Select(_shown[Math.Clamp(at + delta, 0, _shown.Count - 1)]);
    }

    private void DrawSelected()
    {
        if (ImGui.Button("<##npcprev")) Step(-1);
        ImGui.SameLine();
        if (ImGui.Button(">##npcnext")) Step(1);
        ImGui.SameLine();
        if (_selected is null)
        {
            ImGui.TextDisabled("Click an NPC in the list.");
            return;
        }
        ImGui.TextWrapped(_selected.Label);
        ImGui.TextDisabled(_selected.Key);
        ImGui.Text($"Body {_selected.Bodytype}, {_selected.NpcCount} NPC{(_selected.NpcCount > 1 ? "s" : "")} use this look");
        if (_selected.Names.Count > 1)
        {
            ImGui.TextWrapped("Also: " + string.Join(", ", _selected.Names.Skip(1).Take(8)));
            if (_selected.Names.Count > 9) ImGui.TextDisabled($"and {_selected.Names.Count - 9} more names");
        }
        if (character.LoadError is not null) ImGui.TextColored(new Vector4(1, 0.5f, 0.4f, 1), character.LoadError);
    }

    private void DrawExport()
    {
        ImGui.Separator();
        ImGui.TextDisabled("Export to Jedi Academy (settings of the Jedi Academy tab)");
        ImGui.TextWrapped(jka.PlayersFolder);

        bool batchRunning = _batch is not null;
        string? problem = jka.QuickExportProblem();
        bool canNow = _selected is not null && problem is null && !batchRunning;
        if (!canNow) ImGui.BeginDisabled();
        if (ImGui.Button("Export this NPC", new Vector2(-1, 0)) && _selected is not null)
        {
            if (jka.ExportNew(ModelName(_selected))) _status = $"Exporting {ModelName(_selected)}...";
        }
        if (!canNow) ImGui.EndDisabled();

        bool canBatch = _shown.Count is > 0 and <= MaxBatch && problem is null && !batchRunning;
        if (!canBatch) ImGui.BeginDisabled();
        if (ImGui.Button($"Export the {_shown.Count} NPCs of the list", new Vector2(-1, 0))) StartBatch();
        if (!canBatch) ImGui.EndDisabled();
        if (_shown.Count > MaxBatch) ImGui.TextDisabled($"Filter the list to {MaxBatch} NPCs or fewer to export them all.");
        if (batchRunning)
        {
            ImGui.ProgressBar(_batch!.Count == 0 ? 1 : (float)_batchDone / _batch.Count, new Vector2(-1, 0), $"{_batchDone} / {_batch.Count}");
            if (ImGui.Button("Stop", new Vector2(-1, 0))) StopBatch("Stopped");
        }

        if (!batchRunning && problem is not null && jka.Busy == false) ImGui.TextDisabled(problem);
        if (_status is not null) ImGui.TextWrapped(_status);
        if (!batchRunning && jka.Message is { } message)
            ImGui.TextColored(jka.MessageIsError ? new Vector4(1, 0.5f, 0.4f, 1) : new Vector4(0.5f, 0.9f, 0.5f, 1), message);
    }

    private void StartBatch()
    {
        _batch = [.. _shown];
        _batchNext = _batchDone = _batchFailed = 0;
        _batchCurrent = null;
        _firstFailure = null;
        _status = null;
    }

    private void StopBatch(string title)
    {
        _status = $"{title}: {_batchDone} exported, {_batchFailed} failed.{(_firstFailure is null ? "" : "\nFirst failure: " + _firstFailure)}";
        _batch = null;
        _batchCurrent = null;
        if (_selected is not null) character.ShowNpc(_selected);
    }

    // Called every frame. Collects the result of the last NPC, then builds and exports the next one.
    private void ContinueBatch()
    {
        if (_batch is null || jka.Busy) return;
        if (_batchCurrent is not null)
        {
            if (jka.MessageIsError)
            {
                _batchFailed++;
                _firstFailure ??= $"{ModelName(_batchCurrent)}: {jka.Message}";
            }
            else
            {
                _batchDone++;
            }
            _batchCurrent = null;
        }
        while (_batchNext < _batch.Count)
        {
            var look = _batch[_batchNext++];
            if (!character.ShowNpc(look) || jka.QuickExportProblem() is not null || !jka.ExportNew(ModelName(look)))
            {
                _batchFailed++;
                _firstFailure ??= $"{ModelName(look)}: nothing to export";
                continue;
            }
            _batchCurrent = look;
            return;
        }
        StopBatch("Done");
    }

    // Folder name of the model: the NPC name without accents or spaces, plus a short code when two looks have the same name.
    private string ModelName(NpcLook look)
    {
        string slug = Slug(look);
        if (_slugCounts.GetValueOrDefault(slug) <= 1) return slug;
        uint hash = 2166136261;
        foreach (char c in look.Key) hash = (hash ^ c) * 16777619;
        return $"{slug}_{hash & 0xFFFF:x4}";
    }

    private static string Slug(NpcLook look)
    {
        string text = look.Names.Count > 0 ? look.Label : look.ShortKey[(look.ShortKey.LastIndexOf('.') + 1)..];
        var builder = new StringBuilder();
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(c);
            else if (c is >= 'A' and <= 'Z') builder.Append(char.ToLowerInvariant(c));
            else if (builder.Length > 0 && builder[^1] != '_') builder.Append('_');
            if (builder.Length >= 20) break;
        }
        string slug = builder.ToString().Trim('_');
        return slug.Length == 0 ? "npc" : slug;
    }
}
