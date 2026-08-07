using System.Diagnostics;
using System.Globalization;

namespace LocalMorph.Bridge;

public static class Ffmpeg
{
    public static FfmpegInfo? Discover()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var names = OperatingSystem.IsWindows() ? new[] { "ffmpeg.exe", "ffmpeg" } : new[] { "ffmpeg" };
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (IsExecutable(candidate))
                {
                    return new FfmpegInfo(candidate, ReadVersion(candidate));
                }
            }
        }
        return null;
    }

    public static ProcessStartInfo BuildCommand(string executable, string input, string output, ConversionRequest request)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(input);
        startInfo.ArgumentList.Add("-progress");
        startInfo.ArgumentList.Add("pipe:1");
        startInfo.ArgumentList.Add("-nostats");
        if (request.Media.TrimStart is { } trimStart) Add(startInfo, "-ss", trimStart.ToString(CultureInfo.InvariantCulture));
        if (request.Media.TrimEnd is { } trimEnd) Add(startInfo, "-to", trimEnd.ToString(CultureInfo.InvariantCulture));

        switch (request.TargetMime)
        {
            case "video/mp4":
            case "video/quicktime":
                Add(startInfo, "-c:v", "libx264");
                Add(startInfo, "-preset", "medium");
                if (request.Quality is { } mp4Quality) Add(startInfo, "-crf", QualityToCrf(mp4Quality).ToString(CultureInfo.InvariantCulture));
                Add(startInfo, "-c:a", "aac");
                break;
            case "video/webm":
                Add(startInfo, "-c:v", "libvpx-vp9");
                if (request.Quality is { } webmQuality)
                {
                    Add(startInfo, "-crf", QualityToCrf(webmQuality).ToString(CultureInfo.InvariantCulture));
                    Add(startInfo, "-b:v", "0");
                }
                Add(startInfo, "-c:a", "libopus");
                break;
            case "image/gif":
                Add(startInfo, "-vf", GifFilter(request.Image));
                break;
            case "audio/mpeg":
                Add(startInfo, "-vn", "-c:a", "libmp3lame");
                break;
            case "audio/wav":
                Add(startInfo, "-vn", "-c:a", "pcm_s16le");
                break;
        }

        if (request.Media.ChannelMode == "mono") Add(startInfo, "-ac", "1");
        if (request.Media.ChannelMode == "stereo") Add(startInfo, "-ac", "2");
        Add(startInfo, "-fs", BridgeOptions.MaxFileBytes.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(output);
        return startInfo;
    }

    public static ProgressUpdate? ParseProgress(string line, long? durationMicroseconds)
    {
        var parts = line.Split('=', 2);
        if (parts.Length != 2) return null;
        if (parts[0] == "progress" && parts[1] == "end") return new ProgressUpdate(100, true);
        if (parts[0] is not ("out_time_us" or "out_time_ms") ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var elapsed))
        {
            return null;
        }

        var percent = durationMicroseconds is > 0
            ? (int?)Math.Min(99, Math.Max(0, elapsed * 100 / durationMicroseconds.Value))
            : null;
        return new ProgressUpdate(percent, false);
    }

    private static void Add(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
    }

    private static int QualityToCrf(int quality) => 51 - quality * 33 / 100;

    private static string GifFilter(ImageOptions image) => (image.Width, image.Height, image.KeepAspectRatio) switch
    {
        (null, null, _) => "fps=15",
        ({ } width, null, _) => $"fps=15,scale={width}:-2",
        (null, { } height, _) => $"fps=15,scale=-2:{height}",
        ({ } width, { } height, true) => $"fps=15,scale={width}:{height}:force_original_aspect_ratio=decrease",
        ({ } width, { } height, false) => $"fps=15,scale={width}:{height}"
    };

    private static string? ReadVersion(string executable)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, "-version")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            });
            if (process is null || !process.WaitForExit(5_000) || process.ExitCode != 0) return null;
            return process.StandardOutput.ReadLine();
        }
        catch { return null; }
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true;

        try
        {
            const UnixFileMode executable =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & executable) != 0;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed record ProgressUpdate(int? Percent, bool Completed);
