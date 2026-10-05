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
- Mask texture (`PaletteMaskMap`, `_m`): red = primary area, green = secondary area. `PaletteTint` recolors on the CPU with the formula of the game (see "Garment shader" below) when the palette map (`_h`) is known, otherwise with an approximation.
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

## NPC heads

- `art/dynamic/head/index.xml` has about 1,700 heads, many that the creator does not offer: named NPCs (`head_human_bfn_lana_a02`, `theron`, `koth`, `revan`...) and NPC species (trandoshan, gand, rodian, nautolan...). Check box "NPC head" under the head list (`CharacterPanel.NpcHead.cs`): a filtered list of every head (one entry per material) whose body type has the gender of the character. It replaces the head of the creator in the preview and the export; the creator head stays selected, because it decides which hair and other options are allowed.
- "Original colors" (on by default): no skin color, eye color, complexion or face paint on the head, and no skin color on the bare body.
- A head lists the bare body material of each slot (`CustomData/SkinMaterials`, `AppearanceAsset.SkinMaterials`): a trandoshan head gives a trandoshan chest and legs. `NakedPart` uses the naked model of the race with that material (`PartResolver.ResolveWithMaterial`), otherwise the naked asset material. This applies to the creator heads too (they give the same files as before).
- Saved as `npcHead` (asset id + material id) and `npcOriginalColors` in `CharacterSave`.
- The hood of a robe: when it is up, a "Hide hair" check box shows next to "Hood up" (same setting as the helmet one).

## Skin under equipment

- An equipment asset can show bare skin. Its `index.xml` entry has `<SkinMaterialIndex>` (`AppearanceAsset.SkinMaterialIndex`): the position of the mesh piece that takes the skin material of the bare body part of the slot (chest 87 assets, hand 54, leg 20, boot 10; `0` is the naked assets themselves, `-1` none). Often this piece is a full copy of the naked body under the garment. The viewer gives it the texture of `NakedBody` for the slot with the skin color; the JKA export writes it as `<region>_<slot>_skin` with the texture `<slot>_skin`. Waist and face have one such asset each: not handled (no bare part for them). The skin piece uses the same bare body as the empty slot, so the head's `SkinMaterials` apply to it too (see "NPC heads").
- Garment materials with `<AlphaMode>Test` (chest 106, bracer 261, ...) cut holes: the red channel of the rotation map (`_n`, `RotationMap1`) is the transparency (white = hole, black = solid), threshold `AlphaTestValue`. The diffuse alpha is not the opacity. `ImageColor.CutOut` bakes it in the alpha channel; `ModelPreview.MarkCutOut` draws those pieces after the solid ones with non-premultiplied blending (MonoGame `BlendState.AlphaBlend` is premultiplied: it makes the holes white). Without this, the painted skin of the garment texture (always a light human skin) shows instead of the skin piece below.
- Not done: the JKA export writes 24-bit TGA without a `.shader`, so the holes of a cut-out garment are not cut in the game.

## Shading notes

- `ModelPreview` uses `BasicEffect` with default lighting. Its specular color is set to zero: the default white highlight made every texture look shiny. Game specular and gloss maps (`_s`, `GlossMap`) are not used yet.
- Chiss have no `EyeColor` option in their `pcs` spec. Their eye material has a black `PaletteMaskMap` and a dark red glow diffuse. `ImageColor.NormalizeBrightness` brightens it (only if the brightest value is below 170) so the red halo shows. The `palette1`/`palette2` vectors in a `.mat` file are not read yet.

## Rendering test option

- `SWTOR_SHOT=<file.png>` saves the viewer window to a PNG after about 500 frames with a model shown, then quits. `SWTOR_CAM=yaw,pitch,distance,x,y,z` sets the camera first. Units are small: a head is about 0.02 wide, the eyes are at y of about 0.19 to 0.20, a close-up distance is 0.012. Example: `SWTOR_SHOT=a.png SWTOR_CAM=0.1,0,0.012,0,0.1965,0.005 dotnet run --project src/Swtor.App -- --character --load chiss.json`. A save with empty `options` gives the default look of a spec. Use it to check the look of a change instead of driving the desktop.

## Armor weight and class

- An armor art name is `<slot>_<style>_<weight>_<class>_<number>`: weight is `light`, `med` or `heavy`; class is `ge` (generic) or `bh`, `tr`, `sw`, `ss` (Sith Inquisitor), `sm`, `sp` (Imperial Agent), `jk`, `jw` (Jedi Consular). `ArmorInfo.Parse` reads them. The codes were checked against French item names.
- The equipment lists have a weight filter and a class filter (all slots). Adaptive armor is not in the art names. Item objects only show a crafting profession such as `prfProfessionArmormechAdaptive`, so it is not offered.

