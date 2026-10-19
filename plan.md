# LocalMorph desktop app plan

## Current status
- Fixed the installed build 2 launch crash in #43, merged as `1a4cb71`: theme bindings now
  target the shell/page background directly, preserving Windows transparency and Mac themes.
  **1.0.0 / build 3** is uploaded, processed and confirmed in Internal Test Group by
  run `39197645028`. Apple build ID: `9a5ae49a-9664-4290-ad32-c7e69826c38d`.
  Native Release workspace startup passes light/dark/system on Xcode 27 and the unchanged
  signed workspace on macOS 15.7.9; both PR and merged-source CI pass. The new native gate
  rejects the retained pre-fix Release with the original AppShell exception.
  Actual installed TestFlight build 3 now passes signature validation and native startup on
  the owner's Mac: visible workspace, loaded/initialized marker and a live process.
  Broader #41 conversion/permission/relaunch acceptance remains open.
- Merged #36 and focused distribution-profile fix #42. **1.0.0 / build 2** from `8f47ec2`
  uploaded and processed successfully in Mac Store Release run `39182791350`, attempt 2.
  Apple build `10f323ab-7fa3-4fa1-869b-e84cdb584f19` is confirmed in **Internal Test Group**
  (`5e573fb7-8a0f-4446-956d-78f615808373`). No App Review or public release occurred.
- The first attempt caught a strict-check incompatibility with disabled SDK/profile debug flags.
  The fix preserves only Boolean-false flags and still rejects enabled debugging and unapproved
  capabilities. The group-selection retry reused the exact signed package without rebuilding.
- Simplified Mac packaging to build media in place before key import, keeping the signed-package handoff and
  both approvals. Website CI/Pages now use input-path filters; desktop jobs route by component.
- Outstanding decisions and acceptance are tracked in #39 (media/components), #41 (installed
  TestFlight/native grants), #38 (listing/privacy/export/product), #40 (real captures/videos),
  and #37 (beta exit, review and public launch). None authorizes automatic public release.
- The initial Mac beta pipeline is implemented and locally validated, including real ad-hoc
  Release staging, minimal entitlements, bundled SDK privacy reasons and matching native symbols.
  Fifty-eight offline helper tests pass. Real distribution/profile/API/package/upload evidence now
  passes too; an installed TestFlight build and native end-to-end behavior remain separate gates.
- The .NET MAUI desktop app is a full batch conversion workspace (Windows verified end-to-end; the normal Mac workspace still needs end-to-end verification). An isolated signed Mac sandbox proof has exercised shared Core inspection, H.264 conversion, staging/commit, cancellation, and native input/output grants on Apple Silicon.
- `LocalMorph.Core` holds the engine-agnostic conversion model: tool discovery, FFmpeg capability detection, the format catalog, presets, command builders, the queue, and history.
- `LocalMorph.Core.Tests` covers command building and real FFmpeg integration, including target size, loudness, subtitles, HDR, join/split, cancellation, and source-file protection.
- A `winapp ui` UI-automation script exercised the Windows app end-to-end (44 checks: presets, format chooser, trim, convert, skip rules, history, tools, theme).

