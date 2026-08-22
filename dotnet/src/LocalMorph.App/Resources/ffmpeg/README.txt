This directory is the bundling root for conversion tool binaries, per target runtime.
Anything placed here is copied to the output and found before PATH.

Expected layout:
  - win-x64/ffmpeg.exe      win-x64/ffprobe.exe
  - win-arm64/ffmpeg.exe    win-arm64/ffprobe.exe
  - maccatalyst-x64/ffmpeg  maccatalyst-x64/ffprobe
  - maccatalyst-arm64/ffmpeg  maccatalyst-arm64/ffprobe

Optional tools (magick, soffice, pandoc, gs) may be placed in the same per-RID folders.
Resolution order: bundled -> PATH -> well-known install locations (winget, Scoop, Chocolatey,
Homebrew, Program Files). See LocalMorph.Core/Tools/ToolLocator.cs.
