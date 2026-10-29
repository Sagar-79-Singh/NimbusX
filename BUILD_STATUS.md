# Build status

_Last updated: 2026-10-29_

## Short version

**The tree compiles, the unit tests pass, and the pipeline produces the `.exe`.** All four jobs of the
`build` workflow are green on `windows-latest` with .NET 10:

| Job | What it did | Result |
|---|---|---|
| `Static checks (Linux, no SDK)` | the 13-pass Python gate below + the packaging gate | ✅ |
| `Build and test (Windows)` | `dotnet restore`, `dotnet build -c Release`, `dotnet test` | ✅ |
| `Publish the apps (Windows)` | self-contained single-file `NimbusX.exe`, folder build, `cpx.exe`, smoke-tested (`MZ` header, size, `cpx version` exits 0) | ✅ |
| `Build the installer (Windows)` | downloads and hash-verifies rclone / rclone-extra / WinFsp, builds the payload, compiles `NimbusX-Setup-1.0.0.exe` with Inno Setup | ✅ |

Artifacts of every run (Actions ▸ the run ▸ Artifacts):

| Artifact | Contents |
|---|---|
| `NimbusX-win-x64-exe` | `NimbusX.exe` — self-contained, single file, no .NET runtime needed, `asInvoker` |
| `NimbusX-win-x64-folder` | the same app as a folder (faster start, same code) |
| `cpx-win-x64` | the `cpx` command line tool |
| `NimbusX-Setup` | `NimbusX-Setup-1.0.0.exe` + `SHA256SUMS.txt` |
| `test-results`, `build-log` | the `.trx` results and the full MSBuild log, kept for every run |

## What CI proves, and what it does not

Proved by the Windows runner, on every push:

* the solution restores, compiles with zero errors, and every project targets `net10.0-windows` (or `net10.0` for the
  CLI, Core, mount, cache, streaming and games libraries);
* the whole xunit suite passes (see `test-results`; the summary line is posted on the pull request);
* a real self-contained publish runs, and the produced binary is checked before it is uploaded;
* the Inno Setup script compiles against a real ISCC, with real downloaded dependencies whose SHA-256 digests match
  the pinned values in `src/NimbusX.Mount/DependencyVersions.cs`;
* the compile errors that the static gate could not see are gone. Twenty-five of them were found by this loop and
  fixed (`RaiseChanged` signature, `MountSnapshot` conversions, `LaunchResult`'s private constructor,
  `Environment.NewLine` shadowed by a view model property, a duplicate `account` local, `IReadOnlyList.IndexOf`,
  and the rest of the list in the pull request history).

**Not** proved by CI — none of these has ever run:

