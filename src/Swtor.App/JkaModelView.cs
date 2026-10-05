using Microsoft.Xna.Framework.Graphics;
using Swtor.Formats;
using Swtor.Formats.Dds;
using Swtor.Formats.Gr2;
using Swtor.Formats.Jka;
using Vec2 = System.Numerics.Vector2;
using Vec3 = System.Numerics.Vector3;

namespace Swtor.App;

/// <summary>Shows a Jedi Academy model (.glm) in the preview. The model is drawn in its bind pose, with the textures of its skin file.</summary>
public static class JkaModelView
{
    /// <summary>
    /// Replaces the content of the preview with the model. The new textures are added to <paramref name="textures"/>,
    /// the caller disposes them. Tag surfaces and hidden surfaces are not drawn. Must run on the game thread.
    /// </summary>
    public static void Load(ModelPreview preview, GraphicsDevice device, string glmPath, List<Texture2D> textures)
    {
        var model = GlmReader.Parse(File.ReadAllBytes(glmPath));
        string folder = Path.GetDirectoryName(Path.GetFullPath(glmPath))!;
        var skin = ReadSkin(folder);
        var byFile = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);

        preview.Clear();
        for (int i = 0; i < model.Surfaces.Count; i++)
        {
            var info = model.Surfaces[i];
            if ((info.Flags & (GlmSurfaceInfo.FlagBolt | GlmSurfaceInfo.FlagOff)) != 0) continue;
            var geometry = model.Lods[0][i];
            if (geometry.Triangles.Length == 0 || geometry.Vertices.Length > ushort.MaxValue) continue;

            string shader = skin.Get(info.Name) ?? info.Shader;
            if (!byFile.TryGetValue(shader, out var texture)) byFile[shader] = texture = LoadTexture(device, folder, shader, textures);
            preview.Add(ToGr2(info.Name, geometry), texture);
        }
        preview.Frame();
    }

    private static SkinFile ReadSkin(string folder)
    {
        string file = Path.Combine(folder, "model_default.skin");
        return File.Exists(file) ? SkinFile.Parse(File.ReadAllText(file)) : new SkinFile();
    }

    // The shader of a skin line is a game path. The file is next to the model, so only its name matters.
    private static Texture2D? LoadTexture(GraphicsDevice device, string folder, string shader, List<Texture2D> textures)
    {
        string stem = Path.GetFileNameWithoutExtension(shader.Replace('/', Path.DirectorySeparatorChar));
        if (stem.Length == 0 || stem.StartsWith('[')) return null;
        try
        {
            foreach (string extension in new[] { ".tga", ".jpg", ".jpeg", ".png" })
            {
                string path = Path.Combine(folder, stem + extension);
                if (!File.Exists(path)) continue;
                Texture2D texture;
                if (extension == ".tga") texture = TextureLoader.Create(device, TgaReader.Decode(File.ReadAllBytes(path)));
                else
                {
                    using var stream = File.OpenRead(path);
                    texture = Texture2D.FromStream(device, stream);
                }
                textures.Add(texture);
                return texture;
            }
        }
        catch (Exception e) when (e is GameFormatException or IOException or InvalidOperationException)
        {
            return null;
        }
        return null;
    }

    // Jedi Academy: x left, y back, z up. The preview: x left, y up, z front. Both are right handed.
    private static Gr2Model ToGr2(string name, GlmSurface surface)
    {
        int count = surface.Vertices.Length;
        var positions = new Vec3[count];
        var normals = new Vec3[count];
        var uvs = new Vec2[count];
        for (int i = 0; i < count; i++)
        {
            var v = surface.Vertices[i];
            positions[i] = new Vec3(v.Position.X, v.Position.Z, -v.Position.Y);
            normals[i] = new Vec3(v.Normal.X, v.Normal.Z, -v.Normal.Y);
            uvs[i] = v.Uv;
        }
        var mesh = new Gr2Mesh
        {
            Name = name, Flags = Gr2VertexFlags.Position, VertexCount = count, Positions = positions, Normals = normals,
            UvSets = [uvs], Indices = surface.Triangles.Select(t => (ushort)t).ToArray(),
            Pieces = [new Gr2Piece(0, surface.Triangles.Length / 3, 0, Vec3.Zero, Vec3.Zero)], Bones = [],
        };
        return new Gr2Model(1, Vec3.Zero, Vec3.Zero, [mesh], ["default"]);
    }
}
