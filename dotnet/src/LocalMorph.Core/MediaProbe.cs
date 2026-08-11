using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace LocalMorph.Bridge;

public enum SourceMediaKind
{
    Video,
    Audio,
    Image,
    Unknown
}

public sealed record SourceMediaInfo(
    SourceMediaKind Kind,
    double? DurationSeconds,
    int? Width,
    int? Height,
    double? FrameRate,
    int? SampleRate,
    int? Channels);

public static class MediaProbe
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".bmp", ".gif", ".heic", ".heif", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"
    };

    public static async Task<SourceMediaInfo?> ProbeAsync(string ffmpegPath, string inputPath, CancellationToken token = default)
    {
        var ffprobePath = Path.Combine(
            Path.GetDirectoryName(ffmpegPath) ?? string.Empty,
            OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (!File.Exists(ffprobePath)) return null;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ffprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-print_format", "json", "-show_entries",
            "format=duration:stream=codec_type,width,height,avg_frame_rate,sample_rate,channels", inputPath
        })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start()) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(timeout.Token));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            return null;
        }

        return process.ExitCode == 0 ? Parse(await outputTask, inputPath) : null;
    }

    public static SourceMediaInfo? Parse(string json, string inputPath)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        double? duration = null;
        if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var durationValue))
        {
            duration = ParseDouble(durationValue.GetString());
        }

        JsonElement? videoStream = null;
        JsonElement? audioStream = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var value) ? value.GetString() : null;
                if (codecType == "video" && videoStream is null) videoStream = stream;
                if (codecType == "audio" && audioStream is null) audioStream = stream;
            }
        }

        var isImage = ImageExtensions.Contains(Path.GetExtension(inputPath));
        var kind = isImage ? SourceMediaKind.Image
            : videoStream is not null ? SourceMediaKind.Video
            : audioStream is not null ? SourceMediaKind.Audio
            : SourceMediaKind.Unknown;
        var width = GetInt(videoStream, "width");
        var height = GetInt(videoStream, "height");
        var frameRate = videoStream is { } video && video.TryGetProperty("avg_frame_rate", out var frameRateValue)
            ? ParseRate(frameRateValue.GetString())
            : null;

        return new SourceMediaInfo(
            kind,
            duration,
            width,
            height,
            frameRate,
            GetInt(audioStream, "sample_rate"),
            GetInt(audioStream, "channels"));
    }

    private static int? GetInt(JsonElement? element, string propertyName)
    {
        if (element is not { } value || !value.TryGetProperty(propertyName, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)) return number;
        return int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : null;

    private static double? ParseRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('/', 2);
        if (parts.Length == 2 && ParseDouble(parts[0]) is { } numerator && ParseDouble(parts[1]) is > 0 and { } denominator)
        {
            return numerator / denominator;
        }

        return ParseDouble(value);
    }
}