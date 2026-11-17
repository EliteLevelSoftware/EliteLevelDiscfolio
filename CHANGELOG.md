# Changelog

## 0.1.1

- Unsupported files name only the formats this version can read: ISO 9660/Joliet (including PS3), Xbox and Xbox 360 XDVDFS, and folder dumps. The list is built from the registered readers. An NSP or XCI is reported as a Switch image, which is not supported yet. The exit code is still 2.
- `--json` writes failures as a JSON error object on standard output. Exit codes are unchanged: 0 success, 1 user error, 2 unsupported, and 3 reserved for a later release that needs keys.
- `info` and `extract` share one count of protected system entries. Files under a hidden `$SystemUpdate` folder are part of that count and are left out of the visible file total. The desktop summary shows the same protected count.

## 0.1.0

- Initial read-only release for ISO 9660/Joliet, Xbox and Xbox 360 XDVDFS, and folder dumps.
