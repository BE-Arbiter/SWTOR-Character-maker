# SWTOR Character Maker

Outil Windows pour parcourir les personnages et objets de Star Wars: The Old Republic (SWTOR), composer un personnage à partir des pièces du jeu (race, corps, tête, cheveux, armure, couleurs) et l'exporter vers un modèle Jedi Academy (`.glm`).

Ce dépôt ne contient **aucun fichier du jeu**. Il lit une copie extraite des assets, que vous devez produire vous-même à partir de votre propre installation de SWTOR.

## Prérequis

- Windows 10/11.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (seulement pour compiler; les releases sont autonomes).
- Une copie installée de SWTOR (les archives `Assets\swtor_*.tor`, environ 58 Go).
- Les assets extraits (voir ci-dessous): plusieurs centaines de milliers de fichiers. Prévoir de la place disque.

## Extraire les assets

Le jeu stocke ses fichiers dans des archives `.tor`. Il faut les extraire dans un dossier, par exemple `C:\jka_tor_assets\resources`.

1. Téléchargez [extracTOR](https://github.com/UltimaKaosXIII/extracTOR) et suivez ses instructions.
2. Pointez-le vers votre copie de SWTOR et extrayez les ressources dans un dossier de travail.
3. Le dossier obtenu doit contenir `art`, `gamedata`, `systemgenerated`, `fr-fr`, etc. Ce dossier est la racine des assets.

Alternative: la CLI de ce dépôt sait aussi lire les `.tor` (`swtor tor list`, `swtor tor extract <sortie> [dossier] --tree <racine> --names <liste>`). Les archives ne contiennent que des hash de chemins: il faut un arbre déjà extrait ou une liste de noms pour retrouver les noms de fichiers.

## Configuration

Indiquez la racine des assets avec la variable d'environnement `SWTOR_ASSETS`:

```powershell
$env:SWTOR_ASSETS = "C:\jka_tor_assets\resources"
```

Au premier lancement, l'application scanne l'arbre et écrit un index en cache dans `%LOCALAPPDATA%\SwtorCharacterMaker` (environ 5 s). Les lancements suivants utilisent le cache.

## Lancer

Depuis une release (zip `win-x64`), lancez `Swtor.App.exe`. Depuis les sources:

```bash
dotnet build
dotnet test
dotnet run --project src/Swtor.App -- --character
```

Autres options utiles: `<fichier.gr2>` pour voir un modèle, `--load perso.json` pour charger un personnage, `--jka` pour l'onglet Jedi Academy.

## Contenu du dépôt

| Projet | Rôle |
| --- | --- |
| `Swtor.Formats` | Lecteurs de formats (GR2 « GAWB », DDS, GOM, MYP/.tor, GLM/GLA). Aucune dépendance à MonoGame. |
| `Swtor.Assets` | Catalogue des assets: index en cache, apparences, couleurs, objets, base GOM, export Jedi Academy. |
| `Swtor.App` | Visionneuse MonoGame (DesktopGL) avec panneaux Dear ImGui. |
| `Swtor.Cli` | Commandes de debug: `gr2 survey`, `gom survey`, `tor list/extract`, `jka ...`. |
| `Swtor.Tests` | Tests xUnit. Ils n'utilisent que de petits fichiers dans `tests/fixtures`. |

Les détails du fonctionnement (formats, structure des assets, conventions) sont dans [CLAUDE.md](CLAUDE.md).

## Releases

- Un tag `v*` publie une release avec un zip autonome (`win-x64`).
- Une build `nightly` (pré-release) est publiée chaque nuit si `main` a changé.

Ces zips ne contiennent pas les assets du jeu: il faut les extraire comme décrit plus haut.

## Mentions

SWTOR et Star Wars appartiennent à leurs propriétaires respectifs. Ce projet n'est pas affilié à BioWare, EA ou Lucasfilm. Il ne distribue aucun asset du jeu.
