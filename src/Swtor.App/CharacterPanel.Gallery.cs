using System.Numerics;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.ImGuiNet;
using Swtor.Assets;
using Swtor.Formats;
using Swtor.Formats.Dds;
using Swtor.Formats.Xml;

namespace Swtor.App;

// The icon gallery: a modal window that lists the items of an equipment slot with their icon and name.
// It uses the same filters as the lists (name, weight, class, sort).
public sealed partial class CharacterPanel
{
    private const int IconSize = 128;
    private const int MaxCachedIcons = 2000;
    private const int IconsPerFrame = 16;
    private const string GalleryId = "###gallery";

    private ImGuiRenderer? _gui;
    private string? _galleryRequest = Environment.GetEnvironmentVariable("SWTOR_GALLERY");   // slot to open at the next frame (SWTOR_GALLERY opens one at start, for tests)
    private string? _gallerySlot;      // slot of the open window
    private int _iconBudget;
    private readonly Dictionary<long, (Texture2D? Texture, IntPtr Id)> _icons = [];

    /// <summary>Gives the panel the ImGui renderer. Icons are bound to it. Without it the gallery shows no icons.</summary>
    public void SetGui(ImGuiRenderer gui) => _gui = gui;

    private void DrawGallery()
    {
        if (_galleryRequest is { } request)
        {
            _gallerySlot = request;
            _galleryRequest = null;
            ImGui.OpenPopup(GalleryId);
        }
        if (_gallerySlot is not { } slot || _index is null) return;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(Math.Min(1000, viewport.Size.X - 60), Math.Min(740, viewport.Size.Y - 60)), ImGuiCond.Appearing);
        bool open = true;
        if (!ImGui.BeginPopupModal($"{_text.EquipSlot(slot)}{GalleryId}", ref open, ImGuiWindowFlags.NoSavedSettings))
        {
            _gallerySlot = null;
            return;
        }

        _iconBudget = IconsPerFrame;
        if (_icons.Count > MaxCachedIcons) ClearIcons();

        var options = EquipOptions(slot);
        _equipFilter.TryGetValue(slot, out string? filter);
        filter ??= "";
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputTextWithHint("##galleryfilter", "name or art", ref filter, 64)) _equipFilter[slot] = filter;
        ImGui.SameLine();
        DrawSortChoice();
        DrawArmorFilters();

        var shown = FilterEntries(options, filter);
        _equipment.TryGetValue(slot, out var current);
        string none = NakedSlots.Contains(slot) ? "(bare)" : "(none)";
        ImGui.TextDisabled($"{shown.Count} / {options.Count}");

        int picked = -2; // -2 nothing, -1 the empty slot, otherwise a position in the list
        ImGui.BeginChild("##gallerygrid", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), true);
        picked = DrawGrid(shown, current?.Asset, none);
        ImGui.EndChild();

        if (ImGui.Button("Close", new Vector2(120, 0)) || !open) ImGui.CloseCurrentPopup();
        if (picked != -2)
        {
            if (picked < 0) _equipment.Remove(slot);
            else _equipment[slot] = new EquipChoice(shown[picked].Asset);
            ImGui.CloseCurrentPopup();
            Rebuild();
        }
        ImGui.EndPopup();
    }

    // The grid of tiles. Only the rows that show are drawn. The first tile is the empty slot. Returns the clicked tile (see DrawGallery).
    private int DrawGrid(List<EquipEntry> shown, AppearanceAsset? current, string none)
    {
        var spacing = new Vector2(6, 6);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, spacing);
        float tileWidth = IconSize + 16;
        float tileHeight = IconSize + 12 + ImGui.GetTextLineHeight() * 3;
        int columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize) / (tileWidth + spacing.X)));
        int total = shown.Count + 1;
        int rows = (total + columns - 1) / columns;
        float rowStep = tileHeight + spacing.Y;

        int first = Math.Max(0, (int)(ImGui.GetScrollY() / rowStep) - 1);
        int last = Math.Min(rows - 1, (int)((ImGui.GetScrollY() + ImGui.GetWindowHeight()) / rowStep) + 1);
        if (first > 0) ImGui.Dummy(new Vector2(1, first * rowStep - spacing.Y));

        int picked = -2;
        for (int row = first; row <= last; row++)
            for (int column = 0; column < columns; column++)
            {
                int tile = row * columns + column;
                if (tile >= total) break;
                if (column > 0) ImGui.SameLine();
                bool clicked = tile == 0
                    ? DrawTile(0, null, none, none, current is null, tileWidth, tileHeight)
                    : DrawTile(tile, shown[tile - 1], GalleryName(shown[tile - 1]), GalleryTooltip(shown[tile - 1]), shown[tile - 1].Asset == current, tileWidth, tileHeight);
                if (clicked) picked = tile - 1;
            }
        if (last < rows - 1) ImGui.Dummy(new Vector2(1, (rows - 1 - last) * rowStep - spacing.Y));
        ImGui.PopStyleVar();
        return picked;
    }

    private bool DrawTile(int id, EquipEntry? entry, string name, string tooltip, bool selected, float width, float height)
    {
        ImGui.BeginChild($"##tile{id}", new Vector2(width, height), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (selected) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.45f, 0.85f, 1f));
        var size = new Vector2(IconSize, IconSize);
        IntPtr icon = entry is null ? IntPtr.Zero : IconFor(entry);
        bool clicked = icon != IntPtr.Zero
            ? ImGui.ImageButton($"##img{id}", icon, size)
            : ImGui.Button(entry is null ? "-" : "?", size + new Vector2(8, 8));
        if (selected) ImGui.PopStyleColor();
        if (ImGui.IsItemHovered() && tooltip.Length > 0) ImGui.SetTooltip(tooltip);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + IconSize + 8);
        ImGui.TextWrapped(name);
        ImGui.PopTextWrapPos();
        ImGui.EndChild();
        return clicked;
    }

    private static string GalleryName(EquipEntry entry) =>
        entry.Items.Count > 0 ? entry.Items[0].Name : entry.Asset.ArtName;

    private static string GalleryTooltip(EquipEntry entry) =>
        entry.Items.Count > 0 ? $"{ItemsTooltip(entry)}\n[{entry.Asset.ArtName}]" : entry.Asset.ArtName;

    // The icon of an entry as an ImGui texture id, or zero. The icon is the one of the first item that has a file.
    // At most a few icons are read in a frame: the others show a placeholder and come in the next frames.
    private IntPtr IconFor(EquipEntry entry)
    {
        long key = long.Parse(entry.Asset.Id, System.Globalization.CultureInfo.InvariantCulture);
        if (_icons.TryGetValue(key, out var cached)) return cached.Id;
        if (_gui is null || _index is null || _iconBudget <= 0) return IntPtr.Zero;
        _iconBudget--;

        Texture2D? texture = null;
        IntPtr id = IntPtr.Zero;
        foreach (var item in entry.Items.Take(12))
        {
            if (item.IconPath is not { } relative) continue;
            string path = _index.FullPath(relative);
            if (!File.Exists(path)) continue;
            try
            {
                texture = TextureLoader.Create(_device, DdsReader.Decode(File.ReadAllBytes(path)));
                id = _gui.BindTexture(texture);
                break;
            }
            catch (Exception e) when (e is GameFormatException or IOException) { }
        }
        _icons[key] = (texture, id);
        return id;
    }

    private void ClearIcons()
    {
        foreach (var (texture, id) in _icons.Values)
        {
            if (texture is null) continue;
            _gui?.UnbindTexture(id);
            texture.Dispose();
        }
        _icons.Clear();
    }
}
