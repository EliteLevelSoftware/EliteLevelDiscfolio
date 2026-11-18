# Using EliteLevel Discfolio

Discfolio opens a disc image or a folder dump you already have and lets you browse it, search by name, compute a SHA-256 hash, and copy files out. The source is never modified.

## Desktop

```text
dotnet run --project src/EliteLevel.Disc.App
```

Drop an image on the window, or use Open an image. Ctrl+O opens a file, Ctrl+K focuses search, Ctrl+E extracts the selection, Ctrl+Shift+E extracts everything, and Ctrl+H hashes the selected file. On macOS the same shortcuts use Command.

Search matches file names, the title and the title ID. The library tile uses the icon from the image. The banner uses the wide art when the image has it, or a softened icon, and generated art when neither is present. The side card shows the title and, in a monospace line, the title ID.

PlayStation 3 images and folder dumps are read from `PARAM.SFO` (`TITLE`, `TITLE_ID`, `APP_VER` or `VERSION`, `CATEGORY`, `PARENTAL_LEVEL`), `ICON0.PNG`, and `PIC1.PNG` (`PIC0.PNG` when `PIC1.PNG` is absent). `PS3_DISC.SFB` or `PS3_GAME` may sit at the image root or up to two folders down, and the shallowest match is used. Xbox 360 reads the title ID and version from the `default.xex` execution info, and the title and title image from the embedded XDBF resource. `nxeart` is used as banner art when it is an image. Original Xbox reads the title and title ID from the `default.xbe` certificate, and the title image from `$$XTIMAGE` when that section contains a PNG or JPEG. `default.xex` and `default.xbe` are found the same way, up to two folders down. A Nintendo Switch NSP or XCI lists the container without keys. Settings can point at your `prod.keys` (and `title.keys` in that folder). With those keys, RomFS and ExeFS open, and the title, title ID, version, and icon come from the control NCA (`control.nacp` and `icon_AmericanEnglish.dat`). A plain ISO keeps the volume label. If `autorun.inf` has an `icon=` line that points at an image file, that file is the icon. An icon line such as `setup.exe,0` is left unused.

