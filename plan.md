# LocalMorph desktop app plan

## Current status
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

## Remaining work
- Complete source/component approval, Release runtime and codec/filter coverage. Store Release packaging deliberately fails until an approved full payload and distribution-signing validation exist. The proof scripts build/package arm64 FFmpeg/FFprobe only; do not replace them with a copied Homebrew installation.
- Verify the implemented grant layer in the normal isolated workspace: external files after actual process relaunch, iCloud/external/network volumes, denied/read-only destinations, preview/remove/cancel/quit, Finder opening and Dock reopen. Native bookmark create/resolve with a container fixture passes locally, but that does not prove persistent external access. The diagnostic app now saves selected grants and has an explicit Restore action that distinguishes same-process from restarted-process evidence.
- Verify shared feature parity, native Mac behavior, and actual bundled codec/filter capabilities before finalizing claims and assets. The minimal source-built proof still lacks libass/zscale and external codecs; it is not an approved shipping payload.
- Add verified Apple Silicon-only/macOS 15+ Mac Store packaging, protected TestFlight delivery, separate review approval, and manual public release. Mac release tags remain blocked until the Store workflow exists.
- The six Apple Actions secret names are present at repository scope; see the [Mac configuration runbook](docs/release-setup.md#actions-signing-and-upload-configuration). Validate the combined application/installer P12, LocalMorph-specific profile and API access behind a newly protected `mac-app-store` environment before using them. Secret contents and account readiness are not yet verified.
- Prepare Mac-specific metadata/privacy, genuine screenshots, website film, and a separate App Store preview. Keep public Mac launch messaging gated on actual availability.
- Complete Windows Store certification and native x64/ARM64 checks without changing its external-media-tool policy.
- See [release setup](docs/release-setup.md) and the [shipping checklist](docs/shipping-checklist.md) for current gates. Apple account/signing readiness remains unverified; Intel support/testing is no longer in scope.