## Dyeing equipment

- Like the dye modules of the game, each equipped item has a free Primary and Secondary color. The choices are garment palettes (`garmenthue`, 720 of them) in a swatch grid. They replace the palettes of the chosen color scheme. Choosing a scheme, or "(default colors)", clears them. Saved as `primaryId`/`secondaryId` (optional) in `SavedEquipment`.
- Custom color: the palette picker has a "Custom color" part (button "Make a custom color from the current one", then sliders Hue in degrees, Saturation, Brightness -1..1, Contrast 0..3). They are the four values of the garment shader; the Saturation slider is `1 - game value` (the game value 1 is grey). `PaletteTint.Custom` makes the palette and sets its representative color to the result for a typical texel. The texture is rebuilt when a slider is released. Saved as `primaryCustom`/`secondaryCustom` (`[hue, saturation, brightness, contrast]`, game values), which win over `primaryId`/`secondaryId`. Game whites for reference: `matte_white` brightness 0.81, `chrome_white` 0.87, both contrast 1 and saturation 0.994.
- A swatch is the color that the palette gives to a mid-grey pixel (`PaletteTint.Swatch`). Items without a color mask (`part.MaskPath` null) cannot be dyed. The dye uses the same approximate `PaletteTint` as the schemes.

## Helmets and list buttons

- Helmets are the equipment slot `face` (`art/dynamic/face/index.xml`, 724 assets, same structure as chest or leg; the colour schemes have a `face` slot; the game calls it "Tête"). It is the first slot of the equipment list. With a helmet, the hair is hidden (check box "Hide hair", on by default, not saved). `art/dynamic/head` holds the player heads, not helmets. In the Jedi Academy export the helmet is the part `face`: surfaces `head_face`, `head_face1`, ..., texture `face.tga`.
- Robes (chest assets with `..._hooddown.gr2` and `..._hoodup.gr2` attachments, 265 index lines mention a hood) show one hood at a time. Check box "Hood up" under the item (off by default, not saved). A helmet forces the hood down. A raised hood hides the hair like a helmet (same "Hide hair" check box). Before this, both hoods were drawn together.
- Every selection combo of the Character tab has `<` and `>` buttons (`Stepper`): class, gender, race, body, head, hair, face hair, complexion, age, face paint, equipment, variant, color scheme. The list wraps around. Optional slots and equipment go through "(none)" or "(bare)" between the last and the first choice. Equipment steps follow the name and armor filters. The sort, weight and class filter combos have no buttons.

## Icon gallery

- Each equipment slot has a "..." button (after the `>` button) that opens a modal (`CharacterPanel.Gallery.cs`): tiles of 128x128 icons with the item name below, the same filters as the list (name, weight, class, sort), the first tile is the empty slot. A click equips and closes. Only the visible rows are drawn (manual clipping). Icons are read at most 16 per frame (placeholder until then), cached as ImGui textures (2,000 at most, then all are disposed), and need `SetGui` (the `ImGuiRenderer`).
- Icon files: `gfx/icons/<appearance object name>.dds` (about 70,000 files, DXT5, 52x52, so the 128 px tiles are enlarged). The name is the `ipp.*` string of an item (`ItemInfo.AppearanceName`, `ItemInfo.IconPath`). An asset has no icon of its own: the icon is the one of its first item that has a file. Assets with no named item show a "?" tile. Weapons are not done.
- Test option: `SWTOR_GALLERY=<slot>` (for example `chest`) opens the gallery at start.

## Translations (`UiText`)

- `UiText` (Swtor.Assets) gives names of classes, races, genders, appearance slots, equipment slots and body types in the game language, with an English fallback. The text tables have ids only, so the ids are constants in `UiText`: `gui/classnames.stb`, `gui/backgroundnames.stb` (races), `gui/charactercreate.stb` (slot titles in capitals, "Type de corps", Agile/Athlétique/Fort/Robuste, Homme/Femme), `gui/equipslot.stb`. `swtor stb <table> <text or *>` lists a table (for example `swtor stb gui/charactercreate.stb '*'`).
- Not translated, because the extract has no text: single palette names (shown as "dye h35 p"), the generic armor look, face hair and age slots, and the app's own labels ("Primary", "Sort", weights).
- Body type: the third letter of the code in art names. `a` Agile, `n` Athlétique, `s` Fort, `f` or `b` Robuste (checked by measuring the naked body models: `s` is the tallest, `a` the smallest, `f`/`b` the widest). The head list is filtered by the body of the selected head; changing the body picks the head with the same look.

