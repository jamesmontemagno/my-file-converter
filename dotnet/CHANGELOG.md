# LocalMorph Desktop Changelog

All notable changes to the .NET MAUI desktop app. Release workflows copy the section matching
the git tag (`## v1.2.3-windows` / `## v1.2.3-mac`) into the GitHub Release notes.

## Unreleased

### Added
- Batch conversion workspace: queue any number of files via picker, folder, drag & drop, or command line; parallel conversion with progress, speed, ETA, and per-job cancel.
- 50+ output formats across video (H.264/H.265/AV1/VP9/ProRes/GIF/animated WebP/APNG), audio (MP3/AAC/FLAC/ALAC/Opus/OGG/WAV/AIFF), images (PNG/JPEG/WebP/AVIF/JPEG XL/TIFF/BMP/ICO/HEIC), and documents (PDF/DOCX/XLSX/PPTX/ODT/EPUB/Markdown/HTML).
- Quick presets, trim and frame grab from the media preview, two-pass target file size, rotate, playback speed, loudness normalization, strip metadata, lossless toggles.
- Hardware encoding via NVENC, Quick Sync, AMF, and VideoToolbox, verified with a test encode.
- Tools view: detects FFmpeg, ImageMagick, LibreOffice, Pandoc, and Ghostscript; one-click `winget`/`brew` install.
- History of finished conversions with reopen, reveal, and reconvert.
- Light/dark/system theme with a themed title bar.
- In-app update check against the LocalMorph appcast (winget / Homebrew upgrade hints).