| Item | Status |
|---|---|
| NimbusX launched, any window rendered, any page navigated | ❌ never run |
| A real rclone mount with WinFsp, a drive letter in Explorer | ❌ never performed |
| A real provider sign-in (Google, OneDrive, Dropbox, pCloud, Box, Yandex, Koofr, MEGA, WebDAV, SFTP, TeraBox) | ❌ never contacted — the OAuth and rc command sequences come from rclone 1.75.2's source and are pinned, not observed |
| DPAPI protect/unprotect against a real user profile | ❌ no test covers it (Windows API, and it needs the user's own key) |
| The SQLite profile database on a real install | ⚠️ the tests open real files through `Microsoft.Data.Sqlite` on the Windows runner, but never under the installed app |
| Streaming proxy against a real player | ⚠️ the HTTP server logic is unit-tested; no player has talked to it |
| Game launch, "window appeared", median startup against a real game | ❌ never run (the tests use synthetic launch records) |
| WebView2 TeraBox window | ❌ never run |
| `scripts/*.ps1` by hand | ⚠️ they run in CI (`fetch-deps`, `verify-deps`, `build`); nobody has run them on a desktop |
| The installer's *installed* behaviour (Start-menu entry, PATH, uninstall) | ⚠️ compiled, not installed |

Nothing in the list above is silently assumed to work: the UI shows **Unavailable** where a value has not been
measured, `cpx doctor` reports each dependency with a real message and a fix, and `docs/VERIFIED_EXTERNALS.md` records
where every external fact came from.

## The static gate (`tools/devcheck/`)

The gate is not a compiler, and it is kept because it catches the mechanical mistakes that a large hand-written tree
makes, without an SDK: `python3 tools/devcheck/check_csharp.py`.

| Pass | What it proves |
|---|---|
| `check_structure.py` | solution ↔ disk agreement, duplicate project GUIDs, `ProjectReference` targets exist, package versions exist in `Directory.Packages.props`, one namespace per project, every `using NimbusX.*` is reachable, XAML `xmlns`/`x:Type`/element types resolve |
| `check_types.py` + `check_signatures.py` | interface implementation with matching return and parameter types, `override` only names a real inherited member, no two members with the same name and parameters (CS0111) |
| `check_members.py` | every `Type.Member` reference whose type is declared here resolves (partial classes and base classes included) |
| `check_calls.py` | `new T(...)` and `T.Method(...)` argument counts, no instance method called through the type name, no `private` constructor called from outside its type |
| `check_locals.py` | `name.Member` and `name.Member(...)` on explicitly typed locals, parameters, `foreach` variables and fields, one hop down a property chain |
| `check_framework_usings.py` | every framework type a file names has its namespace imported (`ImplicitUsings` is disabled) |
| `check_syntax.py` | the whole tree parses with a real C# grammar (tree-sitter; skipped with a note when it is absent) |
| `check_toolkit.py` | every `[ObservableProperty]` field's `On…Changing`/`On…Changed` partial method matches the generated property |
| `check_xaml.py` | well-formed XAML, `{Binding}` paths on the page view model and on the item type of a `DataTemplate`'s `ItemsSource`, `{StaticResource}` keys, `Style` `TargetType`/`BasedOn`, `x:Name` uniqueness, custom-control attributes, event handler shapes |
| `check_packaging.py` | the Inno Setup script, the PowerShell scripts and the workflow YAML |
| `selftest.py` | all of the above still fire: 28 realistic faults are injected into a copy of the tree, one at a time, and every one must be reported (~290 s) |

Current result on this tree: `checked 156 C# files and 14 XAML files — 0 error(s), 0 warning(s)`, and
`28/28 injected faults detected`.

## Getting the `.exe`

You do not need a toolchain.

1. **Actions → build → the newest run.** Download `NimbusX-win-x64-exe`, unzip it, run `NimbusX.exe`. Self-contained,
   per-user, no administrator rights.
2. For the installer (`NimbusX-Setup-1.0.0.exe`, Start-menu entry, `cpx` on the user PATH, bundled licence texts):
   download the `NimbusX-Setup` artifact of the same run.
3. For a GitHub Release with checksums, push a tag: `git tag v1.0.0 && git push origin v1.0.0`. The `release` workflow
   reuses the same steps and attaches `NimbusX-1.0.0-win-x64.exe`, `cpx-1.0.0-win-x64.exe`,
   `NimbusX-Setup-1.0.0.exe` and `SHA256SUMS.txt`.

WinFsp is not bundled. It is a one-time, per-machine install from <https://winfsp.dev> and the only component that
asks for elevation; NimbusX itself runs `asInvoker`.

## First run on a real machine

```powershell
dotnet restore NimbusX.sln
dotnet build NimbusX.sln -c Release
dotnet test tests/NimbusX.Tests/NimbusX.Tests.csproj
dotnet run --project src/NimbusX.Cli -- doctor      # should list WinFsp, rclone and the cache folder
dotnet run --project src/NimbusX.Desktop
```

Before trusting the mount code, exercise it against the bundled simulator:

```powershell
dotnet run --project tools/NimbusX.SimCloud -- --root C:\dev\simcloud
```

Add its URL as a WebDAV account, mount it, scan it for games, pin a file, clear the cache, stream a file with
`cpx stream`. That path touches the mount manager, the rc client, the cache manager, the game scanner and the
streaming proxy at once.

## Module status

| Module | Compiles | Notes on what is least certain |
|---|---|---|
| `NimbusX.Core` | ✅ | DPAPI, the job object and process observation are Windows-only and have no test; `ProcessRunner` redaction has not met a real rclone |
| `NimbusX.Mount` | ✅ | `RcloneProvisioner` and `MountManager` (ready-wait, restart backoff, reattach) are the riskiest files: written from rclone 1.75.2's source, never observed live |
| `NimbusX.Providers` | ✅ | OAuth state machine follows `fs/config/rc.go`; the question/answer sequence has never been observed against a provider |
| `NimbusX.Cache` | ✅ | Budget arithmetic and the pin store are tested; `CacheUsageReader` parses rclone's vfs metadata files, whose layout is only assumed |
| `NimbusX.Streaming` | ✅ | Range parsing and media probing are tested; the `TcpListener` HTTP server has never served a request |
| `NimbusX.Games` | ✅ | PE inspection and ranking are tested with synthetic headers; "window appeared" has never been checked against a real game |
| `NimbusX.Diagnostics` | ✅ | The export bundle is zipped with every line redacted; the redactor is tested |
| `NimbusX.App` | ✅ | Composition root, SQLite profile database (WAL) and the accelerator; the database is exercised by the tests on the Windows runner |
| `NimbusX.Cli` (`cpx`) | ✅ | Argument parsing is tested; `cpx version` runs in CI, the other commands have not been executed |
| `NimbusX.Desktop` | ✅ | 8 pages + 3 dialogs, MVVM. The XAML compiles and every binding path is checked, but no window has ever been shown |
| `tools/NimbusX.SimCloud` | ✅ | A small WebDAV server for testing. Never run |
| `tests/NimbusX.Tests` | ✅ | The suite passes on the Windows runner |
| `installer/NimbusX.iss` | ✅ | Compiled by CI with real payload files, per-user, `PrivilegesRequired=lowest`; not installed by anyone yet |
| `scripts/` | ✅ | `fetch-deps.ps1` (digest-verified downloads), `verify-deps.ps1`, `build.ps1` — all executed in CI |

## Known gaps and deliberate omissions

1. **No frame-rate or GPU "boost".** An honest implementation needs a driver, ETW or a kernel component, and all three
   are excluded by the design rules of this rebuild.
2. **No kernel filter, no ETW, no service.** Caching is rclone's VFS cache; process observation uses the normal process
   APIs. That caps some measurements (per-file read tracing of a running game, for instance).
3. **TeraBox is experimental** and labelled so everywhere. Its API is unofficial and its cookie expires.
4. **No OAuth device flows**; only the browser flow and the credential flows.
5. **Accelerator predictions need ≥3 launches that showed a window and stayed verified.** Before that the UI shows a
   plan without a prediction instead of inventing a number.
6. **`CacheUsageReader`** may need adjusting after the first live run against rclone 1.75.2.
7. **Multi-range HTTP requests** are refused with 416 on purpose; a player that needs them will not work.
8. **The installer does not bundle WinFsp** (see above) and does not install it silently.

## When something does fail

1. The `build` workflow comments the compiler errors, or the failing test names with their assertions, on the pull
   request. The full logs are in the run's `build-log` and `test-results` artifacts.
2. `python3 tools/devcheck/check_csharp.py` runs anywhere Python 3 runs and catches the mechanical classes of mistake
   before you push; `selftest.py` next to it proves the checks still fire.
3. Every refusal in the app is logged with a reason and a fix; `cpx doctor` prints the same list without the UI.
