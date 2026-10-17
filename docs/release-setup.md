# Desktop release setup

> **Start with [`shipping-checklist.md`](shipping-checklist.md)** — it walks through every secret,
> environment, Azure/Apple account step, and verification needed before the first tag. This file
> describes how the pipeline behaves once that is in place.

LocalMorph Desktop has separate direct and Store release paths:

| Channel | Trigger | Workflow | Output |
|---|---|---|---|
| Windows (winget + direct MSIX) | tag `vX.Y.Z-windows` | `.github/workflows/windows-release.yml` | Signed x64/ARM64 MSIX on the GitHub Release, `winget` manifest artifact, `public/appcast-windows.xml` PR |
| Microsoft Store | Windows tag packages only; manual package/diagnose/publish modes | `.github/workflows/windows-store-publish.yml` | Unsigned x64/ARM64 `.msixupload`; submission only after independent approval |
| winget community repo | manual `WinGet Submission` workflow | `.github/workflows/winget-submit.yml` | PR to `microsoft/winget-pkgs` |
| macOS (Homebrew cask + direct .app) | tag `vX.Y.Z-mac` | `.github/workflows/mac-release.yml` | Notarized universal `.app` zip on the GitHub Release, Sparkle appcast + `Casks/localmorph.rb` PR |

Direct builds check `https://localmorph.com/appcast.xml` (macOS) or `https://localmorph.com/appcast-windows.xml`
(Windows) about once a day and shows an **Update available** pill linking to the release and the
`winget upgrade` / `brew upgrade --cask` command. Both feeds live in `public/` and are published by the
existing GitHub Pages deploy, so merging the release-metadata PR is what makes an update visible.
Store builds disable appcast checks and open Microsoft Store's updates page for a manual check.

## Cutting a release

1. Add a `## vX.Y.Z-windows` and/or `## vX.Y.Z-mac` section to `dotnet/CHANGELOG.md` (becomes the release notes).
2. Tag and push:
   ```powershell
   .\scripts\create-release-tags.ps1 vX.Y.Z -Windows -Mac -Push
   ```
   (or `./scripts/create-release-tags.sh vX.Y.Z --windows --mac --push`).
3. Watch the two release workflows. Each opens a `release-metadata/<tag>` pull request — merge it to publish the appcast (and the cask on macOS).
4. After reviewing the Windows GitHub Release, run **WinGet Submission** with the tag to open the `winget-pkgs` PR.

Users then install with:

```text
winget install Refractored.LocalMorph
brew tap jamesmontemagno/my-file-converter https://github.com/jamesmontemagno/my-file-converter
brew install --cask localmorph
```

## Windows secrets (environment `windows-release`)

