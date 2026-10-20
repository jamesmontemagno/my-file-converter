# Desktop release setup

> **Start with [`shipping-checklist.md`](shipping-checklist.md)** — it walks through every secret,
> environment, Azure/Apple account step, and verification needed before the first tag. This file
> describes how the pipeline behaves once that is in place.

LocalMorph Desktop is distributed only through the platform Stores:

| Channel | Trigger | Workflow | Output |
|---|---|---|---|
| Microsoft Store | Windows tag builds, then gates Internal Testing and production; manual package/diagnose/publish modes | `.github/workflows/windows-store-publish.yml` | Unsigned x64/ARM64 `.msixupload`; two sequential submission approvals |
| Mac App Store | Not enabled yet | Sandbox/tool/signing proof required first | Planned Apple Silicon / macOS 15+ Store package, TestFlight testing, separately approved review and manual release |

Direct desktop MSIX/ZIP release workflows, LocalMorph WinGet/Homebrew manifests, Sparkle/appcast
feeds, and in-app installer downloads have been removed. Store updates do not poll localmorph.com.
The Windows update action opens Microsoft Store; Mac users open App Store and choose Updates.
Local development builds remain available without a third public distribution channel.

This does not remove FFmpeg/other tool installation through WinGet or development/bridge setup
through Homebrew. Browser conversion, NuGet/global-tool distribution, and bridge releases are
unchanged.

## Windows build performance

Windows bridge tests, desktop CI, and Store packaging use the same pinned Windows SDK
and MAUI workload set in their workflows. NuGet package caches are partitioned by host
OS/architecture, toolchain versions, and project/build/NuGet configuration inputs; a
compatible older dependency cache can seed a changed project graph. Installed SDKs and
MSI workload directories are not cached. Background workload advertising downloads are
disabled in the Windows app jobs; the explicit workload installation still runs.

These remain x64 `windows-latest` jobs, including builds that produce ARM64 packages.
There is no ARM-host disk formatting or runner migration. Existing path-based CI routing,
Store upload verification, symbols, and both approval gates remain unchanged.
Compare repeated cold/warm runs at the same commit and image, including cache transfer,
SDK setup and workload installation in job totals. A cache hit is not evidence that workload
installation was skipped or that a speedup came from storage. Toolchain and NuGet paths
are printed in the app jobs. To reset caches, increment their `nuget-v1` prefix.

The tag-helper regression test uses an executable POSIX Git shim and therefore runs
only on Unix. It is explicitly skipped on Windows, where invoking the real Git binary
instead of that shim could create or push release tags.

## Cutting a Windows Store release

1. Add a `## vX.Y.Z-windows` section to `dotnet/CHANGELOG.md`.
2. Tag and push:
   ```powershell
   .\scripts\create-release-tags.ps1 vX.Y.Z -Windows -Push
   ```
   (or `./scripts/create-release-tags.sh vX.Y.Z --windows --push`).
3. Inspect the package and approve Internal Testing only after package checks pass.
4. Wait for flight availability, install as a tester, and separately approve the same upload
   for production. Review certification and actual Store availability separately.

Mac tags are rejected by the helper until the Mac Store workflow exists. The helpers also check
that the selected Store workflow exists, so a release command cannot silently tag a removed channel.

Windows Store packages compile the native Explorer COM DLL using Visual Studio 2022 or newer,
Windows SDK, and the C++ tools for **each** target architecture. The project uses the selected Visual
Studio installation's default platform toolset, rather than forcing v143 on newer installations.
An x64-only MSVC installation cannot produce the ARM64 package. Unpackaged MAUI builds do not require
the shell DLL.

## Microsoft Store: separate identity, payload, and approval

