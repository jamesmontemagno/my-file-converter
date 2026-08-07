using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalMorph.Bridge;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ConversionRequest
{
    public required string TargetMime { get; init; }
    public required string OutputName { get; init; }
    public required string MediaType { get; init; }
    public int? Quality { get; init; }
    public ImageOptions Image { get; init; } = new();
    public MediaOptions Media { get; init; } = new();

    public bool IsValid()
    {
        if (Image is null || Media is null ||
            Quality is < 1 or > 100 ||
            string.IsNullOrWhiteSpace(OutputName) || OutputName.Length > 128 ||
            OutputName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-') ||
            Image.Width is < 1 or > 16_384 || Image.Height is < 1 or > 16_384 ||
            !double.IsFinite(Media.TrimStart ?? 0) || (Media.TrimStart ?? 0) < 0 ||
            (Media.TrimEnd is { } trimEnd && (!double.IsFinite(trimEnd) || trimEnd <= (Media.TrimStart ?? 0))) ||
            Media.ChannelMode is not ("source" or "mono" or "stereo"))
        {
            return false;
        }

        return (TargetMime, MediaType) switch
        {
            ("video/mp4" or "video/quicktime" or "video/webm", "video") => true,
            ("image/gif", "image") => true,
            ("audio/mpeg" or "audio/wav", "audio") => true,
            _ => false
        };
    }

    public string Extension => TargetMime switch
    {
        "video/mp4" => "mp4",
        "video/quicktime" => "mov",
        "video/webm" => "webm",
        "image/gif" => "gif",
        "audio/mpeg" => "mp3",
        "audio/wav" => "wav",
        _ => throw new InvalidOperationException("The request must be validated first.")
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ImageOptions
{
    public int? Width { get; init; }
    public int? Height { get; init; }
    public bool KeepAspectRatio { get; init; } = true;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MediaOptions
{
    public double? TrimStart { get; init; }
    public double? TrimEnd { get; init; }
    public string ChannelMode { get; init; } = "source";
}

public enum JobStatus { Queued, Running, Completed, Failed, Canceled }

public sealed record JobView(
    Guid Id,
    JobStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ProgressPercent,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Error);

public sealed record FfmpegInfo(string Path, string? Version);

public sealed record FfmpegState(FfmpegInfo? Info);
