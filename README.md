# NimbusX

**Your cloud drives, with a drive letter.** NimbusX turns Google Drive, OneDrive, Dropbox, pCloud, Box, Yandex Disk,
Koofr, MEGA, WebDAV, SFTP and (experimental) TeraBox into ordinary Windows drives backed by a local cache, then adds
the things a cloud drive needs to run a game: real startup timings, a warm-up pass before launch, honest diagnostics,
and a cache that never eats your disk.

> NimbusX is a from-scratch rebuild of the CLOUDPLAY X idea. It is not a fork: no kernel driver, no filter driver, no
> ETW session, no administrator rights, no changes to Windows security settings.

---

## What it does

| Area | What actually happens |
|---|---|
| **Cloud drives** | One rclone process per account, mounted to a free drive letter through WinFsp, with the loopback rc interface bound to `127.0.0.1` using a random port and a random password. |
| **Add an account** | OAuth providers open the provider's own sign-in page; Koofr/MEGA/WebDAV/SFTP ask for exactly the fields rclone needs; TeraBox uses an embedded WebView2 window. The login is proven by reading the account before it is saved. |
| **Smart cache** | A single budget (10/20/30/40 GB or custom) split evenly across mounted drives, each mount given an explicit `--vfs-cache-max-size`. A disk guard pauses prefetch before the Windows reserve is touched. |
| **Game library** | Scans a mounted drive for executables, reads their PE headers, and reports confidence (`Possible` → `Likely` → `Confirmed` only after a launch really stayed up). Installers, crash handlers and redistributables are filtered out. |
| **Game accelerator** | Warms the executable plus the files the launcher actually observed being read, and shows the median startup time of verified launches — never a guess. |
| **Performance** | Cache growth chart from real measurements, prefetch jobs with byte counts, streaming sessions with throughput. |
| **Streaming** | A loopback-only media proxy (`/s/<token>`, `Range`/`206`, `ETag`, 16-connection cap, 120 s idle timeout) so a media file on the cloud drive streams like a local file. |
| **Diagnostics** | WinFsp, rclone, cache, config encryption, mounts and accounts, each with a real message and a real fix; one-click bundle export with every secret redacted. |
| **Command line** | `cpx` — the same engine, scriptable: `version`, `doctor`, `status`, `mounts`, `accounts`, `mount`, `unmount`, `cache`, `games`, `logs`, `diag`, with `--json` on the reporting commands. |

Everything above is implemented in this repository. There is no simulated success anywhere: when something cannot be
measured, the UI says **Unavailable** and says why.

---

## Requirements

