# Changelog

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
