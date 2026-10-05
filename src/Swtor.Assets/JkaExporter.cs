using System.Text.Json;
using System.Text.RegularExpressions;
using Swtor.Formats;
using Swtor.Formats.Dds;
using Swtor.Formats.Gr2;
using Swtor.Formats.Jka;

namespace Swtor.Assets;

/// <summary>One part of the character to export: its models and the textures that the preview shows.</summary>
public sealed class JkaExportPart
{
    /// <summary>"head", "face" (helmet), "hair", "facehair", "chest", "hand", "leg", "boot", "waist" or "bracer".</summary>
    public required string Slot { get; init; }

    /// <summary>The main model.</summary>
    public required Gr2Model Model { get; init; }

    /// <summary>More models that share the texture (shoulders, back pieces).</summary>
    public IReadOnlyList<Gr2Model> Attachments { get; init; } = [];

    /// <summary>The diffuse texture with the colors of the character applied.</summary>
    public DdsImage? Texture { get; init; }

    /// <summary>Texture of the eyes (head only). The eye surface uses <see cref="Texture"/> when this is null.</summary>
    public DdsImage? EyeTexture { get; init; }

    /// <summary>Texture of the bare skin that an equipment item shows (the bare body part of the slot, skin color applied), or null.</summary>
    public DdsImage? SkinTexture { get; init; }

    /// <summary>Position of the mesh piece that uses <see cref="SkinTexture"/> in each model of the part, or -1.</summary>
    public int SkinPieceIndex { get; init; } = -1;

    /// <summary>Body type of the part, for example "bma". Chooses the SWTOR skeleton.</summary>
    public string? Bodytype { get; init; }
}

public sealed class JkaExportOptions
{
    /// <summary>Where the models are written: <c>PlayersFolder/name/</c>. Any folder works. To install, use the "players" folder of the game.</summary>
    public required string PlayersFolder { get; init; }

    /// <summary>Prefix of the texture file names. "swtor" gives "swtor_chest.tga".</summary>
    public string TexturePrefix { get; init; } = "";

    /// <summary>The largest side of the textures. Larger images are halved until they fit.</summary>
    public int MaxTextureSize { get; init; } = 1024;

    /// <summary>Vertex budget of the model, 0 for no limit. The default fits the standard game heap (see <see cref="JkaConverter.MaxTotalVertices"/>).</summary>
    public int MaxTotalVertices { get; init; } = JkaConverter.MaxTotalVertices;

    /// <summary>
    /// Name of the sound set written in <c>sounds.cfg</c> of a new model. It must be a sound folder of the game.
    /// The default depends on the gender of the body type.
    /// </summary>
    public string? VoiceSet { get; init; }

    /// <summary>
    /// "Add to model" only: name of the new skin, written as <c>model_&lt;name&gt;.skin</c> (a skin with this name is replaced).
    /// Null or empty gives the first free number (<c>model_1.skin</c>, <c>model_2.skin</c>, ...).
    /// </summary>
    public string? SkinName { get; init; }

    /// <summary>
    /// The choices of the Character tab. When set, they are written next to the model (<see cref="JkaExporter.CharacterFileName"/>),
    /// so that File &gt; Load character can open the character again.
    /// </summary>
    public CharacterSave? Character { get; init; }
}

public sealed record JkaExportResult(string ModelPath, List<string> Surfaces, List<string> Files, List<string> Notes);

/// <summary>
/// Exports the character to a Jedi Academy player model. Needs the SWTOR skeleton of the body type
/// (<c>art/dynamic/spec/bmanew_skeleton.gr2</c> and similar). The Jedi Academy side (skeleton, tags) is embedded: no game file is read.
/// </summary>
public static class JkaExporter
{
    private const string SkeletonFolder = "art/dynamic/spec";

    /// <summary>Written in the folder of every model that this application makes. Tells that the model is ours and how it was made.</summary>
    public const string ManifestName = "swtor_export.json";

    // Sound sets that the sample models use (imperial_hf1 in three of them, new_clones in one).
    private const string DefaultFemaleVoice = "imperial_hf1";
    private const string DefaultMaleVoice = "new_clones";

