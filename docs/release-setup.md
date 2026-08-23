# Desktop release setup

> **Start with [`shipping-checklist.md`](shipping-checklist.md)** — it walks through every secret,
> environment, Azure/Apple account step, and verification needed before the first tag. This file
> describes how the pipeline behaves once that is in place.

LocalMorph Desktop ships through three channels, all driven by git tags:

| Channel | Trigger | Workflow | Output |
|---|---|---|---|
| Windows (winget + direct MSIX) | tag `vX.Y.Z-windows` | `.github/workflows/windows-release.yml` | Signed x64/ARM64 MSIX on the GitHub Release, `winget` manifest artifact, `public/appcast-windows.xml` PR |
| winget community repo | manual `WinGet Submission` workflow | `.github/workflows/winget-submit.yml` | PR to `microsoft/winget-pkgs` |
| macOS (Homebrew cask + direct .app) | tag `vX.Y.Z-mac` | `.github/workflows/mac-release.yml` | Notarized universal `.app` zip on the GitHub Release, Sparkle appcast + `Casks/localmorph.rb` PR |

The app checks `https://localmorph.com/appcast.xml` (macOS) or `https://localmorph.com/appcast-windows.xml`
(Windows) about once a day and shows an **Update available** pill linking to the release and the
`winget upgrade` / `brew upgrade --cask` command. Both feeds live in `public/` and are published by the
existing GitHub Pages deploy, so merging the release-metadata PR is what makes an update visible.

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
