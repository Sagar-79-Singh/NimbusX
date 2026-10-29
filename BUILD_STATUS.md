# Build status

_Last updated: 2026-10-29_

## Read this first

**This source tree has never been compiled.** It was written in a sandbox without a .NET SDK, without Windows, and
without network access to NuGet or to any provider. Every C# and XAML file was checked with a purpose-written static
gate (`tools/devcheck/`, plain Python 3; PyYAML and tree-sitter are optional and only extend it), which is not a
compiler. What it does check:

| Pass | What it proves |
|---|---|
| `check_structure.py` | solution ↔ disk agreement, duplicate project GUIDs, `ProjectReference` targets exist, package versions exist in `Directory.Packages.props`, one namespace per project, every `using NimbusX.*` is reachable through that project's references, XAML `xmlns`/`x:Type`/element types resolve |
| `check_types.py` | a class implements every member of the interfaces it declares, and `override` only names a real inherited member |
| `check_members.py` | every `Type.Member` reference whose type is declared here actually resolves (partial classes and solution base classes included) |
| `check_calls.py` | `new T(...)` argument counts and `T.Method(...)` argument counts against the declarations, no instance method called through the type name, no two methods with the same name *and* the same parameter types |
| `check_syntax.py` | the whole tree parses with a real C# grammar (tree-sitter; skipped with a note when it is not installed) |
| `check_toolkit.py` | every `[ObservableProperty]` field's `On…Changing`/`On…Changed` partial method still matches the generated property type |
| `check_xaml.py` | well-formed XAML, `{Binding}` paths exist on the page view model (and, inside a `DataTemplate`, on the item type of the `ItemsSource`), `{StaticResource}` keys exist, `Style` `TargetType`/`BasedOn` agree, no duplicate `x:Name`, attributes on custom controls exist, event handlers exist in code-behind |
| `check_packaging.py` | the Inno Setup script (sections, `Source:` paths, licence/readme files, `PrivilegesRequired=lowest`), the PowerShell scripts (brace balance, `$PSScriptRoot` joins) and the workflow YAML |
| `selftest.py` | all of the above still *fire*: 19 realistic faults are injected into a copy of the tree, one at a time, and each must be reported |

It does **not** type-check expressions, generic arguments, nullability flow, conversion operators, or anything that
needs the real BCL. Two bugs of exactly that kind were found and fixed while adding the member/call passes
(`AppInfo.WindowTitle` and `TextFormat.Pluralize` were referenced but never declared), so treat the list above as
"fewer compiler errors than before", not "compiles".

So: expect the first `dotnet build` to produce errors. The table below says what is likely to need attention first.

## Verified here / not verified here

| Item | Status |
|---|---|
| Every C# file passes the nine passes listed above | ✅ checked (156 files) |
| Every XAML file is well formed, every binding path exists on a view model or item type, every StaticResource key exists | ✅ checked (14 files) |
| The gate itself is tested (19 injected faults, all reported) | ✅ `selftest.py`, ~130 s |
| Pinned third-party digests are the published GitHub values | ✅ recorded in `src/NimbusX.Mount/DependencyVersions.cs` and `docs/VERIFIED_EXTERNALS.md` |
| `dotnet restore` / `build` / `test` executed | ❌ **never run** — no SDK in the authoring environment |
| Unit tests executed | ❌ **never run** (127 test methods, ~194 cases once `[InlineData]` rows expand; written, not proven) |
| NimbusX launched, any window rendered | ❌ never run |
| A real cloud provider contacted (OAuth, MEGA, WebDAV, TeraBox) | ❌ never contacted |
| rclone mount performed, drive letter checked in Explorer | ❌ never performed |
| WinFsp detection on a machine with WinFsp | ❌ never run |
| DPAPI protect/unprotect round trip | ❌ never run (Windows-only API) |
| Inno Setup script compiled with ISCC | ❌ never compiled |
| `scripts/*.ps1` executed | ❌ never executed |
| GitHub workflows executed | ❌ never executed |

## First three commands to run (on Windows, with .NET 10 and WinFsp installed)

```powershell
dotnet restore NimbusX.sln
dotnet build NimbusX.sln -c Debug
dotnet test tests/NimbusX.Tests/NimbusX.Tests.csproj
```

Then, before trusting the UI:

```powershell
dotnet run --project tools/NimbusX.SimCloud -- --root C:\dev\simcloud
dotnet run --project src/NimbusX.Cli -- doctor
dotnet run --project src/NimbusX.Desktop
```