## Garment shader (exact formula)

- Source: the SWTOR Slicers ZG SWTOR Tools add-on for Blender (installed in `%APPDATA%\Blender Foundation\Blender\5.1\scripts\addons\zg_swtor_tools\rsrc\Custom SWTOR Shaders.blend`). Its node groups were read with `blender -b <file> --python` and written as text. The groups: `SWTOR - Garment Shader`, `SW Aux - HuePixel`, `ChosenPalette`, `ManipulateHSL`, `ExpandHSL`, `OffsetHue`, `OffsetSaturation`, `AdjustLightness`, `ManipulateAO`, `ConvertHSLToRGB`. Texture files of a Garment material: `_d` diffuse, `_h` PaletteMap (R = ambient occlusion, G = hue, B = saturation, A = lightness), `_m` PaletteMaskMap (R = palette 1, G = palette 2, B = metallic mask), `_n` rotation map (normal, emissive, opacity), `_s` gloss map (RGB specular color, A shininess). A `.mat` has `palette1` and `palette2` as vector4 (Hue, Saturation, Brightness, Contrast; neutral value 0, 0.5, 0, 1).
- Per pixel (`PaletteTint.ApplyGame`, `PaletteTint.DyeColor`): weight W = min(maskR + maskG, 1). The palette is the secondary one when maskG > maskR, otherwise the primary one. Inside the areas the diffuse map is ignored: `hue = fract(0.3923 G - 0.09806 + Hue)`, `S = clamp((0.5882 B)^Saturation * (1 - Saturation), 0, 1)`, `L = Brightness + (1 - Brightness) * Contrast * (0.70588 A)^Contrast`, `AO = clamp(R * (1 + Brightness - Brightness * R), 0, 1)`, color = HSL(hue, S, L * AO) converted to RGB through HSV. Result = diffuse * (1 - W) + color * W. The add-on applies a gamma of 2.1 afterwards only to get linear values for Blender; the viewer does not.
- Checks: with Contrast 0 the lightness is the Brightness, and a "Saturation" of 0.994 gives almost no saturation, so the light grey dye is grey (rep. color 0.695). The representative color of a palette is the result for a typical texel (G about 0.42, B about 0.8, A about 0.43, AO 1): hue and saturation of the files `dye_h23` and `dye_h57` agree with it. High Contrast dyes (black has 3) keep light highlights on worn areas: that is the game look, not a bug.
- Not done: specular (palette Specular and Metallic Specular, `_s` gloss map, phong with MaxSpecPower 32, metallic mask), normal and emissive maps, rim light. The viewer has a single `BasicEffect` without specular. Skin, hair and eye shaders (`HueSkinPixel`, `SkinB`, `HairC`, `Eye` groups) are in the same file and are not read yet: skin, hair and eyes still use the average color method.
- Debug: `swtor dds stats <file> [mask]` (channel statistics), `swtor dds dyecheck <slot> <art name> [n]` (default schemes of the item through the formula, compared with the diffuse preview).

## Color picker and speed

- A palette file has a `Representativecolor` (sRGB): the color that the game shows. `Palette.Representative` reads it and `PaletteTint.Swatch` uses it (the old swatch from hue and saturation was wrong for many dyes). About 15 `garmenthue_dye_*` files are fillers with the same values and a grey of about 0.338 (`Palette.IsPlaceholder`): they are hidden. 6 dye palettes of the index have no file. `ColorNames.Describe` gives an English name from the color ("Black", "Navy", "Dark red"...): the extract has no text for single palettes. The real dyes are 48 distinct colors, including 2 blacks and 1 navy.
- Without the palette map, `PaletteTint.Apply` falls back to the representative color: in each mask area every channel is multiplied by one factor so that the average color of the area becomes the representative color (limit 4x). Without it, it falls back to the old hue method. Both are approximations; items of the game have a palette map, so they use the exact formula.
- The palette picker has two groups: "Colors of this item" (the palettes of all color schemes of the item for its slot, so what the game gives to it) and "Dyes" (any item can take them, the game dye modules are pairs of them). Other families (class palettes of other armor, tiers) are not offered: they did not suit the item. Both groups are sorted: grays dark to light, then by hue. The tooltip shows the color name and the palette file. `swtor palettes [filter]` lists palettes with their swatch and name; `swtor gom grep <text> [prefix]` searches values in the game database.
- `PrecachePalettes` reads all palette files and computes the swatches on a background thread at start. `ColorCatalog.ReadPalette` is thread-safe.
- `DdsCache` keeps decoded DDS images (384 MB budget, oldest dropped first). A rebuild of the character went from about 400 ms to about 140 ms with a warm cache. Cached images are read-only: all color functions return new images.

