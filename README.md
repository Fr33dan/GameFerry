# GameFerry

<img src="assets/logo.png" width="112" align="right" alt="GameFerry logo">

Copies Wii games to an SD card or USB drive in the layout USB loaders (WiiFlow, USB Loader GX, CFG) expect:

```
<drive>:\wbfs\Game Title [GAMEID]\GAMEID.wbfs
<drive>:\wbfs\Big Game Title [GAMEID]\GAMEID.wbfs
                                      GAMEID.wbf1     ← split below FAT32's 4 GB file limit
```

It reads **NKit** images, including ones packed in `.7z`, `.zip` or `.rar` archives, as well as plain `.iso` and
`.wbfs` files. It only uses ordinary file access, so it does not need to run as administrator.

## Requirements

`GameFerry.exe` and `ferry.exe` are each a single, portable file for 64-bit Windows 10/11; download them from
[Releases](https://github.com/Fr33dan/GameFerry/releases). Nothing needs installing: the .NET runtime, every library
and the NKit engine are inside the file. Two things stay outside it:

- An NKit v1.4 folder with its `Dats` and `Recovery` sub-folders (set in **Settings**). NKit images made with the
  update partition removed can only be rebuilt if the matching file is in `Recovery\Redump\Wii`, and that set is
  far too large to carry in an executable.
- Optional: [7-Zip](https://www.7-zip.org). When installed it is used to unpack archives, which is several
  times faster than NKit's built-in reader.

Nothing is written beside the executable. Settings go in `%APPDATA%\GameFerry`; the scan cache, the covers
shown in the lists, the work folder and the unpacked NKit helper (a small .NET Framework program, which Windows
can run as it is) go in `%LOCALAPPDATA%\GameFerry`; and the app unpacks five of its own native libraries (about
8 MB) to `%TEMP%\.net\GameFerry` the first time it starts.

## Using it

**`GameFerry.exe`** — pick the folder with your games on the left, tick the ones you want (Space toggles the
highlighted rows), pick the drive on the right and press **Copy selected games**. Games already on the drive are
greyed out.

Whatever you copy to is treated as the root of the drive: games go in its `wbfs` folder and cover art in the
loader's folders beside it. That holds for a folder too, so a staging folder ends up laid out exactly like the
card and can be copied onto it as it is. (Pick a folder that is itself named `wbfs`, or one that already has
games directly in it, and games go straight into that folder.)

**`ferry.exe`** — the same engine from the command line:

```
ferry scan     "E:\Games\Wii"
ferry transfer "E:\Games\Wii" --to D:
ferry transfer "E:\Games\Wii\Game Title (USA).7z" --to D: --partitions no-update
ferry list     D:
ferry covers   D: --loader wiiflow
ferry verify   "D:\wbfs\Game Title [GAMEID]\GAMEID.wbfs" --deep
```

Run `ferry help` for every option.

## In the browser

`web/` is the same tool as a web page that runs entirely on your computer. It works in Chrome, Edge, Brave and Opera on Windows, macOS, Linux and ChromeOS, which can write to folders you choose. Choose the folder with your games, choose the card and press **Copy**. Nothing is uploaded, and there is nothing to install.

Big libraries work too, even on a network share. The games are listed at once. About 32 KB of each file is then read to identify the game, six files at a time, and the answers are kept in the browser, so later visits show the list straight away. A folder of more than 30 games starts with none ticked; the search box finds games by name, title or ID. Ticked games are remembered too, so reloading the page in the middle of a batch keeps them. Click a column heading to sort the list (games are copied in the order shown), and tick "Only ticked" to hide the rest.

The engine runs in the page as WebAssembly. It turns an NKit image straight into the WBFS file in a single pass: the full disc never exists as a file and no work folder is needed. Each game's archive is checked against its CRC as it is read, and every rebuilt part of the disc is checked against the disc's own hash table.

Differences from the desktop app:

- **Sources:** NKit images, loose or in `.7z` (LZMA or LZMA2), and loose `.iso` files.
- **Update partition:** always left out. It is never restored from NKit's recovery files.
- **Splitting:** files are always split at 4 GiB − 32 KiB.
- **Folder names:** the disc's own title is used.
- **Cover art:** shown in the lists, straight from GameTDB, but not copied to the card, because GameTDB doesn't let other sites save its images. USB Loader GX and WiiFlow can download covers themselves.
- **No free-space check:** browsers can't see how much space is left. The Copy button shows roughly how much room the ticked games need, and the card panel how much its games take already.
- **No read-back verify:** the copy is not read back from the card.

Copies come out byte for byte the same as the desktop app's, apart from one region. On desktop, the dropped update partition's first 1.7 MB stay in the first 2 MB block, which no loader reads; the web version leaves zeros there.

## What happens to each game

1. **Extract** (archives only) — 7-Zip unpacks the image into the work folder.
2. **Rebuild** — NKit turns the NKit image back into the full disc and checks its checksum against the NKit header
   and the Redump database.
3. **Analyse** — every partition's file table is read to find the sectors that hold data; the rest is padding.
4. **Write** — only the 2 MB blocks containing data are written, as `.wbfs` / `.wbf1` files under temporary names.
5. **Verify** (only when switched on in Settings, or with `--verify`) — the files are read back from the drive,
   bypassing the Windows cache, and compared with what was sent. This takes about as long again, and a bad copy shows
   up anyway as a game that won't start, so it is off by default.
6. **Commit** — the files get their real names and the work files are deleted.

Cancelling, or a failure at any step, removes the unfinished game from the drive. Source files are never modified.
While one game is being written, the next one is already being extracted and rebuilt.

## Cover art

Covers are downloaded from [GameTDB](https://www.gametdb.com) as `GAMEID.png` into the folders each loader reads.
Loaders keep them in different places, so the loader is a setting:

| Loader | Folders (on the drive the loader is on) |
|---|---|
| WiiFlow | `wiiflow\boxcovers` (full cover), `wiiflow\covers` (front) |
| USB Loader GX | `apps\usbloader_gx\images` (3D), `images\2D`, `images\full`, `images\disc` |
| Configurable USB Loader | `usb-loader\covers` (front), `covers\3d`, `covers\disc`, `covers\full` |

By default the loader is detected from the folders already on the drive, and covers are fetched for each game
right after it is copied. **Download covers** in the app (or `ferry covers D:`) fetches whatever is missing for
every game on the drive; covers that are already there are left alone. If the loader lives on a different drive
from the games (loader on the SD card, games on a USB drive), set the loader drive in Settings or pass
`--cover-root`.

The app's own two lists show each game's front cover as well, and hovering over one shows it full size. These
are fetched from GameTDB as games scroll into view and kept in `%LOCALAPPDATA%\GameFerry\covers` (about 75 KB a
game), so each is downloaded once; a cover the loader on the target already has is copied from there instead.
They have nothing to do with what is on the drive: turning them off in Settings only makes the lists plainer
and more compact.

## Settings

| Setting | Default | Notes |
|---|---|---|
| Partitions to keep | Everything | "Remove the update partition" saves about 150 MB per game; loaders never use it |
| File splitting | Automatic | Split at 4 GiB − 32 KiB on FAT32, not at all on NTFS/exFAT. Choose "Always" when copying to an NTFS folder whose contents will later go onto a FAT32 card |
| Folder names | GameTDB title | Or the title stored on the disc, or the source file name |
| Cover art | Automatic | Loader detected from the drive; or WiiFlow, USB Loader GX, Configurable USB Loader, all, none |
| Covers in the game lists | On | Front covers from GameTDB, kept on this PC |
| Verify after writing | Off | Reads every game back from the drive and compares it; takes about as long again |
| Work folder | `%LOCALAPPDATA%\GameFerry\work` | Local drive; up to 24 GB while two dual-layer games overlap |

Settings are saved in `%APPDATA%\GameFerry\settings.json`.

## Not supported

GameCube images, WiiWare/WAD files, raw WBFS-formatted partitions and drive formatting. The web version also
can't read `.zip` and `.rar` archives yet, nor plain `.iso` files inside an archive.

## Building

```
dotnet build GameFerry.slnx
dotnet test  tests\GameFerry.Tests
dotnet publish src\GameFerry.App -p:PublishProfile=Portable
dotnet publish src\GameFerry.Cli -p:PublishProfile=Portable
```

The two publish commands write `publish\GameFerry.exe` (about 60 MB) and `publish\ferry.exe` (about 36 MB). Add
`-p:SelfContained=false` for 1–2 MB executables that need the .NET 10 runtime to be installed instead of
carrying it.

The web version needs the WebAssembly build tools:

```
dotnet workload install wasm-tools
pwsh scripts/build-web.ps1          # writes site/, about 10 MB of static files
```

Serve `site/` from any web server; `node scripts/serve-web.mjs` serves it at http://localhost:8080. Browsers only let a page write to folders over `https://` or from `localhost`. To open it from another computer over plain `http://`, add its address on that computer under `chrome://flags/#unsafely-treat-insecure-origin-as-secure` (`edge://flags/...` in Edge), or serve it over https. A build with `-p:GameFerryBuiltInKeys=false` leaves the Wii common key out. The page then asks for the `keys.bin` from a BootMii backup of the user's console, and finds it by itself if it is at the card's top level.

| Project | Purpose |
|---|---|
| `src\GameFerry.Engine` | What both versions share: disc parsing, the WBFS layout, AES, Wii keys, a 7z reader, and an NKit v1 decoder that streams straight to WBFS |
| `src\GameFerry.Core` | The desktop side: files and drives, source scanning, the transfer engine, NKit 1.4 and 7-Zip, cover art |
| `src\GameFerry.NKitHost` | Small .NET Framework process that runs `NKit.dll`; talks JSON lines to the engine. Embedded in `GameFerry.Core` and unpacked on first use |
| `src\GameFerry.Cli` | `ferry.exe` |
| `src\GameFerry.App` | `GameFerry.exe` (WPF) |
| `src\GameFerry.Web.Engine` | The engine compiled to WebAssembly, with the LZMA SDK's C decoder, and its worker script |
| `web` | The web page (plain ES modules, no build step) |
| `tests\GameFerry.Tests` | Unit tests, using a synthetic encrypted disc image |

`assets\app.ico` (the icon of both executables) and `assets\logo.png` are drawn by `assets\make-icon.ps1`. Change
the design there and run `pwsh assets\make-icon.ps1` to write both files again.

### Releases

Changes reach `main` through pull requests. `.github/workflows/ci.yml` runs the tests and builds both versions for
every pull request, and attaches the two executables to the run so they can be tried before merging. Once a change
is on `main`, the workflow:

- publishes the web version with GitHub Pages;
- if `<Version>` in `Directory.Build.props` hasn't been released yet, publishes the executables as release
  `v<Version>`, with checksums and notes listing the pull requests merged since the last release.

To release a pull request's changes, raise `<Version>` in it; a version with a hyphen, such as `0.2.0-beta.1`, is
released as a pre-release. A pull request's checks say whether merging it makes a release.

## Third-party software

| Component | Used for | Licence |
|---|---|---|
| [NKit](https://github.com/Nanook/NKitv1) 1.4 by Nanook (`lib\NKit\NKit.dll`) | Rebuilding full discs from NKit images | MIT |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) 0.22 by Adam Hathcock, in the build NKit ships (`lib\NKit\SharpCompress.dll`) | NKit's reading of archives | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4.0 | View models of the app | MIT |
| .NET 10 runtime and WPF | Carried inside the portable executables | MIT |
| NKit's reader, ported to `src\GameFerry.Engine\NKit` | Rebuilding discs from NKit images in a single pass (both versions can use it; the web version does) | MIT (NKit) |
| [LZMA SDK](https://www.7-zip.org/sdk.html) 26.04 by Igor Pavlov (`src\GameFerry.Web.Engine\Native\lzma`) | Unpacking `.7z` archives in the web version | Public domain |
| .NET 10 WebAssembly runtime and `System.IO.Hashing` | The web version's engine | MIT |

`lib\NKit` also holds a copy of NKit's configuration with every default path confined to the helper's own folder.
Cover art and game titles come from [GameTDB](https://www.gametdb.com).
