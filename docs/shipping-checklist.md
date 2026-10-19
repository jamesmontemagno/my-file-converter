# Store-only desktop shipping checklist

LocalMorph desktop distribution is **Microsoft Store for Windows** and **Mac App Store for Mac**.
Direct MSIX/ZIP releases, LocalMorph WinGet/Homebrew distribution, and appcasts are removed.
Windows conversion-tool acquisition, browser conversion, and bridge/global-tool releases remain.

See [release-setup.md](release-setup.md) for the operator runbook and exact configuration scopes.
No successful package, helper test, or compilation establishes certification or public availability.

## Microsoft Store configuration

- Confirm the reserved product and Store identity secrets:
  `MICROSOFT_STORE_IDENTITY_NAME`, `MICROSOFT_STORE_PUBLISHER`,
  `MICROSOFT_STORE_PUBLISHER_DISPLAY_NAME`.
- Set `MICROSOFT_STORE_PRODUCT_ID` in the `microsoft-store` environment. The repository secret
  of the same name remains a fallback.
- Keep `PARTNER_CENTER_TENANT_ID`, `PARTNER_CENTER_SELLER_ID`, `PARTNER_CENTER_CLIENT_ID`,
  and `PARTNER_CENTER_CLIENT_SECRET` inside the protected `microsoft-store` environment.
- Require reviewer approval and disable administrator bypass. The owner explicitly permits
  self-approval; the workflow checks this policy with `Test-StoreApproval.ps1 -AllowSelfReview`.
  Missing/unreadable protections must fail closed.
- Confirm exactly one existing package flight named **Internal Testing**, with intended testers.
  The scripts discover it by name; they do not create it or fall back to production.
- Confirm category, pricing/markets, support and published desktop privacy policy, age ratings,
  and the [Microsoft Store listing kit](../store/microsoft-store/README.md).
- Keep the FFmpeg/FFprobe external requirement at the beginning of the listing and in review notes.
  The Windows package bundles .NET but not media tools. Tool installation may require internet
  access and acceptance of the tool publisher's licenses.

## Windows package and runtime gates

- Use PowerShell 7 and Visual Studio 2022 or newer with Windows SDK and C++ tools for both
  x64 and ARM64. The Explorer DLL and its matching PDB must be built for each architecture.
- Run `scripts/windows/Test-StoreScripts.ps1`, then package an exact valid immutable Store tag:
  `v1.0.0-windows` or `v1.0.0.0-windows` (fourth component must be zero).
- Verify the real `.msixupload`: Store identity/version, both payloads, self-contained .NET,
  Store-resolved Windows App SDK frameworks, native Explorer registrations, and source provenance.
- Verify both architecture-specific `.appxsym` archives carry matching App/Core/Shell symbols.
  Revalidate downloaded artifacts and hashes before submission; PDBs must not be installed in
  MSIX payloads. Symbols do not guarantee crash reports or enable telemetry.
- Run WACK and clean installation on native x64 and ARM64 machines without system .NET/FFmpeg.
  ARM64 compilation on x64 is not native runtime evidence.
- Verify missing-tool guidance, WinGet installation and manual installation of FFmpeg/FFprobe,
  Refresh, inspection and real conversions. Include missing FFprobe, unavailable WinGet, and
  native-architecture compatibility. A full manual Windows build can put both executables in
  `C:\ffmpeg\bin`.
- Verify installed Explorer cold/warm/minimized activation, multiple/unsupported selections,
  spaces/Unicode and uninstall. It must queue only, not convert or change default associations.
- Verify keyboard-accessible queue/history menus, join ordering, split output selection,
  retained partial outputs, settings restoration, themes, scaled layouts and accessibility.
- Confirm only Store-managed updates remain; no website feed or direct installer is contacted.

## Windows submission gates

1. Manual **package** builds and verifies only; no Partner Center mutation.
2. Protected **diagnose** is read-only token/app access and flight discovery, not draft creation.
3. A tag push or manual **publish** waits for approval before submitting the validated upload
   to **Internal Testing**.
4. Wait for flight publication, install as a tester, and complete the checks above.
5. Separately approve production. It must use the same upload hash/source/version as the flight.
6. Check processing, certification and Store availability separately from submission success.

Do not use `--noCommit` as a claimed read-only diagnostic: creating/uploading a draft is a mutation.

## Mac App Store gates

Mac delivery is not yet enabled. Do not tag or advertise a public Mac release until these pass.

- Confirm Apple membership, app record, effective bundle identifier, category,
  pricing/territories, agreements and required questionnaires.
- Enforce the approved **macOS 15+ / Apple Silicon-only** baseline: Catalyst target 18.0,
  arm64 app and bundled executables, no Intel/universal package. Verify generated minimum-OS
  metadata rather than interpreting the Catalyst version as a macOS version.
