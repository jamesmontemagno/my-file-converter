# Shipping checklist: everything to configure before the first release

This is the one-time setup needed so that pushing a `vX.Y.Z-windows` / `vX.Y.Z-mac` tag produces a
signed, installable LocalMorph Desktop release on winget and Homebrew, with a separate protected
Microsoft Store path. Work through it top to bottom;
each section ends with a way to verify it. The companion `release-setup.md` explains how the
pipeline works once this is done.

> Where to set things: **Settings → Secrets and variables → Actions** (repository-level), and
> **Settings → Environments** for channel-specific environments below. Anything marked *environment secret*
> must be created **inside** that environment, not at repository level, because the workflows declare
> `environment: windows-release`, `macos-release`, or `microsoft-store`.

---

## 0. Repository settings (5 minutes)

| Item | Where | Value / action |
|---|---|---|
| Direct-release environments | Settings → Environments | Create `windows-release` and `macos-release` if absent; neither was present during the configuration review. Add reviewers according to signing policy. |
| Store environment | Settings → Environments → `microsoft-store` | Already exists with credentials/product configuration, but reviewer protection was absent. Require independent reviewers, prevent self-review, and disable administrator bypass before any diagnostic or submission. |
| Workflow permissions | Settings → Actions → General → Workflow permissions | Keep default permissions read-only. Direct/mac jobs declare needed write permissions explicitly. Enable Actions pull-request creation only if repository policy allows the metadata PRs; the Store workflow does not create PRs and needs only read permissions. |
| GitHub Pages | Settings → Pages | Already configured for `localmorph.com` via `deploy.yml`. The desktop app reads `https://localmorph.com/appcast.xml` and `appcast-windows.xml`; both placeholders exist in `public/`. Nothing to change. |
| Branch protection (optional) | Settings → Branches | If `main` requires reviews, the auto-created release-metadata PRs will wait for you; merge them to publish the appcast/cask. |

**Verify:** `gh api repos/jamesmontemagno/my-file-converter/environments -q '.environments[].name'`
lists the intended environments. Check Store reviewer protections separately; environment existence
alone is insufficient.

---

## 1. Windows: Azure Artifact Signing (Trusted Signing)

