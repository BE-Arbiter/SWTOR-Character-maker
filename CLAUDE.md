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

## GOM (game object database)

- Files: `systemgenerated/buckets/*.bkt` (715k objects, zstd, one frame per object), `prototypes/*.node` (10k objects, not compressed), `client.gom` (type definitions: enums, classes, fields). All 726,494 objects decode with exact byte consumption (`swtor gom survey`, about 5 s, must report 0 failures).
- Numbers are prefix varints (`GomReader`). Object data describes itself: each field has an id (64-bit hash of the field name, stored as a delta) and a type byte. `GomObjectReader` needs no schema to read values.
- Alignment of records is relative to the start of the `DBLB` tag, not the file. A bucket record: `u32 length` (includes itself), `u64 id` at +8, `u16 data offset` at +0x12, `u16 name offset` at +0x14, `u64 class id` at +0x18, `i16 glommed count` at +0x24, `i32 object size` at +0x28.
- **Names are not in the files.** Field and class names are hashes. Only enum value names (for example `appSlotAge`) and object names (`pcs.trooper.male.human`) are readable. Work out the meaning of a field from its values. `GomSchema` gives declared types and enum names.
- `GomDatabase` (Swtor.Assets) opens all headers in about 3 s and keeps them in memory (hundreds of MB). Do not open it at viewer start. Open it only for a feature that needs it.
- Useful objects: `pcs.<class>.<gender>.<race>` (player character specs), `itmAppearanceDatatable` (2,368 item appearances), `pcsSliderDataTablePrototype`.
- Debug: `swtor gom find <text>`, `swtor gom dump <exact name>`, `swtor gom schema`.
- Format notes were checked against the public GomLib source (PugTools). Do not copy its code; this repository has its own implementation.

## Character creator data (pcs)

- `pcs.<class>.<gender>.<race>[_legacy]` objects hold the creator options. Field ids are hashes. The ones used are named in `CharacterSpec`. An option is `(slot, asset id, material id)`. Asset ids match `<ID>` in `art/dynamic/<slot>/index.xml`. Material ids match `<Material id>`. Slots are the enum `appSlot*` (`AppearanceSlot`).
- A second map gives, for each option, the options of other slots that go with it (`CharacterSpec.Compatible`). Heads drive this.
- Skin, hair and eye color options are assets whose XML is missing, but their index entry has `RepresentativeColor` (average color). `CharacterPanel` multiplies the head texture by the skin color and hair textures by the hair color. This is an approximation.
- `PartResolver` turns an option into files. In file names, `[bt]` is the body type and `[gen]` is `f` or `m`. Material file names can contain `[bt]` too.
- The viewer opens `GomDatabase` on a background thread after the index (about 800 MB of memory for the whole process). The Character tab shows "Loading the game database..." until it is ready.
- Not done yet: body, armor and hands in the creator; real skin/hair shading; eye color on the eye mesh; `--character` starts the viewer on the Character tab.

## Body and equipment

- All parts (head, hair, body, armor) are in one shared model space. The scale is about 1/10 of a meter per unit (a head is at y of about 0.19). Meshes are in bind pose, so parts can be placed side by side without a skeleton.
- The bare body is the asset `<slot>_naked_<variant>_young_a01` for chest, hand, leg and boot (`NakedBody`). The body type (`bma`, `bfa`, ...) is the one of the head, so all parts match.
- Equipment comes from the same slot indexes. One asset gives a base model plus attachments (shoulders, back piece). They share one material and one texture. A material variant can have color schemes: the scheme gives two palettes for the slot (`PaletteTint`).
- `--equip slot=art_name` (repeatable) equips assets at start, for testing: `--character --equip chest=chest_armor01_heavy_bh_a02`.
- Not done: waist and bracer have no bare part; no skeleton, animation or pose; no real skin shading; no eye color on the eye mesh.

## Saving characters

- `CharacterSave` is JSON (version 1). Choices are stored as game ids (asset id + material id per slot, equipment asset id + material id + color scheme guid), not as list positions. Race, class and gender select the spec.
- Default folder: `Documents\SWTOR Character Maker\Characters`. Menu File > Save character / Load character. `--load file.json` loads a file at start.
- Loading skips choices that no longer exist and keeps the defaults for them. Loading before the game database is ready is queued (`CharacterPanel.Apply`).
- Not tested by an automatic test: the ImGui dialogs. The JSON, the load path and `ToSave` were checked by hand.

## Skin and eyes

