# NimbusX 1.0.0

**Your cloud drives, with a drive letter.** Google Drive, OneDrive, Dropbox, pCloud, Box, Yandex Disk, Koofr, MEGA,
WebDAV, SFTP and (experimental) TeraBox as ordinary Windows drives, backed by a local cache, plus the things a cloud
drive needs to run a game: measured startup timings, a warm-up pass, honest diagnostics and a cache that never eats
your disk.

## Download

| File | What it is |
|---|---|
| **`NimbusX-Setup-1.0.0.exe`** | The installer. Per-user, `PrivilegesRequired=lowest`, no elevation. Start-menu entry, `cpx` on the user `PATH`, bundled and SHA-256-verified rclone 1.75.2, the TeraBox build and the WinFsp MSI, licence texts. |
| **`NimbusX-1.0.0-win-x64.exe`** | The app as one self-contained file. No .NET runtime to install; unpack anywhere and run. |
| **`cpx-1.0.0-win-x64.exe`** | The command line tool on its own (`cpx doctor`, `cpx status`, `cpx mounts`, `cpx mount …`, `--json`). |
| `SHA256SUMS.txt` | SHA-256 of the three files above. Verify with `Get-FileHash <file> -Algorithm SHA256`. |

## Before you run it

* Windows 10 1809+ or Windows 11, **x64**.
* **WinFsp** is the only thing that ever needs an administrator: install it once from <https://winfsp.dev>
  (the pinned, tested build is 2.1.25156, which the installer already carries). NimbusX itself runs `asInvoker`
  and never changes Windows security settings.
* Microsoft Edge **WebView2** runtime — only for the in-app TeraBox sign-in window.

## First run

1. Start NimbusX. The first-run wizard asks for one thing: a cache folder on a local NTFS drive.
2. **Cloud Drives → Add account**, pick a provider, sign in. The login is proven by reading the account before it is
   saved; secrets are protected with DPAPI and never written to a log or a command line.
3. **Dashboard → Mount** (or "Mount all"). The drive letter appears in Explorer.

## What is verified, and what is not

These binaries were produced by the GitHub Actions pipeline on `windows-latest` with .NET 10.0.403: the solution
compiled, **190 of 190 unit tests passed**, the self-contained single-file publish was smoke-tested (PE header, size,
`cpx version`), and the Inno Setup installer was compiled against digest-verified dependencies.

Not verified, and stated plainly in `BUILD_STATUS.md`: no window has ever been rendered, no rclone mount has been
performed, no cloud provider has been contacted, no DPAPI round trip has run, no game has been launched, and this
installer has not been installed by anyone yet. Where a value has not been measured, the UI says **Unavailable**
rather than inventing one.

MIT licensed. rclone, rclone-extra and WinFsp keep their own licences — see `LICENSE` and
`docs/VERIFIED_EXTERNALS.md`.
