using System.Runtime.InteropServices;

namespace LocalMorph.Bridge;

public static class FfmpegResolver
{
    public static FfmpegInfo? Resolve()
    {
        var bundled = TryResolveBundled();
        return bundled ?? Ffmpeg.Discover();
    }

    private static FfmpegInfo? TryResolveBundled()
    {
        var candidateRoots = GetCandidateRoots();
        foreach (var root in candidateRoots)
        {
            foreach (var candidate in EnumerateBundledCandidates(root))
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                if (OperatingSystem.IsWindows() || Ffmpeg.ReadVersion(candidate) is not null)
                {
                    return new FfmpegInfo(candidate, Ffmpeg.ReadVersion(candidate));
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidateRoots()
    {
        var appRoot = AppContext.BaseDirectory;
        return
        [
            Path.Combine(appRoot, "ffmpeg"),
            Path.Combine(appRoot, "Resources", "ffmpeg"),
            Path.Combine(appRoot, "..", "..", "..", "..", "Resources", "ffmpeg"),
            Path.Combine(appRoot, "..", "..", "..", "..", "ffmpeg")
        ];
    }

    private static IEnumerable<string> EnumerateBundledCandidates(string root)
    {
        var runtimeIds = GetRuntimeIds();
        foreach (var runtimeId in runtimeIds)
        {
            var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            yield return Path.Combine(root, runtimeId, name);
            yield return Path.Combine(root, runtimeId, "bin", name);
        }

        yield return Path.Combine(root, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
    }

    private static IEnumerable<string> GetRuntimeIds()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        if (OperatingSystem.IsWindows())
        {
            yield return $"win-{arch}";
            yield return "win-x64";
            yield return "win-arm64";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return $"maccatalyst-{arch}";
            yield return "maccatalyst-x64";
            yield return "maccatalyst-arm64";
        }
        else
        {
            yield return $"linux-{arch}";
            yield return "linux-x64";
            yield return "linux-arm64";
        }
    }
}
