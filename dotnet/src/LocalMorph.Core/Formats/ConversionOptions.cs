namespace LocalMorph.Core.Formats;

public enum EncodingSpeed
{
    Fast,
    Balanced,
    Quality
}

public enum ChannelMode
{
    Source,
    Mono,
    Stereo
}

public enum SubtitleMode
{
    /// <summary>MKV keeps every subtitle track; other containers drop them.</summary>
    Auto,
    /// <summary>Drop every subtitle track.</summary>
    Remove,
    /// <summary>Keep text subtitle tracks (converted to the container's subtitle format when needed).</summary>
    Keep,
    /// <summary>Render the chosen subtitle track (or the external file) into the picture.</summary>
    BurnIn
}

/// <summary>Center-crop targets. <see cref="None"/> keeps the full frame.</summary>
public static class CropAspects
{
    public const string None = "";
    public static readonly IReadOnlyList<string> All = ["16:9", "9:16", "1:1", "4:5", "4:3", "21:9"];

    public static bool TryParse(string? value, out int width, out int height)
    {
        width = height = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(':', 2);
        return parts.Length == 2 &&
               int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out width) &&
               int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out height) &&
               width is > 0 and <= 100 && height is > 0 and <= 100;
    }
}

/// <summary>Every tunable a conversion can carry. Engines ignore what does not apply to their format.</summary>
public sealed record ConversionOptions
{
    public int Quality { get; init; } = 80;
    public EncodingSpeed Speed { get; init; } = EncodingSpeed.Balanced;
    public int? TargetHeight { get; init; }
    public int? FrameRate { get; init; }
    public int? AudioBitrateKbps { get; init; }
    public int? SampleRate { get; init; }
    public ChannelMode Channels { get; init; } = ChannelMode.Source;
    public int WavBitDepth { get; init; } = 16;
    public double? TrimStartSeconds { get; init; }
    public double? TrimEndSeconds { get; init; }
    public bool UseHardwareEncoder { get; init; } = true;
    public double? TargetSizeMegabytes { get; init; }
    public double? FrameTimeSeconds { get; init; }
    public int Rotation { get; init; }
    public bool RemoveAudio { get; init; }
    public bool StripMetadata { get; init; }
    public bool Lossless { get; init; }
    public double PlaybackSpeed { get; init; } = 1.0;
    public int VolumePercent { get; init; } = 100;
    public bool NormalizeAudio { get; init; }
    public bool FastStart { get; init; } = true;

    // ---- tracks ----
    /// <summary>Audio track to use, counted among the source's audio streams (0 = first). Null uses the first track.</summary>
    public int? AudioTrack { get; init; }
    /// <summary>Keep every audio track instead of just one; <see cref="AudioTrack"/> becomes the first, default track.</summary>
    public bool KeepAllAudioTracks { get; init; }
    /// <summary>Use this file's audio instead of the source's soundtrack (the video is kept).</summary>
    public string? ReplacementAudioPath { get; init; }

    // ---- subtitles ----
    public SubtitleMode Subtitles { get; init; } = SubtitleMode.Auto;
    /// <summary>Subtitle track to burn in or extract, counted among subtitle streams (0 = first).</summary>
    public int? SubtitleTrack { get; init; }
    /// <summary>An .srt/.vtt/.ass file to add as a track (or burn in when <see cref="Subtitles"/> is <see cref="SubtitleMode.BurnIn"/>).</summary>
    public string? ExternalSubtitlePath { get; init; }

    // ---- picture edits ----
    /// <summary>Center-crop to this aspect ratio, e.g. "9:16". Empty or null keeps the whole frame.</summary>
    public string? CropAspect { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }
    public bool Deinterlace { get; init; }
    public bool Denoise { get; init; }

