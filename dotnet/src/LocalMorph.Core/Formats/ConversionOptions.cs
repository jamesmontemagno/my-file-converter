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

    public static readonly ConversionOptions Default = new();

    public bool HasTrim => TrimStartSeconds is > 0 || TrimEndSeconds is > 0;

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
        return null;
    }
}
