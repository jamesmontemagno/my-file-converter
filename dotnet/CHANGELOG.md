# LocalMorph Desktop Changelog

All notable changes to the .NET MAUI desktop app. Release workflows copy the section matching
the git tag (`## v1.2.3-windows` / `## v1.2.3-mac`) into the GitHub Release notes.

## Unreleased

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
- In-app update check against the LocalMorph appcast (winget / Homebrew upgrade hints).
- "Open with" support: LocalMorph registers as a handler for video, audio, image, and document files on Windows (MSIX file type associations) and macOS (`CFBundleDocumentTypes`). Opening files from Explorer/Finder, or launching a second time, adds them to the running app's queue and brings it to the front.

### Fixed
- Failed/canceled conversions no longer truncate existing destinations. Same-directory staged results commit after validation, and every queued source path is protected from overwrite.
- Target-size accounting uses decimal MB, includes all retained audio tracks, and rejects results exceeding the requested byte limit.
- Update failures and history persistence failures are reported instead of being presented as successful no-op checks.
