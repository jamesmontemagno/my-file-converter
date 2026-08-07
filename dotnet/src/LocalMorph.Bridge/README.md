# LocalMorph Bridge .NET tool

`localmorph-bridge` is a local ASP.NET Core companion service. It uses only a
system-installed `ffmpeg` found on `PATH`; it never bundles or downloads it.

Install .NET 10 and ensure `ffmpeg` is on `PATH`, then run the tool without
installing it:

```shell
dnx LocalMorph.Bridge
```

Or install and run it globally:

```shell
dotnet tool install --global LocalMorph.Bridge
localmorph-bridge
```

The service prints its localhost URL and a new per-launch token on standard
output. Enter both values in LocalMorph's Local FFmpeg Bridge settings. The
tool binds only to `127.0.0.1` and does not bundle or download FFmpeg.