* Windows 10 version 1809 (build 17763) or newer, or Windows 11 — **x64**
* [WinFsp](https://winfsp.dev) 1.12 or newer (2.1.25156 is the pinned, tested build) — needed to mount a drive
* .NET 10 desktop runtime (or the self-contained build)
* Microsoft Edge WebView2 runtime — only for the in-app TeraBox sign-in window
* No administrator rights for NimbusX itself

NimbusX runs `asInvoker`. WinFsp is the one component that needs an elevation prompt, and that prompt belongs to
WinFsp's own installer, which you run yourself.

---

## Install

### From a release

1. Download `NimbusX-Setup-<version>.exe` and `SHA256SUMS.txt`; verify the hash.
2. Run the installer. It installs per user into `%LOCALAPPDATA%\Programs\NimbusX` and never asks for elevation.
3. Start NimbusX. The first-run wizard asks for one thing: a cache folder on a local NTFS drive.
4. **Cloud Drives → Add account**, pick a provider, sign in.
5. **Dashboard → Mount** (or "Mount all"). The drive letter appears in Explorer.

### From the CI build (no toolchain needed)

The GitHub Actions workflows build the real executables on a Windows runner:

| What | Where | Produces |
|---|---|---|
| every push to `main` / every pull request | **Actions → build → the run → Artifacts** | `NimbusX-win-x64-exe` (one self-contained `NimbusX.exe`), `NimbusX-win-x64-folder`, `cpx-win-x64`, `NimbusX-Setup` (the installer, compiled by Inno Setup), plus `test-results` and `build-log` |
| a tag such as `v1.0.0` (or *Run workflow* on **release**) | **Releases** | `NimbusX-Setup-<version>.exe`, a standalone `NimbusX-<version>-win-x64.exe`, `cpx-<version>-win-x64.exe` and `SHA256SUMS.txt` |

So: open the newest green run, download `NimbusX-win-x64-exe` and run it — it is self-contained (the .NET runtime is
inside the file) and needs no administrator rights. WinFsp is still a separate, visible install; NimbusX tells you
exactly which build to get and verifies its SHA-256.

> Every job of the `build` workflow — static checks, build, tests, publish, installer — is green on
> `windows-latest` with .NET 10. What that does *not* prove is in `BUILD_STATUS.md`: no window has been shown, no drive
> mounted and no provider contacted yet.

### From source

```powershell
git clone https://github.com/Sagar-79-Singh/NimbusX.git
cd NimbusX
pwsh -File scripts/fetch-deps.ps1     # downloads rclone, the TeraBox build and WinFsp, verifying SHA-256
dotnet build NimbusX.sln -c Release
dotnet test  tests/NimbusX.Tests/NimbusX.Tests.csproj -c Release
dotnet run --project src/NimbusX.Desktop
```

A full release build (dependencies → build → tests → publish → installer) is one command:

```powershell
pwsh -File scripts/build.ps1 -Version 1.0.0
```

---

## Command line (`cpx`)

```
cpx version                       Show the version
cpx doctor [--json]               Check WinFsp, rclone, cache and accounts
cpx status [--json]               Mounts, cache usage, running games
cpx mounts [--json]               List mounted drives
cpx mount <account|all>           Mount one account (or every auto-mount account)
cpx unmount <account|all>         Unmount a drive
cpx accounts [--json]             List connected cloud accounts
cpx accounts add <provider>       Connect a new account (prompts; secrets are never echoed)
cpx accounts remove <account>     Forget an account locally (nothing is deleted in the cloud)
cpx accounts quota [<account>]    Refresh quota for one or all accounts
cpx cache status [--json]         Cache budget and real usage
cpx cache set <gigabytes>         Set the budget (10, 20, 30 or 40)
cpx cache clear <account|all>     Delete cache files (pinned files are kept)
cpx cache pins <account>          List pinned files
cpx cache pin <account> <path>    Pin a file so a clear keeps it
cpx games scan <account> [root]   Find game folders on a mounted drive
cpx games list [<account>]        Show the game library
cpx games play <gameId>           Launch and report the measured result
cpx stream <account> <path>       Serve a file to a player over 127.0.0.1
cpx stream list                   List active stream sessions
cpx stream stop <session>         Stop a stream session
cpx diag export [--note "text"]   Write a redacted diagnostics bundle
cpx deps install                  Download and verify the pinned rclone builds
cpx logs [--tail 40]              Show the newest log lines
```

Exit codes: `0` success, `1` failure, `2` usage error, `3` a dependency is not ready (the message says which).

---

## Security model

| Concern | How NimbusX handles it |
|---|---|
| Secrets at rest | Windows DPAPI (`CurrentUser`, per-purpose entropy). Never a plain file. |
| Secrets in transit | Only HTTP bodies to `127.0.0.1`, or provider HTTPS. **Never** on a command line. |
| rc interface | Bound to `127.0.0.1`, random port, random 40-character password, generated per mount. |
| Logs and exports | Every line passes through a redactor (tokens, cookies, passwords, `Authorization`, URL credentials, known token shapes) before it is written or exported. `rclone.conf` is always excluded from exports. |
| Mount flags | NimbusX builds the command line itself. Extra flags a user adds are checked against the installed `rclone mount --help`, and anything touching `rc`, `config`, cache/log paths, `--daemon` or a password prompt is refused. |
| Cloud data | Never deleted. Removing an account only removes local configuration and its DPAPI-protected secrets. |
| Games | NimbusX starts the game's own executable and watches it. No injection, no launcher/DRM/anti-cheat bypass, no ownership checks. |
| Windows settings | Nothing is changed: no firewall rule (loopback needs none), no security option, no service, no driver. |
| Single instance | A lock file in the data folder with the owning PID; a second start offers to focus the running one. |

---

## Where your data lives

```
%LOCALAPPDATA%\NimbusX\                    settings.json, accounts.json, mounts.json, games.json,
                                           pools.json, cache-pins.json, nimbusx.db, rclone.conf
%LOCALAPPDATA%\NimbusX\secrets\            DPAPI-protected blobs
%LOCALAPPDATA%\NimbusX\logs\               nimbusx-YYYYMMDD.log (redacted)
%LOCALAPPDATA%\NimbusX\rclone-logs\        rclone output per mount (redacted on export)
%LOCALAPPDATA%\NimbusX\tools\rclone\       rclone 1.75.2
%LOCALAPPDATA%\NimbusX\tools\terabox\      rclone-extra (TeraBox only)
%LOCALAPPDATA%\NimbusX\launches\           per-launch diagnostics for a game that failed
%LOCALAPPDATA%\NimbusX\exports\            diagnostics bundles
<your cache folder>\m-<letter>\            the rclone VFS cache for one mount
```

A `portable.flag` file next to `NimbusX.exe` switches everything to `.\data`, for a USB stick or a test rig.

---

## Repository layout

```
src/NimbusX.Core           logging + redaction, DPAPI secrets, process runner, paths, atomic JSON,
                           formatting, drive/port allocators, settings
src/NimbusX.Mount          pinned dependencies, provisioner, WinFsp detection, rc client, log tailer,
                           flag catalogue, MountManager (start/ready/health/restart/unmount)
src/NimbusX.Providers      provider catalogue, OAuth and credential flows, TeraBox cookie flow, account store
src/NimbusX.Cache          budget plan, disk safety guard, usage reader, pin store, cleaner
src/NimbusX.Streaming      media probe, HTTP range server, sessions, prefetch engine
src/NimbusX.Games          PE inspection, executable ranking, library scanner, launcher, verification
src/NimbusX.Diagnostics    environment checks, dependency reports, diagnostic bundle export
src/NimbusX.App            composition root, profile/launch database, accelerator, launch coordinator
src/NimbusX.Desktop        WPF shell (MVVM, CommunityToolkit.Mvvm): 8 pages + dialogs
src/NimbusX.Cli            cpx
tools/NimbusX.SimCloud     a local WebDAV server for testing mounts without a cloud account
tools/devcheck             static checks that replace the compiler in a no-SDK environment
tests/NimbusX.Tests        xunit tests for every module
installer                  Inno Setup script (per-user, no elevation)
scripts                    fetch-deps.ps1, verify-deps.ps1, build.ps1
```

---

## Testing without a cloud account

`tools/NimbusX.SimCloud` is a real (small) WebDAV server on `127.0.0.1` with basic auth:

```powershell
dotnet run --project tools/NimbusX.SimCloud -- --root C:\dev\simcloud --port 0
# prints e.g. http://127.0.0.1:54321/
```

Add it in NimbusX as a **WebDAV** account (URL `http://127.0.0.1:54321/`, vendor `other`, user `nimbusx`,
password `nimbusx`), then mount, scan for games, pin a file, clear the cache and stream a media file — every code path
is the same one a real provider uses. It implements `OPTIONS`, `PROPFIND`, `GET`/`HEAD` with `Range`, `PUT`,
`MKCOL`, `DELETE`, `MOVE` and `COPY`, and refuses path traversal.

---

## Honest limitations

Read `BUILD_STATUS.md` before trusting anything here. In short:

* The code was written in an environment **without the .NET SDK or Windows**. The GitHub Actions pipeline then compiled
  it on `windows-latest` with .NET 10, ran the whole test suite, published the self-contained `.exe` and compiled the
  installer — all four jobs green. The 25 compiler errors the first runs found are fixed; `BUILD_STATUS.md` lists
  exactly what is still unverified (no window has ever been shown, no drive has ever been mounted, no provider has
  ever been contacted).
* No cloud provider was contacted while building this. Provider behaviour comes from rclone 1.75.2's own source and
  its documented option names.
* TeraBox is **experimental**: unofficial API, slow downloads, and a cookie that expires. The provider is labelled as
  experimental everywhere it appears.
* The game accelerator cannot see inside a running game without injecting code, which NimbusX never does. It warms what
  it observed and reports a median only when at least three verified launches exist.
* Frame-rate overlays, GPU power manipulation and similar "boost" features from the original product are **not**
  implemented, because they cannot be done honestly without a driver or a fake metric.

---

## Licence

MIT for NimbusX itself. rclone, rclone-extra and WinFsp keep their own licences — see `LICENSE` and
`docs/VERIFIED_EXTERNALS.md`.