## Jedi Academy export (Jedi Academy `.glm` on the `_humanoid` skeleton)

- Goal: put the character of the Character tab in a Jedi Academy player model. Tab "Jedi Academy" (`JkaPanel`): "Create new model" (new folder, `model.glm`, `model_default.skin`, TGA textures, `sounds.cfg`, `swtor_export.json`) or "Add surfaces to an existing model" (checkbox per part: head, hair, face hair, chest, hands, legs, boots, waist, bracers). `JkaExporter` (Swtor.Assets) does the work. Settings are in `%LOCALAPPDATA%\SwtorCharacterMaker\jka.json`.
- **The export needs no Jedi Academy file.** The Jedi Academy side is embedded in `Swtor.Formats`: the template (`Jka/jka_template.json`) and the `_humanoid` bone table (`Jka/jka_humanoid.json`, `GlaSkeleton.Humanoid`, 53 bones with bind poses). The sample models in `C:jka_modelsGameDataasemodelsplayers` are only used to rebuild them: `swtor jka template <players folder> <out.json>` and `swtor jka skeleton-data <_humanoid.gla> <out.json>`. The players folder of the panel is only the output folder (default `DocumentsSWTOR Character MakerJedi Academymodelsplayers`, or `JKA_PLAYERS`). It can be the game folder to install. The skeleton of SWTOR comes from the SWTOR assets (`SWTOR_ASSETS`).
- `.glm` (ident `2LGM`, version 6): header 164 bytes, then hierarchy offsets + entries, then LODs. Offsets are relative (surface offsets from the end of the LOD `ofsEnd` field, hierarchy offsets from the end of the header, `ofsHeader` of a surface is minus its position). A surface is: 40 byte header, triangles, vertices, UVs (8 bytes each), bone references. A vertex is normal, position, a packed word, then one weight byte per weight, padded to 4 bytes. Packed word: bits 30-31 = weight count - 1, 5 bits per bone reference (so 32 bones at most per surface), bits 20+2i = high 2 bits of weight i (10 bits, sum 1023). `GlmWriter` writes the same bytes as the game tools (checked on 3 of 6 sample files, the others differ only in the order of bone references: `swtor jka roundtrip`).
- `.gla` header is 100 bytes; bone offsets table follows it. Bone: name, flags, parent, 3x4 bind pose, 3x4 inverse, children. Z is up, the model faces -Y, left is +X. 72 units tall.
- Surface flags: `0x1` tag ("*name": the game reads its position), `0x2` off, `0x100` no descendants (this last value is from the game source and is not used here). JKA triangles are clockwise when seen from outside. UV V is not flipped (SWTOR and JKA both have v=0 at the top). TGA is written bottom-left origin, 24 bit (32 bit with alpha for hair).
- A new model needs no reference model. `JkaTemplate.Standard` (embedded `Jka/jka_template.json`) holds the fixed part: the root `stupidtriangle_off` and 46 tag surfaces (`*back`, `*chestg`, `*hip_*`, `*shldr_*`, `*head_*`, `*<limb>_cap_*`, `*l_hand`, ...), each one triangle with named bones and a parent main surface. The file is made by vote from the 6 sample models (`swtor jka template <players folder> <out.json>`; a surface needs 2 models, the most common geometry and parent win, ties go to `jaesa`). The samples agree on names, flags and parents; they differ by about 0.3 units in tag positions (two families) and in the parent of the `*hip_*` tags (`torso` in 4, `hips` in 2: `torso` wins). Only 3 samples have the `*head_*` tags (`arbiter` has odd values, the vote ignores it). The `_off` caps are not in the template: 4 of 6 samples have them but they are model specific (own mesh, texture of another model), and `jaesa` and `arbiter` work without. Hierarchy of a new model: root, hips; below hips: l_leg, r_leg, torso; below torso: head, l_arm, r_arm; below the arms: the hands; tags below their main surface; content surfaces below the main surface of their region. The nine main surfaces (hips, l_leg, r_leg, torso, head, l_arm, r_arm, l_hand, r_hand) always exist. A main surface without content is a hidden surface with one triangle of no area.
- **SWTOR has real skeleton files**: `art/dynamic/spec/<bodytype>new_skeleton.gr2` (bma, bmf, bfa, ... 140 bones). `Gr2Skeleton` reads them. Per bone: u64 name pointer, i32 parent, 4 bytes, local matrix, inverse bind matrix (row vectors, translation in the last row; the root has a rotation of 180 degrees around Y, so use the inverse of the stored matrix as the world matrix). Meshes are in the same space: x left, y up, z front, 1 unit = 10 m.
- `JkaRetarget` moves vertices into the JKA bind pose: uniform scale (neck of both skeletons, about 338), axes (x, -z, y), the spine is straightened, and each arm and leg segment (upper arm, forearm, hand, thigh, shin, foot) is moved, turned and stretched so that its joints land on the JKA joints. The JKA bind pose has lower arms (about 40 degrees) and wide legs (about 15 degrees); without this step animations would bend the mesh wrongly. The sole is at z = 2.7 (the sample models stand at this height).
- `JkaBoneMap` links SWTOR bone names to JKA bones (some bones share: `Chest1` is half `upper_lumbar`, half `thoracic`). Bones without counterpart (hair, cloth, effects) lose their weight. A vertex with no weight left gets its nearest bone. `JkaConverter` cuts every mesh in regions by the weights of the triangles (a slot can only fill some regions: chest = torso and arms, hand = hands, leg = hips and legs, boot = legs). Names: main surface when the slot is the usual one (chest -> torso, l_arm, r_arm; hand -> l_hand, r_hand; leg -> hips, l_leg, r_leg; head -> head), otherwise `<region>_<slot>` (`l_leg_boot`, `hips_waist`, `head_hair`, `head_eyes`). Attachments (shoulder pieces) get a number: `l_arm_chest1`.
- Folder of a new model (`<players folder>/<name>/`): `model.glm`, `model_default.skin`, `sounds.cfg` (sound set and gender letter, two lines, no final newline; gender is the second letter of the body type; defaults `imperial_hf1`/`f` and `new_clones`/`m` come from the samples and must exist in the game, `JkaExportOptions.VoiceSet` changes the set), one `<slot>.tga` per texture (`head`, `head_eyes`, `hair`, `chest`, `hand`, `leg`, `boot`, ...) and `swtor_export.json` (manifest: tool, date, template version, body type, skeleton, surfaces, textures). The skeleton `_humanoid/_humanoid.gla` is a game file: it is not exported and not needed. No `icon_default.jpg` yet.
- A surface can have 1,000 vertices at most (`JkaConverter.MaxVerticesPerSurface`; `GlmWriter` throws above it). `JkaConverter.SplitByVertexLimit` cuts a larger surface in parts, in triangle order: the first part keeps the name, the next ones are `<name>_2`, `<name>_3`. A vertex used by two parts is copied. Every part has its own 32-bone limit.
- "Add to model" never changes the geometry or a texture file of the model (but see "Skin variants" below for names and hierarchy) (`JkaModelBuilder.MergeSurfaces`, `JkaExporter.AddToModel`). A draft equal to a surface of the model (same geometry within the file rounding, same texture) is skipped, so selecting all parts only adds what changed. A new surface whose name is taken gets a free name: a main surface gets a letter (`r_handa` to `r_handh`, below `r_hand`), because a skin that turns `r_hand` off must show a variant with this name, otherwise the game thinks the hand was cut off and hides the saber (`G_GetRootSurfNameWithVariant` in OpenJK `g_combat.cpp`, 8 variants; same test for arms, legs, torso, hips); split parts follow (`r_handa_2`); other surfaces get the first free `<name>_N`. Only a hidden main placeholder is filled in place. A texture equal byte for byte to a `.tga` of the folder reuses that file; otherwise it gets a free name (`swtor_chest_2`). Skins: the existing `model_*.skin` files keep their lines and only get `<new surface>,*off` (a surface missing from a skin shows the `.glm` shader). A new skin is written: `model_<name>.skin` when the panel field "Name of the new skin" (`JkaExportOptions.SkinName`; one word of letters, digits, `_`, `-`, not `default`) is set, and a skin with that name is replaced; otherwise `model_N.skin` with the first free N. In the game: `model <folder>/<name>`. With a name, the skin is made even when no surface is new. The new skin is a copy of `model_default.skin` with the new and the kept surfaces shown and, set to `*off`, the surfaces that a new surface was renamed for and those whose texture belongs to an exported slot (texture name `<prefix><slot>[_eyes][_N]`).
- **Skin variants** (`JkaVariants.Organize`, run at the end of "Add to model", CLI `swtor jka variants <glm>`). The game models do this (`jedi_hf`: `torso_off` hidden, children `torsoa`...`torsog`, one per outfit, content below its variant: `torsoa_belt`, `r_handa_cuff`; tags hang on the root). For each main surface that a skin hides: the main surface becomes hidden (flag off, one triangle of no area; its children are still drawn, flag off does not hide descendants); each skin gets a variant `<main><letter>` below it (default `a`, then the skins oldest file first, so a skin has the same letter on every main surface); the content shown by one skin only goes below its variant; shared content stays below the main surface. Why: when a skin turns `r_hand` off, the game looks for `r_handa`...`r_handh` (`G_GetRootSurfNameWithVariant`, OpenJK `code/game/g_combat.cpp`, 8 variants); with none shown it thinks the hand was cut off and does not draw the saber; dismemberment cuts the variant and its children. A skin without own geometry for a main surface gets an empty variant. The surfaces are written in hierarchy order. A second run changes nothing. `swtor jka skinstats <glm>` lists what each skin draws (to check that a change keeps the look).
- A vertex can be used by 32 triangles at most (`JkaConverter.MaxTrianglesPerVertex`): the stencil shadow of the game keeps 32 edges per vertex (`MAX_EDGE_DEFS`), and above that the game breaks. `LimitTrianglesPerVertex` copies such a vertex (no vertex is removed). The converter applies it before the 1,000-vertex split, the simplifier refuses a collapse that goes over it, and `GlmWriter` applies it to every surface it writes, so it holds on every path (new model, add to model, kept surfaces, `jka reduce`).
- Not done: LOD, `.shader` files (hair has an alpha channel but the game ignores it without a shader script), icon, normal and specular maps, skinning check by playing real animations (only the bind pose was checked, by rendering with `--glm`).
- CLI: `swtor jka bones <gla>`, `jka info <glm> [gla]`, `jka roundtrip <glm...>`, `jka skeleton <gr2>`, `jka measure|joints <gr2...>` (bone centers), `jka template <players folder> <out.json> [sample folders...]` (rebuilds the template), `jka export <players folder> <name>` (bare human male), `jka rename <glm> old=new...` (renames surfaces in the model and its `model_*.skin` files, backups `*.rename`).