Add the SimCloud URL as a WebDAV account, mount it, scan it, pin a file, clear the cache. That exercises the mount
manager, the rc client, the cache manager, the game scanner and the streaming proxy against a real (if local) server.

## Module status

| Module | Written | Notes on what is least certain |
|---|---|---|
| `NimbusX.Core` | Complete | DPAPI and job-object code is Windows-only and has never run. `ProcessRunner` output redaction paths are untested against a real rclone. |
| `NimbusX.Mount` | Complete | `RcloneProvisioner` (zip extraction + hash recording) and `MountManager` (ready-wait, restart backoff, reattach) are the two riskiest files; they were written from rclone's observed behaviour, not from a live run. |
| `NimbusX.Providers` | Complete | OAuth state machine follows `fs/config/rc.go`; the exact question/answer sequence has not been observed live. TeraBox cookie handling follows the fork's option names. |
| `NimbusX.Cache` | Complete | Budget arithmetic and the pin store are unit-tested on paper; `CacheUsageReader` parses rclone's vfs metadata files, whose exact layout may differ by version. |
| `NimbusX.Streaming` | Complete | Range parsing and media probing are unit-tested on paper; the raw `TcpListener` HTTP server has never served a request. |
| `NimbusX.Games` | Complete | PE inspection and executable ranking are unit-tested on paper with synthetic headers; the "window appeared" heuristic has not been observed against a real game. |
| `NimbusX.Diagnostics` | Complete | The export path writes a ZIP with redacted contents; the redactor is applied to every report line (a real bug fixed while writing the tests). |
| `NimbusX.App` | Complete | Composition root, profile database (SQLite, WAL) and the accelerator. `Microsoft.Data.Sqlite` has never opened a real file here. |
| `NimbusX.Cli` (`cpx`) | Complete | Argument parsing is unit-tested on paper; nothing has been executed. |
| `NimbusX.Desktop` | Complete | 8 pages + 3 dialogs, MVVM. XAML compiles only in this checker's sense; WPF runtime binding behaviour, the merged theme and the WebView2 window are all unverified. |
| `tools/NimbusX.SimCloud` | Complete | A small WebDAV server used for testing. Never run. |
| `tests/NimbusX.Tests` | Complete | 127 test methods (~194 cases) across Core, Mount, Providers, Cache, Streaming, Games, Diagnostics, App and Cli. Not executed. |
| `installer/NimbusX.iss` | Complete | Inno Setup 6, per-user, `PrivilegesRequired=lowest`, WinFsp offered but never installed silently. Never compiled. |
| `scripts/` | Complete | `fetch-deps.ps1` (pinned, digest-verified downloads), `verify-deps.ps1`, `build.ps1`. Never executed. |

## Known gaps and deliberate omissions

1. **No frame-rate / GPU "boost".** The original product exposed overlay FPS counters and GPU power tweaks. NimbusX
   does not, because an honest implementation needs a driver, ETW or a kernel component — all three are excluded by
   the design rules for this rebuild.
2. **No kernel filter, no ETW, no service.** Caching is rclone's VFS cache; process observation uses plain process
   APIs. That caps some measurements (for example, per-file read tracing of a running game).
3. **TeraBox is experimental** and marked as such everywhere. Its API is unofficial and its cookie expires.
4. **OAuth device flows** are not offered; only the browser flow and the credential flows.
5. **Accelerator predictions need ≥3 verified launches.** Before that the UI shows a plan without a prediction rather
   than inventing a number.
6. **`CacheUsageReader`** may need adjusting after the first live run against rclone 1.75.2 (it reads
   `vfs/<remote>/vfs-meta` and the cache folder layout).
7. **Multi-range HTTP requests** are refused with 416 by design; a player that needs them will not work.
8. **The installer does not bundle WinFsp.** A package that bundles rclone must ship its licence texts; the script and
   the installer have a slot for them (`tools/licenses`).

## What to do when the first build fails

1. Paste the errors into an issue; they will be in a small number of places (most likely: an analyzer turned into an
   error by `AnalysisLevel=latest`, a checker-approved but compiler-rejected short type name, or a WPF XAML type that
   needs a different `xmlns`).
2. `python3 tools/devcheck/check_csharp.py` is the fastest way to confirm you have not introduced a namespace,
   member, arity, syntax or XAML mistake while fixing; it runs anywhere Python 3 runs. `selftest.py` next to it proves
   the checks still fire.
3. Everything the app refuses to do is logged with a reason and a fix; `cpx doctor` prints the same list without the UI.
