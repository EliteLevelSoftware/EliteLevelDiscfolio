# Using EliteLevel Discfolio

Discfolio opens a disc image or a folder dump you already have and lets you browse it, search by name, compute a SHA-256 hash, and copy files out. The source is never modified.

## Desktop

```text
dotnet run --project src/EliteLevel.Disc.App
```

Drop an image on the window, or use Open an image. Ctrl+O opens a file, Ctrl+K focuses search, Ctrl+E extracts the selection, Ctrl+Shift+E extracts everything, and Ctrl+H hashes the selected file. On macOS the same shortcuts use Command.

Extract asks for a destination. Skip files that already match is on by default: an existing file of the same size is left in place and counted as success. Protected paths are listed in the dialog and skipped unless you include them. Included protected files are copied byte for byte and are not decrypted.

## Command line

```text
dotnet run --project src/EliteLevel.Disc.Cli -- info game.iso
dotnet run --project src/EliteLevel.Disc.Cli -- list game.iso --path /PS3_GAME --long
dotnet run --project src/EliteLevel.Disc.Cli -- extract game.iso --out ./out --pattern "*.png" --skip-existing
dotnet run --project src/EliteLevel.Disc.Cli -- hash game.iso --manifest game.sha256
dotnet run --project src/EliteLevel.Disc.Cli -- search game.iso ICON0
```

Exit codes: `0` success, `1` usage or other user error, `2` unsupported or unreadable image. Exit code `3` is reserved for a later release that needs keys.

## Tests

```text
dotnet test
```

Tests build tiny ISO 9660 and XDVDFS images in a temporary directory. They do not use retail game data.