The MSIX is signed with [Azure Artifact Signing](https://learn.microsoft.com/azure/trusted-signing/)
using OIDC — no certificate file lives in GitHub. This is the same account/profile tiny-clips uses, so
if that is already set up you only need to add the federated credential for this repo and copy the
values.

### 1a. Azure resources (skip if reusing tiny-clips' account)

1. Create an **Artifact Signing account** (Azure portal → *Trusted Signing Accounts* → Create). Note the
   **account name** and its **endpoint** (for example `https://wus2.codesigning.azure.net`).
2. Complete **identity validation** for *Refractored LLC* (Organization validation). This can take a few
   days; the certificate subject must come out as
   `CN=Refractored LLC, O=Refractored LLC, L=Seattle, S=Washington, C=US` — it has to match
   `Publisher` in `dotnet/src/LocalMorph.App/Platforms/Windows/Package.appxmanifest` exactly, or
   Windows refuses to install the MSIX and the `PackageFamilyName` (`Refractored.LocalMorph_vmshqmcyy894t`)
   changes.
3. Create a **certificate profile** of type *Public Trust* and note its **name**.

### 1b. Entra ID app registration + federated credential

1. Entra ID → App registrations → New registration (for example `localmorph-release-signing`).
   Note **Application (client) ID**, **Directory (tenant) ID**, and the **Subscription ID** that holds
   the signing account.
2. Certificates & secrets → **Federated credentials** → Add → *GitHub Actions deploying Azure resources*:
   - Organization: `jamesmontemagno`
   - Repository: `my-file-converter`
   - Entity type: **Environment**, name: `windows-release`
3. On the Artifact Signing account, IAM → Add role assignment → **Trusted Signing Certificate Profile
   Signer** → assign to the app registration.

### 1c. Environment secrets (`windows-release`)

| Secret | Value |
|---|---|
| `AZURE_CLIENT_ID` | Application (client) ID from 1b |
| `AZURE_TENANT_ID` | Directory (tenant) ID |
| `AZURE_SUBSCRIPTION_ID` | Subscription that contains the signing account |
| `AZURE_ARTIFACT_SIGNING_ENDPOINT` | e.g. `https://wus2.codesigning.azure.net` |
| `AZURE_ARTIFACT_SIGNING_ACCOUNT_NAME` | Signing account name |
| `AZURE_ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME` | Certificate profile name |

**Verify:** run **Windows Release** manually (Actions → Windows Release → Run workflow → tag
`v0.0.1-windows`) after creating that tag with `.\scripts\create-release-tags.ps1 v0.0.1 -Windows -Push`.
The *Sign MSIX packages* and *Verify signatures* steps must pass. Delete the test release/tag afterwards.

---

## 2. Windows: winget submission

`winget-submit.yml` is run by hand after you have looked at the GitHub Release.

| Secret (repository-level) | Value |
|---|---|
| `WINGET_CREATE_GITHUB_TOKEN` | A **classic** personal access token with the `public_repo` scope, from an account that can fork `microsoft/winget-pkgs` (your own account is fine). Fine-grained tokens do not work with `wingetcreate`. See <https://aka.ms/winget-create-token>. |

One-time: fork <https://github.com/microsoft/winget-pkgs> to your account so `wingetcreate` can push
branches there.

First submission of a **new package** (`Refractored.LocalMorph`) is reviewed by the winget maintainers
and usually takes 1–3 days; updates are mostly automated. The package's dependencies
(`Microsoft.DotNet.DesktopRuntime.10`, `Microsoft.WindowsAppRuntime.2`, `Gyan.FFmpeg`) are installed by
winget before the app.

**Verify:** after the PR merges, `winget search Refractored.LocalMorph` finds it and
`winget install Refractored.LocalMorph` installs and launches the app on a clean machine.

---

## 3. macOS: Developer ID signing + notarization

You need a paid Apple Developer Program membership (Refractored LLC team).

### 3a. Developer ID Application certificate

1. On a Mac: Keychain Access → Certificate Assistant → **Request a Certificate From a Certificate
   Authority** → save to disk.
2. <https://developer.apple.com/account/resources/certificates> → `+` → **Developer ID Application**
   → G2 Sub-CA → upload the CSR → download the `.cer` → double-click to install.
3. In Keychain Access select the certificate **and its private key** → Export → `.p12` with a strong
   password.
4. `base64 -i DeveloperID.p12 | pbcopy`

No provisioning profile is required: the app declares no restricted entitlements (sandbox is off so
it can spawn FFmpeg; the JIT entitlements in `Entitlements.plist` are hardened-runtime flags).

### 3b. App-specific password for notarytool

<https://appleid.apple.com> → Sign-In and Security → **App-Specific Passwords** → Generate
("LocalMorph notarization").

### 3c. Sparkle signing key

Used by `generate_appcast` to EdDSA-sign enclosures in the appcast (keeps the feed valid for a
future in-process Sparkle updater; the current updater just reads versions).

```bash
curl -L -o Sparkle.tar.xz https://github.com/sparkle-project/Sparkle/releases/download/2.8.1/Sparkle-2.8.1.tar.xz
mkdir Sparkle && tar -xf Sparkle.tar.xz -C Sparkle
./Sparkle/bin/generate_keys            # creates the key in your login keychain, prints the public key
./Sparkle/bin/generate_keys -x sparkle_private_key.txt   # export for GitHub; back this file up securely
```

Keep the **public key** printed by `generate_keys`; add it to `Info.plist` as `SUPublicEDKey` if/when
a native Sparkle updater is added. `sparkle_private_key*` is git-ignored.

### 3d. Environment secrets (`macos-release`)

| Secret | Value |
|---|---|
| `DEVELOPER_ID_CERTIFICATE_BASE64` | Output of step 3a.4 |
| `DEVELOPER_ID_CERTIFICATE_PASSWORD` | Password chosen when exporting the `.p12` |
| `KEYCHAIN_PASSWORD` | Any strong random string (protects the temporary CI keychain) |
| `APPLE_ID` | The Apple ID e-mail that owns the team membership |
| `APP_PASSWORD` | App-specific password from 3b |
| `APPLE_TEAM_ID` | 10-character Team ID (developer.apple.com → Membership details) |
| `SPARKLE_PRIVATE_KEY` | Full contents of `sparkle_private_key.txt` |

**Verify:** create and push `v0.0.1-mac`; the **macOS Release** workflow must pass *Notarize and
staple* (`status: Accepted`) and `spctl -a -vv` must print `accepted` / `source=Notarized Developer ID`.
Download the zip from the draft release and open the app on a Mac that has never seen it.

---

## 4. Homebrew cask

The cask lives in this repository (`Casks/localmorph.rb`) and is bumped automatically by the macOS
release PR. Users install with:

```bash
brew tap jamesmontemagno/my-file-converter https://github.com/jamesmontemagno/my-file-converter
brew install --cask localmorph
```

Nothing to configure. Optional later: submit to `homebrew/cask` once there is a notarized release
(cask name must be unique there; `livecheck` already points at the appcast).

---

## 5. Website / appcast publishing

`public/appcast.xml` (macOS) and `public/appcast-windows.xml` (Windows) are published by the existing
Pages deploy. Each release workflow opens a PR that replaces the file; **merging that PR is what makes
the in-app "Update available" pill appear** for users on older versions. The PWA service worker is
configured to never intercept those URLs.

**Verify:** `curl -s https://localmorph.com/appcast-windows.xml | head` returns XML (currently an empty
channel).

---

## 6. Cutting a release

1. Add a section to `dotnet/CHANGELOG.md`:
   ```markdown
   ## v1.0.0-windows
   ### Added
   - ...
   ## v1.0.0-mac
   ### Added
   - ...
   ```
   (The workflows copy the section whose heading matches the tag into the GitHub Release notes; the
   `tag-new-release` script warns if it is missing.)
2. Bump `ApplicationDisplayVersion` in `LocalMorph.App.csproj` only if you want the dev build to show
   the new number — the workflows stamp the real version from the tag.
3. Tag and push (both platforms, or just one):
   ```powershell
   .\scripts\create-release-tags.ps1 v1.0.0 -Windows -Mac -Push
   ```
4. Watch **Windows Release** and **macOS Release** in Actions. If the environments have required
   reviewers, approve them.
5. Review both GitHub Releases (assets, notes, hashes).
6. Merge the two `release-metadata/v1.0.0-*` PRs → appcasts and cask go live.
7. Run **WinGet Submission** with tag `v1.0.0-windows` → PR to `microsoft/winget-pkgs`.

Tag format: `vMAJOR.MINOR.PATCH[-REV]-windows|mac` — the optional fourth number is a same-day rebuild
(`v1.0.0.1-windows` → MSIX version `1.0.0.1`, asset `LocalMorph-1.0.0.1-x64.msix`).

---

## 7. Quick reference: every secret in one table

| Scope | Name | Used by |
|---|---|---|
| env `windows-release` | `AZURE_CLIENT_ID` | windows-release.yml |
| env `windows-release` | `AZURE_TENANT_ID` | windows-release.yml |
| env `windows-release` | `AZURE_SUBSCRIPTION_ID` | windows-release.yml |
| env `windows-release` | `AZURE_ARTIFACT_SIGNING_ENDPOINT` | windows-release.yml |
| env `windows-release` | `AZURE_ARTIFACT_SIGNING_ACCOUNT_NAME` | windows-release.yml |
| env `windows-release` | `AZURE_ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME` | windows-release.yml |
| repository | `WINGET_CREATE_GITHUB_TOKEN` | winget-submit.yml |
| env `macos-release` | `DEVELOPER_ID_CERTIFICATE_BASE64` | mac-release.yml |
| env `macos-release` | `DEVELOPER_ID_CERTIFICATE_PASSWORD` | mac-release.yml |
| env `macos-release` | `KEYCHAIN_PASSWORD` | mac-release.yml |
| env `macos-release` | `APPLE_ID` | mac-release.yml |
| env `macos-release` | `APP_PASSWORD` | mac-release.yml |
| env `macos-release` | `APPLE_TEAM_ID` | mac-release.yml |
| env `macos-release` | `SPARKLE_PRIVATE_KEY` | mac-release.yml |
| repository | `MICROSOFT_STORE_IDENTITY_NAME`, `MICROSOFT_STORE_PUBLISHER`, `MICROSOFT_STORE_PUBLISHER_DISPLAY_NAME` | windows-store-publish.yml package identity |
| repository (fallback) | `MICROSOFT_STORE_PRODUCT_ID` | Store product ID if the environment variable is absent |
| env `microsoft-store` | `PARTNER_CENTER_TENANT_ID`, `PARTNER_CENTER_SELLER_ID`, `PARTNER_CENTER_CLIENT_ID`, `PARTNER_CENTER_CLIENT_SECRET` | protected Store diagnose/publish |
| (built-in) | `GITHUB_TOKEN` | job-scoped permissions; Store uses read only, metadata PR creation is channel-specific |

Actions **variables**: `MICROSOFT_STORE_PRODUCT_ID` in `microsoft-store`, plus repository-level
`FFMPEG_COMPLIANCE_URL` and `FFMPEG_COMPLIANCE_SHA256`. Other direct-channel constants remain in
workflow `env:` blocks (`PACKAGE_FAMILY_NAME`, `SPARKLE_VERSION`, project paths).

Paste-ready `gh` commands once you have the values (run from the repo root):

```powershell
# Windows signing (environment secrets)
foreach ($s in 'AZURE_CLIENT_ID','AZURE_TENANT_ID','AZURE_SUBSCRIPTION_ID','AZURE_ARTIFACT_SIGNING_ENDPOINT','AZURE_ARTIFACT_SIGNING_ACCOUNT_NAME','AZURE_ARTIFACT_SIGNING_CERTIFICATE_PROFILE_NAME') {
  gh secret set $s --env windows-release
}
# winget (repository secret)
gh secret set WINGET_CREATE_GITHUB_TOKEN
# macOS (environment secrets)
foreach ($s in 'DEVELOPER_ID_CERTIFICATE_PASSWORD','KEYCHAIN_PASSWORD','APPLE_ID','APP_PASSWORD','APPLE_TEAM_ID') {
  gh secret set $s --env macos-release
}
gh secret set DEVELOPER_ID_CERTIFICATE_BASE64 --env macos-release < DeveloperID.p12.base64.txt
gh secret set SPARKLE_PRIVATE_KEY --env macos-release < sparkle_private_key.txt
```

Each `gh secret set` without a value prompts for it interactively so nothing lands in shell history.

---

## 8. Things that are intentionally *not* needed

- **Code-signing certificate files in the repo** — Windows uses Azure OIDC; macOS imports the `.p12`
  from a secret into a throwaway keychain and deletes it afterwards.
- **Bundled FFmpeg for direct channels** — winget and the cask install `Gyan.FFmpeg` / `ffmpeg` as
  dependencies. The Microsoft Store channel is different: it requires pinned bundled FFmpeg/ffprobe
  with approved corresponding sources/notices, and bundles .NET.
- **Mac App Store** — not wired up. The macOS build remains Developer ID, not sandboxed.

---

## Microsoft Store and native Explorer release gate

Use the complete [Store release setup](release-setup.md#microsoft-store-separate-identity-payload-and-approval)
before dispatching the new workflow:

- [ ] Confirm the Store identity secrets and canonical environment Product ID match Partner Center.
- [ ] Configure the required reviewer gate, prevent self-review, disable administrator bypass, and
  verify protection can be read by the workflow. Do not change GitHub settings without authorization.
- [ ] Review the exact pinned GPL FFmpeg redistribution and corresponding sources/build instructions/
  third-party notices; configure the public archive URL and SHA256. Do not treat a nonempty ZIP as
  legal approval.
- [ ] Ensure native C++ v143 and SDK tools exist for x64 **and** ARM64. Local x64 compilation is not
  proof of ARM64 compilation or runtime behavior.
- [ ] Build package-only from an exact valid Store tag (`v1.0.0-windows`, fourth component zero).
  Verify the real `.msixupload`, architecture-matched payloads and source-commit provenance.
- [ ] Run WACK and clean-install/conversion checks on native x64 and ARM64 without system .NET or
  FFmpeg. Confirm Store-resolved Windows App SDK frameworks.
- [ ] Verify the installed Explorer command with multi-selection, spaces/Unicode, cold/warm/minimized
  activation, unsupported selections and uninstall. It must queue only, not convert or change defaults.
- [ ] Verify keyboard/foreground queue and history menus, join ordering, split output selection,
  partial outputs, saved-settings restoration, scaled layouts, and accessibility.
- [ ] Validate Mac Catalyst compilation/runtime separately on macOS.
- [ ] Only after package/install validation, run protected **diagnose** for read-only account/app access.
- [ ] Obtain explicit submission intent and independent environment approval before manual **publish**.
  Review processing/certification status in Partner Center; upload success is not Store availability.

No actual Store upload, WACK pass, ARM64 runtime pass, or submission is implied by the helper tests
or a successful unpackaged Windows build.
