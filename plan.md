# LocalMorph desktop app plan

## Current status
- The .NET MAUI desktop app is a full batch conversion workspace (Windows verified end-to-end; macOS builds share the same code but are not yet exercised on hardware).
- `LocalMorph.Core` holds the engine-agnostic conversion model: tool discovery, FFmpeg capability detection, the format catalog, presets, command builders, the queue, and history.
- `LocalMorph.Core.Tests` (73 tests) covers command building for every format family plus real FFmpeg integration runs (resize, trim/remux, two-pass target size, GIF, audio codecs, image codecs, image→video, frame grab, cancel, parallel batch, hardware encoder).
- A `winapp ui` UI-automation script exercised the Windows app end-to-end (44 checks: presets, format chooser, trim, convert, skip rules, history, tools, theme).

## What is working
- Multi-file queue: file picker, folder picker, drag & drop (Windows + Mac Catalyst code path), command-line arguments.
- Per-file inspection via ffprobe (codec, dimensions, duration, audio) with extension fallback.
- Format catalog with 50+ targets across video, audio, image, and documents; availability reflects installed tools and compiled-in encoders; mixed batches skip files a format does not apply to.
- Engines: FFmpeg (all media), ImageMagick (HEIC/RAW/SVG/PSD/ICO/HEIC/PDF), LibreOffice (office documents), Pandoc (Markdown/HTML/EPUB), Ghostscript (PDF compress/rasterize).
- Hardware encoders verified by a test encode and used automatically (NVENC, Quick Sync, AMF, VideoToolbox).
- Two-pass target-size encoding, trim from the preview scrubber, frame grab at the current position, rotate, playback speed, loudness normalization, strip metadata, lossless toggles.
- Live progress/ETA/speed, per-job cancel, cancel all, friendly failure messages with the tool log, open/reveal results, history with reconvert.
- Tools view lists detected engines and offers one-click `winget`/`brew` installs.
- Light/dark/system theme with a themed title bar.

## Remaining work
- Bundle FFmpeg per RID for Windows and macOS release builds (layout documented in `Resources/ffmpeg/README.txt`).
- Run and polish the Mac Catalyst build on hardware (drag & drop, VideoToolbox, Finder reveal).
- Packaging: MSIX / notarized macOS bundle and a direct-download updater.
- Optional: image sequence → video, subtitle burn-in, watermark overlay, per-file format overrides.
