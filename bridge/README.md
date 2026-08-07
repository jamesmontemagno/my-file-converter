# LocalMorph Bridge

LocalMorph Bridge is an optional .NET 10 global tool that gives LocalMorph
access to a system-installed FFmpeg without uploading files to a managed
server. It runs on Windows, macOS, and Linux, including Windows ARM64.

## Install and run

Install .NET 10 and FFmpeg, make sure `ffmpeg` is available on `PATH`, then run:

```shell
dnx LocalMorph.Bridge
```

To install the command permanently:

```shell
dotnet tool install --global LocalMorph.Bridge
localmorph-bridge
```

Upgrade or remove the global installation with:

```shell
dotnet tool update --global LocalMorph.Bridge
dotnet tool uninstall --global LocalMorph.Bridge
```

The service binds only to `127.0.0.1`. By default, it selects an available
ephemeral port; set `LOCALMORPH_BRIDGE_PORT` to a specific local port. Startup
prints one JSON line:

```text
LOCALMORPH_BRIDGE={"baseUrl":"http://127.0.0.1:53421","token":"...","version":"0.1.0"}
```

Copy the `baseUrl` and token into the converter's Local FFmpeg Bridge settings.
Each launch creates a new token. Keep it private and pair again after restarting
the tool.

## FFmpeg prerequisite

The NuGet package does not bundle or download FFmpeg. Install it separately:

- **Windows:** install a trusted FFmpeg package, such as with Winget.
- **macOS:** `brew install ffmpeg`
- **Linux:** install your distribution's `ffmpeg` package.

If `/v1/health` reports `ffmpeg.available: false`, fix `PATH` and restart the
bridge.

## Security and protocol

Every API request requires an exact allowed `Origin` and the per-launch bearer
token. Browser preflight requests validate the origin before credentials are
sent. The bridge accepts only fixed conversion options, invokes FFmpeg without
a shell, limits input and output to 2 GiB, and retains only a bounded diagnostic
tail.

The endpoints are:

| Endpoint | Method | Purpose |
| --- | --- | --- |
| `/v1/health` | GET | Report the bridge, FFmpeg, and supported targets |
| `/v1/jobs` | POST | Submit one conversion |
| `/v1/jobs/{id}` | GET | Read job status |
| `/v1/jobs/{id}/events` | GET | Stream status and progress |
| `/v1/jobs/{id}` | DELETE | Cancel a job |
| `/v1/jobs/{id}/output` | GET | Download completed output |

Completed, failed, and canceled job directories are removed after one hour.
All remaining job data is removed when the bridge shuts down.

## Development

```shell
dotnet test dotnet/LocalMorph.Bridge.sln
dotnet pack dotnet/src/LocalMorph.Bridge/LocalMorph.Bridge.csproj --configuration Release
```
