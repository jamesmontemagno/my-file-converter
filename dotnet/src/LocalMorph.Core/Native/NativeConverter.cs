using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Native;

/// <summary>
/// In-process conversions provided by the operating system's own frameworks. On Mac this is ImageIO,
/// Core Graphics, and UIKit text rendering, so photos, PDF pages, and simple documents convert without
/// any external tool (important for the sandboxed Mac App Store build, which cannot run user-installed tools).
/// </summary>
public interface INativeConverter
{
    /// <summary>Describes the frameworks, or null when they are unusable.</summary>
    ToolInfo? Probe();

    /// <summary>Output format ids this device can actually write (checked at runtime against the OS encoders).</summary>
    IReadOnlySet<string> WritableFormats { get; }

    /// <summary>Lower-case source extensions (with dot) this device can decode as still images.</summary>
    IReadOnlySet<string> ReadableImageExtensions { get; }

    /// <summary>Upright pixel size of a still image, or null when it cannot be read.</summary>
    (int Width, int Height)? ReadImageSize(string path);

    Task ConvertAsync(SourceFile source, OutputFormat format, ConversionOptions options, string outputPath, CancellationToken token);
}

/// <summary>Registration point for the host app's native converter plus the platform-neutral capability rules.</summary>
public static class NativeConverter
{
    public static INativeConverter? Current { get; set; }

    /// <summary>Image outputs a native image pipeline may produce (subject to <see cref="INativeConverter.WritableFormats"/>).</summary>
    public static readonly IReadOnlySet<string> ImageOutputs = new HashSet<string>(StringComparer.Ordinal)
    {
        "png", "jpg", "tiff", "bmp", "gif-still", "heic", "avif", "ico", "pdf-image"
    };

    /// <summary>First-page renders of a PDF.</summary>
    public static readonly IReadOnlySet<string> PdfPageOutputs = new HashSet<string>(StringComparer.Ordinal) { "doc-png", "doc-jpg" };

    /// <summary>Text documents the OS text system reads (Markdown is rendered to HTML first).</summary>
    public static readonly IReadOnlySet<string> TextInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".rtf", ".txt", ".md", ".markdown", ".html", ".htm"
    };

    public static readonly IReadOnlySet<string> TextOutputs = new HashSet<string>(StringComparer.Ordinal) { "pdf", "rtf", "html", "txt" };

    public static bool IsMarkdown(string path) => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown";

    /// <summary>True when <paramref name="converter"/> can turn this source into <paramref name="format"/>.</summary>
    public static bool CanConvert(INativeConverter? converter, string sourcePath, OutputFormat format)
    {
        if (converter is null || !converter.WritableFormats.Contains(format.Id)) return false;
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        return SourceClassifier.Classify(sourcePath) switch
        {
            MediaCategory.Image => ImageOutputs.Contains(format.Id) && converter.ReadableImageExtensions.Contains(extension),
            MediaCategory.Document when extension == ".pdf" => PdfPageOutputs.Contains(format.Id),
            MediaCategory.Document => TextOutputs.Contains(format.Id) && TextInputs.Contains(extension),
            _ => false
        };
    }
}
