# LocalMorph desktop app plan

## Current status
- The .NET MAUI desktop app is now scaffolded and the polished UI shell is in place.
- The project includes the CommunityToolkit.Maui and CommunityToolkit.Mvvm packages and a file-pick + FFmpeg preview flow.
- The Windows app has been launched successfully from the generated build output, and the host process is running.

## What is working
- Shared conversion models and FFmpeg command-building logic are centralized in the core library.
- The MAUI app resolves bundled FFmpeg before falling back to PATH.
- The Windows desktop target builds and runs locally.

## Remaining work
- Finish the real conversion execution service and progress reporting.
- Package FFmpeg per RID for Windows and macOS targets.
- Implement the final macOS direct-download and notarized packaging flow.
- Add the Sherpa-style updater story for direct-download app replacement.
- Complete the production polish for the media scrubber and conversion UX.
- Add end-to-end validation for packaged Windows/macOS builds.

## Immediate next steps
1. Validate the conversion pipeline end-to-end with a real file selection and generated command.
2. Wire the actual FFmpeg process execution and progress reporting into the app.
3. Add per-platform bundled FFmpeg packaging and release automation.
4. Finalize the update flow and release pipeline for Windows/macOS.
