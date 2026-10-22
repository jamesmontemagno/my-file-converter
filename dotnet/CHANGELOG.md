# LocalMorph Desktop Changelog

All notable changes to the .NET MAUI desktop app. Platform tags identify Store package sources;
desktop installers are not distributed through GitHub Releases.

## Unreleased

### Added
- Desktop: import, export, and extract YouTube SBV, LRC lyrics, and paragraph-timed TTML alongside
  SRT/WebVTT/ASS. New subtitle formats also work as selectable video tracks and for burn-in.
  Plain-text interchange follows CaptionStack conventions; LRC end times are inferred on import
  and omitted on export. Pickers, presets, and Windows file associations include the new extensions.
- Mac: bundled FFmpeg now statically includes libass (CoreText fonts, FreeType, FriBidi,
  HarfBuzz) and zimg, enabling subtitle burn-in and HDR-to-SDR tone mapping. Pinned,
  hash-verified dependency sources and licenses ship with every package; Tools → Licenses
  shows all third-party notices, including Markdig.
- Mac: macOS built-in converters (ImageIO, Core Graphics, UIKit text) replace the optional
  ImageMagick/LibreOffice/Pandoc/Ghostscript engines that the sandbox cannot run. Images
  (incl. HEIC, RAW, PSD) convert to PNG/JPEG/TIFF/HEIC/AVIF/GIF/BMP/ICO/PDF; PDFs render their
  first page to an image; Markdown/HTML/RTF/TXT convert to PDF/RTF/HTML/TXT (Markdown via
  Markdig, BSD-2). The Tools page lists them as built in. Windows behavior is unchanged.

### Fixed
- Windows: omit the nonexistent app build number from About and feedback drafts; Mac still shows its build number.
- Gridded HEIC photos showed the 512×512 tile size; Mac now reads the real image size natively.

### Changed
- Added a protected Mac Store beta pipeline with package-only, read-only diagnostics and
  approved TestFlight delivery using the existing Apple secrets. Release bundles contain
  source-pinned FFmpeg/FFprobe, attribution, SDK privacy reasons and matching crash symbols.
  Store Release uses the interpreter without JIT exceptions. Initial beta codec limitations
  remain explicit; App Review and public release are never automatic.
- Added an isolated, sandboxed Mac workspace build with bundle-only media-tool discovery,
  no external installers, and explicit repair errors. Store Release requires the explicit
  TestFlight beta flag and pinned media payload.
- Mac sandbox intake retains native URL bookmarks across picker/Finder/drop, attachment and
  history flows. Preview, inspection and queued jobs hold independent permissions through cleanup;
  conversion requires an authorized output folder rather than assuming sibling write access.
  External relaunch/cloud-volume and final Store runtime checks remain release gates.
- FFmpeg can use Apple's H.264/HEVC software encoders when libx264/libx265 are absent.
  Software mode exposes an explicit bitrate instead of unsupported quality/speed controls.
  Apple target-size jobs make at most three bitrate-adjusted attempts and never commit an
  oversized result; the existing x264 two-pass path is unchanged.
- Mac builds target macOS 15+ on Apple Silicon only, replacing Intel/universal output.
  Apple app heads adopt MAUI's single-window scene lifecycle. Mac CI pins a compatible
  Xcode 27 workload and checks generated scene/OS/architecture metadata; macOS 27 startup
  and Store certification remain release gates.
- Mac CI builds source-pinned, non-GPL proof tools and runs an isolated ad-hoc-signed
  sandbox app to check scene startup, H.264/HEVC software conversion, hard caps and cancellation.
  This diagnostic payload is not the approved shipping tool bundle or Store certification.
- Desktop distribution is Microsoft Store and Mac App Store only. Removed direct MSIX/ZIP
  release workflows, LocalMorph WinGet/Homebrew distribution, appcasts, and in-app download checks.
  Windows conversion-tool installation and browser/bridge delivery are unchanged.
- Updates are managed by the platform Store. Windows can open Store updates; Mac shows
  App Store update instructions without polling a website or offering terminal upgrades.

## v1.0.1-windows

