using System.Globalization;
using System.Numerics;
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

        var pose = TestPose(Environment.GetEnvironmentVariable("SWTOR_POSE"));

        preview.Clear();
        for (int i = 0; i < model.Surfaces.Count; i++)
        {
            var info = model.Surfaces[i];
            if ((info.Flags & (GlmSurfaceInfo.FlagBolt | GlmSurfaceInfo.FlagOff)) != 0) continue;
            var geometry = model.Lods[0][i];
            if (geometry.Triangles.Length == 0 || geometry.Vertices.Length > ushort.MaxValue) continue;

            string shader = skin.Get(info.Name) ?? info.Shader;
            if (!byFile.TryGetValue(shader, out var texture)) byFile[shader] = texture = LoadTexture(device, folder, shader, textures);
            preview.Add(ToGr2(info.Name, geometry, pose), texture);
        }
        preview.Frame();
    }

    /// <summary>
    /// Test pose for skinning checks (env <c>SWTOR_POSE</c>). Input: <c>bone=x,y,z;bone=x,y,z</c>, angles in degrees around the
    /// Jedi Academy axes (x left, y back, z up), each turn around the bind pose origin of the bone. Children follow their parent.
    /// Output: one model space matrix per bone of <see cref="GlaSkeleton.Humanoid"/>, or null when the text is empty.
    /// </summary>
    private static Matrix4x4[]? TestPose(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var skeleton = GlaSkeleton.Humanoid;
        var local = new Matrix4x4[skeleton.Bones.Count];
        Array.Fill(local, Matrix4x4.Identity);
        foreach (string item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split('=');
            int bone = parts.Length == 2 ? skeleton.IndexOf(parts[0]) : -1;
            var angles = parts.Length == 2 ? parts[1].Split(',').Select(a => float.Parse(a, CultureInfo.InvariantCulture) * MathF.PI / 180).ToArray() : [];
            if (bone < 0 || angles.Length != 3) throw new FormatException($"SWTOR_POSE: bad item \"{item}\" (expected bone=x,y,z).");
            var origin = skeleton.Bones[bone].Origin;
            local[bone] = Matrix4x4.CreateTranslation(-origin)
                * Matrix4x4.CreateRotationX(angles[0]) * Matrix4x4.CreateRotationY(angles[1]) * Matrix4x4.CreateRotationZ(angles[2])
                * Matrix4x4.CreateTranslation(origin);
        }
        // Parents come before their children in the bone list, except the face bones (parent "face", the last bone).
        var world = new Matrix4x4[local.Length];
        var done = new bool[local.Length];
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < local.Length; i++)
            {
                int parent = skeleton.Bones[i].Parent;
                if (done[i] || (parent >= 0 && !done[parent])) continue;
                world[i] = parent < 0 ? local[i] : local[i] * world[parent];
                done[i] = true;
            }
        return world;
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
    private static Gr2Model ToGr2(string name, GlmSurface surface, Matrix4x4[]? pose)
    {
        int count = surface.Vertices.Length;
        var positions = new Vec3[count];
        var normals = new Vec3[count];
        var uvs = new Vec2[count];
        for (int i = 0; i < count; i++)
        {
            var v = surface.Vertices[i];
            var p = v.Position;
            var n = v.Normal;
            if (pose is not null)
            {
                p = Vec3.Zero;
                n = Vec3.Zero;
                foreach (var w in v.Weights)
                {
                    if (w.Bone < 0 || w.Bone >= pose.Length) continue;
                    p += w.Weight * Vec3.Transform(v.Position, pose[w.Bone]);
                    n += w.Weight * Vec3.TransformNormal(v.Normal, pose[w.Bone]);
                }
                if (n.LengthSquared() > 1e-12f) n = Vec3.Normalize(n);
            }
            positions[i] = new Vec3(p.X, p.Z, -p.Y);
            normals[i] = new Vec3(n.X, n.Z, -n.Y);
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