## Test options of the viewer

- Tests that open a window must use monitor 2: `SWTOR_SCREEN=2` (1 is the primary monitor, then left to right).
- `--jka` starts on the Jedi Academy tab. `--glm <file.glm>` shows a Jedi Academy model with its skin (`SWTOR_CAM` for the camera, units are JKA units: `0,0,110,0,33,0` is a front view of the whole body).
- `SWTOR_JKA_EXPORT="players folder|name[|sound set]"` exports the loaded character as a new model and quits (`players folder|@model.glm` adds to a model). Use it with `--character [--load file.json] [--equip slot=art]`.

## Unpacking .tor archives

- The game keeps its assets in `.tor` files (`<SWTOR>\Assets\swtor_*.tor`, about 100 files, 58 GB). They are MYP archives: `MypArchive` (Swtor.Formats/Myp) reads the tables, `Read(entry)` gives the bytes. Layout is in the class comment. Version 6 files store "compressed" entries with **zstd** (the reader also accepts zlib, found by the first bytes).
- **Archives hold no names**, only a 64-bit hash of the path (`MypHash`: Jenkins lookup3 `hashlittle2`, seeds 0, over `/resources/<path>`, high half = secondary value). Names must come from outside: `TorNames` takes a tree already extracted (`--tree`, default `SWTOR_ASSETS`) and text lists (`--names`, one path per line, or the `ph#sh#path#crc` lines of community hash lists). Files without a known name go to `<out>/_unknown/<hash>.<ext guessed from the first bytes>`.
- Checked on `swtor_main_art_dynamic_head_1.tor`: 11,798 of 11,800 files named, 0 differences with the existing tree.
- CLI: `swtor tor list [tor|folder]`; `swtor tor extract <out> [tor|folder] [--tree root] [--names list] [--filter text] [--jobs n]`. Default source is `SWTOR_GAME_ASSETS` or the EA install folder. `<out>` becomes the asset root (`art`, `gamedata`, ... below it). A file that exists with the right size is skipped, so a run can restart. Names with `..` go to `_unknown`.
