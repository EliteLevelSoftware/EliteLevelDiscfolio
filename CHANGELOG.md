# Changelog

## 0.6.1

- Release files are named for the system they install. Download `Discfolio-<version>-Windows-Setup.exe`, `Discfolio-<version>-macOS-AppleSilicon-Setup.pkg`, or `Discfolio-<version>-Linux-x64.AppImage`. Portable zips and delta packages are no longer published.
- `*-full.nupkg` and `releases.*.json` stay on the release under the names the built-in updater requests. The release notes label those files so they are not installers. `SHA256SUMS.txt` lists every other file on the release under the name that was uploaded.

## 0.6.0

- NSZ and XCZ images open like NSP and XCI. An NSZ is a PFS0 whose NCA files are stored as NCZ. An XCZ is an XCI whose NCA files are stored as NCZ. The entries are listed as the decompressed NCA. With the same keys as 0.5.0, RomFS, ExeFS, the title, the title ID, the version and the icon are read from that NCA.
- An NCZ starts with the original 0x4000-byte NCA header, then an NCZSECTN table. The body is one zstd stream, or an NCZBLOCK table of independent zstd blocks. A block stored at least as large as its plain size is kept raw. Sections encrypted with AES-CTR are compressed decrypted and restored with the key stored in the section record, so the result matches the original NCA without prod.keys. An AES-XTS section is restored the same way when the keys you supplied can read the NCA key area. Without those keys, the XTS body is left as the bytes that were compressed.
- `extract --decompress` writes an NCZ entry as one decompressed `.nca`. The extract page and the extract dialog have the same toggle. `convert game.nsz --out game.nsp` and `convert game.xcz --out game.xci` write the decompressed container, report progress, and print its SHA-256.
- A file that is only a zstd frame, or an NSZ or XCZ extension that is not a PFS0 or XCI, is still refused. A truncated or corrupt NCZ says what is wrong, and the other files in the container stay listed. Decompression is streamed and can be cancelled.
- Zstandard decompression uses ZstdSharp.Port, which is MIT licensed.

## 0.5.0

- Nintendo Switch NSP and XCI images open and can be browsed. An NSP is a PFS0 container: NCAs, tickets, certificates, and `cnmt.xml` are listed, and those entries extract as stored. An XCI reads the game card header and the HFS0 partitions (`update`, `normal`, `secure`, `logo`, and any others in the root).
- NCA contents stay encrypted unless you supply your own keys. Settings takes a path to `prod.keys`, or to a folder that also contains `title.keys`. The command line uses `--keys`. Discfolio does not ship keys. With a header key, NCA headers decrypt and RomFS and ExeFS can be browsed and extracted. The title, title ID, and version are read from NACP, and `icon_AmericanEnglish.dat` is used for the library tile. Without keys, the container still opens and the notice says the keys have to come from your own console.
- A compressed NSZ or XCZ is recognized. Solid archives are refused. An NCZ entry inside a container is listed and is not decompressed.
- The art cache key includes the keys path, so adding keys and reopening an image computes the title and icon again.
- macOS releases for Apple silicon are packed with Velopack. The release gets an unsigned setup pkg and a portable zip. The build machine has no Apple signing identity, so Gatekeeper asks before the app opens.

## 0.4.0

- Open a disc in an optical drive and browse it the same way as an image file. The drive is read a sector at a time and is never written. Windows uses the drive letter, Linux uses `/dev/sr*` and `/dev/cdrom`, and macOS tries `/dev/rdiskN` when the disk is optical.
- Rip to ISO copies the disc with progress and cancel. A sector that still fails after the configured retries is stored as zeros and reported. SHA-256, SHA-1 and MD5 are written next to the ISO. The app can then add that ISO to the library.
- Files and folders can be extracted straight from the drive. Title art is cached from the volume label, the disc size and a hash of the first sectors, so it does not depend on a file path.
- PlayStation 3 game data on a disc is encrypted. The file list can be read. A `.ird` or a 16-byte key you already have can be loaded; this version does not decrypt the files. Xbox and Xbox 360 game partitions need a drive with flashed firmware. The video partition and anything else the drive can see stay readable. Audio CD tracks are not supported.
- A drive entry in the library is marked as a drive. A drive with no disc counts as missing and is removed only when you click Clear missing.
- The command line lists drives with `drives` (`--json` included). `info`, `list` (`ls`), `extract` and `art` accept a drive such as `D:` or `/dev/sr0`. `rip <drive> --out <file.iso>` writes the image.
- If the operating system refuses raw access, Discfolio explains that Windows needs an administrator and Linux needs permission to read the device, instead of crashing.

## 0.3.1

- Right-click Remove from library on a library tile, and on the open images list, removes that entry and its cached art. The image file is left in place.

## 0.3.0

- A library entry can be removed from the tile menu or the side card. That deletes the entry and its cached art and metadata. The image file is left in place. Clear missing drops entries whose file is no longer there.
- Long titles wrap to two lines on the side card, the hero, and the tiles. The full title is on the tooltip.
- Settings can check for updates on startup, or check now. The channel is stable or prerelease. An update notice downloads in the background and restarts only after you choose OK.
- Windows releases are packed with Velopack. The setup program installs per user to `%LocalAppData%\Discfolio`, adds Start Menu and Desktop shortcuts, uses the Discfolio icon, and uninstalls from Installed apps. The portable zip, full package, and delta package (when a previous release exists) are published with it. The app reads the public GitHub releases. No token is required.
- The download page points at those GitHub release files: Setup.exe, the portable zip, and SHA256SUMS.txt. The window screenshot matches this version. The separate WiX installer is no longer built.

## 0.2.1

- Title art is found when `PS3_DISC.SFB`, `PS3_GAME/PARAM.SFO`, `default.xex` or `default.xbe` sits up to two folders below the image root. The shallowest match is used. A PlayStation 3 disc whose game folder is nested under the title name, such as a `POTCAWE` volume, shows that title and its icon instead of the volume label.
- The art cache key includes a reader version, so an entry saved before this fix is computed again.

## 0.2.0

- Library tiles use the icon from the image. The open banner uses PIC1 (or PIC0, or a softened icon) and the side card shows the title and the title ID. PlayStation 3 reads PARAM.SFO, ICON0.PNG and PIC1.PNG. Xbox 360 reads the default.xex execution info and the embedded XDBF title and image, and nxeart when that file is present. Original Xbox reads the default.xbe certificate and the $$XTIMAGE section when it holds a PNG or JPEG. Other ISO 9660 images keep the volume label, and a simple autorun.inf icon is used when the icon line points at an image file.
- Extracted art and metadata are cached in the Discfolio data folder, keyed by path, size and modification time. The image itself is not modified.
- Search matches the title and the title ID as well as file names.
- `info` prints the title, title ID, version and which art was found. `art --out` writes the icon and banner.
- Tests keep settings and the library cache in a temporary directory.

## 0.1.1

- Unsupported files name only the formats this version can read: ISO 9660/Joliet (including PS3), Xbox and Xbox 360 XDVDFS, and folder dumps. The list is built from the registered readers. An NSP or XCI is reported as a Switch image, which is not supported yet. The exit code is still 2.
- `--json` writes failures as a JSON error object on standard output. Exit codes are unchanged: 0 success, 1 user error, 2 unsupported, and 3 reserved for a later release that needs keys.
- `info` and `extract` share one count of protected system entries. Files under a hidden `$SystemUpdate` folder are part of that count and are left out of the visible file total. The desktop summary shows the same protected count.

## 0.1.0

- Initial read-only release for ISO 9660/Joliet, Xbox and Xbox 360 XDVDFS, and folder dumps.
