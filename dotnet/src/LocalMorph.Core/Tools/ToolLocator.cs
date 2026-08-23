using System.Diagnostics;
using System.Runtime.InteropServices;
using LocalMorph.Core.Imaging;

namespace LocalMorph.Core.Tools;

/// <summary>
/// Finds conversion tools on the user's device: bundled with the app first, then PATH,
/// then well-known install locations (winget, Chocolatey, Scoop, Homebrew, Program Files).
/// </summary>
public static class ToolLocator
{
    public static ToolInfo? Find(ToolKind kind, string? appBaseDirectory = null)
    {
        var descriptor = ToolCatalog.Get(kind);
        if (descriptor.IsStoreCodec)
        {
            try { return PlatformImageCodec.Current?.Probe(); }
            catch { return null; }
        }

        foreach (var (candidate, source) in EnumerateCandidates(descriptor, appBaseDirectory ?? AppContext.BaseDirectory))
        {
            if (!IsExecutable(candidate)) continue;
            return new ToolInfo(kind, candidate, ReadVersion(kind, candidate), source);
        }

        return null;
    }

    public static IReadOnlyDictionary<ToolKind, ToolInfo> FindAll(string? appBaseDirectory = null)
    {
        var found = new Dictionary<ToolKind, ToolInfo>();
        foreach (var descriptor in ToolCatalog.All)
        {
            if (Find(descriptor.Kind, appBaseDirectory) is { } info) found[descriptor.Kind] = info;
        }

        // ffprobe almost always sits beside ffmpeg; prefer the sibling so versions match.
        if (found.TryGetValue(ToolKind.Ffmpeg, out var ffmpeg))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg.Path) ?? string.Empty,
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (IsExecutable(sibling))
            {
                found[ToolKind.Ffprobe] = new ToolInfo(ToolKind.Ffprobe, sibling, ReadVersion(ToolKind.Ffprobe, sibling), ffmpeg.Source);
            }
        }

        return found;
    }

    public static IEnumerable<(string Path, ToolSource Source)> EnumerateCandidates(ToolDescriptor descriptor, string appBaseDirectory)
    {
        foreach (var root in BundledRoots(appBaseDirectory))
        {
            foreach (var runtimeId in RuntimeIds())
            {
                foreach (var name in descriptor.ExecutableNames)
                {
                    yield return (Path.Combine(root, runtimeId, name), ToolSource.Bundled);
                    yield return (Path.Combine(root, runtimeId, "bin", name), ToolSource.Bundled);
                }
            }

            foreach (var name in descriptor.ExecutableNames)
            {
                yield return (Path.Combine(root, name), ToolSource.Bundled);
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in descriptor.ExecutableNames)
            {
                yield return (Path.Combine(directory, name), ToolSource.Path);
            }
        }

        foreach (var directory in KnownDirectories(descriptor.Kind))
        {
            foreach (var name in descriptor.ExecutableNames)
            {
                yield return (Path.Combine(directory, name), ToolSource.KnownLocation);
            }
        }
    }

    private static IEnumerable<string> BundledRoots(string appBaseDirectory) =>
    [
        Path.Combine(appBaseDirectory, "ffmpeg"),
        Path.Combine(appBaseDirectory, "tools"),
        Path.Combine(appBaseDirectory, "Resources", "ffmpeg"),
        Path.Combine(appBaseDirectory, "..", "..", "..", "..", "Resources", "ffmpeg")
    ];

    private static IEnumerable<string> RuntimeIds()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };

        if (OperatingSystem.IsWindows())
        {
            yield return $"win-{arch}";
            yield return "win-x64";
        }
        else if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            yield return $"maccatalyst-{arch}";
            yield return $"osx-{arch}";
            yield return "maccatalyst-x64";
        }
        else
        {
            yield return $"linux-{arch}";
        }
    }

    private static IEnumerable<string> KnownDirectories(ToolKind kind)
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
            yield return Path.Combine(userProfile, "scoop", "shims");
            yield return @"C:\ProgramData\chocolatey\bin";

            switch (kind)
            {
                case ToolKind.Ffmpeg:
                case ToolKind.Ffprobe:
                    yield return @"C:\ffmpeg\bin";
                    foreach (var directory in Glob(Path.Combine(localAppData, "Microsoft", "WinGet", "Packages"), "Gyan.FFmpeg*", "ffmpeg-*", "bin")) yield return directory;
                    foreach (var directory in Glob(Path.Combine(localAppData, "Microsoft", "WinGet", "Packages"), "BtbN.FFmpeg*", "ffmpeg-*", "bin")) yield return directory;
                    break;
                case ToolKind.ImageMagick:
                    foreach (var directory in Glob(programFiles, "ImageMagick-*")) yield return directory;
                    break;
                case ToolKind.LibreOffice:
                    yield return Path.Combine(programFiles, "LibreOffice", "program");
                    yield return Path.Combine(programFilesX86, "LibreOffice", "program");
                    break;
                case ToolKind.Pandoc:
                    yield return Path.Combine(programFiles, "Pandoc");
                    yield return Path.Combine(localAppData, "Pandoc");
                    break;
                case ToolKind.Ghostscript:
                    foreach (var directory in Glob(programFiles, "gs", "gs*", "bin")) yield return directory;
                    break;
            }
        }
        else
        {
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
            yield return "/opt/local/bin";
            yield return "/usr/bin";
            if (kind == ToolKind.LibreOffice)
            {
                yield return "/Applications/LibreOffice.app/Contents/MacOS";
                yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "LibreOffice.app", "Contents", "MacOS");
            }
        }
    }

    private static IEnumerable<string> Glob(string root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) yield break;
        IEnumerable<string> current = [root];
        foreach (var segment in segments)
        {
            var next = new List<string>();
            foreach (var directory in current)
            {
                try
                {
                    next.AddRange(segment.Contains('*')
                        ? Directory.EnumerateDirectories(directory, segment).OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
                        : [Path.Combine(directory, segment)]);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            current = next;
        }

        foreach (var directory in current.Where(Directory.Exists)) yield return directory;
    }

    public static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            const UnixFileMode executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & executable) != 0;
        }
        catch (PlatformNotSupportedException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static string? ReadVersion(ToolKind kind, string executable)
    {
        var argument = kind switch
        {
            ToolKind.Ffmpeg or ToolKind.Ffprobe => "-version",
            ToolKind.ImageMagick => "-version",
            ToolKind.LibreOffice or ToolKind.Pandoc or ToolKind.Ghostscript => "--version",
            _ => "--version"
        };

        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, argument)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(8_000))
            {
                try { process.Kill(true); } catch { }
                return null;
            }

            var text = output.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(text)) text = error.GetAwaiter().GetResult();
            var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
        }
        catch
        {
            return null;
        }
    }
}
