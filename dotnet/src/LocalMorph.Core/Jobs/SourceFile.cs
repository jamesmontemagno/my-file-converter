using LocalMorph.Bridge;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Jobs;

/// <summary>A file the user wants to convert, with everything we learned about it.</summary>
public sealed record SourceFile(
    string Path,
    long SizeBytes,
    MediaCategory Category,
    DocumentFlavor Flavor,
    SourceMediaInfo? Media)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
    public double? DurationSeconds => Media?.DurationSeconds;
    public bool HasAudio => Media?.Channels is > 0 || Category == MediaCategory.Audio;
    public bool HasVideoStream => Media?.Width is > 0;
    public bool IsAnimatedImage => Category == MediaCategory.Image && SourceClassifier.AnimatedImageInputs.Contains(Extension) && Media?.DurationSeconds is > 0.05;

    public string Summary
    {
        get
        {
            var parts = new List<string> { FormatBytes(SizeBytes) };
            if (Media?.Width is { } width && Media.Height is { } height) parts.Add($"{width}×{height}");
            if (Media?.DurationSeconds is { } duration && duration > 0 && Category != MediaCategory.Image) parts.Add(FormatDuration(duration));
            if (Media?.VideoCodec is { } videoCodec && Category == MediaCategory.Video) parts.Add(videoCodec.ToUpperInvariant());
            if (Media?.AudioCodec is { } audioCodec && Category is MediaCategory.Audio or MediaCategory.Video) parts.Add(audioCodec.ToUpperInvariant());
            if (Media?.SampleRate is { } sampleRate && Category == MediaCategory.Audio) parts.Add($"{sampleRate / 1000.0:0.#} kHz");
            return string.Join("  ·  ", parts);
        }
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };

    public static string FormatDuration(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }
}

public static class SourceInspector
{
    public static async Task<SourceFile> InspectAsync(string path, ToolInventory tools, CancellationToken token = default)
    {
        var info = new FileInfo(path);
        var category = SourceClassifier.Classify(path);
        var flavor = category == MediaCategory.Document ? SourceClassifier.ClassifyDocument(path) : DocumentFlavor.None;

        SourceMediaInfo? media = null;
        if (category is MediaCategory.Video or MediaCategory.Audio or MediaCategory.Image or MediaCategory.Unknown && tools.PathFor(ToolKind.Ffprobe) is { } ffprobe)
        {
            try
            {
                media = await MediaProbe.ProbeWithAsync(ffprobe, path, token);
            }
            catch (OperationCanceledException) { throw; }
            catch { media = null; }
        }

        // Let ffprobe reclassify unknown extensions it understands.
        if (category == MediaCategory.Unknown && media is not null)
        {
            category = media.Kind switch
            {
                SourceMediaKind.Video => MediaCategory.Video,
                SourceMediaKind.Audio => MediaCategory.Audio,
                SourceMediaKind.Image => MediaCategory.Image,
                _ => MediaCategory.Unknown
            };
        }

        // A "video" with one frame and no audio is really a picture (ffprobe reports PNG/JPEG as video streams).
        if (category == MediaCategory.Video && media is { DurationSeconds: null or <= 0.05, Channels: null or 0 } && SourceClassifier.Classify(path) == MediaCategory.Image)
        {
            category = MediaCategory.Image;
        }

        return new SourceFile(path, info.Exists ? info.Length : 0, category, flavor, media);
    }
}