## What is working
- Multi-file queue: file picker, folder picker, drag & drop (Windows + Mac Catalyst code path), command-line arguments, and OS "Open with" (Windows `fileTypeAssociation` + `AppInstance` single-instance redirection; macOS `CFBundleDocumentTypes` + scene URL contexts). Verified on Windows both unpackaged and as a registered MSIX.
- Per-file inspection via ffprobe (codec, dimensions, duration, audio) with extension fallback.
- Format catalog with 50+ targets across video, audio, image, and documents; availability reflects installed tools and compiled-in encoders; mixed batches skip files a format does not apply to.
- Engines: FFmpeg (all media), ImageMagick (HEIC/RAW/SVG/PSD/ICO/HEIC/PDF), LibreOffice (office documents), Pandoc (Markdown/HTML/EPUB), Ghostscript (PDF compress/rasterize).
- Hardware encoders verified by a test encode and used automatically (NVENC, Quick Sync, AMF, VideoToolbox).
- Target-size encoding uses x264 two-pass or, without libx264, Apple software with at most three bitrate-adjusted attempts and authoritative byte-limit rejection. Apple H.264/HEVC software mode requests an explicit bitrate. Trim, frame grab, rotation, playback speed, loudness normalization, metadata and lossless options retain their existing behavior.
- Live progress/ETA/speed, per-job cancel, cancel all, friendly failure messages with the tool log, open/reveal results, history with reconvert.
- Developer/Windows tools views retain external-tool setup. The separate Mac sandbox workspace discovers only FFmpeg/FFprobe in its own bundle, reports missing/broken tools as repair errors, and offers no package-manager or installer actions.
- Light/dark/system theme with a themed title bar.
- Desktop distribution is Store-only. Windows uses `windows-store-publish.yml`, self-contained .NET, external media tools, and sequential Internal Testing/production approvals. Direct desktop workflows, LocalMorph WinGet/Homebrew metadata, and the appcast updater have been removed. Browser and bridge distribution are unchanged.
- The approved Mac baseline is macOS 15+ on Apple Silicon only, replacing the earlier universal plan. Mac Catalyst targets 18.0/arm64. Apple heads register MAUI single-window scenes. The Xcode 27/macOS 27 diagnostic app and the exact transferred app on macOS 15.7.9 passed native startup, Apple software encoding, hard-cap and cancellation checks. This is not exact macOS 15.0 or normal Store/TestFlight runtime evidence.
- Added a Debug-only `LocalMorphMacStoreBuild` workspace with separate identity/output/container and sandbox/bookmark entitlements. Native pickers, Finder and drop URLs are bookmarked before becoming paths. Inspection, preview and jobs have independent reference-counted access; the job lease survives terminal notification through process cleanup.
- Output authorization is explicit before conversion; saved path-only settings/history require reauthorization. Attachment and history paths use the same grant layer, revoked/moved permissions fail explicitly, and Tools can forget permissions without deleting files/history. Native file/folder opening avoids shell fallback in this flavor.
- Added `Mac Store Release` (`mac-store-publish.yml`): protected package, read-only diagnose and separately approved TestFlight modes using the six existing Apple secrets. Tags package only; merges never upload. The owner authorized the protected `mac-app-store` environment and the current pinned beta payload. Package hashes/source/symbols and Apple processing/internal-group confirmation are separate checks; review/public release are not automated.

## Remaining work
- Complete full source/component approval, installed Release runtime and codec/filter coverage. Release builds require the explicit beta flag and pinned media path; default Release remains blocked. Initial distribution signing/profile/API access is verified, not final public-shipping approval. Do not replace source-built arm64 tools with a copied Homebrew installation.
- Verify the implemented grant layer in the normal isolated workspace: external files after actual process relaunch, iCloud/external/network volumes, denied/read-only destinations, preview/remove/cancel/quit, Finder opening and Dock reopen. Native bookmark create/resolve with a container fixture passes locally, but that does not prove persistent external access. The diagnostic app now saves selected grants and has an explicit Restore action that distinguishes same-process from restarted-process evidence.
- Verify shared feature parity, native Mac behavior, and actual bundled codec/filter capabilities before finalizing claims and assets. The minimal source-built proof still lacks libass/zscale and external codecs; it is not an approved shipping payload.
- Install and validate the processed TestFlight build on supported Macs, then retain separate review approval and manual public release. Mac tags invoke package-only builds; future TestFlight versions still need explicit dispatch and approvals.
- The six Apple Actions secrets worked in the approved beta run; see the [Mac configuration runbook](docs/release-setup.md#actions-signing-and-upload-configuration). The `mac-app-store` environment requires owner approval, disables administrator bypass, and restricts main/Mac tags with self-review explicitly allowed. The optional internal-group variable now selects the owner-provided Internal Test Group.
- Prepare Mac-specific metadata/privacy, genuine screenshots, website film, and a separate App Store preview. Keep public Mac launch messaging gated on actual availability.
- Complete Windows Store certification and native x64/ARM64 checks without changing its external-media-tool policy.
- See [release setup](docs/release-setup.md) and the [shipping checklist](docs/shipping-checklist.md) for current gates. Public-submission account/product decisions remain open; Intel support/testing is no longer in scope.
