# LocalMorph Bridge

LocalMorph Bridge is an opt-in, local companion service for conversions that
need a system-installed `ffmpeg`. It runs on Windows, macOS, and Linux. It does
**not** bundle or download FFmpeg; it uses only the `ffmpeg` executable already
available on the bridge process's `PATH`.

## Install and run

### From source

Install Rust and FFmpeg, then build and run the bridge:

```powershell
cd bridge
cargo build --release
cargo run --release
```

On Windows, run `.\target\release\localmorph-bridge.exe` after building; on
macOS and Linux, run `./target/release/localmorph-bridge`. The service binds to
`127.0.0.1` only. By default it asks the operating system for an ephemeral
port; set `LOCALMORPH_BRIDGE_PORT` to use a specific local port. On startup it
writes one JSON line to standard output:

```text
LOCALMORPH_BRIDGE={"baseUrl":"http://127.0.0.1:53421","token":"...","version":"0.1.0"}
```

The hosted PWA cannot start a program on your device. Start the bridge yourself,
then copy the printed `baseUrl` and `token` into the converter's Local FFmpeg
Bridge settings and connect. A new URL/token pair is created every time the
bridge starts; keep the token private and pair again after restarting it.

## FFmpeg prerequisite

Install FFmpeg separately and ensure `ffmpeg` is on `PATH` before starting the
bridge. Verify with `ffmpeg -version`.

* **Windows:** use a trusted FFmpeg build provider, extract it, and add its
  `bin` folder to your user `PATH`.
* **macOS:** install through a package manager such as Homebrew (`brew install
  ffmpeg`) or use a trusted build.
* **Linux:** install your distribution's `ffmpeg` package.

If `/v1/health` reports `ffmpeg.available: false`, the bridge remains running
but refuses conversion jobs. Install FFmpeg or correct `PATH`, then restart the
bridge; it checks for FFmpeg at startup.

## Downloads and verification

When official binary releases are published, download the asset for your
operating system only from this repository's GitHub Releases page. Before
running it, compare its SHA-256 hash with the checksum published with that
release (for example, `Get-FileHash <asset> -Algorithm SHA256` in PowerShell or
`shasum -a 256 <asset>` on macOS/Linux). Prefer assets with the platform's
normal code-signing verification available, and treat a missing or invalid
signature/checksum as a reason not to run the file.

Releases contain the bridge only, never FFmpeg. You must obtain FFmpeg
separately as described above. Until a matching release asset is available for
your platform, use the source build instructions instead.

## Connection and API protocol

Every actual API request must include both an exact allowed `Origin` and the
per-launch bearer token. Browser CORS preflight requests validate the Origin
only, because browsers do not send credentials on preflight. Allowed origins
are `https://localmorph.com` and Vite's common local origins:
`http://localhost:5173`, `http://127.0.0.1:5173`,
`http://localhost:4173`, and `http://127.0.0.1:4173`.

| Endpoint | Method | Purpose |
| --- | --- | --- |
| `/v1/health` | GET | `{version,ffmpeg:{available,version?},supportedTargets}` |
| `/v1/jobs` | POST | Submit one conversion |
| `/v1/jobs/{id}` | GET | Current job state |
| `/v1/jobs/{id}/events` | GET | Server-sent status/progress events |
| `/v1/jobs/{id}` | DELETE | Cancel a queued or running job |
| `/v1/jobs/{id}/output` | GET | Download a completed output |

`POST /v1/jobs` is `multipart/form-data` with `file` and `request` fields
(either order). `request` is exact JSON with these camel-case fields:

```json
{
  "targetMime": "video/mp4",
  "outputName": "converted.mp4",
  "mediaType": "video",
  "quality": 75,
  "image": {"width": 1920, "height": 1080, "keepAspectRatio": true},
  "media": {"trimStart": 0, "trimEnd": 60, "channelMode": "source"}
}
```

`targetMime` is one of the values in `supportedTargets`; `mediaType` must be
the matching `video`, `audio`, or `image`. `quality` is 1–100. Image dimensions
are 1–16384. `outputName` is a plain filename (not a path). `trimEnd`, when
provided, must be greater than `trimStart`; `channelMode` is `source`, `mono`,
or `stereo`.

The response is promptly `{"id":"..."}` after the upload is persisted. The
SSE `status` event has JSON
`{status,progress,message,detail?,rawOutput?}` where `status` is `queued`,
`running`, `completed`, `failed`, or `canceled`.

The bridge accepts no filesystem paths, shell snippets, codec strings, filters,
or arbitrary FFmpeg arguments from clients. It saves input and output using
fixed names in a unique per-job data directory and invokes FFmpeg using Rust's
`Command` API without a shell. Standard error is retained only as a bounded
16 KiB tail. FFmpeg's `-progress pipe:1` records drive status events.

Completed, failed, and cancelled job directories are removed after one hour.
On Windows they live beneath `%LOCALAPPDATA%\LocalMorphBridge\jobs`; on other
platforms they live beneath `~/.local/share/LocalMorphBridge/jobs`.

## Development

```powershell
cd bridge
cargo fmt --check
cargo test
```

The unit and integration-style router tests cover request validation,
origin/bearer protection, FFmpeg path discovery, fixed argument construction,
progress parsing, and job state transitions.

## Release packaging

Release builds are made separately on Windows, macOS, and Linux. Package only
the platform's bridge binary and the applicable license/notice material; do not
include FFmpeg. Sign distributable binaries with the normal platform mechanism,
publish SHA-256 checksums alongside each asset, and publish the signed assets
through GitHub Releases. Any installer or release notes must state that users
start the bridge themselves and install FFmpeg separately.
