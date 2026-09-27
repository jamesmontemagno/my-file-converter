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
    int? Channels,
    string? VideoCodec = null,
    string? AudioCodec = null,
    long? BitRate = null,
    string? ContainerName = null,
    double? Rotation = null,
    IReadOnlyList<MediaStreamInfo>? Streams = null,
    int ChapterCount = 0)
{
    public IReadOnlyList<MediaStreamInfo> AudioStreams => Streams?.Where(stream => stream.Type == MediaStreamType.Audio).ToList() ?? [];
    public IReadOnlyList<MediaStreamInfo> SubtitleStreams => Streams?.Where(stream => stream.Type == MediaStreamType.Subtitle).ToList() ?? [];
}

public enum MediaStreamType
{
    Video,
    Audio,
    Subtitle,
    Other
}

/// <summary>One stream (track) inside a media file. <see cref="TypeIndex"/> is the position among streams of the same type, as used by FFmpeg's <c>0:a:N</c> specifiers.</summary>
public sealed record MediaStreamInfo(
    int Index,
    int TypeIndex,
    MediaStreamType Type,
    string? Codec,
    string? Language = null,
    string? Title = null,
    int? Channels = null,
    string? ChannelLayout = null,
    bool IsDefault = false,
    bool IsForced = false,
    bool IsAttachedPicture = false)
{
    private static readonly HashSet<string> PictureSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle", "xsub", "dvb_teletext"
    };

    /// <summary>Bitmap subtitles (Blu-ray PGS, DVD VobSub) cannot be converted to text or rendered by libass.</summary>
    public bool IsPictureSubtitle => Type == MediaStreamType.Subtitle && Codec is not null && PictureSubtitleCodecs.Contains(Codec);

    /// <summary>Human label such as "Track 2 · English · AAC 5.1 · Commentary".</summary>
    public string Describe()
    {
        var parts = new List<string> { $"Track {TypeIndex + 1}" };
        if (LanguageName(Language) is { } language) parts.Add(language);
        if (Codec is { Length: > 0 } codec) parts.Add(Type == MediaStreamType.Audio && ChannelLabel is { } channels ? $"{CodecLabel(codec)} {channels}" : CodecLabel(codec));
        if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title.Trim());
        if (IsDefault) parts.Add("default");
        if (IsForced) parts.Add("forced");
        return string.Join(" · ", parts);
    }

    private string? ChannelLabel => ChannelLayout switch
    {
        "mono" => "mono",
        "stereo" => "stereo",
        { Length: > 0 } layout when layout.StartsWith("5.1", StringComparison.Ordinal) => "5.1",
        { Length: > 0 } layout when layout.StartsWith("7.1", StringComparison.Ordinal) => "7.1",
        _ => Channels switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", > 0 and var count => $"{count} ch", _ => null }
    };

    private static string CodecLabel(string codec) => codec.ToLowerInvariant() switch
    {
        "subrip" => "SRT",
        "hdmv_pgs_subtitle" => "PGS",
        "dvd_subtitle" => "VobSub",
        "mov_text" => "MP4 text",
        "webvtt" => "WebVTT",
        "eac3" => "E-AC-3",
        "truehd" => "TrueHD",
        var other when other.StartsWith("pcm_", StringComparison.Ordinal) => "PCM",
        var other => other.ToUpperInvariant()
    };

    private static string? LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Equals("und", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .FirstOrDefault(candidate => candidate.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase) ||
                                             candidate.TwoLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (culture is not null && culture != CultureInfo.InvariantCulture) return culture.EnglishName;
        }
        catch
        {
        }

        // Bibliographic ISO 639-2 codes that differ from the terminology codes .NET knows.
        return code.ToLowerInvariant() switch
        {
            "fre" => "French",
            "ger" => "German",
            "dut" => "Dutch",
            "chi" => "Chinese",
            "cze" => "Czech",
            "gre" => "Greek",
            "per" => "Persian",
            "rum" => "Romanian",
            "slo" => "Slovak",
            "ice" => "Icelandic",
            "arm" => "Armenian",
            "baq" => "Basque",
            "wel" => "Welsh",
            "mac" => "Macedonian",
            "may" => "Malay",
            "bur" => "Burmese",
            "geo" => "Georgian",
            "alb" => "Albanian",
            "tib" => "Tibetan",
            _ => code.ToUpperInvariant()
        };
    }
}

