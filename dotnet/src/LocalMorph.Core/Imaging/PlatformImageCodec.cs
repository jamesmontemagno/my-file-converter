using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Imaging;

/// <summary>
/// An in-process image codec supplied by the host app. On Windows this is the Windows Imaging Component,
/// which decodes HEIC/HEIF through the Microsoft Store "HEIF Image Extensions" package.
/// </summary>
public interface IPlatformImageCodec
{
    /// <summary>Describes the installed codec, or null when the HEIF decoder is missing.</summary>
    ToolInfo? Probe();

    /// <summary>Decodes <paramref name="source"/> and writes it as <paramref name="format"/> to <paramref name="outputPath"/>.</summary>
    Task ConvertAsync(SourceFile source, OutputFormat format, ConversionOptions options, string outputPath, CancellationToken token);
}

/// <summary>Registration point for the host app's codec; Core has no platform dependencies of its own.</summary>
public static class PlatformImageCodec
{
    public static IPlatformImageCodec? Current { get; set; }

    /// <summary>Source extensions that need the platform HEIF decoder.</summary>
    public static readonly IReadOnlySet<string> HeifInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".heic", ".heif" };

    /// <summary>Formats the Windows Imaging Component can encode without extra codecs.</summary>
    public static readonly IReadOnlySet<string> EncodableFormats = new HashSet<string>(StringComparer.Ordinal) { "png", "jpg", "bmp", "tiff", "gif-still" };

    public static bool IsHeif(string path) => HeifInputs.Contains(Path.GetExtension(path));
}