This adapts the package/diagnose/publish separation from
[Tiny Clips](https://github.com/jamesmontemagno/tiny-clips) and manifest stamping from
[App Fire Calculator](https://github.com/jamesmontemagno/app-fire-calculator), with submissions
blocked until each approval is granted.

[`Build-Store.ps1`](../scripts/windows/Build-Store.ps1) stamps the Store template with the configured
identity, imports shared file/COM registrations, and restores the development manifest in `finally`.
The development manifest is not a public distribution channel. Explorer derives its activation
AUMID from the installed package.

Configuration (names/scopes only; never print secret values):

| Scope | Names | Purpose |
|---|---|---|
| Repository secrets | `MICROSOFT_STORE_IDENTITY_NAME`, `MICROSOFT_STORE_PUBLISHER`, `MICROSOFT_STORE_PUBLISHER_DISPLAY_NAME` | Partner Center package identity |
| `microsoft-store` environment variable | `MICROSOFT_STORE_PRODUCT_ID` | Canonical Partner Center product ID; repository secret of the same name is a fallback |
| `microsoft-store` environment secrets | `PARTNER_CENTER_TENANT_ID`, `PARTNER_CENTER_SELLER_ID`, `PARTNER_CENTER_CLIENT_ID`, `PARTNER_CENTER_CLIENT_SECRET` | Protected account/application access |

The `microsoft-store` environment must have required reviewers and **administrator bypass disabled**.
The repository owner explicitly allows self-approval so `jamesmontemagno` can approve runs they
started. The workflow passes `-AllowSelfReview` to
[`Test-StoreApproval.ps1`](../scripts/windows/Test-StoreApproval.ps1); without that explicit switch
the script still requires **prevent self-review**. Missing/unreadable reviewer protection or enabled
administrator bypass always blocks credential use. Flight and production are sequential jobs using
this environment: approve the flight job first, then separately approve production when it becomes
pending. Existing Store credentials stay environment-scoped; no copies or flight-ID secret are needed.
The Store workflow needs only `contents: read` and `actions: read`; do not broaden repository
workflow defaults for it.

### Package and verify

- Valid Store tags: `vX.Y.Z-windows` or `vX.Y.Z.0-windows`, with major at least 1 and components at
  most 65535. Nonzero fourth revisions are rejected by Store builds.
- The exact tag commit is checked out and verified, not merely used to stamp another branch's code.
- Store packages are unsigned StoreUpload builds with self-contained .NET, Store-resolved Windows
  App SDK frameworks, and architecture-matched Explorer DLLs. They do not bundle or download FFmpeg
  or FFprobe, and no FFmpeg compliance-archive variables are required.
- Users install FFmpeg and FFprobe separately. The Tools page opens a user-initiated
  `winget install --id Gyan.FFmpeg -e` terminal, or the FFmpeg website for manual installation.
  A full Windows build can be extracted so both executables are in `C:\ffmpeg\bin`; other
  installations can use PATH or supported WinGet locations. Select **Refresh** after installation.
  Installing external tools may require internet access and acceptance of their licenses. Verify
  compatible tools on native ARM64 rather than assuming the WinGet package has a native ARM64 build.
- Disclose the external dependency at the **beginning** of the Store listing description and in
  certification notes. See [Store policy 10.2.4](https://learn.microsoft.com/windows/apps/publish/store-policies#102-security).
  Packaging success does not establish certification or licensing approval.
- Each package carries `localmorph-build.json` with commit, version, runtime identifier, and
  `mediaTools: external`. [`Test-StoreUpload.ps1`](../scripts/windows/Test-StoreUpload.ps1) checks
  identities, both architectures, self-contained .NET, native DLLs, framework dependencies, commit
  metadata, Explorer registration, absence of FFmpeg/FFprobe and direct-release signatures, and
  matching crash symbols.
- Each architecture produces an SDK-generated `.appxsym` archive containing PDBs for
  `LocalMorph.App`, `LocalMorph.Core`, and the native `LocalMorph.Shell` Explorer command. The final
  `.msixupload` contains the bundle **and both architecture-specific symbol archives**. Package
  validation checks each PDB against its binary's CodeView identity (portable PDB GUID/stamp or native
  PDB GUID/age), rejects missing/corrupt/stale symbols, and rejects PDBs inside installed MSIX payloads.
  Publication revalidates the downloaded artifact, then sends that same upload, including symbols,
  to Partner Center; there is no separate symbol-upload command or credential.
- The build resolves `mspdbcmf.exe` from the installed Visual C++ x64 tools explicitly; `dotnet`
  MSBuild's default lookup can otherwise point inside the .NET SDK instead of Visual Studio.
  Both the normal and upload-symbol tasks receive this path, including the SDK's host-specific
  `PdbCmfx64ExeFullPath` and `PdbCmfx86ExeFullPath` properties.
- These are full first-party symbols, retained in the Store upload rather than the installed app.
  Third-party FFmpeg and Microsoft runtime PDBs are not supplied by this pipeline. Store crash
  analytics still depend on Windows diagnostic-data availability and Partner Center processing;
  uploading symbols does not enable application telemetry or guarantee crash reports.

Local package build using PowerShell 7 after configuration (identity values supplied through the named environment
variables, never committed):

```powershell
.\scripts\windows\Test-StoreScripts.ps1
.\scripts\windows\Build-Store.ps1 -Tag v1.0.0-windows
```

Helper tests compile managed PDB fixtures for both architectures and use synthetic ZIP/PE fixtures
to check rejection paths. Windows CI also builds and verifies real native x64/ARM64 DLL/PDB pairs.
These checks do not replace a real SDK-produced `.msixupload`, WACK, or installation testing.

### Workflow modes

1. **package** (manual default): builds/validates the upload artifact and never calls Partner Center.
2. **diagnose** (manual, protected environment): read-only token/application-access and Internal
   Testing flight-discovery checks; does not create a draft submission. Even diagnostics require
   reviewer approval.
3. **publish** (manual) **or a Windows tag push**: packages the selected tag, then runs the rollout:
   - **Approve and submit Internal Testing flight** pauses for environment approval. It revalidates
     the artifact against the exact version/source commit, authenticates, and enumerates all package
     flight pages through the Store API. Exactly one case-sensitive name `Internal Testing` must
     match, with a valid flight ID. Missing/duplicate/malformed flights fail closed; the script never
     falls back to the first flight or the production listing, and never creates a flight.
   - After approval, `msstore publish <upload> --appId <product> --flightId <resolved-id>` submits
     the actual `.msixupload`, including crash symbols, only to that existing flight. Tester groups
     and membership remain configured in Partner Center.
   - **Approve and submit tested upload to production** depends on successful flight submission and
     pauses for a **second** environment approval. Before approving, check Partner Center for flight
     publication, install through an account in the flight's tester group, and complete smoke/WACK/
     native-architecture tests. Upload acceptance alone does not mean testers can install it yet.
   - Production downloads the same run's artifact, repeats validation and checks its SHA256 against
     the flight upload before calling `msstore publish` without `--flightId`. A failed/rejected flight
     blocks production; neither gate is automatically approved.

Deployment scripts are checked out from the workflow commit; the package still comes from the
requested immutable tag. This permits manual rollout of an existing tag using current deployment
logic without changing its source provenance. Each run is serialized with other Store operations.
The job summary records flight ID, version, source commit and upload hash for the production reviewer.

For an already-built tag, manually dispatch **publish** with that exact tag; it builds a fresh,
verified upload before the first approval. Do not move an existing release tag. Dispatch **package**
instead when no deployment approvals should be queued.

Never use `--noCommit` as a claimed read-only test: creating/uploading a draft mutates Partner Center.
Build success, accepted upload, submission processing, certification, and Store availability are
different milestones; review Partner Center status separately.

### Remaining release gates

- Verify the listing starts with the FFmpeg/FFprobe dependency disclosure and document setup for reviewers.
- Configure/verify environment reviewers and disabled administrator bypass before diagnostics or
  publishing. Keep the explicitly authorized self-approval policy in mind when reviewing runs.
- Produce and inspect the real bundle, run WACK, then clean-install a test-signed equivalent on native
  x64 and ARM64 machines without system .NET/FFmpeg. Verify useful missing-tool guidance, install
  FFmpeg/FFprobe through WinGet or manually, refresh, and confirm inspection and real conversions.
  Also test an installation with FFmpeg but no FFprobe and a device without WinGet.
- Verify Explorer cold/warm/minimized activation, multiple files and spaces/Unicode, unsupported
  selections, keyboard-accessible queue/history menus, multi-result actions, and uninstall cleanup.
- Verify ARM64 on ARM64; an x64 runner building ARM64 is not runtime evidence. Validate shared
  Mac Catalyst code on macOS before the macOS release.

No Store submission or credential diagnostics have been run as part of the implementation.

### Store listing assets

The [English (US) listing kit](../store/microsoft-store/README.md) contains native screenshots,
separate website/social promotional graphics, tile icons, text-free hero art, listing copy,
and a desktop privacy-policy review draft. Its upload guide identifies the required publisher
confirmations. These assets do not replace the packaging, licensing, approval, installation,
and certification gates above.

## macOS toolchain

Mac Catalyst CI pins .NET SDK **10.0.401**, workload set **10.0.401.1**, and
Xcode **27.0**, following the [official compatibility notes](https://github.com/dotnet/macios/releases/tag/dotnet-10.0.1xx-xcode27.0-10722).
That workload contains Apple SDK **27.0.10722** and MAUI **10.0.110**. Apple app heads use
MAUI Controls 10.0.110; Windows and Android retain their existing package version.
Update the three pins together in `bridge-ci.yml` and any future Mac Store workflow; an unversioned workload
install can advance to a Mac Catalyst SDK that requires an Xcode version not yet on the runner.
The `desktop-app-mac` job uses GitHub's arm64 **`xcode-27`** runner on macOS 27, not
`macos-26` with an unavailable Xcode selector. The runner became
[generally available on October 15, 2026](https://github.blog/changelog/2026-10-15-xcode-27-runner-image-generally-available/).
Only the `maui-maccatalyst` workload is needed for Mac builds. macOS integration tests use Homebrew's keg-only
`ffmpeg-full` on PATH so HDR tests exercise the real `zscale`/`tonemap` filters rather than skipping.

The approved deployment baseline is **macOS 15+ on Apple Silicon only**. This means
`SupportedOSPlatformVersion=18.0` for Mac Catalyst and `RuntimeIdentifier=maccatalyst-arm64`
in both Debug and Release. Catalyst version numbers are not macOS marketing versions:
Apple's SDK maps Catalyst 18.0 to macOS 15.0. The selected workload's minimum is Catalyst
17.0/macOS 14; our product baseline is deliberately higher. Do not set the Catalyst target
to 15.0 (macOS 12), require macOS 27 merely because it is the build OS, or restore universal
Intel output. The project rejects Intel/universal runtime overrides.

Both Apple app heads register `SceneDelegate : MauiUISceneDelegate` and matching
single-window `UIApplicationSceneManifest` entries. Keep MAUI's default scene configuration
name; the framework owns window creation and lifecycle forwarding. The existing AppDelegate,
`App.CreateWindow`, and Mac cold/warm Finder URL handlers remain in place. This is UIKit
scene lifecycle adoption, not a dependency on the unrelated SceneKit 3D framework.

CI validates the **generated app bundle**, not just the source plist, with
`python3 scripts/mac/validate_app.py <path-to.app>`: macOS 15.0 minimum, arm64-only main
executable, and the expected single-window scene manifest. Run this for both Release and
the isolated Debug proof. Compilation and these checks are not startup evidence.
CI additionally builds the source-pinned minimal FFmpeg/FFprobe proof payload, ad-hoc signs
an isolated Debug app, and launches it with `scripts/mac/smoke_sandbox.py`. The check requires
scene-page startup, native bookmark creation/resolution for a container fixture,
H.264/HEVC Apple software encoding, a target-size hard cap, and shared-queue
cancellation markers; a crash, timeout or failure marker fails the job. Logs, exact proof
sources, flags and provenance are retained in the `mac-sandbox-proof` artifact.
The same signed app is archived once with a SHA256, transferred to the arm64 `macos-15`
job, verified, and launched there without rebuilding or installing the .NET SDK/media tools.
That job asserts the host OS/architecture and retains separate `mac-sandbox-proof-macos15`
runtime evidence. A declared deployment minimum alone does not satisfy this check.
The transfer artifact includes the matching FFmpeg source archive, license texts, configure
flags and build instructions. The proof app also carries FFmpeg attribution and license texts
under `Contents/Resources/FFmpeg`; diagnostic artifacts are not exempt from component notices.
It uses no Store credentials and cannot launch the normal app identity. The ad-hoc CI identity
is `com.refractored.localmorph.sandboxproof.ci`, separate from the Apple Development proof
identity to avoid reusing a container across signing identities.
This is container-only Debug evidence, not persistent external-file access, normal workspace
verification, Release/TestFlight certification, or complete codec/filter parity.
The implementation machine currently has Xcode 26.6, not Xcode 27; do not claim that a local
build on that older toolchain verifies the new CI combination.

## Mac App Store readiness

The protected beta workflow below enables an initial TestFlight package, not public release.
The normal Mac development build is not a Store upload. Full shipping still requires native
workspace/relaunch acceptance, codec/filter parity, component licensing and the final privacy/
listing review. A host Homebrew installation must not be mistaken for an approved,
self-contained shipping bundle. The beta shares the existing SDK required-reason privacy
manifest, including app-local preferences, and validates its placement in the actual bundle;
this does not settle the separate data-collection questionnaire or complete the privacy audit.

### Apple encoder feasibility

FFmpeg remains the media pipeline; it invokes Apple's H.264/HEVC encoders through VideoToolbox.
When libx264/libx265 are absent and no requested hardware encoder has been verified, the shared
engine can use explicit Apple software encoding (`-require_sw 1`). The UI requests a video
bitrate rather than presenting unsupported quality/speed controls. Native experiments found
that H.264 software quality mode and H.264/HEVC software constant-bitrate mode are not usable
on the tested system; these flags are not emitted.

Target-size Apple jobs use software encoding with at most three attempts, reducing the bitrate
after an oversized staged result. The final byte check is authoritative: an oversized result
is discarded without replacing an existing destination. Existing libx264 two-pass behavior
is unchanged. Core tests and the isolated signed sandbox app exercise this path; they do not
establish full shipping parity. The bundled payload statically links libass (CoreText fonts)
and zimg for subtitle burn-in and `zscale` HDR tone mapping; external GPL/nonfree codecs
(libx264/libx265 and similar) remain excluded.

Free/open-source distribution is not component, patent or App Store licensing approval.
The proof disables FFmpeg GPL/nonfree components, including libx264/libx265. Review the exact
shipping configuration, dependencies, notices, corresponding source/build obligations and
distribution terms before selecting a release payload.

### Isolated Mac Store workspace

The normal shared workspace now has a separate opt-in sandbox runtime:

```bash
dotnet build dotnet/src/LocalMorph.App -f net10.0-maccatalyst \
  -p:MacOnly=true -p:LocalMorphMacStoreBuild=true -p:EnableCodeSigning=false
```

This compiles `LocalMorph Sandbox Workspace.app` under
`bin/mac-store/Debug/net10.0-maccatalyst/maccatalyst-arm64/`, with the distinct
`com.refractored.localmorph.sandboxworkspace` identity/container. Compilation alone does not
sign or launch it. Production `Release`, Windows Store flags and diagnostic-proof combinations
are rejected. Do not remove the Release guard until the approved complete media payload,
provenance and distribution-signing/package validation are implemented.

Only `ffmpeg` and `ffprobe` directly inside this app's `Contents/MacOS` are discovered.
Missing tools show repair/update guidance; corrupt, symlinked or unsuccessful executables
fail explicitly. PATH, Homebrew, optional-engine installs and Terminal automation cannot
repair or extend this flavor. Developer and Windows tool acquisition remain unchanged.
Version checks are not a substitute for the outstanding signed payload/hash/component audit.

Native file/folder pickers, Finder activation and native drops retain bookmark data before
paths enter the workspace. The local `file-access.json` is atomically replaced with owner-only
permissions. Folder scopes use path boundaries, source paths are canonicalized, and case-distinct
Mac paths are retained. Native scope leases are reference-counted independently for workspace
rows, preview, inspection and jobs; a job releases its permissions only after the runner's
final process/staging cleanup, not on its terminal notification.

Conversion restores or requests an authorized output **directory** before preflight/enqueue.
Input-file selection never grants sibling output writes. Legacy output/history paths need
reauthorization; attachments use the same registry. Moved/revoked/unavailable bookmarks fail
explicitly instead of selecting another file or destination. **Forget file permissions** clears
workspace grants/output preferences without deleting files or history. Native open/folder
actions have no shell fallback; Finder opening still needs real workspace runtime verification.

The installed Apple `NSURL.h` marks the security-scope bookmark flags available on Mac Catalyst
13.0+. The current .NET enum annotations incorrectly mark them macOS-only; the native backend
documents narrowly scoped analyzer suppressions rather than casting unsupported flag values.
Native create/resolve/read of a container fixture passes in the isolated proof. This does
**not** establish external bookmarks after restart, cloud-file coordination, read-only/removable
volumes, normal preview/Finder/Dock/quit behavior or final Store runtime. Keep those native checks
open even when Core tests and compiled XAML pass.

### Remaining package and delivery work

Store packaging requires a verified app record/bundle identity, distribution provisioning,
application and installer signing, matching symbols, and bundle validation. Configure protected
credential use only after these requirements and the actual toolchain are established. The Apple
secret names below are already provisioned; their presence does not verify their contents or
account access. Do not reuse Developer ID/notarytool/Sparkle setup as a substitute.

The intended flow is package-only or read-only diagnostics, separately approved TestFlight
internal testing, review submission of the same distributable build, and manual public release
after Apple approval. An upload marked TestFlight Internal Only cannot later ship to customers.
No upload, submission, or public Mac availability is implied by this document.

### Actions signing and upload configuration

As of October 18, 2026, all six names below exist as **repository secrets**. Do not add
duplicate `APPSTORE_*` aliases when wiring the Mac workflow:

| Existing secret | Intended use / required validation |
|---|---|
| `APPLE_MACCATALYST_APPSTORE_CERTIFICATE_P12` | Base64 P12 containing exactly one usable Apple Distribution application identity and one Mac Installer Distribution identity, both with private keys |
| `APPLE_MACCATALYST_APPSTORE_CERTIFICATE_PASSWORD` | P12 import password; never print or put it in artifacts |
| `APPLE_MACCATALYST_APPSTORE_PROFILE` | Base64 Mac Catalyst **App Store** profile matching `com.refractored.localmorph`, the signing team and certificate; check expiry and distribution entitlements |
| `APPLE_REFRACTORED_ISSUER_ID` | App Store Connect API issuer |
| `APPLE_REFRACTORED_KEY_ID` | App Store Connect API key ID |
| `APPLE_REFRACTORED_P8_KEY` | App Store Connect private key, with access to the intended LocalMorph app |

A separate installer-certificate secret is unnecessary **if** the existing P12 contains both
required private-key identities. Secret listing cannot verify that condition. A profile or app
record for another product is not interchangeable with LocalMorph's, even when the Apple team
and API key are shared.

The implemented signing path is temporary-keychain import, unambiguous application/installer
identity resolution, decoded profile validation, an SDK-signed application build, nested-media
signing, enclosing-app re-signing and explicit `productbuild` installer signing. Retain the
approved Apple Silicon-only architecture policy and the pinned toolchain; another app's architecture,
minimum OS, workload set or runner label is not a LocalMorph compatibility decision.

The upload path uses a separately approved job consuming the already verified package, with
`app-type: macos` and the existing issuer/key/private-key names above. It must compare the
artifact hash and source/build provenance before upload, wait for processing, resolve the
intended internal testing group, and retain the processed build ID. A successful upload alone
does not establish tester availability, review submission or public release. Do not copy an
arbitrary build-number offset: validate the selected build number against accepted Store builds,
handle retries explicitly, and retain it with the immutable artifact.

The owner authorized creating **`mac-app-store` on October 18, 2026**. It requires
`jamesmontemagno` approval, disables administrator bypass, and permits only the `main` branch
and `v*-mac` tags. Self-review is explicitly allowed, matching the Windows release policy.
The workflow rechecks these rules before credential use, including inside protected jobs.
Prefer environment-scoped Apple secrets; moving or removing the current repository secrets
is a separate owner-controlled action, not an automatic cleanup.
Keep signing and API material scoped to the steps that need it, use an isolated temporary
keychain, and clean up keys/profiles even on cancellation or failure.

No Apple private-key/secret values were read locally or moved. The protected beta completed
real profile/certificate validation, app/installer signing, API access and upload in
run `39197645028` (1.0.0/build 3, source `1a4cb71`). Apple processing is `VALID` and membership
in the owner-selected **Internal Test Group** (`5e573fb7-8a0f-4446-956d-78f615808373`) is confirmed.
The optional `MAC_APP_STORE_INTERNAL_GROUP` variable selects that existing group.
This does not submit review/release.

Apple build ID: `9a5ae49a-9664-4290-ad32-c7e69826c38d`.
Package SHA256: `fa29e7a79dc10bb5cc187276e86a630f6aa69bd0e7166686b1f1ca63981f3610`.
Matching source/build evidence and symbols are retained with the package. Installed TestFlight
acceptance, full component/privacy/product review and public release remain separate gates.
Build 3 replaces build 2's startup theme-binding failure (#43). CI now launches the actual
Release workspace in an isolated sandbox identity under light/dark/system themes, including
the unchanged signed workspace on macOS 15.7.9. This is not installed TestFlight acceptance.
Separately, the actual installed Apple-distributed build 3 has passed signature validation
and native workspace startup on the owner's Mac, with a visible window and the loaded/initialized
marker. Clean supported-OS, conversion, permission and relaunch acceptance in #41 remains open.

### Mac Store release and TestFlight

`.github/workflows/mac-store-publish.yml` is displayed as **Mac Store Release** in Actions.
It uses the six existing secrets above without alternate names or additional required variables.
An optional `MAC_APP_STORE_INTERNAL_GROUP` repository variable can select an existing internal
TestFlight group; a dispatch's `testflight_group` overrides it. With neither set, exactly one
internal group must exist. The App Store Connect app record must match
`com.refractored.localmorph`; the workflow does not create records or tester groups.

After merging, use Actions on `main` or run:

```bash
# Read-only Apple app/group/profile/certificate diagnostics, after environment approval:
gh workflow run mac-store-publish.yml --ref main \
  -f mode=diagnose -f version=1.0.0 -f build_number=1

# Build/sign, approve the exact package upload, await processing and assign the internal group:
gh workflow run mac-store-publish.yml --ref main \
  -f mode=testflight -f version=1.0.0 -f build_number=1
```

Use an **unused increasing** build number from 1 to 9999, not necessarily `1` from the examples.
An empty `build_number` uses this workflow's run number without an arbitrary offset. Reruns
reuse that number; App Store preflight rejects an already uploaded version/build. If an upload
succeeds but processing/assignment fails or times out, inspect the existing build in App Store
Connect or run diagnostics before retrying. Do not blindly upload it again.

`mode=package` signs and retains artifacts but never contacts App Store Connect or uploads.
A pushed `v1.0.0-mac` tag also runs **package only**, never TestFlight automatically. Tags must
point to commits already on `main`; manual dispatch is accepted only from `main` or an allowed
Mac tag. No merge itself uploads anything.

The workflow:

1. Checks source/ref, versions and the protected environment before Apple credential use.
2. After package approval, builds pinned arm64 FFmpeg/FFprobe in the package job from the
   verified upstream source archive, without Homebrew, GPL/nonfree, networking or automatic
   dependency discovery.
3. After approval, checks the combined P12's application/installer private-key identities,
   profile expiry/platform/distribution type, exact LocalMorph identifier/team and certificate.
   Credentials live only in an isolated temporary keychain/profile and are cleaned on failure.
4. Builds the actual sandboxed Store **Release** app with SDK 10.0.401, workload 10.0.401.1 and
   Xcode 27. The Store-only Mono interpreter setting avoids adding a JIT entitlement.
5. Signs the nested media tools with sandbox/inherit, preserves the SDK's profile entitlements,
   re-signs the enclosing app, and creates the signed `.pkg` with Apple's `productbuild --component`
   path. This explicit order puts the media in the app **before** packaging. It does not use
   blanket deep signing.
6. Verifies package/app/tool signatures, architecture, minimum OS/scenes, source/build identity,
   raw/signed tool hashes and matching native dSYM. Retains the immutable package/hash, source
   archive, licenses, build recipe/evidence and crash symbols for 90 days.
7. For `testflight` only, requests approval again, validates the downloaded package against
   the signing job's hash/team, checks app/internal-group access and rejects duplicate uploads.
   Uploads a normal distributable build with the existing `APPLE_REFRACTORED_*` API credentials.
8. Waits for Apple processing, fails on rejected/ambiguous/timed-out builds, and confirms the
   exact processed Mac version/build is in the selected internal group. Retains its build ID.

Media builds in place before signing keys are imported; there is no separate media runner or
intermediate media upload/download. The signed package artifact still crosses the separate
upload approval and is revalidated there. Offline helper regression tests run in CI rather than
being repeated in every release dispatch.

This is the owner's explicitly selected **initial TestFlight beta payload**, not a claim of
full Windows parity. libass subtitle burn-in and zscale HDR mapping are bundled (pinned,
hash-verified dependency sources; licenses in `Contents/Resources/FFmpeg/licenses`). Optional
external engines are replaced by macOS built-in converters; unsupported outputs stay unavailable
according to actual capabilities. No full shipping
component/legal review, external-bookmark/native workspace acceptance or TestFlight installation
is implied by compilation, ad-hoc staging or helper tests.

The workflow contains **no App Review submission or public-release operation**. Those remain
separately approved, manual gates after actual TestFlight results and shipping prerequisites.
Do not mark this run as TestFlight Internal Only if its exact tested build is intended for
later App Store submission.

### Directory-aware CI and deployment

Website CI and Pages only run for `src/`, `public/`, HTML, Node/TypeScript/Vite inputs and their
own workflow files. Desktop CI ignores website and prose-only changes. Its changed-file job uses
the full push range or PR merge-base diff, including deletions and both sides of renames:
Mac-specific changes run Mac checks, Windows-specific changes run Windows checks, and shared
app/Core/build changes retain cross-platform coverage. Python/tag guard tests run without
rebuilding unrelated apps. The iOS privacy manifest shared by Mac remains a Mac build input.
Superseded CI runs cancel per PR/ref; release operations remain serialized without auto-cancel.

Store/NuGet release workflows stay explicit dispatch/tag operations, not ordinary directory-change
builds. Keep the required signing, source/hash/symbol verification, cleanup and approval gates.

### Isolated sandbox feasibility proof

The development-only `LocalMorphMacSandboxProof=true` flavor runs the shared Core inspection,
conversion queue, staging/commit, and cancellation code in a separate
`com.refractored.localmorph.sandboxproof` app. It does not instantiate the normal workspace or
read its history/preferences. Release configurations and Windows Store flags reject this flavor.
The tools are located only in the proof app's `Contents/MacOS`; there is no PATH/Homebrew fallback.

On October 18, 2026, the signed Apple Silicon proof successfully inspected a generated source,
converted it to H.264/AAC through VideoToolbox, verified the output and unchanged source, and
canceled a queue job without leaving staged/committed output. Native source and output-folder
pickers both returned security-scoped access; bundled child tools read the selected source and
wrote the final conversion into the selected folder. The independently inspected result was
640x360 H.264/AAC, about four seconds long. The parent had only sandbox/user-selected read-write
entitlements; FFmpeg/FFprobe had only sandbox/inherit entitlements. No JIT, unsigned-memory,
library-validation, or network exception was needed for this **Debug proof**.

The proof tools were built from source-pinned FFmpeg 9.0.2 with GPL/nonfree components and
automatic external dependency discovery disabled. They report LGPL 2.1-or-later; this is not
legal approval for Store distribution. The earlier proof built and merged both native tool
slices; their dynamic dependencies were only system libraries/frameworks. That historical
artifact predates the approved Apple Silicon-only/macOS 15 baseline and scene migration.
The proof scripts now build and package arm64 tools only, with a macOS 15 deployment target.
The Xcode 27/macOS 27 CI proof now passes native scene startup, Apple H.264/HEVC software
conversion, target-size enforcement and cancellation, with all 56 focused encoder tests passing
against the source-built tools. This verifies the isolated Debug app, not the normal workspace,
final Release/Store runtime/signing configuration. A subsequent exact-app transfer also passed
on macOS 15.7.9 without rebuilding; that is not exact macOS 15.0 or normal product execution.
Intel runtime testing is no longer a release requirement.

| Candidate capability | Evidence / remaining work |
|---|---|
| Ordinary H.264 | Actual sandboxed Core conversion passed using Apple VideoToolbox |
| HEVC and explicit Apple software modes | Shared Core and the scene-enabled sandbox app produce H.264/HEVC with `-require_sw 1` and an explicit bitrate. Unsupported quality/constant-bitrate flags are not used |
| Target-size modes | Shared Core and sandbox tests verify Apple software with at most three bitrate-adjusted attempts, decimal-MB hard-cap checks and safe commit. Existing x264 two-pass behavior remains. Nominal bitrate alone is not a file-size guarantee; oversize failure preserves an existing destination |
| HDR / subtitle burn-in | Bundled: static libass 0.17.4 (FreeType/FriBidi/HarfBuzz, CoreText fonts) and zimg 3.0.6. The builder fails if `subtitles`, `ass` or `zscale` is missing; dependency source archives ship beside each package |
| Other formats / optional engines | Not proven by this minimal dependency configuration; complete the codec/filter and optional-engine inventory before making listing claims |
| Persistent access | Grant registry and workspace wiring implemented; native container bookmark round trip passes. External selection/relaunch, revoked grants, cloud volumes and all normal workspace entry points still need native verification |

Reproduce locally with Xcode, the pinned .NET/Mac Catalyst workload, and an existing Apple
Development signing identity. This builds diagnostic artifacts only, not Store packages:

```bash
work="$(mktemp -d)"
curl --fail --location https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz \
  --output "$work/ffmpeg-9.0.2.tar.xz"
bash scripts/mac/build-proof-media.sh "$work/ffmpeg-9.0.2.tar.xz" "$work/media" arm64
bash scripts/mac/prepare-sandbox-proof.sh "$work/media" "$work/proof"
```

The source pin is checked before extraction; configure flags, source trees, license text, raw
tool hashes and dependency reports are retained. The preparer verifies those hashes, rejects
Intel/universal helpers, validates the effective app bundle, signs helpers before the app,
and records signed hashes and proof sources.
Set `LOCALMORPH_PROOF_SIGNING_IDENTITY` only when a different local signing identity is needed.
Use `LOCALMORPH_PROOF_SIGNING_IDENTITY=-` for the separate ad-hoc CI identity when no development
certificate is available. Run the automated container check and retain its log:

```bash
python3 scripts/mac/smoke_sandbox.py \
  "$work/proof/app/Debug/net10.0-maccatalyst/maccatalyst-arm64/LocalMorph Sandbox Proof.app" \
  "$work/runtime"
```

The runner stops only its own launched process group after verification or timeout.
For external access, launch the resulting **LocalMorph Sandbox Proof** app manually and use its native
pickers with generated demo media to verify external access. Outputs have unique proof names;
picker cancellation makes no conversion claim. The diagnostic now saves scoped bookmarks
with its separate app-scope entitlement. After selecting a generated source and destination,
quit and relaunch the **same signed identity**, then use **Restore saved selections** without
another picker. Only `RELAUNCHED_SELECTED_GRANTS_PROOF_PASSED` establishes a new-process external
grant check; `SAVED_SELECTION_RESTORED` means the same process and is not persistence evidence.
The automated `BOOKMARK_API_PROOF_PASSED` marker covers only native APIs with a container fixture.

This does not establish source-signature verification, component/legal approval, complete codec
parity, distribution provisioning, Store validation, TestFlight installation, or Apple acceptance.
Those remain release gates. No features have been removed to accommodate the proof bundle.

## Retired-channel configuration cleanup

No secrets or external registry entries are automatically deleted. Review whether the former
`windows-release` / `macos-release` environments and their Azure signing, Developer ID,
notarization, Sparkle, and `WINGET_CREATE_GITHUB_TOKEN` credentials are used elsewhere before
removing or revoking them. Preserve Partner Center credentials, shared Apple credentials, and
bridge publishing configuration.

Former workflow references, to audit by name without exposing values:

| Former use | Configuration names |
|---|---|
| Azure direct-MSIX signing | `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_ARTIFACT_SIGNING_ENDPOINT`, `AZURE_ARTIFACT_SIGNING_ACCOUNT_NAME`, `AZURE_ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME` |
| Developer ID signing | `DEVELOPER_ID_CERTIFICATE_BASE64`, `DEVELOPER_ID_CERTIFICATE_PASSWORD`, `KEYCHAIN_PASSWORD` |
| Direct-Mac notarization | `APPLE_ID`, `APP_PASSWORD`, `APPLE_TEAM_ID` |
| Sparkle and LocalMorph WinGet submission | `SPARKLE_PRIVATE_KEY`, `WINGET_CREATE_GITHUB_TOKEN` |

Check for externally published LocalMorph WinGet/Homebrew entries separately. Existing GitHub
releases/tags are historical records, not authorization to delete them. Bridge releases must remain.