- Skin: the head diffuse already has a light skin color. `ImageColor.MatchAverage` moves the average of the masked area (red channel of `PaletteMaskMap`) to the representative skin color and keeps the detail. Lips and teeth are outside the mask. Body parts and hair use the same function.
- Complexion (eyebrows, blush) multiplies the skin texture. Face paint is drawn over it with its alpha. These option assets name a `.dds` file as their base file (`PartResolver.OverlayPath`). The age overlay is not applied yet.
- Eyes: a head material has a `MaterialOverride` that gives the eye material. Its `index` is `1` for 1,464 materials and `-1` ("the eye") for 2,481. Both go on mesh slot 1 of the head (`FindEyeOverride`). Before this fix the `-1` heads had no eye texture. The iris is the red area of its mask and gets the eye color.
- All of this approximates the game shader. Layout of the window: 18% models, 57% preview, 25% details.

## Weapons

- Weapons are in `art/dynamic/weapon/` (no index.xml). The list comes from the game object `itmAppearanceDatatable` (2,368 appearances): key, combat type enum (`cbtType_*`), model path, attachment socket, blade/glow color, label. See `WeaponCatalog` for the field ids.
- Texture: `art/shaders/materials/<model stem>.mat` gives the diffuse map. Fallback: same-name texture (`AssetIndex.FindTextures` indexes both `name_vNN` and `name`). About 70% of the weapons have textures in the extract. Some files are missing from the extract, so some weapons show without texture.
- The "Weapons" tab filters by type and name. `--weapon <key>` opens one at start (for testing). The blade of a lightsaber is an effect, not part of the model. Holding a weapon in a hand needs a skeleton and sockets: not done.

## Item names

- Item names are in `fr-fr/str/itm.stb` (only French is in the extract; `TextTables.Find` picks the first language folder that has the table). `StringTable` reads the format: 3 header bytes, `i32` count, 26 bytes per entry, then the texts. An item's name field is a reference `str.itm#<id>`.
- `ItemCatalog` links items to art: item (`itm.*`) -> appearance object name (`ipp.*`) -> (slot, asset id, material id). 46,812 items have a name and an appearance. One asset is shown by many items, sorted from the lowest level: the first one is the item that introduced the look, so its name is used as the label.
- The equipment lists show `item name (+N) [art name]`, can be sorted by item name, art name, item level or quality, and the filter searches all item names of an asset. Building the catalog takes about 4 s on the background thread that opens the game database.

## Weapon item names

- An item (`itm.*`) whose appearance string is not an `ipp.*` object holds a key of `itmAppearanceDatatable`. `ItemCatalog.ForWeaponKey` returns these items. 1,175 of 2,368 weapon appearances have a named item. The others are store (`mtx`), NPC or color variants that no item uses.
- The weapons list shows `item name (+N)  [key]`, can be sorted by item name, and the filter searches item names and keys.

## Shading notes

- `ModelPreview` uses `BasicEffect` with default lighting. Its specular color is set to zero: the default white highlight made every texture look shiny. Game specular and gloss maps (`_s`, `GlossMap`) are not used yet.
- Chiss have no `EyeColor` option in their `pcs` spec. Their eye material has a black `PaletteMaskMap` and a dark red glow diffuse. `ImageColor.NormalizeBrightness` brightens it (only if the brightest value is below 170) so the red halo shows. The `palette1`/`palette2` vectors in a `.mat` file are not read yet.

## Rendering test option

- `SWTOR_SHOT=<file.png>` saves the viewer window to a PNG after about 500 frames with a model shown, then quits. `SWTOR_CAM=yaw,pitch,distance,x,y,z` sets the camera first. Units are small: a head is about 0.02 wide, the eyes are at y of about 0.19 to 0.20, a close-up distance is 0.012. Example: `SWTOR_SHOT=a.png SWTOR_CAM=0.1,0,0.012,0,0.1965,0.005 dotnet run --project src/Swtor.App -- --character --load chiss.json`. A save with empty `options` gives the default look of a spec. Use it to check the look of a change instead of driving the desktop.

## Armor weight and class

- An armor art name is `<slot>_<style>_<weight>_<class>_<number>`: weight is `light`, `med` or `heavy`; class is `ge` (generic) or `bh`, `tr`, `sw`, `ss` (Sith Inquisitor), `sm`, `sp` (Imperial Agent), `jk`, `jw` (Jedi Consular). `ArmorInfo.Parse` reads them. The codes were checked against French item names.
- The equipment lists have a weight filter and a class filter (all slots). Adaptive armor is not in the art names. Item objects only show a crafting profession such as `prfProfessionArmormechAdaptive`, so it is not offered.
