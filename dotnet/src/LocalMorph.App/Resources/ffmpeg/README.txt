This is the legacy/local-development bundling root for conversion tool binaries, per target runtime.
Windows Store builds exclude it and require separately installed tools. Mac App Store packaging
is not enabled; copying tools here does not produce a sandboxed, signed, self-contained Store bundle.

Expected layout:
  - win-x64/ffmpeg.exe      win-x64/ffprobe.exe
  - win-arm64/ffmpeg.exe    win-arm64/ffprobe.exe
  - maccatalyst-arm64/ffmpeg  maccatalyst-arm64/ffprobe

Optional tools (magick, soffice, pandoc, gs) may be placed in the same per-RID folders.
Resolution order: bundled -> PATH -> well-known install locations (winget, Scoop, Chocolatey,
Homebrew, Program Files). See LocalMorph.Core/Tools/ToolLocator.cs.

The isolated Mac sandbox proof uses source-built tools in Contents/MacOS, with no host discovery.
See scripts/mac/build-proof-media.sh, scripts/mac/prepare-sandbox-proof.sh, and docs/release-setup.md.
