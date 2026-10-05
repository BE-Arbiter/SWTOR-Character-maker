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

        /// <summary>Palette asset ids picked by hand. They replace the palettes of the scheme. Null keeps the scheme (or the original colors).</summary>
        public string? PrimaryId { get; set; }

        public string? SecondaryId { get; set; }

        /// <summary>Custom colors (made with PaletteTint.Custom). They win over <see cref="PrimaryId"/> and <see cref="SecondaryId"/>.</summary>
        public Palette? PrimaryCustom { get; set; }

        public Palette? SecondaryCustom { get; set; }

        /// <summary>True when the last build found a color mask, so the item can be colored. Set by the build.</summary>
        public bool HasMask { get; set; } = true;
    }

    // Slots that can hold equipment. "face" is the helmet (the head gear). The chest, hand, leg and boot slots show a bare body part when empty.
    private static readonly string[] EquipSlots = ["face", "chest", "hand", "leg", "boot", "waist", "bracer"];
    private static readonly string[] NakedSlots = ["chest", "hand", "leg", "boot"];
    private const int MaxListed = 400;

    private readonly Dictionary<string, EquipChoice> _equipment = [];
    private readonly Dictionary<string, string> _equipFilter = [];
    private string? _bodytype;

    // With a helmet or a raised hood, the hair is not shown by default: it would come through.
    private bool _hideHairUnderHelmet = true;

    // Robes have two attachments: "..._hooddown.gr2" (hood on the shoulders) and "..._hoodup.gr2" (hood on the head).
    // Only one is shown. A helmet always gives the hood down.
    private bool _hoodUp;

    private static bool IsHoodUp(string path) => path.Contains("_hoodup", StringComparison.OrdinalIgnoreCase);
    private static bool IsHoodDown(string path) => path.Contains("_hooddown", StringComparison.OrdinalIgnoreCase);
    private static bool HasHood(AppearanceAsset asset) => asset.Attachments.Any(a => IsHoodUp(a) || IsHoodDown(a));

    // True when the hood is on the head: asked by the user, no helmet, and an equipped item has a hood.
    private bool HoodShownUp() =>
        _hoodUp && !_equipment.ContainsKey("face") && _equipment.Values.Any(c => c.Asset.Attachments.Any(IsHoodUp));

    // Adds the bare body or the equipment of each slot to the preview.
    private void AddBodyAndEquipment(char gender, string? bodytype, AppearanceAsset? head)
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
            int skinIndex = -1;
            if (_equipment.TryGetValue(slot, out var choice))
            {
                var variant = choice.Asset.Materials.Count > 0 ? choice.Asset.Materials[Math.Min(choice.Variant, choice.Asset.Materials.Count - 1)] : null;
                part = PartResolver.ResolveAsset(_index, choice.Asset, variant?.Id, gender, bodytype);
                (primary, secondary) = SchemePalettes(choice.SchemeId, slot);
                if (choice.PrimaryId is not null) primary = _index.Colors.ReadPalette(choice.PrimaryId) ?? primary;
                if (choice.SecondaryId is not null) secondary = _index.Colors.ReadPalette(choice.SecondaryId) ?? secondary;
                primary = choice.PrimaryCustom ?? primary;
                secondary = choice.SecondaryCustom ?? secondary;
                skinIndex = choice.Asset.SkinMaterialIndex;
            }
            else if (NakedPart(slot, gender, bodytype, head) is { } naked)
            {
                part = naked;
                tint = skin;
            }
            else
            {
                continue;
            }
            if (part is null) continue;

            if (choice is not null) choice.HasMask = part.MaskPath is not null;
            var texture = LoadTexture(part, tint, primary, secondary);
            // Some equipment shows bare skin: one mesh piece takes the skin of the bare body part of the slot.
            var skinTexture = skinIndex >= 0 && NakedPart(slot, gender, bodytype, head) is { } bare ? LoadTexture(bare, skin) : null;
            var skinPiece = skinTexture is null ? null : new SkinPiece(skinIndex, skinTexture);
            AddModel(part.ModelPath, texture, slot, skinPiece: skinPiece);
            bool hoodUp = HoodShownUp();
            foreach (string attachment in part.Attachments)
            {
                if (hoodUp ? IsHoodDown(attachment) : IsHoodUp(attachment)) continue;
                AddModel(attachment, texture, slot, isAttachment: true, skinPiece: skinPiece);
            }
        }
    }

    // The bare body part of a slot (chest, hand, leg or boot) for the race and the head. Null for other slots.
    // The model comes from the naked asset of the race. The material is the one that the head gives for the slot
    // (a trandoshan head gives a trandoshan body), otherwise the material of the naked asset.
    private ResolvedPart? NakedPart(string slot, char gender, string? bodytype, AppearanceAsset? head)
    {
        if (!NakedSlots.Contains(slot) || _index!.Appearances.FindAsset(slot, NakedBody.AssetName(slot, _spec!.Race, head?.ArtName)) is not { } naked)
            return null;
        if (head?.SkinMaterials is { } skins && skins.TryGetValue(slot, out string? material) && material.Length > 0
            && PartResolver.ResolveWithMaterial(_index, naked, material, gender, bodytype) is { DiffusePath: not null } own)
        {
            return own;
        }
        return PartResolver.ResolveAsset(_index, naked, null, gender, bodytype);
    }

    /// <summary>A mesh piece of an equipment model that shows skin: its position in the piece list (index.xml "SkinMaterialIndex") and its texture.</summary>
    private sealed record SkinPiece(int Index, Texture2D Texture);

    // The preview finds piece textures by material index, but the game counts the skin piece by its position.
    // So the key is the material index of the piece at that position (-1 in the file means "the position of the piece").
    private static Dictionary<int, Texture2D>? SkinPieceTextures(Gr2Model model, SkinPiece skin)
    {
        Dictionary<int, Texture2D>? result = null;
        foreach (var mesh in model.Meshes)
        {
            if (skin.Index >= mesh.Pieces.Count) continue;
            int material = mesh.Pieces[skin.Index].MaterialIndex;
            (result ??= [])[material < 0 ? skin.Index : material] = skin.Texture;
        }
        return result;
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

    private void AddModel(string modelPath, Texture2D? texture, string label, bool isAttachment = false, SkinPiece? skinPiece = null)
    {
        try
        {
            var model = Gr2Reader.Parse(File.ReadAllBytes(_index!.FullPath(modelPath)));
            _preview.Add(model, texture, skinPiece is null ? null : SkinPieceTextures(model, skinPiece));
            RecordExport(ExportSlot(label), model, texture, isAttachment, skinTexture: skinPiece?.Texture, skinIndex: skinPiece?.Index ?? -1);
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