The bytes are read in memory. Art and the title are then stored in the Discfolio data folder (the `EliteLevel/Discfolio` folder under the account's application data), keyed by a reader version, path, size and modification time. An entry from an older reader is computed again. Reopening the library uses that cache. Nothing is written next to the image, and the image is not modified.

Right-click a library or recent tile and choose Remove from library, or use the same button on the side card. That removes the entry and deletes its cached art and metadata. The image file is not deleted or changed. Clear missing, on the library and on Open images, removes entries whose file or folder is no longer there. A drive entry is marked Drive. It counts as missing when that drive has no disc, and Clear missing is the only thing that removes it. A long title wraps to two lines on the side card, the hero, and the tiles. Point at the title to see the full text.

Open disc drive lists drives that have a disc, with the letter or device, the volume label, the size and the detected platform. Rip to ISO, on the side card while a drive is open, copies sectors into a file you choose. Progress and cancel are on that dialog. When it finishes, the hashes and any bad sectors are shown, and Add to library opens the new ISO. A bad sector is retried, then stored as zeros.

PlayStation 3 game data on a physical disc is encrypted. The file list is readable. Load disc key accepts a `.ird`, a 32-character hex key, or a 16-byte file you already have. The files are not decrypted. Xbox and Xbox 360 game partitions need flashed drive firmware. Discfolio still shows the video partition and the rest of what the drive can read. Audio CD tracks are not read. If Windows or Linux denies access to the device, the message says what permission is missing. The drive is never written.

## Updates

The installed app checks the public GitHub releases for EliteLevelSoftware/EliteLevelDiscfolio. The check runs on startup unless you turn it off under Settings. Check now uses the same source. The channel is stable or prerelease. Stable is the default.

When a newer release is listed, Discfolio shows Update available and downloads it in the background. Choose OK to restart and install it. Not now leaves the download unused. Discfolio does not install an update by itself.

Leave the update source blank. That selects the public releases, and no token is required. A different feed can be pasted there. A private feed can read a token from the `DISCFOLIO_UPDATE_TOKEN` environment variable, or from `UpdateToken` in the local settings file. A public repository does not need either. The build never contains a token.

A copy you start from `dotnet run`, or from a folder that was not installed, has nothing to update. Check now says so.

Extract asks for a destination. Skip files that already match is on by default: an existing file of the same size is left in place and counted as success. Protected paths are listed in the dialog and skipped unless you include them. Included protected files are copied byte for byte and are not decrypted.

## Command line

```text
dotnet run --project src/EliteLevel.Disc.Cli -- drives
dotnet run --project src/EliteLevel.Disc.Cli -- info D:
dotnet run --project src/EliteLevel.Disc.Cli -- ls /dev/sr0 --long
dotnet run --project src/EliteLevel.Disc.Cli -- rip D: --out game.iso --retries 5
dotnet run --project src/EliteLevel.Disc.Cli -- info game.iso
dotnet run --project src/EliteLevel.Disc.Cli -- list game.iso --path /PS3_GAME --long
dotnet run --project src/EliteLevel.Disc.Cli -- extract game.iso --out ./out --pattern "*.png" --skip-existing
dotnet run --project src/EliteLevel.Disc.Cli -- hash game.iso --manifest game.sha256
dotnet run --project src/EliteLevel.Disc.Cli -- search game.iso ICON0
dotnet run --project src/EliteLevel.Disc.Cli -- info game.nsp --keys ./prod.keys
dotnet run --project src/EliteLevel.Disc.Cli -- list game.xci --keys ./prod.keys --long
dotnet run --project src/EliteLevel.Disc.Cli -- art game.nsp --keys ./prod.keys --out ./art
```

`drives --json` prints the drives that have a disc. `info`, `list`, `ls`, `extract` and `art` take a drive such as `D:` or `/dev/sr0` in place of an image file. `rip` writes the ISO plus `.sha256`, `.sha1` and `.md5` beside it. Bad sector numbers go in `.badsectors` when any read still fails. `--disc-key` points at a `.ird` or a key file. `--keys` points at your `prod.keys`, or a folder containing `prod.keys` and `title.keys`. Neither key is printed.

Exit codes:

| Code | `kind` with `--json` | Meaning |
|---|---|---|
| 0 | none | Success |
| 1 | `user` | Usage or another user error |
| 2 | `unsupported` | The file is not a format this version can read |
| 3 | `needs-keys` | Reserved. This version does not return it |

`--json` prints indented JSON on standard output. Success output is the command's result. A failure is one error object, and the exit code is the same as the text mode:

```json
{
  "error": {
    "code": 2,
    "kind": "unsupported",
    "message": "This file is a compressed Nintendo Switch image (NSZ or XCZ). Decompression isn't supported yet."
  }
}
```

Without `--json`, the same message is written to standard error.

`info` prints the title, then the title ID, version, category, parental level and art when those were found. The art line names the files inside the image, for example `ICON0.PNG, PIC1.PNG`, or `none`. With `--json` the same values are `title`, `titleId`, `version`, `category`, `parentalLevel`, `icon` and `banner`. `info` also prints the visible file count. If the default extract will skip protected paths, the files line also names that count, for example `22 files, plus 150 protected system entries skipped on extract`. With `--json`, `files` is the visible count and `protected` is the skipped count. `extract` reports that same protected count for a full extract: it includes `EBOOT.BIN`, `PS3_UPDATE`, `LICDIR`, and files under `$SystemUpdate`, including when that folder is hidden from the listing. `--show-system` lists the folder and does not change the protected count. `--include-protected` copies those bytes unchanged and reports protected 0 for that run.

`art --out <folder>` writes the icon and banner into that folder as `icon.png` (or the matching image extension) and `banner.png`. The folder is the only place those files are written. If the image has no title art, the command exits 1.

An unrecognized file names the readers this version has: ISO 9660/Joliet (including PS3), Xbox and Xbox 360 XDVDFS, Nintendo Switch NSP and XCI, and folder dumps. A compressed NSZ or XCZ is a different message and still exits 2. A Switch NSP or XCI without keys exits 0; the notice says keys have to come from your own console.

## Tests

```text
dotnet test
```

Tests build tiny ISO 9660 and XDVDFS images, and tiny PARAM.SFO, PNG, XEX, XBE, PFS0, HFS0, and NCA structures, in a temporary directory. The NCA test generates its own dummy keys. They do not use retail game data or console keys. Drive tests put those images behind an in-memory block device, including injected read errors, and do not open a real optical drive. Desktop tests keep settings and the art cache in a temporary directory as well, so a sample image is not added to the library on this machine. Library removal is tested the same way: the temporary image file is still there afterwards, and only the temporary cache entry is deleted.