    /// <summary>Makes <c>PlayersFolder/name/model.glm</c> with a skin file and the textures.</summary>
    public static JkaExportResult CreateNew(AssetIndex index, IReadOnlyList<JkaExportPart> parts, string modelName, JkaExportOptions options)
    {
        if (string.IsNullOrWhiteSpace(modelName) || modelName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The model name is not valid as a folder name.", nameof(modelName));

        string folder = Path.Combine(options.PlayersFolder, modelName);
        string gamePath = $"models/players/{modelName}";
        var template = JkaTemplate.Standard;
        var skeleton = HumanoidFor(template.AnimationName);

        var (drafts, textures) = Convert(index, parts, skeleton, options, gamePath);
        var model = JkaModelBuilder.CreateNew(template, drafts, skeleton, $"{gamePath}/model", gamePath);
        string bodytype = parts.Select(p => p.Bodytype).FirstOrDefault(b => !string.IsNullOrEmpty(b)) ?? "bma";
        var files = textures.ToDictionary(t => t.Key, t => Encode(t.Value, options));
        var result = Save(folder, gamePath, model, drafts, files, [], createSkin: true, voice: VoiceFor(bodytype, options), bodytype: bodytype, character: options.Character is null ? null : CharacterFileName("default"));
        WriteCharacter(folder, options, "default", result.Files, result.Notes);
        return result;
    }

    /// <summary>
    /// Adds the parts to an existing model file. No surface and no texture file of the model is changed:
    /// <list type="bullet">
    /// <item>A part that the model already has (same geometry, same texture) is skipped.</item>
    /// <item>A new surface whose name is taken gets the first free name (<c>torso_2</c>, <c>torso_3</c>, ...).</item>
    /// <item>A texture equal byte for byte to a .tga file of the folder uses that file. Otherwise it gets a free file name.</item>
    /// </list>
    /// The old model file is kept as "model.glm.bak" (only the first time). Every skin file next to the model gets the new surfaces.
    /// </summary>
    public static JkaExportResult AddToModel(AssetIndex index, IReadOnlyList<JkaExportPart> parts, string glmPath, JkaExportOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SkinName) && !IsValidSkinName(options.SkinName.Trim()))
            throw new ArgumentException($"The skin name '{options.SkinName}' is not valid: use letters, digits, '_' or '-', and not \"default\".");
        var model = GlmReader.Parse(File.ReadAllBytes(glmPath));
        string folder = Path.GetDirectoryName(Path.GetFullPath(glmPath))!;
        string gamePath = GamePathOf(folder);

        var skeleton = HumanoidFor(model.AnimationName);

        var (drafts, textures) = Convert(index, parts, skeleton, options, gamePath);
        var (names, files, reused) = PlaceTextures(folder, textures, options);
        drafts = drafts.Select(d => d with { TextureName = names.GetValueOrDefault(d.TextureName, d.TextureName) }).ToList();

        string defaultSkinFile = Path.Combine(folder, "model_default.skin");
        var baseSkin = File.Exists(defaultSkinFile) ? SkinFile.Parse(File.ReadAllText(defaultSkinFile)) : new SkinFile();
        string? TextureOf(int surface)
        {
            string? shader = baseSkin.Get(model.Surfaces[surface].Name) ?? model.Surfaces[surface].Shader;
            return string.IsNullOrEmpty(shader) || shader == OffShader ? null : Path.GetFileNameWithoutExtension(shader.Replace('\\', '/').Split('/')[^1]);
        }
        var originalSurfaces = model.Surfaces.Select(s => s.Name).ToList();
        var shaders = originalSurfaces.Select((_, i) => TextureOf(i)).ToList();
        var merge = JkaModelBuilder.MergeSurfaces(model, drafts, skeleton, gamePath, TextureOf);
        var added = merge.Added;

