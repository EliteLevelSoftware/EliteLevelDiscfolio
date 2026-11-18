<div align="center">

<img src="docs/logo.png" width="128" alt="EliteLevel Discfolio">

# EliteLevel Discfolio

by Elite Level Software

[![License: MIT](https://img.shields.io/badge/license-MIT-b48c28)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512bd4)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-3a3335)](#download)

A read-only explorer for console disc images and folder dumps.

[Download](#download)

<img src="docs/screenshot.png" width="920" alt="EliteLevel Discfolio with a synthetic image open">

</div>

## Download

Windows builds are on the [releases](https://github.com/EliteLevelSoftware/EliteLevelDiscfolio/releases) page: `Setup.exe`, the portable zip, and `SHA256SUMS.txt`.

`Setup.exe` installs per user to `%LocalAppData%\Discfolio`, adds Start Menu and Desktop shortcuts, and registers in Installed apps. It does not need administrator rights. Uninstall from Installed apps. The portable zip runs without installing. `SHA256SUMS.txt` is the SHA-256 digest of each file on that release.

Discfolio checks those public releases for updates when it starts. That check is on by default and can be turned off under Settings. Check now is there too, along with the channel: stable or prerelease. When an update is available, a notice downloads it in the background. Discfolio restarts only after you choose OK. The public releases need no token.

The Linux AppImage is attached when that pack succeeds. macOS (Apple silicon) is an unsigned setup pkg and a portable zip on the same release. Gatekeeper asks before that app opens. You can also build from source.

## Browse

Open an ISO 9660 or Joliet image, an Xbox or Xbox 360 XDVDFS image (plain, XGD1, XGD2, XGD3, or a Redump layout with a video partition in front), or a folder dump. Detection uses the contents of the file, not the extension. A Nintendo Switch NSP or XCI is recognized and refused. Switch images are not supported yet.

The window shows the title, the file tree, and recent images on this machine. A PlayStation 3 image uses the title and title ID from `PARAM.SFO`, `ICON0.PNG` on the library tile, and `PIC1.PNG` (or `PIC0.PNG`) on the banner. Xbox 360 reads the title and title ID from `default.xex`, including the embedded title image, and `nxeart` when that file is present. Original Xbox reads the certificate and `$$XTIMAGE` section of `default.xbe` when that section holds a PNG or JPEG. Those files are recognised at the image root or up to two folders down. Anything else keeps the volume label. A plain ISO can also use an `autorun.inf` icon when that line points at an image file rather than an executable resource.

Art and the title are cached in the Discfolio data folder, keyed by a reader version, the image path, size and modification time, so the library can show them again without opening the disc. The image itself is not modified. Drag an image onto the window to open it. Search matches file names, the title and the title ID.

Right-click a library tile, or use the side card, to remove that entry. Removal deletes the library entry and its cached art and metadata. It does not delete the image file. Clear missing drops entries whose file is no longer there. A long title wraps to two lines, and the tooltip shows the full text.

## Disc drive

Open disc drive reads a disc in an optical drive. Windows uses the drive letter (`D:`), Linux uses `/dev/sr*` and `/dev/cdrom`, and macOS tries `/dev/rdiskN` when that disk is optical. The same readers used for image files detect the format. The drive is opened read-only and is never written. On Windows, raw access may need an administrator. On Linux, the account needs permission to read the device, usually the `cdrom` group. Discfolio says so instead of crashing.

Rip to ISO copies the disc a sector at a time, with progress and cancel. A sector that still fails after the configured retries is written as zeros and listed. SHA-256, SHA-1 and MD5 are written beside the ISO so they can be compared with an archival list. After the rip, the ISO can be added to the library.

Files and folders extract with the same flow as an image. Title art for a drive is cached by the volume label, the disc size and a hash of the first sectors.

A drive entry is marked in the library. A drive with no disc counts as missing. Clear missing removes it. Discfolio does not remove it on its own.

PlayStation 3 game data on a disc is encrypted. A normal drive can list the file system. Reading the game files needs a compatible drive and a disc key from a `.ird` file you supply. Discfolio can load a `.ird`, a 32-character hex key, or a 16-byte key, and does not decrypt the files. Xbox and Xbox 360 game partitions need a drive with flashed firmware. The video partition, and anything else the drive can see, can still be read. Audio CD tracks are not supported.

## Extract

Copy the selection or the whole image to a folder you choose. Skip-existing treats a destination file of the same size as success, so extracting again into the same folder is safe. The source image is opened read-only and is never rewritten.

`EBOOT.BIN`, `PS3_UPDATE`, `LICDIR`, and `$SystemUpdate` are skipped unless you explicitly include protected paths. That copy is still the original bytes.

## Verify

SHA-256 is one click, or `discfolio hash`. SHA-1 and MD5 are available because some archival lists still publish them. A manifest can be saved as text. Click a hash to copy it.

## Command line

`discfolio` is the same core library, without the user interface.

```text
discfolio drives
discfolio info D:
discfolio list /dev/sr0 --long
discfolio extract D: --out ./out --skip-existing
discfolio rip D: --out game.iso
discfolio info game.iso
discfolio list game.iso --path /PS3_GAME/USRDIR --long
discfolio extract game.iso --out ./out --pattern "*.png" --skip-existing
discfolio hash game.iso --manifest game.sha256
discfolio search game.iso ICON0
discfolio art game.iso --out ./art
```

`drives` lists optical drives that have a disc. `--json` works on that command too. `info`, `list` (also `ls`), `extract` and `art` accept a drive in place of an image. `rip` writes the ISO and the three hash files next to it. `--retries` is how many times each sector is read before it is reported as bad. `--disc-key` loads a `.ird` or a key file and does not print the key.

`--json` prints indented JSON on standard output, including failures. A failure looks like this, and the process exit code is unchanged:

```json
{
  "error": {
    "code": 2,
    "kind": "unsupported",
    "message": "This file is a Nintendo Switch image (NSP or XCI). Switch images aren't supported yet."
  }
}
```

Exit codes are `0` success, `1` user error (`kind` is `user`), `2` unsupported image (`kind` is `unsupported`), and `3` reserved for a later release that needs keys (`kind` would be `needs-keys`).

`info` reports the title, title ID, version and which art was found, then the visible file count. When the default extract will skip protected paths, that line also reports them, for example `22 files, plus 150 protected system entries skipped on extract`. The extra number includes files under a hidden `$SystemUpdate` folder. `extract` uses the same definition for its protected count. `art --out` writes `icon` and `banner` files into the folder you name. See [docs/user-guide.md](docs/user-guide.md).

## Build

```text
dotnet test
dotnet run --project src/EliteLevel.Disc.App
dotnet run --project src/EliteLevel.Disc.Cli -- info path/to/image.iso
```

The Windows portable profile:

```text
dotnet publish src/EliteLevel.Disc.App -c Release /p:PublishProfile=win-x64-portable
```

## Legal

Discfolio is for dumps and folders you are allowed to possess. It does not include encryption keys or game content, and it does not modify the source. PlayStation, Xbox, and Nintendo are trademarks of their owners. Elite Level Software is not affiliated with them.

The stance is in [LEGAL.md](LEGAL.md). Third-party licenses are in [THIRD_PARTY_NOTICES](THIRD_PARTY_NOTICES). Changes are in [CHANGELOG.md](CHANGELOG.md). This project is [MIT](LICENSE) licensed, copyright Elite Level Software.
