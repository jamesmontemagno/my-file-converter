namespace LocalMorph.Core.Formats;

public sealed record Preset(
    string Id,
    string Name,
    string Description,
    string Icon,
    string FormatId,
    ConversionOptions Options,
    MediaCategory[] ForSources)
{
    public OutputFormat Format => FormatCatalog.Find(FormatId) ?? throw new InvalidOperationException($"Unknown format '{FormatId}' in preset '{Id}'.");
}

/// <summary>One-click starting points for the most common jobs.</summary>
public static class Presets
{
    public static readonly IReadOnlyList<Preset> All =
    [
        new("share-mp4", "Share anywhere", "1080p H.264 MP4 that plays on every phone, TV, and browser.", "\uF850", "mp4-h264",
            new ConversionOptions { Quality = 78, TargetHeight = 1080, Speed = EncodingSpeed.Balanced, AudioBitrateKbps = 160 }, [MediaCategory.Video]),
        new("discord-25", "Fit in 25 MB", "Two-pass H.264 sized for Discord, email, and chat uploads.", "\uF782", "mp4-h264",
            new ConversionOptions { TargetSizeMegabytes = 24.5, TargetHeight = 720, Speed = EncodingSpeed.Balanced, AudioBitrateKbps = 96 }, [MediaCategory.Video]),
        new("small-hevc", "Shrink with HEVC", "Halve the file size with H.265 while keeping detail.", "\uEA13", "mp4-h265",
            new ConversionOptions { Quality = 72, Speed = EncodingSpeed.Balanced, AudioBitrateKbps = 128 }, [MediaCategory.Video]),
        new("web-webm", "Web embed", "VP9 WebM for sites, docs, and HTML5 players.", "\uF45B", "webm-vp9",
            new ConversionOptions { Quality = 75, TargetHeight = 720, Speed = EncodingSpeed.Balanced, AudioBitrateKbps = 96 }, [MediaCategory.Video]),
        new("gif-loop", "GIF loop", "480p, 15 fps animated GIF with an optimized palette.", "\uF191", "gif",
            new ConversionOptions { TargetHeight = 480, FrameRate = 15 }, [MediaCategory.Video]),
        new("extract-audio", "Extract audio", "Pull the soundtrack out as MP3 at 192 kbps.", "\uE855", "mp3",
            new ConversionOptions { AudioBitrateKbps = 192 }, [MediaCategory.Video]),
        new("remux-mp4", "Remux to MP4", "Change the container instantly without re-encoding.", "\uE619", "mp4-copy",
            new ConversionOptions(), [MediaCategory.Video]),
        new("thumbnail", "Grab a frame", "Save a JPEG still from the current scrub position.", "\uF255", "jpg",
            new ConversionOptions { Quality = 90 }, [MediaCategory.Video]),

        new("podcast", "Podcast", "Mono 96 kbps MP3 tuned for speech.", "\uEB43", "mp3",
            new ConversionOptions { AudioBitrateKbps = 96, Channels = ChannelMode.Mono, SampleRate = 44100 }, [MediaCategory.Audio, MediaCategory.Video]),
        new("music-aac", "Music · AAC", "256 kbps M4A for Apple Music and iTunes.", "\uE855", "m4a-aac",
            new ConversionOptions { AudioBitrateKbps = 256 }, [MediaCategory.Audio, MediaCategory.Video]),
        new("lossless-flac", "Archive · FLAC", "Lossless compression for a master copy.", "\uEAC9", "flac",
            new ConversionOptions(), [MediaCategory.Audio, MediaCategory.Video]),
        new("voice-opus", "Voice note", "Tiny Opus file for messaging.", "\uF7CC", "opus",
            new ConversionOptions { AudioBitrateKbps = 48, Channels = ChannelMode.Mono }, [MediaCategory.Audio, MediaCategory.Video]),
        new("editing-wav", "Editing · WAV", "24-bit 48 kHz WAV for DAWs and video editors.", "\uF33B", "wav",
            new ConversionOptions { WavBitDepth = 24, SampleRate = 48000 }, [MediaCategory.Audio, MediaCategory.Video]),

        new("web-image", "Web image", "WebP at 82% quality, capped at 1920 px tall.", "\uF45B", "webp",
            new ConversionOptions { Quality = 82, TargetHeight = 1920 }, [MediaCategory.Image]),
        new("photo-jpg", "Photo · JPEG", "High-quality JPEG for sharing and printing.", "\uF489", "jpg",
            new ConversionOptions { Quality = 92 }, [MediaCategory.Image]),
        new("png-lossless", "PNG · Lossless", "Keep every pixel and transparency.", "\uEAC9", "png",
            new ConversionOptions(), [MediaCategory.Image]),
        new("avif-next", "AVIF · Next-gen", "Smallest file with modern browser support.", "\uEB34", "avif",
            new ConversionOptions { Quality = 70 }, [MediaCategory.Image]),
        new("favicon", "Favicon", "Multi-size ICO for websites and apps.", "\uF77D", "ico",
            new ConversionOptions(), [MediaCategory.Image]),

        new("to-pdf", "Save as PDF", "Fixed-layout PDF from any office document.", "\uF3AD", "pdf",
            new ConversionOptions(), [MediaCategory.Document]),
        new("to-docx", "Word document", "Editable DOCX.", "\uE558", "docx",
            new ConversionOptions(), [MediaCategory.Document]),
        new("compress-pdf", "Compress PDF", "Shrink a PDF for email.", "\uEA13", "pdf-compress",
            new ConversionOptions { Quality = 60 }, [MediaCategory.Document])
    ];

    public static IEnumerable<Preset> For(MediaCategory category) => All.Where(preset => preset.ForSources.Contains(category));

    public static IEnumerable<Preset> For(IEnumerable<MediaCategory> categories)
    {
        var set = categories.Distinct().ToHashSet();
        if (set.Count == 0) return [];
        return All.Where(preset => set.All(category => preset.ForSources.Contains(category)));
    }
}