    // ---- effects ----
    public bool Reverse { get; init; }
    public double FadeInSeconds { get; init; }
    public double FadeOutSeconds { get; init; }
    /// <summary>Shift the audio against the picture: positive delays the sound, negative makes it play earlier.</summary>
    public int AudioDelayMilliseconds { get; init; }

    public static readonly ConversionOptions Default = new();

    public bool HasTrim => TrimStartSeconds is > 0 || TrimEndSeconds is > 0;
    public bool HasCrop => CropAspects.TryParse(CropAspect, out _, out _);
    public bool BurnsSubtitles => Subtitles == SubtitleMode.BurnIn;

    public string? Validate(OutputFormat format)
    {
        if (Quality is < 1 or > 100) return "Quality must be between 1 and 100.";
        if (TargetHeight is { } height && (height < 16 || height > 8192)) return "Resolution must be between 16 and 8192 pixels tall.";
        if (FrameRate is { } fps && (fps < 1 || fps > 240)) return "Frame rate must be between 1 and 240.";
        if (AudioBitrateKbps is { } kbps && (kbps < 8 || kbps > 1024)) return "Audio bitrate must be between 8 and 1024 kbps.";
        if (SampleRate is { } rate && rate is not (8000 or 11025 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000 or 88200 or 96000)) return "Choose a standard sample rate.";
        if (WavBitDepth is not (16 or 24 or 32)) return "Bit depth must be 16, 24, or 32.";
        if (TrimStartSeconds is { } start && (!double.IsFinite(start) || start < 0)) return "Trim start must be zero or later.";
        if (TrimEndSeconds is { } end && (!double.IsFinite(end) || end <= (TrimStartSeconds ?? 0))) return "Trim end must be after trim start.";
        if (TargetSizeMegabytes is { } size && (!double.IsFinite(size) || size <= 0)) return "Enter a target size in megabytes (for example 25).";
        if (FrameTimeSeconds is { } frame && (!double.IsFinite(frame) || frame < 0)) return "Frame time must be zero or later.";
        if (Rotation is not (0 or 90 or 180 or 270)) return "Rotation must be 0, 90, 180, or 270 degrees.";
        if (PlaybackSpeed is < 0.25 or > 4.0) return "Playback speed must be between 0.25× and 4×.";
        if (VolumePercent is < 0 or > 400) return "Volume must be between 0% and 400%.";
        if (TargetSizeMegabytes is not null && !format.Supports(FormatFeatures.TargetSize)) return $"{format.DisplayName} does not support a target file size.";
        if (AudioTrack is < 0) return "Choose an audio track.";
        if (SubtitleTrack is < 0) return "Choose a subtitle track.";
        if (!string.IsNullOrWhiteSpace(CropAspect) && !HasCrop) return "Choose a crop aspect ratio such as 16:9 or 9:16.";
        if (!double.IsFinite(FadeInSeconds) || FadeInSeconds is < 0 or > 60) return "Fade in must be between 0 and 60 seconds.";
        if (!double.IsFinite(FadeOutSeconds) || FadeOutSeconds is < 0 or > 60) return "Fade out must be between 0 and 60 seconds.";
        if (AudioDelayMilliseconds is < -60_000 or > 60_000) return "Audio sync offset must be within ±60 seconds (±60000 ms).";
        if (!string.IsNullOrWhiteSpace(ReplacementAudioPath) && !File.Exists(ReplacementAudioPath)) return $"The replacement audio file {Path.GetFileName(ReplacementAudioPath)} no longer exists.";
        if (!string.IsNullOrWhiteSpace(ExternalSubtitlePath) && !File.Exists(ExternalSubtitlePath)) return $"The subtitle file {Path.GetFileName(ExternalSubtitlePath)} no longer exists.";
        if (!string.IsNullOrWhiteSpace(ExternalSubtitlePath) && SourceClassifier.Classify(ExternalSubtitlePath) != MediaCategory.Subtitle) return "Subtitle files must be .srt, .vtt, .ass, or .ssa.";
        return null;
    }
}