public static class MediaProbe
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".bmp", ".gif", ".heic", ".heif", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"
    };

    public static Task<SourceMediaInfo?> ProbeAsync(string ffmpegPath, string inputPath, CancellationToken token = default)
    {
        var ffprobePath = Path.Combine(
            Path.GetDirectoryName(ffmpegPath) ?? string.Empty,
            OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        return ProbeWithAsync(ffprobePath, inputPath, token);
    }

    public static async Task<SourceMediaInfo?> ProbeWithAsync(string ffprobePath, string inputPath, CancellationToken token = default)
    {
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
            "format=duration,bit_rate,format_name:stream=index,codec_type,codec_name,width,height,avg_frame_rate,sample_rate,channels,channel_layout:stream_side_data=rotation:stream_tags=rotate,language,title:stream_disposition=default,forced,attached_pic:chapter=id", inputPath
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
        long? bitRate = null;
        string? container = null;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var durationValue)) duration = ParseDouble(durationValue.GetString());
            if (format.TryGetProperty("bit_rate", out var bitRateValue) && long.TryParse(bitRateValue.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBitRate)) bitRate = parsedBitRate;
            if (format.TryGetProperty("format_name", out var formatName)) container = formatName.GetString();
        }

        JsonElement? videoStream = null;
        JsonElement? audioStream = null;
        var streamInfos = new List<MediaStreamInfo>();
        var typeCounts = new Dictionary<MediaStreamType, int>();
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            var position = 0;
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var value) ? value.GetString() : null;
                var attachedPicture = GetDisposition(stream, "attached_pic");
                // Cover art (an MP3/M4A "attached picture") is not a real video track.
                if (codecType == "video" && videoStream is null && !attachedPicture) videoStream = stream;
                if (codecType == "audio" && audioStream is null) audioStream = stream;

                var type = codecType switch
                {
                    "video" => MediaStreamType.Video,
                    "audio" => MediaStreamType.Audio,
                    "subtitle" => MediaStreamType.Subtitle,
                    _ => MediaStreamType.Other
                };
                typeCounts.TryGetValue(type, out var typeIndex);
                typeCounts[type] = typeIndex + 1;
                var tags = stream.TryGetProperty("tags", out var tagValues) && tagValues.ValueKind == JsonValueKind.Object ? tagValues : (JsonElement?)null;
                streamInfos.Add(new MediaStreamInfo(
                    GetInt(stream, "index") ?? position,
                    typeIndex,
                    type,
                    GetString(stream, "codec_name"),
                    GetString(tags, "language"),
                    GetString(tags, "title"),
                    GetInt(stream, "channels"),
                    GetString(stream, "channel_layout"),
                    GetDisposition(stream, "default"),
                    GetDisposition(stream, "forced"),
                    attachedPicture));
                position++;
            }
        }

        var chapterCount = root.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array ? chapters.GetArrayLength() : 0;

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
            GetInt(audioStream, "channels"),
            GetString(videoStream, "codec_name"),
            GetString(audioStream, "codec_name"),
            bitRate,
            container,
            GetRotation(videoStream),
            streamInfos,
            chapterCount);
    }

    private static bool GetDisposition(JsonElement stream, string name) =>
        stream.TryGetProperty("disposition", out var disposition) && disposition.ValueKind == JsonValueKind.Object &&
        disposition.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.Number && flag.GetInt32() != 0;

    private static string? GetString(JsonElement? element, string propertyName) =>
        element is { } value && value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double? GetRotation(JsonElement? element)
    {
        if (element is not { } video) return null;
        if (video.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                if (entry.TryGetProperty("rotation", out var rotation) && rotation.ValueKind == JsonValueKind.Number) return rotation.GetDouble();
            }
        }

        if (video.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate))
        {
            return ParseDouble(rotate.GetString());
        }

        return null;
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