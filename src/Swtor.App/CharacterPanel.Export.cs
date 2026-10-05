using Microsoft.Xna.Framework.Graphics;
using Swtor.Assets;
using Swtor.Formats.Dds;
using Swtor.Formats.Gr2;

namespace Swtor.App;

// The parts of the character as the preview shows them, kept for the Jedi Academy export:
// the models and the final textures (colors, skin color and overlays applied).
public sealed partial class CharacterPanel
{
    private sealed class ExportEntry(Gr2Model model)
    {
        public Gr2Model Model { get; } = model;
        public List<Gr2Model> Attachments { get; } = [];
        public DdsImage? Texture { get; set; }
        public DdsImage? EyeTexture { get; set; }

        // Texture and piece position of the bare skin of an equipment item. Attachments use the same.
        public DdsImage? SkinTexture { get; set; }
        public int SkinIndex { get; set; } = -1;
    }

    private readonly Dictionary<Texture2D, DdsImage> _imageOf = [];
    private readonly Dictionary<string, ExportEntry> _export = [];

    /// <summary>Slot names of the export, in the order that the Jedi Academy panel lists them.</summary>
    public static readonly string[] ExportSlots = ["head", "face", "hair", "facehair", "chest", "hand", "leg", "boot", "waist", "bracer"];

    /// <summary>Slots that the last build of the preview put on the character.</summary>
    public IEnumerable<string> AvailableExportSlots => ExportSlots.Where(_export.ContainsKey);

    /// <summary>The parts of the slots in <paramref name="slots"/> that the character has. The models and images are not changed by the panel, so this is safe to use on another thread.</summary>
    public IReadOnlyList<JkaExportPart> ExportParts(IEnumerable<string> slots)
    {
        var parts = new List<JkaExportPart>();
        foreach (string slot in slots)
        {
            if (!_export.TryGetValue(slot, out var entry)) continue;
            parts.Add(new JkaExportPart
            {
                Slot = slot, Model = entry.Model, Attachments = [.. entry.Attachments],
                Texture = entry.Texture, EyeTexture = entry.EyeTexture, Bodytype = _bodytype,
                SkinTexture = entry.SkinTexture, SkinPieceIndex = entry.SkinTexture is null ? -1 : entry.SkinIndex,
            });
        }
        return parts;
    }

    private void ClearExport()
    {
        _imageOf.Clear();
        _export.Clear();
    }

    private void RecordExport(string slot, Gr2Model model, Texture2D? texture, bool attachment, Texture2D? eyeTexture = null,
        Texture2D? skinTexture = null, int skinIndex = -1)
    {
        DdsImage? ImageOf(Texture2D? t) => t is not null && _imageOf.TryGetValue(t, out var image) ? image : null;
        if (attachment)
        {
            if (_export.TryGetValue(slot, out var main)) main.Attachments.Add(model);
            return;
        }
        _export[slot] = new ExportEntry(model) { Texture = ImageOf(texture), EyeTexture = ImageOf(eyeTexture), SkinTexture = ImageOf(skinTexture), SkinIndex = skinIndex };
    }

    // "Hair" and "FaceHair" are names of appearance slots, the others are names of equipment slots.
    private static string ExportSlot(string label) => label.ToLowerInvariant();
}
