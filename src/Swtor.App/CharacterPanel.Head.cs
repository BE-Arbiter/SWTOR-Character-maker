using System.Numerics;
using Microsoft.Xna.Framework.Graphics;
using Swtor.Assets;
using Swtor.Formats;
using Swtor.Formats.Dds;
using Swtor.Formats.Gr2;

namespace Swtor.App;

// The head has its own rules: skin color, complexion and face paint go on the skin texture, eye color goes on the eye material.
public sealed partial class CharacterPanel
{
    private const int EyeMaterialIndex = 1;

    private void AddHead(ResolvedPart part)
    {
        var pieces = new Dictionary<int, Texture2D>();
        if (part.Overrides is not null && part.Overrides.TryGetValue(EyeMaterialIndex, out var eyes)
            && LoadTexture(eyes.DiffusePath, eyes.MaskPath, EyeTint(), null, null, GlowingEye(eyes)) is { } eyeTexture)
        {
            pieces[EyeMaterialIndex] = eyeTexture;
        }

        var skin = LoadTexture(part.DiffusePath, part.MaskPath, TintFor(AppearanceSlot.Head), null, null,
            image => AddFaceOverlays(image, part.Bodytype));
        try
        {
            _preview.Add(Gr2Reader.Parse(File.ReadAllBytes(_index!.FullPath(part.ModelPath))), skin, pieces.Count > 0 ? pieces : null);
        }
        catch (Exception e) when (e is GameFormatException or IOException)
        {
            _error = $"Head: {e.Message}";
        }
    }

    // An eye without a color mask (Chiss) is a glow: a dark red halo in the file, drawn bright in the game.
    // Without an eye color option the texture is brightened.
    private Func<DdsImage, DdsImage>? GlowingEye(ResolvedMaterial eyes) =>
        eyes.MaskPath is null && EyeTint() is null ? ImageColor.NormalizeBrightness : null;

    // Complexion (eyebrows, blush) multiplies the skin. Face paint is drawn over it with its alpha.
    private DdsImage AddFaceOverlays(DdsImage skin, string? bodytype)
    {
        if (LoadOverlay(AppearanceSlot.Complexion, bodytype) is { } complexion) skin = ImageColor.Multiply(skin, complexion);
        if (LoadOverlay(AppearanceSlot.FacePaint, bodytype) is { } paint) skin = ImageColor.AlphaOver(skin, paint);
        return skin;
    }

    private DdsImage? LoadOverlay(AppearanceSlot slot, string? bodytype)
    {
        if (OptionFor(slot) is not { } option || _index is null) return null;
        if (_index.Appearances.FindAsset(option.AssetId) is not { } found) return null;
        string? path = PartResolver.OverlayPath(_index, found.Asset, bodytype);
        if (path is null) return null;
        try
        {
            return DdsReader.Decode(File.ReadAllBytes(_index.FullPath(path)));
        }
        catch (Exception e) when (e is GameFormatException or IOException)
        {
            _error = $"{Path.GetFileName(path)}: {e.Message}";
            return null;
        }
    }

    private Vector4? EyeTint()
    {
        if (OptionFor(AppearanceSlot.EyeColor) is not { } option) return null;
        string? color = _index?.Appearances.FindAsset(option.AssetId)?.Asset.RepresentativeColor;
        return color is null ? null : ParseColor(color);
    }
}
