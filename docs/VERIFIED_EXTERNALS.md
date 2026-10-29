# Verified external binaries

NimbusX does not download anything while it runs. Every external binary is **pinned by version and by SHA-256**,
verified before it is extracted, and re-verified (by recording the hash of the extracted `rclone.exe`) the first
time it is used.

The authoritative copy of the values is `src/NimbusX.Mount/DependencyVersions.cs`. `scripts/fetch-deps.ps1` carries
a second copy for packaging; `scripts/verify-deps.ps1` fails the build when the two disagree.

| Component | Version | Asset | SHA-256 |
|---|---|---|---|
| rclone | 1.75.2 | `rclone-v1.75.2-windows-amd64.zip` | `8a9db2bad1dc79a65b3348fd857069598e2aab7cf4d118b77b1df23055aff818` |
| rclone-extra (TeraBox) | 1.75.2-extra | `rclone-windows-amd64.zip` | `d5a5d9383a607ccb1af9a871d4cb92906121f32cf111497adb94958b99315c69` |
| WinFsp | 2.1.25156 | `winfsp-2.1.25156.msi` | `073a70e00f77423e34bed98b86e600def93393ba5822204fac57a29324db9f7a` |

Minimum WinFsp version accepted: **1.12**. rclone version NimbusX is built and tested against: **1.75.2**
(the mount flags are checked against `rclone mount --help` at runtime, and anything the installed rclone does not
support is refused rather than passed on blindly).

## Why these three

* **rclone 1.75.2 (official, MIT).** Provides the VFS mount, the optional rc interface, the provider backends and
  the union remote used for the pooled drive. It is a single self-contained `rclone.exe`.
* **rclone-extra 1.75.2-extra (community build, MIT).** The only maintained build that contains the unofficial
  `terabox` backend (a fork of `x1arch/rclone`, branch `backend/terabox`). It is used **only** for TeraBox mounts
  and is installed into its own folder (`tools/terabox`), so a TeraBox build can never be used for the reviewed
  providers. TeraBox is marked experimental in the UI.
* **WinFsp 2.1.25156 (GPLv3 with a FLOSS exception).** The file system driver rclone's Windows mount needs. It is
  **not** bundled silently: it is an MSI that requires an administrator install, and NimbusX only detects it, shows
  the pinned installer and its digest, and lets the user run it.

## TeraBox auth method (verified against the fork)

The `terabox` backend of `1.75.2-extra` authenticates with the browser session cookie `ndus`. Read from
`backend/terabox/terabox.go` of that release:

* option `cookie` (required, `Advanced: false`): either the bare `ndus` value or a full cookie string. The backend
  passes it through `valueCookie(...)`, which accepts `ndus=<value>; lang=en`. NimbusX always sends the full form.
* option `domain` (advanced, default `https://www.terabox.com`): the backend accepts a bare domain too — if the value
  does not match `https?://` it prefixes `https://` itself, so both forms work. NimbusX's UI offers the four official
  domains as full URLs.
* other options the backend defines: `delete_permanently` (default false), `upload_threads` (default 5),
  `user_agent`, `debug_level`, `encoding`.

NimbusX therefore opens the provider's real sign-in page in an embedded WebView2 window (its own profile folder,
no injected scripts), waits for the user to press **Done**, and reads only the cookies for the chosen domain. The
cookie is stored with DPAPI and handed to rclone through `config/create` over the loopback rc interface — never on a
command line. See `src/NimbusX.Desktop/Services/WebView2LoginSessionFactory.cs` and
`src/NimbusX.Providers/TeraboxCookieBuilder.cs`.

## OAuth client IDs

For the providers rclone supports with OAuth, NimbusX uses the client IDs compiled into rclone 1.75.2 itself
(`lib/oauthutil/oauthutil.go` and each backend), so the "Sign in" button opens the provider's page with the same
application rclone would use:

| Provider | Client ID |
|---|---|
| Google Drive | `202264815644.apps.googleusercontent.com` |
| OneDrive | `b15665d9-eda6-4092-8539-0eec376afd59` |
| Dropbox | `5jcck7diasz0rqy` |
| pCloud | `DnONSzyJXpm` |
| Box | `d0374ba6pgmaguie02ge15sv1mllndho` |
| Yandex Disk | `ac39b43b9eba4cae8ffb788c06d816a8` |

A user who would rather use their own application can paste a client ID and secret in Settings; the secret is kept
with DPAPI and passed to rclone through `config/create`, never as an argument.

## Licensing note for a packaged build

A package that bundles these binaries must also ship their licence texts. `scripts/fetch-deps.ps1` copies them into
`tools/licenses` when they are present in an archive, and the installer copies that folder next to the binaries.
WinFsp is GPLv3 with a FLOSS exception; rclone and rclone-extra are MIT.
