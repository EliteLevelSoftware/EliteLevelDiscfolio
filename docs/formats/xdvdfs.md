# XDVDFS reader

The Xbox and Xbox 360 reader in `EliteLevel.Disc.Core` is a clean-room implementation of the public XDVDFS layout. It is not derived from extract-xiso or XISOSharp.

## Sources

- [XDVDFS](https://xboxdevwiki.net/XDVDFS) on xboxdevwiki: 2048-byte sectors, volume descriptor at sector 32 of the game partition, the `MICROSOFT*XBOX*MEDIA` tag at the start of that sector and again at offset `0x7EC`, root-directory sector and size, and the directory attribute bits.
- [Xbox DVD Filesystem Details](https://multimedia.cx/xdvdfs.html) (Xbox-Linux): directory entries stored as a binary tree, left and right links as dword offsets from the start of the directory table, file data addressed by start sector and byte length, unused directory bytes filled with `0xFF`.

## Layouts

The volume descriptor is 32 sectors into the game partition. The reader probes these published partition bases:

| Layout | Partition base |
|---|---|
| Plain XISO | `0` |
| XGD3 | `0x02080000` |
| XGD2 | `0x0FD90000` |
| XGD1 | `0x18300000` |

A Redump-style image has a video partition in front of that base. When the prefix contains an ISO 9660 `CD001` marker, the image is reported as the matching XGD layout with a video partition. The browsable tree is the game partition.

A descriptor that itself starts at byte 0 of the file is also accepted.

`$SystemUpdate` is omitted from the default tree and skipped by the default extract. `info` reports those files in a separate protected count, and `extract` uses that same count. `--show-system` lists the folder. `--include-protected` copies it unchanged.

Security sectors, drive authentication, and executable decryption are out of scope.
