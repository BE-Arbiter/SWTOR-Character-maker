# SWTOR Character Maker

Windows tool to browse Star Wars: The Old Republic (SWTOR) characters and items, build a character from game parts (race, body, head, hair, armor, colors), and export it to a Jedi Academy model (`.glm`).

This repository contains **no game files**. It reads an extracted copy of the game assets, which you must produce yourself from your own SWTOR installation.

## Prerequisites

- Windows 10/11.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (only to build from source; releases are self-contained).
- An installed copy of SWTOR (the `Assets\swtor_*.tor` archives, about 58 GB).
- The extracted assets (see below): several hundred thousand files, so plan for disk space.

## Extracting the assets

The game keeps its files in `.tor` archives. You must extract them to a folder, for example `C:\jka_tor_assets\resources`.

1. Download [extracTOR](https://github.com/UltimaKaosXIII/extracTOR) and follow its instructions.
2. Point it at your SWTOR copy and extract the resources to a working folder.
3. The result must contain `art`, `gamedata`, `systemgenerated`, `fr-fr`, and so on. This folder is the asset root.

Alternative: the CLI of this repository can also read `.tor` files (`swtor tor list`, `swtor tor extract <out> [folder] --tree <root> --names <list>`). The archives store only path hashes, so you need an already extracted tree or a list of names to recover the file names.

## Configuration

Set the asset root with the `SWTOR_ASSETS` environment variable:

```powershell
$env:SWTOR_ASSETS = "C:\jka_tor_assets\resources"
```

On first start, the app scans the tree and writes an index cache to `%LOCALAPPDATA%\SwtorCharacterMaker` (about 5 s). Later starts use the cache.

## Running

From a release (`win-x64` zip), run `Swtor.App.exe`. From source:

```bash
dotnet build
dotnet test
dotnet run --project src/Swtor.App -- --character
```

Other useful options: `<file.gr2>` to view a model, `--load character.json` to load a saved character, `--jka` to open the Jedi Academy tab.

## Repository layout

| Project | Role |
| --- | --- |
| `Swtor.Formats` | Format readers (GR2 "GAWB", DDS, GOM, MYP/.tor, GLM/GLA). No MonoGame dependency. |
| `Swtor.Assets` | Asset catalog: cached index, appearances, colors, items, GOM database, Jedi Academy export. |
| `Swtor.App` | MonoGame (DesktopGL) viewer with Dear ImGui panels. |
| `Swtor.Cli` | Debug commands: `gr2 survey`, `gom survey`, `tor list/extract`, `jka ...`. |
| `Swtor.Tests` | xUnit tests. They use only small files in `tests/fixtures`. |

Implementation details (formats, asset tree structure, conventions) are in [CLAUDE.md](CLAUDE.md).

## Releases

- A `v*` tag publishes a release with a self-contained `win-x64` zip.
- A `nightly` pre-release is published every night if `main` changed.

The zips do not contain the game assets: extract them as described above.

## Disclaimer

SWTOR and Star Wars belong to their respective owners. This project is not affiliated with BioWare, EA or Lucasfilm. It does not distribute any game asset.
