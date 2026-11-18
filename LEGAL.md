# Legal and safety

EliteLevel Discfolio is for people who already have a legal copy of a game and a dump, folder, or disc they are allowed to possess. It is not a key-distribution or circumvention product.

- The source image, folder, or optical drive is opened read-only. Extract writes only to a folder you choose. Rip to ISO writes a new file you choose. The drive is never written.
- A sector copy writes the bytes the drive returns. PlayStation 3 game data is not decrypted. Discfolio does not derive disc keys, download keys, or ship exploits. A `.ird` or key file you already have can be loaded, and the key is not stored in the library.
- No encryption keys and no game content are bundled, in the repository or in a release.
- PlayStation 3 paths such as `EBOOT.BIN`, `PS3_UPDATE`, and `LICDIR` are skipped by the default extract. An explicit option can copy those bytes out unchanged. That is a copy, not a decryption.
- Xbox and Xbox 360 images are read as XDVDFS file systems. Retail executables are not decrypted.
- Nintendo Switch containers are recognized and are not read.
- PlayStation, Xbox, Nintendo, and related names are trademarks of their owners. Elite Level Software is not affiliated with them.

Bug reports that ask for keys, circumvention, or copyrighted test files will be closed. Those files are not accepted in pull requests.
