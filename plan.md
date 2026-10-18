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
- Tools view lists detected engines and offers user-initiated tool installation. The current development-only Mac Homebrew behavior is not approved for a Mac App Store build.
- Light/dark/system theme with a themed title bar.
- Desktop distribution is Store-only. Windows uses `windows-store-publish.yml`, self-contained .NET, external media tools, and sequential Internal Testing/production approvals. Direct desktop workflows, LocalMorph WinGet/Homebrew metadata, and the appcast updater have been removed. Browser and bridge distribution are unchanged.
- The approved Mac baseline is macOS 15+ on Apple Silicon only, replacing the earlier universal plan. Mac Catalyst targets 18.0/arm64. Apple heads register MAUI single-window scenes; CI targets Xcode 27/macOS 27 with compatible SDK/workload pins and effective-bundle validation. The isolated scene-enabled sandbox app now passes native startup, Apple software encoding, hard-cap and cancellation checks on that CI toolchain; normal workspace/Store and macOS 15 runtime remain unverified.

## Remaining work
- Complete the sandbox proof beyond Apple Silicon Debug evidence: source/component approval, minimum-baseline execution, Release runtime, and complete codec/filter coverage. Scene-enabled, ad-hoc-signed container checks pass locally and on Xcode 27/macOS 27 CI for Apple software H.264/HEVC, target-size enforcement and cancellation, without Store credentials. The proof scripts build/package arm64 FFmpeg/FFprobe only; do not replace them with a copied Homebrew installation. Earlier universal artifacts are historical evidence, not the new shipping payload.
- Preserve authorized native URLs and persistent security-scoped bookmarks for all input/output entry points. Ask for an output folder before first conversion; authorize destination staging and history reuse.
- Verify shared feature parity, native Mac behavior, and actual bundled codec/filter capabilities before finalizing claims and assets. The minimal source-built proof still lacks libass/zscale and external codecs; it is not an approved shipping payload.
- Add verified Apple Silicon-only/macOS 15+ Mac Store packaging, protected TestFlight delivery, separate review approval, and manual public release. Mac release tags remain blocked until the Store workflow exists.
- The six Apple Actions secret names are present at repository scope; see the [Mac configuration runbook](docs/release-setup.md#actions-signing-and-upload-configuration). Validate the combined application/installer P12, LocalMorph-specific profile and API access behind a newly protected `mac-app-store` environment before using them. Secret contents and account readiness are not yet verified.
- Prepare Mac-specific metadata/privacy, genuine screenshots, website film, and a separate App Store preview. Keep public Mac launch messaging gated on actual availability.
- Complete Windows Store certification and native x64/ARM64 checks without changing its external-media-tool policy.
- See [release setup](docs/release-setup.md) and the [shipping checklist](docs/shipping-checklist.md) for current gates. Apple account/signing readiness remains unverified; Intel support/testing is no longer in scope.