        // Only the textures that an added surface shows are written.
        var used = added.Select(d => d.TextureName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        files = files.Where(f => used.Contains(f.Key)).ToDictionary(f => f.Key, f => f.Value);
        var notes = new List<string> { $"{merge.Kept.Count} surfaces skipped: the model already has them." };
        notes.AddRange(reused.Where(r => used.Contains(r.Value)).Select(r => $"Texture {r.Key} uses the existing file {r.Value}.tga."));
        string? skinName = string.IsNullOrWhiteSpace(options.SkinName) ? null : options.SkinName.Trim();
        JkaExportResult result;
        if (added.Count == 0)
        {
            // Nothing new. A named skin is still made: it shows this combination of existing surfaces.
            result = new JkaExportResult(glmPath, [], [], [.. notes, "No new surface: the model was not changed."]);
            if (skinName is null) return result;
        }
        else
        {
            string backup = glmPath + ".bak";
            if (!File.Exists(backup)) File.Copy(glmPath, backup);
            result = Save(folder, gamePath, model, added, files, notes, createSkin: false, voice: null, bodytype: null, glmFile: glmPath);
        }

        // The existing skins keep their lines. They only hide the new surfaces: a surface that a skin does not name shows the
        // shader of the .glm file, so the new armor would be drawn over the old one.
        var skinFiles = Directory.GetFiles(folder, "model_*.skin").ToList();
        foreach (string skinFile in skinFiles)
        {
            var skin = SkinFile.Parse(File.ReadAllText(skinFile));
            foreach (var draft in added)
                if (skin.Get(draft.Name) is null) skin.Set(draft.Name, OffShader);
            File.WriteAllText(skinFile, skin.ToString());
            result.Files.Add(skinFile);
        }

        // A new skin "model_N.skin" shows the exported character: a copy of the default skin, with the new and the kept surfaces
        // shown, and the surfaces that they replace hidden. A replaced surface is one that a new surface had to be renamed for,
        // or one whose texture belongs to an exported slot (a texture name is "<prefix><slot>[_eyes|_skin][_N]").
        var slots = parts.Select(p => p.Slot).ToList();
        bool FromExportedSlot(string? texture) => texture is not null
            && slots.Any(s => Regex.IsMatch(texture, $"(^|_){Regex.Escape(s)}(_eyes|_skin)?(_\\d+)?$", RegexOptions.IgnoreCase));
        var shown = merge.Kept.Concat(added.Select(d => d.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newSkin = SkinFile.Parse(baseSkin.ToString());
        for (int i = 0; i < originalSurfaces.Count; i++)
        {
            var info = model.Surfaces[i];
            if ((info.Flags & (GlmSurfaceInfo.FlagBolt | GlmSurfaceInfo.FlagOff)) != 0 || info.Parent < 0 || shown.Contains(info.Name)) continue;
            if (merge.Replaced.Contains(info.Name, StringComparer.OrdinalIgnoreCase) || FromExportedSlot(shaders[i])) newSkin.Set(info.Name, OffShader);
        }
        foreach (string name in merge.Kept)
            if (newSkin.Get(name) == OffShader) newSkin.Set(name, Path.ChangeExtension(model.Surfaces[model.SurfaceIndex(name)].Shader, ".tga"));
        foreach (var (surface, shader) in JkaModelBuilder.SkinLines(added, gamePath, ".tga")) newSkin.Set(surface, shader);

        if (skinName is null)
        {
            int number = 1;
            while (File.Exists(Path.Combine(folder, $"model_{number}.skin"))) number++;
            skinName = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        string newSkinFile = SkinPath(folder, skinName);
        bool replaced = File.Exists(newSkinFile);
        File.WriteAllText(newSkinFile, newSkin.ToString());
        result.Files.Add(newSkinFile);
        result.Notes.Add($"{(replaced ? "Skin replaced" : "New skin")}: {Path.GetFileName(newSkinFile)} (in the game: model {Path.GetFileName(folder)}/{skinName}).");
        result.Notes.AddRange(OrganizeVariants(glmPath));
        WriteCharacter(folder, options, skinName, result.Files, result.Notes);
        return result;
    }

    /// <summary>
    /// Reads a model and its <c>model_*.skin</c> files, puts the surfaces of each skin below a variant of their main surface
    /// (<see cref="JkaVariants.Organize"/>) and writes the files again. Files that do not change are not written.
    /// </summary>
    /// <returns>One line for each change.</returns>
    public static List<string> OrganizeVariants(string glmPath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(glmPath))!;
        var model = GlmReader.Parse(File.ReadAllBytes(glmPath));
        // Oldest skin first: it keeps the first letter after the default skin.
        var skins = Directory.GetFiles(folder, "model_*.skin").OrderBy(File.GetCreationTimeUtc).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => (File: f, Name: Path.GetFileNameWithoutExtension(f)["model_".Length..], Text: File.ReadAllText(f))).ToList();
        var parsed = skins.Select(s => (s.Name, Skin: SkinFile.Parse(s.Text))).ToList();
        var log = JkaVariants.Organize(model, parsed);
        if (log.Count == 0) return log;

        File.WriteAllBytes(glmPath, GlmWriter.Write(model));
        for (int i = 0; i < skins.Count; i++)
        {
            string text = parsed[i].Skin.ToString();
            if (text != skins[i].Text) File.WriteAllText(skins[i].File, text);
        }
        return log;
    }

    /// <summary>The character file of a skin: "swtor_character.json" for the default skin, "swtor_character_&lt;skin&gt;.json" for the others.</summary>
    public static string CharacterFileName(string skinName) =>
        skinName.Equals("default", StringComparison.OrdinalIgnoreCase) ? "swtor_character.json" : $"swtor_character_{skinName}.json";

    private static void WriteCharacter(string folder, JkaExportOptions options, string skinName, List<string> files, List<string> notes)
    {
        if (options.Character is null) return;
        string file = Path.Combine(folder, CharacterFileName(skinName));
        options.Character.Save(file);
        files.Add(file);
        notes.Add($"Character saved: {Path.GetFileName(file)} (File > Load character opens it).");
    }

    /// <summary>
    /// True when <paramref name="name"/> can name a skin: letters, digits, '_' and '-' (the game reads "model folder/skin" as one word),
    /// and not "default" (the skin of the first export, which the add mode never rewrites).
    /// </summary>
    public static bool IsValidSkinName(string name) =>
        Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,40}$") && !name.Equals("default", StringComparison.OrdinalIgnoreCase);

