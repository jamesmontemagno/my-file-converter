This directory is the bundling root for FFmpeg binaries for each target runtime.
Expected layout:
  - win-x64/ffmpeg.exe
  - win-arm64/ffmpeg.exe
  - maccatalyst-x64/ffmpeg
  - maccatalyst-arm64/ffmpeg

The application resolves bundled FFmpeg before falling back to PATH.
