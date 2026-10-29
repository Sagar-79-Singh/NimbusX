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
| `Publish the GitHub Release` | on a push to `main`: collects those artifacts and creates/updates the `v1.0.0` release with checksums | ✅ (run 40868739037, tag `v1.0.0`) |

The test step itself reports, verbatim, on the pull request:

```
Test Run Successful.
Total tests: 190
Passed: 190
Runner: win25-vs2026, .NET 10.0.403
```

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
* the whole xunit suite passes: **190 of 190 tests** (`test-results` keeps the `.trx`; the summary line is posted
  on the pull request by the workflow itself);
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

**There is a published release: <https://github.com/Sagar-79-Singh/NimbusX/releases/tag/v1.0.0>**

| Asset | Size | SHA-256 |
|---|---|---|
| `NimbusX-1.0.0-win-x64.exe` | 128.6 MB | `2aa169e444b123cb762447b098bfa493933f58dc1016bd405806e63b8ab7359c` |
| `NimbusX-Setup-1.0.0.exe` | 111.2 MB | `c390803bb97dfa1da06d56282c90feb47d98321718cbfacadd1e53d29da327bd` |
| `cpx-1.0.0-win-x64.exe` | 71 MB | `851c8fbeb4563db7daf0c6f4e816844de65d109a11b1889ceef6652478b37eb1` |
| `SHA256SUMS.txt` | — | the same three lines, to check with `Get-FileHash` |

GitHub shows the same digest beside every asset. `SHA256SUMS.txt` is generated by the same step that produces the
files, so it lists those three hashes.

How the next one is produced, with no manual step:

1. **Merge to `main`.** The `build` workflow runs the static gate, the build, the tests, the self-contained publish and
   the installer, then its `release` job assembles `NimbusX-<version>-win-x64.exe`, `cpx-<version>-win-x64.exe`,
   `NimbusX-Setup-<version>.exe` and a fresh `SHA256SUMS.txt` and creates or updates the `v<version>` release.
   Nothing is published from a pull request.
2. **Tag a new version** (`git tag v1.0.1 && git push origin v1.0.1`, or *Run workflow* on **release**) for a
   versioned release built from that tag; `release.yml` reuses the same steps and the description in
   `.github/release-notes.md`.
3. Every run also keeps its artifacts (`NimbusX-win-x64-exe`, `NimbusX-win-x64-folder`, `cpx-win-x64`,
   `NimbusX-Setup`, `test-results`, `build-log`) under Actions ▸ the run ▸ Artifacts.

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