    /// <summary>The file of a skin: <c>model_&lt;name&gt;.skin</c> next to the model.</summary>
    public static string SkinPath(string folder, string skinName) => Path.Combine(folder, $"model_{skinName}.skin");

    /// <summary>The shader that hides a surface in a skin file.</summary>
    private const string OffShader = "*off";

    private static byte[] Encode((DdsImage Image, bool Alpha) texture, JkaExportOptions options) =>
        TgaWriter.Write(TgaWriter.Downscale(texture.Image, options.MaxTextureSize), texture.Alpha);

    // Chooses the file of each texture in the folder of an existing model. A texture equal to a .tga file of the folder uses it
    // (nothing is written). Otherwise it takes its name, or "name_2", "name_3", ... when the file exists.
    // Returns the new name of each texture, the files to write and the reused textures (texture name -> existing file name).
    private static (Dictionary<string, string> Names, Dictionary<string, byte[]> Files, Dictionary<string, string> Reused) PlaceTextures(
        string folder, Dictionary<string, (DdsImage Image, bool Alpha)> textures, JkaExportOptions options)
    {
        var existing = Directory.GetFiles(folder, "*.tga").Select(f => new FileInfo(f)).ToList();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var reused = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, texture) in textures)
        {
            byte[] bytes = Encode(texture, options);
            var same = existing.FirstOrDefault(f => f.Length == bytes.Length && File.ReadAllBytes(f.FullName).AsSpan().SequenceEqual(bytes));
            if (same is not null)
            {
                names[name] = reused[name] = Path.GetFileNameWithoutExtension(same.Name);
                continue;
            }
            string free = name;
            for (int k = 2; File.Exists(Path.Combine(folder, free + ".tga")) || files.ContainsKey(free); k++) free = $"{name}_{k}";
            names[name] = free;
            files[free] = bytes;
        }
        return (names, files, reused);
    }

    // The folder path as the game writes it: everything from "models" on, with '/' separators.
    public static string GamePathOf(string folder)
    {
        var parts = Path.GetFullPath(folder).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int at = Array.FindLastIndex(parts, p => p.Equals("models", StringComparison.OrdinalIgnoreCase));
        return at >= 0 ? string.Join('/', parts.Skip(at)) : $"models/players/{Path.GetFileName(folder)}";
    }

    // Only the _humanoid skeleton is supported. Its bone table is embedded (see GlaSkeleton.Humanoid).
    private static GlaSkeleton HumanoidFor(string animationName)
    {
        if (!animationName.Contains("_humanoid", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The model uses the skeleton '{animationName}'. Only the _humanoid skeleton is supported.");
        return GlaSkeleton.Humanoid;
    }

    private static (List<JkaSurfaceDraft> Drafts, Dictionary<string, (DdsImage Image, bool Alpha)> Textures) Convert(
        AssetIndex index, IReadOnlyList<JkaExportPart> parts, GlaSkeleton jka, JkaExportOptions options, string gamePath)
    {
        if (parts.Count == 0) throw new ArgumentException("Nothing to export.", nameof(parts));
        string bodytype = parts.Select(p => p.Bodytype).FirstOrDefault(b => !string.IsNullOrEmpty(b)) ?? "bma";
        var swtor = LoadSwtorSkeleton(index, bodytype);
        var retarget = new JkaRetarget(swtor, jka);

        var textures = new Dictionary<string, (DdsImage, bool)>();
        var pieces = new List<JkaSourcePiece>();
        foreach (var part in parts)
        {
            string mainName = options.TexturePrefix + part.Slot;
            if (part.Texture is not null) textures[mainName] = (part.Texture, part.Slot is "hair" or "facehair");
            string eyeName = options.TexturePrefix + "head_eyes";
            if (part.Slot == "head" && part.EyeTexture is not null) textures[eyeName] = (part.EyeTexture, false);
            string skinName = mainName + "_skin";
            if (part.SkinTexture is not null && part.SkinPieceIndex >= 0) textures[skinName] = (part.SkinTexture, false);

            var models = new List<Gr2Model> { part.Model };
            models.AddRange(part.Attachments);
            for (int i = 0; i < models.Count; i++)
                foreach (var mesh in models[i].Meshes)
                    for (int p = 0; p < mesh.Pieces.Count; p++)
                    {
                        var piece = mesh.Pieces[p];
                        bool eyes = part.Slot == "head" && piece.MaterialIndex == 1;
                        bool skin = part.SkinTexture is not null && p == part.SkinPieceIndex;
                        pieces.Add(new JkaSourcePiece
                        {
                            Slot = part.Slot, Mesh = mesh, StartTriangle = piece.StartTriangle, TriangleCount = piece.TriangleCount,
                            TextureName = eyes && part.EyeTexture is not null ? eyeName : skin ? skinName : mainName,
                            Role = eyes ? "eyes" : skin ? "skin" : "", Index = i,
                        });
                    }
        }
        return (JkaConverter.Convert(pieces, retarget, jka, options.MaxTotalVertices), textures);
    }

    private static Gr2Skeleton LoadSwtorSkeleton(AssetIndex index, string bodytype)
    {
        foreach (string code in new[] { bodytype, "bma" })
        {
            string file = Path.Combine(index.Root, SkeletonFolder.Replace('/', Path.DirectorySeparatorChar), $"{code}new_skeleton.gr2");
            if (File.Exists(file)) return Gr2Skeleton.Parse(File.ReadAllBytes(file));
        }
        throw new FileNotFoundException($"The SWTOR skeleton for body type '{bodytype}' was not found in {SkeletonFolder}.");
    }

    // The sound set of a new model. The second letter of the body type is the gender ("bma" is male, "bfa" is female).
    private static (string Set, char Gender) VoiceFor(string bodytype, JkaExportOptions options)
    {
        char gender = bodytype.Length > 1 && bodytype[1] == 'f' ? 'f' : 'm';
        string set = options.VoiceSet ?? (gender == 'f' ? DefaultFemaleVoice : DefaultMaleVoice);
        return (set, gender);
    }

    // Writes the model, the textures (name without extension -> TGA bytes), the skin files and, for a new model, sounds.cfg and the manifest.
    private static JkaExportResult Save(string folder, string gamePath, GlmModel model, List<JkaSurfaceDraft> drafts,
        Dictionary<string, byte[]> textures, List<string> notes, bool createSkin, (string Set, char Gender)? voice, string? bodytype, string? glmFile = null, string? character = null)
    {
        Directory.CreateDirectory(folder);
        var files = new List<string>();

        glmFile ??= Path.Combine(folder, "model.glm");
        File.WriteAllBytes(glmFile, GlmWriter.Write(model));
        files.Add(glmFile);

        foreach (var (name, bytes) in textures)
        {
            string file = Path.Combine(folder, name + ".tga");
            File.WriteAllBytes(file, bytes);
            files.Add(file);
        }

        // The skins of an existing model are written by AddToModel.
        if (createSkin)
        {
            var skin = new SkinFile();
            foreach (var (surface, shader) in JkaModelBuilder.SkinLines(drafts, gamePath, ".tga")) skin.Set(surface, shader);
            string skinFile = Path.Combine(folder, "model_default.skin");
            File.WriteAllText(skinFile, skin.ToString());
            files.Add(skinFile);
        }

        // sounds.cfg is two lines without a final newline: the name of a sound set of the game, then the gender letter.
        if (voice is { } v)
        {
            string soundFile = Path.Combine(folder, "sounds.cfg");
            File.WriteAllText(soundFile, $"{v.Set}\r\n{v.Gender}");
            files.Add(soundFile);
            notes.Add($"Voice: {v.Set} ({v.Gender}). The sound set must exist in the game.");
        }

        if (bodytype is not null)
        {
            string manifest = Path.Combine(folder, ManifestName);
            File.WriteAllText(manifest, JsonSerializer.Serialize(new
            {
                version = 1, tool = "SWTOR Character Maker", createdUtc = DateTime.UtcNow, templateVersion = JkaTemplate.Standard.Version,
                bodytype, skeleton = JkaTemplate.Standard.AnimationName, surfaces = drafts.Select(d => d.Name).ToArray(),
                textures = textures.Keys.Select(t => t + ".tga").ToArray(), character,
            }, new JsonSerializerOptions { WriteIndented = true }));
            files.Add(manifest);
        }

        notes.Add($"{drafts.Count} surfaces, {textures.Count} textures.");
        return new JkaExportResult(glmFile, drafts.Select(d => d.Name).ToList(), files, notes);
    }
}
