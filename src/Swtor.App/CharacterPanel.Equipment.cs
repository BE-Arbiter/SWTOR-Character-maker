using System.Numerics;
using ImGuiNET;
using Microsoft.Xna.Framework.Graphics;
using Swtor.Assets;
using Swtor.Formats;
using Swtor.Formats.Gr2;
using Swtor.Formats.Xml;

namespace Swtor.App;

// Body and equipment: bare body parts by default, replaced by equipment assets that the user picks.
public sealed partial class CharacterPanel
{
    private sealed class EquipChoice(AppearanceAsset asset)
    {
        public AppearanceAsset Asset { get; } = asset;
        public int Variant { get; set; }
        public string? SchemeId { get; set; }
    }

    // Slots that can hold equipment. The first four show a bare body part when empty.
    private static readonly string[] EquipSlots = ["chest", "hand", "leg", "boot", "waist", "bracer"];
    private static readonly string[] NakedSlots = ["chest", "hand", "leg", "boot"];
    private const int MaxListed = 400;

    private readonly Dictionary<string, EquipChoice> _equipment = [];
    private readonly Dictionary<string, string> _equipFilter = [];
    private string? _bodytype;

    // Adds the bare body or the equipment of each slot to the preview.
    private void AddBodyAndEquipment(char gender, string? bodytype, string? headName)
    {
        if (_index is null || _spec is null) return;
        _bodytype = bodytype;
        ApplyStartupEquipment();
        var skin = TintFor(AppearanceSlot.Head);

        foreach (string slot in EquipSlots)
        {
            ResolvedPart? part;
            Vector4? tint = null;
            Palette? primary = null, secondary = null;
            if (_equipment.TryGetValue(slot, out var choice))
            {
                var variant = choice.Asset.Materials.Count > 0 ? choice.Asset.Materials[Math.Min(choice.Variant, choice.Asset.Materials.Count - 1)] : null;
                part = PartResolver.ResolveAsset(_index, choice.Asset, variant?.Id, gender, bodytype);
                (primary, secondary) = SchemePalettes(choice.SchemeId, slot);
            }
            else if (NakedSlots.Contains(slot)
                && _index.Appearances.FindAsset(slot, NakedBody.AssetName(slot, _spec.Race, headName)) is { } naked)
            {
                part = PartResolver.ResolveAsset(_index, naked, null, gender, bodytype);
                tint = skin;
            }
            else
            {
                continue;
            }
            if (part is null) continue;

            var texture = LoadTexture(part, tint, primary, secondary);
            AddModel(part.ModelPath, texture, slot);
            foreach (string attachment in part.Attachments) AddModel(attachment, texture, slot);
        }
    }

    /// <summary>Equips assets by art name before the first display. Used for start-up arguments such as "--equip chest=chest_armor01_heavy_bh_a02".</summary>
    public void SetStartupEquipment(IEnumerable<(string Slot, string ArtName)> items)
    {
        _startupEquipment = items.ToList();
    }

    private List<(string Slot, string ArtName)>? _startupEquipment;

    private void ApplyStartupEquipment()
    {
        if (_startupEquipment is null || _index is null) return;
        foreach (var (slot, name) in _startupEquipment)
            if (_index.Appearances.FindAsset(slot, name) is { } asset) _equipment[slot] = new EquipChoice(asset);
        _startupEquipment = null;
    }

    private void AddModel(string modelPath, Texture2D? texture, string label)
    {
        try
        {
            _preview.Add(Gr2Reader.Parse(File.ReadAllBytes(_index!.FullPath(modelPath))), texture);
        }
        catch (Exception e) when (e is GameFormatException or IOException)
        {
            _error = $"{label}: {e.Message}";
        }
    }

    // The two palettes that a color scheme gives for a slot. Null when no scheme is chosen.
    private (Palette?, Palette?) SchemePalettes(string? schemeId, string slot)
    {
        if (schemeId is null || _index is null) return (null, null);
        var colors = _index.Colors;
        if (colors.FindScheme(schemeId) is not { } scheme || !scheme.Slots.TryGetValue(slot, out var palettes)) return (null, null);
        return (colors.ReadPalette(palettes.Primary), colors.ReadPalette(palettes.Secondary));
    }

}