- Use the pinned Xcode 27/.NET SDK/workload combination in the
  [Mac toolchain runbook](release-setup.md#macos-toolchain). Verify scene delegates/manifests
  and actual single-window startup on macOS 27, including cold/warm Finder intake, Dock
  reactivation and quit/relaunch. Also test macOS 15; build success is not runtime evidence.
- The six existing Apple repository secret names and their intended uses are recorded in
  [Actions signing and upload configuration](release-setup.md#actions-signing-and-upload-configuration).
  Verify that the P12 contains both application and installer private-key identities and that the
  profile belongs to LocalMorph. Do not request duplicate API-key aliases merely to match another app.
- Recheck the owner-approved `mac-app-store` environment before credential diagnostics, signing
  or upload: owner review, no administrator bypass, main/Mac tags only and self-review allowed.
  It was created October 18, 2026; moving repository secrets remains a separate owner action.
- Prove signed sandboxed execution using bundled FFmpeg/FFprobe without Homebrew or host PATH.
  Review component licenses, exact source/build provenance, hashes, codec/filter availability,
  native dependencies and necessary entitlements before approving a tool payload.
- Inspect the `mac-sandbox-proof` CI evidence independently from host-FFmpeg test results.
  The minimal Debug proof checks Apple software H.264/HEVC, target-size caps and cancellation,
  and now includes libass/zscale. Confirm `subtitles`, `ass` and `zscale` appear on the Tools
  page and that the dependency `sources/` archives and `licenses/` ship with the package. Verify explicit software bitrate,
  at most three size attempts, and rejection without destination replacement if the cap is missed.
- Verify persistent file/folder authorization for pickers, Finder, drag/drop, subtitles,
  replacement audio, preview, conversion, history and reauthorization after relaunch.
  The Debug-only sandbox workspace and grant wiring now compile; this is not native workspace
  evidence. Use the diagnostic Restore action after a real process restart for external grants;
  its automated native container-bookmark round trip is a separate, narrower check.
- Keep default `LocalMorphMacStoreBuild` Release blocked. The owner-selected TestFlight beta
  requires `LocalMorphMacStoreBetaRelease`, pinned media and verified application/installer
  signing. Optional external engines are replaced by macOS built-in converters. Preserve the separate
  Debug workspace identity/output/container; beta permission is not final shipping approval.
- Request an output folder before first conversion and remember authorized access. Source-file
  selection alone must not be treated as permission to create sibling outputs.
- Verify staging/commit, overwrite protection, cancellation, join/split and partial results
  against real sandbox permissions and the actual bundled codec/filter set.
- Configure application distribution and installer signing, provisioning, package validation,
  architecture checks and matching crash symbols. Notarized direct downloads are not the channel.
  Credentialed evidence passes in run `39197645028`: 1.0.0/build 3 is signed, uploaded,
  processed and confirmed in the selected internal group. It includes #43's build 2 startup fix;
  the actual Release workspace passes native CI under all themes. The actual installed build 3
  also passes signature validation and visible/initialized workspace startup on the owner's Mac.
  Clean supported-OS and full native workflow acceptance remain required. Package mode and tag pushes do not upload; future TestFlight uploads
  still require explicit dispatch and separate upload approval.
- Validate build-number monotonicity and retry behavior; retain the exact package hash, version,
  build number and source before upload. Review submission must select the same processed build
  tested through TestFlight, not a rebuilt package.
- Audit applicable privacy manifests and data declarations; publish reviewed desktop privacy
  and support information. Local media processing alone does not answer every privacy question.
- Capture genuine Mac screenshots at accepted dimensions, with build/tool/output evidence.
  Produce a website launch film and a separate screen-recording-led App Store preview; validate
  each against its own requirements. Do not reuse Windows captures or fabricate UI/history.
- Configure protected TestFlight upload and review gates, then test the same distributable
  build on clean Apple Silicon Macs at the macOS 15 baseline and on macOS 27.
  Do not upload as TestFlight Internal Only if it
  is intended to ship to customers.
- Require separate review submission approval and manual public release after Apple approval.
  Update website launch claims only after actual availability is verified.

## Repository and retired-channel cleanup

- Keep Actions defaults read-only; retain only the permissions needed by active Store/bridge/
  website workflows.
- Retire unused direct-release environment/credential names only after checking other consumers
  and obtaining authorization. Do not print secret values or remove shared credentials.
- Report any external LocalMorph WinGet/Homebrew entries needing retirement separately.
- Preserve existing published history/tags and all bridge releases.
- Keep the Windows and browser website routes usable while Mac delivery remains unavailable.
