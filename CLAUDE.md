# SWTOR Character Maker

Desktop tool that reads an extracted SWTOR asset tree (default `C:\jka_tor_assets\resources`) to browse characters and items, and to build a character from game parts (race, body, head, hair, armor, skin colors).

Language: **C# 14 / .NET 10** (nullable enabled). Renderer: **MonoGame DesktopGL** (or WindowsDX, decide once and keep). Windows is the main target.

## Approach

Solution layout (`src/`):

1. `Swtor.Formats`: class library, no I/O dependency on MonoGame. One folder per format: `Gr2` (Granny meshes and skeleton), `Dds`/`Tex` (textures), `Clo`, `Xml` (slot `index.xml`, materials), `Gom` (`systemgenerated/*.node` prototypes). Input is `ReadOnlySpan<byte>` or `Stream`.
2. `Swtor.Assets`: asset catalog. Scans the tree once, writes a binary/JSON index cache, resolves a part id to files. Reads the root from env `SWTOR_ASSETS`, never from a hard-coded path.
3. `Swtor.App`: MonoGame viewer and editor. Converts parsed meshes to `VertexBuffer`/`IndexBuffer`, skinned on the GPU with a custom `Effect`, part slots, color tints.
4. `Swtor.Tests`: xUnit. Uses small fixture files only.
5. `Swtor.Cli`: debug commands (`gr2 info <file>`, `gr2 survey [root]`). Use it to check parsers against the full tree.

`Swtor.App` is the MonoGame DesktopGL viewer with Dear ImGui panels (`ViewerGame.cs`). `Swtor.Assets` holds `AssetIndex`: a cached scan of models and textures (about 5 s for 800k files, cache in `%LOCALAPPDATA%SwtorCharacterMaker`).

Why this split: parsing is slow and format-heavy. It runs once, behind the cache. The render loop only touches ready GPU buffers.

## Asset tree facts (not obvious)

- Armor and body parts live in `art/dynamic/<slot>/model/*.gr2`; each slot folder has an `index.xml` that is the source of truth for part lists.
- Skin/head textures are in `art/dynamic/player_character/<race>/...` as `.dds` + `.tex` pairs. Load `.dds`; `.tex` is metadata.
- `.clo` files are cloth simulation data. They do not hold geometry. Ignore them for static display.
- The tree has 100k+ files. Never walk it at startup or per frame, and never run `du` or a full recursive listing in a tool call (it times out). Use the cached index.
- Game data (names, item stats, appearance mapping) is in `systemgenerated/` (GOM `.node`). Strings are in `gamedata/str` and `fr-fr/str`.
- `.gr2` here is **not** Granny. It is BioWare's "GAWB" format: uncompressed memory image, 64-bit pointers stored as file offsets. No Oodle needed. Layout is in `Gr2Reader.cs`.
- GR2 meshes hold bone *names* and bounding boxes only. The bone hierarchy and animations are not in the mesh file. (Animation import exists in the author's separate Blender plugin; do not rebuild it here.)
- Vertex layout depends on the flag bits in the mesh header. Check `Gr2Reader.ExpectedStride` before you add a flag. After any change to `Gr2Reader`, run `swtor gr2 survey` (about 8 min, 133k files, must report 0 failures).

## Code rules

- Binary parsing: use `BinaryPrimitives` and `ReadOnlySpan<byte>`. Little-endian unless the format says otherwise. Validate magic numbers and section bounds. Throw `FormatException` subclasses that carry file path and byte offset.
- `Swtor.Formats` must not reference MonoGame. Keep it testable and deterministic.
- Do not allocate in `Update`/`Draw`. Reuse buffers and matrices. Avoid LINQ and `foreach` over non-array collections in the render loop.
- Dispose `VertexBuffer`, `IndexBuffer`, `Texture2D`, and `Effect` when a part is swapped. The GC does not free GPU memory in time.
- Load assets from disk without the MonoGame Content Pipeline. Use `Texture2D.FromStream` or decode DDS (BC1/3/5/7) to `SurfaceFormat.Dxt*` directly.
- Convert game coordinates and units once, in the mesh conversion step. Do not add per-call fixes in the viewer.
- Build paths with `Path.Combine`. Never concatenate strings.
- Load files on a background task. Create GPU resources only on the game thread.
- Tests never depend on `C:\jka_tor_assets`. Copy small fixtures into `tests/fixtures`.

## Comment style

- English, ASD-STE100 style: short sentences, active voice, one idea per sentence, simple verbs.
- Comment only methods (what, inputs, output) and complex logic (why). Do not comment obvious lines.
- Do not write comments that repeat the code or describe a one-line change.
- Document format quirks and magic numbers where they are decoded.

## Commands

```bash
dotnet build
dotnet test
dotnet run --project src/Swtor.Cli -c Release -- gr2 survey   # full-tree parser check
```

## UI notes

- The `MonoGame.ImGuiNet` NuGet package ships its DLL at the package root, not in `lib/`. The csproj references it with a `HintPath`. Its namespace is `MonoGame.ImGuiNet`. It pulls an older ImGui.NET (no `ImGuiChildFlags`, `IniFilename` is read-only).
- `System.Numerics` and `Microsoft.Xna.Framework` both define `Vector2/3/4`. In UI files, alias the one you need.
- Run the viewer with a model: `dotnet run --project src/Swtor.App -- <file.gr2>`.

## Models and textures

- The explorer lists only models (`.gr2`, not `.lod.gr2`). Do not add other file types to it.
- NPC and creature models find textures by name: `name.gr2` -> `name_v01_d.dds` (`_d` diffuse, `_n` normal, `_s`/`_h` other maps). `AssetIndex.FindTextures` does this.
- Player parts (head, chest, hair, ...) have no same-name texture. The link is: slot `art/dynamic/<slot>/index.xml` -> asset (model + material variants) -> `.mat` file (`art/shaders/materials`) -> `DiffuseMap`. `AppearanceCatalog` does this. In file names, `[bt]` is the body type (`bfa`, `bma`, ...) and `[gen]` is `f` or `m`, the second letter of the body type. The `.gr2` own material name is always a placeholder ("default").
- Colors (palettes, `ColorScheme` guids, skin tones) are in GOM data (`systemgenerated/*.node`, `.bkt`: zstd, custom format). Not read yet. Models show the untinted texture.
- DDS: BC1/BC2/BC3 and uncompressed BGRA/BGRX cover 224,737 of 224,780 files. The rest are DX10 minimaps and broken files. No BC5/BC7 is used.
- UV V is not flipped. Textures look correct as is.

## Colors

- Garment colors: material variant -> `ColorSchemeIds` (index.xml) -> `colorscheme/index.xml` (scheme per slot: primary and secondary palette ids) -> `garmenthue/*.xml` (Hue, Saturation, Brightness, Contrast). `ColorCatalog` reads them.
- Mask texture (`PaletteMaskMap`, `_m`): red = primary area, green = secondary area. `PaletteTint` recolors on the CPU. **The real shader is unknown, so the result is an approximation.** Do not describe it as exact.
- Skin, hair and eye color XML files are not in the extract (only index entries with a representative color). They need GOM data.
- Run the viewer with a scheme: `dotnet run --project src/Swtor.App -- <file.gr2> <scheme guid>`.