### Added
- Task-oriented batch preflight, persistent conversion action, bounded queue view, cancellable import/inspection, and durable intake summaries. Each import accepts 2,000 supported files, traversing folders through depth eight.
- Responsive image/video preview with expand/collapse controls, a persistent preview header, and scrollable per-file settings without replacing the active player.
- Task/presets/settings panel and its persistent Convert action grow proportionally with the window instead of remaining at a fixed width.
- Measured per-track loudness normalization and audio-output leading/trailing silence trimming that preserves internal pauses.
- Explicit PQ/HLG HDR-to-BT.709 SDR tone mapping, gated by FFmpeg filter availability.
- Ordered compatible-video joining without transcoding, and duration splitting with accurate re-encoding or explicitly keyframe-aligned stream copy. Multiple/partial results are retained and recorded in backward-compatible history.
- Native queue/history context menus and a packaged Windows Explorer "Convert with LocalMorph" command. Explorer queues files without converting or changing default associations.
- Separate Store build flavor and package/diagnose/publish workflow, using the configured Store identity and protected Partner Center credentials. Store packages bundle .NET, use separately installed FFmpeg/FFprobe, and disable direct appcast updates. The Tools page supports user-initiated WinGet or manual installation. Real package certification, missing-tool setup, and native-architecture installation checks remain release gates.
- Audio tracks: every track is listed with language, channels, and title. Choose which track to keep or extract, keep all tracks with the chosen one first and default, or replace the soundtrack with another audio file.
- Subtitles: keep, remove, add an SRT/VTT/ASS file as a track, or burn a track/file into the picture (text and PGS/VobSub). Extract subtitle tracks and convert between SRT, WebVTT, and ASS; subtitle files can now be queued directly.
- Edit & effects: crop to common aspect ratios (9:16, 1:1, 4:5, …), mirror/flip, deinterlace, denoise, fade in/out, reverse, volume, and audio-sync offset.
- New outputs and presets: contact sheet (4×4 thumbnails), waveform image, Vertical 9:16, Square 1:1, Mute video, Extract subtitles, SubRip, WebVTT.
- The file summary shows track, subtitle, and chapter counts.
- Batch conversion workspace: queue files via picker, folder, drag & drop, or command line; parallel conversion with progress, speed, ETA, and per-job cancel.
- 50+ output formats across video (H.264/H.265/AV1/VP9/ProRes/GIF/animated WebP/APNG), audio (MP3/AAC/FLAC/ALAC/Opus/OGG/WAV/AIFF), images (PNG/JPEG/WebP/AVIF/JPEG XL/TIFF/BMP/ICO/HEIC), and documents (PDF/DOCX/XLSX/PPTX/ODT/EPUB/Markdown/HTML).
- Quick presets, trim and frame grab from the media preview, two-pass target file size, rotate, playback speed, loudness normalization, strip metadata, lossless toggles.
- Hardware encoding via NVENC, Quick Sync, AMF, and VideoToolbox, verified with a test encode.
- Tools view: detects FFmpeg, ImageMagick, LibreOffice, Pandoc, and Ghostscript; one-click `winget`/`brew` install.
- Windows: HEIC/HEIF photos decode natively through the Windows HEIF Image Extensions codec (Windows Imaging Component) for PNG/JPEG/BMP/TIFF/GIF output, no ImageMagick required. When the codec is missing, the Tools view and the queue offer a "Get from Store" action that opens its Microsoft Store page, and the Tools view warns when the companion HEVC decoder is absent.
- History of finished conversions with reopen, reveal, and reconvert.
- Light/dark/system theme with a themed title bar.
- In-app update check against the LocalMorph appcast (removed in the Store-only transition).
- "Open with" support: LocalMorph registers as a handler for video, audio, image, and document files on Windows (MSIX file type associations) and macOS (`CFBundleDocumentTypes`). Opening files from Explorer/Finder, or launching a second time, adds them to the running app's queue and brings it to the front.

### Fixed
- Failed/canceled conversions no longer truncate existing destinations. Same-directory staged results commit after validation, and every queued source path is protected from overwrite.
- Target-size accounting uses decimal MB, includes all retained audio tracks, and rejects results exceeding the requested byte limit.
- Update failures and history persistence failures are reported instead of being presented as successful no-op checks.