The MSIX is signed with [Azure Artifact Signing](https://learn.microsoft.com/azure/trusted-signing/) via OIDC, exactly like tiny-clips.

| Secret | Purpose |
|---|---|
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | Federated (OIDC) identity allowed to use the signing account |
| `AZURE_ARTIFACT_SIGNING_ENDPOINT` | e.g. `https://wus2.codesigning.azure.net` |
| `AZURE_ARTIFACT_SIGNING_ACCOUNT_NAME` | Artifact Signing account name |
| `AZURE_ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME` | Certificate profile whose subject is `CN=Refractored LLC, O=Refractored LLC, L=Seattle, S=Washington, C=US` |
| `WINGET_CREATE_GITHUB_TOKEN` (repository secret) | Classic PAT with `public_repo` from an account that can fork `microsoft/winget-pkgs` |

The manifest publisher **must** match the certificate subject or MSIX installation fails. The
resulting `PackageFamilyName` is `Refractored.LocalMorph_vmshqmcyy894t` (hard-coded in both Windows
workflows and `dotnet/packaging/winget/*.installer.yaml`).

The package is framework-dependent: winget installs `Microsoft.DotNet.DesktopRuntime.10`,
`Microsoft.WindowsAppRuntime.2`, and `Gyan.FFmpeg` as dependencies. Validate a local build with:

```powershell
dotnet publish dotnet/src/LocalMorph.App/LocalMorph.App.csproj -f net10.0-windows10.0.19041.0 -c Release `
  -p:WindowsOnly=true -p:RuntimeIdentifier=win-x64 -p:WindowsPackageType=MSIX -p:SelfContained=false `
  -p:WindowsAppSDKSelfContained=false -p:GenerateAppxPackageOnBuild=true -p:AppxPackageDir=$PWD\artifacts\ `
  -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxPackageSigningEnabled=false
winget validate --manifest dotnet/packaging/winget
```

Both packaged Windows channels compile the native Explorer COM DLL using Visual Studio 2022 or newer,
Windows SDK, and the C++ tools for **each** target architecture. The project uses the selected Visual
Studio installation's default platform toolset, rather than forcing v143 on newer installations.
An x64-only MSVC installation cannot produce the ARM64 package. Unpackaged MAUI builds do not require
the shell DLL.

## Microsoft Store: separate identity, payload, and approval

This adapts the package/diagnose/publish separation from
[Tiny Clips](https://github.com/jamesmontemagno/tiny-clips) and manifest stamping from
[App Fire Calculator](https://github.com/jamesmontemagno/app-fire-calculator), without publishing
automatically on tag pushes.

The direct manifest, direct package family, Azure signing, winget dependencies, and macOS release
path stay separate. [`Build-Store.ps1`](../scripts/windows/Build-Store.ps1) stamps the Store template
with the configured identity, imports shared file/COM registrations, and restores the direct
manifest in `finally`. Explorer derives its activation AUMID from the installed package.

Configuration (names/scopes only; never print secret values):

| Scope | Names | Purpose |
|---|---|---|
| Repository secrets | `MICROSOFT_STORE_IDENTITY_NAME`, `MICROSOFT_STORE_PUBLISHER`, `MICROSOFT_STORE_PUBLISHER_DISPLAY_NAME` | Partner Center package identity, not the Azure direct-signing identity |
| `microsoft-store` environment variable | `MICROSOFT_STORE_PRODUCT_ID` | Canonical Partner Center product ID; repository secret of the same name is a fallback |
| `microsoft-store` environment secrets | `PARTNER_CENTER_TENANT_ID`, `PARTNER_CENTER_SELLER_ID`, `PARTNER_CENTER_CLIENT_ID`, `PARTNER_CENTER_CLIENT_SECRET` | Protected account/application access |

The environment must have required reviewers, **prevent self-review** enabled, and **administrator
bypass disabled**. [`Test-StoreApproval.ps1`](../scripts/windows/Test-StoreApproval.ps1) fails closed
before credential use if these protections are absent or cannot be verified. The current environment
was observed without reviewer protection; adding secrets alone does not make publication ready.
The Store workflow needs only `contents: read` and `actions: read`; do not broaden repository
workflow defaults for it.

### Package and verify

- Valid Store tags: `vX.Y.Z-windows` or `vX.Y.Z.0-windows`, with major at least 1 and components at
  most 65535. Nonzero fourth revisions remain direct-release-only and are rejected by Store builds.
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

1. **package** (default, also the only tag-push action): builds/validates the upload artifact and
   never calls Partner Center.
2. **diagnose** (manual, protected environment): read-only token/application-access checks;
   does not create a draft submission. Even diagnostics require independent approval.
3. **publish** (manual, protected environment): packages the selected tag, revalidates the artifact
   from that run against its source commit, checks access, then passes the actual `.msixupload`
   and Product ID to `msstore publish`. This is a real submission, not a dry run.

Never use `--noCommit` as a claimed read-only test: creating/uploading a draft mutates Partner Center.
Build success, accepted upload, submission processing, certification, and Store availability are
different milestones; review Partner Center status separately.

### Remaining release gates

- Verify the listing starts with the FFmpeg/FFprobe dependency disclosure and document setup for reviewers.
- Configure/verify environment reviewer protections before diagnostics or publishing.
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

Mac Catalyst CI and releases pin .NET SDK **10.0.401**, workload set **10.0.401**, and
Xcode **26.6**, following the [official compatibility notes](https://github.com/dotnet/macios/releases/tag/dotnet-10.0.1xx-xcode26.5-10318).
Update the three pins together in `bridge-ci.yml` and `mac-release.yml`; an unversioned workload
install can advance to a Mac Catalyst SDK that requires an Xcode version not yet on the runner.
Only the `maui-maccatalyst` workload is needed. macOS integration tests use Homebrew's keg-only
`ffmpeg-full` on PATH so HDR tests exercise the real `zscale`/`tonemap` filters rather than skipping.

## macOS secrets (environment `macos-release`)

| Secret | Purpose |
|---|---|
| `DEVELOPER_ID_CERTIFICATE_BASE64` | `base64 -i DeveloperID.p12` of the **Developer ID Application** certificate |
| `DEVELOPER_ID_CERTIFICATE_PASSWORD` | Password used when exporting the `.p12` |
| `KEYCHAIN_PASSWORD` | Any strong password for the temporary CI keychain |
| `APPLE_ID`, `APP_PASSWORD`, `APPLE_TEAM_ID` | Apple ID, app-specific password, and team ID for `notarytool` |
| `SPARKLE_PRIVATE_KEY` | Contents of `generate_keys -x` (EdDSA private key) used by `generate_appcast` to sign enclosures |

Generate the Sparkle key pair once with `Sparkle-2.x/bin/generate_keys` and back up the private key.
The public key is not needed yet: Sparkle's runtime framework does not support Mac Catalyst, so the
app uses its own lightweight appcast reader (`LocalMorph.Core/Updates/AppcastReader.cs`) and hands
off to Homebrew or a browser download. Signed enclosures keep the feed forward-compatible if a native
Sparkle updater is added later.

The bundle is published universal (`maccatalyst-x64;maccatalyst-arm64`), signed with the hardened
runtime using `Platforms/MacCatalyst/Entitlements.plist` (sandbox off so FFmpeg can be spawned; JIT
entitlements for .NET), notarized, and stapled.

### Homebrew cask

`Casks/localmorph.rb` is a tap-style cask in this repository (`brew tap jamesmontemagno/my-file-converter
https://github.com/jamesmontemagno/my-file-converter`). It depends on the `ffmpeg` formula, uses a
Sparkle `livecheck` against the appcast, and is version/sha-bumped automatically by the release PR.
Submitting to `homebrew/cask` is a manual follow-up once the app has a notarized release.

## Staging the update check

Set `LOCALMORPH_APPCAST_URL=https://…/appcast.xml` before launching the app to point the update
check at a staging feed.
