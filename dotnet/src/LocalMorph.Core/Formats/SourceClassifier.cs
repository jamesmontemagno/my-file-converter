namespace LocalMorph.Core.Formats;

public enum MediaCategory
{
    Video,
    Audio,
    Image,
    Document,
    Unknown
}

public enum DocumentFlavor
{
    None,
    Text,
    Spreadsheet,
    Presentation,
    Markup,
    Pdf
}

/// <summary>Classifies files by extension so the UI can offer sensible targets before probing.</summary>
public static class SourceClassifier
{
    private static readonly Dictionary<string, MediaCategory> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        // video
        [".mp4"] = MediaCategory.Video, [".m4v"] = MediaCategory.Video, [".mov"] = MediaCategory.Video, [".mkv"] = MediaCategory.Video,
        [".webm"] = MediaCategory.Video, [".avi"] = MediaCategory.Video, [".wmv"] = MediaCategory.Video, [".flv"] = MediaCategory.Video,
        [".mpg"] = MediaCategory.Video, [".mpeg"] = MediaCategory.Video, [".ts"] = MediaCategory.Video, [".m2ts"] = MediaCategory.Video,
        [".mts"] = MediaCategory.Video, [".3gp"] = MediaCategory.Video, [".ogv"] = MediaCategory.Video, [".vob"] = MediaCategory.Video,
        [".asf"] = MediaCategory.Video, [".divx"] = MediaCategory.Video, [".mxf"] = MediaCategory.Video, [".f4v"] = MediaCategory.Video,
        // audio
        [".mp3"] = MediaCategory.Audio, [".wav"] = MediaCategory.Audio, [".aac"] = MediaCategory.Audio, [".m4a"] = MediaCategory.Audio,
        [".flac"] = MediaCategory.Audio, [".ogg"] = MediaCategory.Audio, [".oga"] = MediaCategory.Audio, [".opus"] = MediaCategory.Audio,
        [".wma"] = MediaCategory.Audio, [".aiff"] = MediaCategory.Audio, [".aif"] = MediaCategory.Audio, [".alac"] = MediaCategory.Audio,
        [".amr"] = MediaCategory.Audio, [".ac3"] = MediaCategory.Audio, [".mka"] = MediaCategory.Audio, [".caf"] = MediaCategory.Audio,
        [".ape"] = MediaCategory.Audio, [".wv"] = MediaCategory.Audio, [".mid"] = MediaCategory.Audio,
        // image
        [".png"] = MediaCategory.Image, [".jpg"] = MediaCategory.Image, [".jpeg"] = MediaCategory.Image, [".gif"] = MediaCategory.Image,
        [".webp"] = MediaCategory.Image, [".bmp"] = MediaCategory.Image, [".tif"] = MediaCategory.Image, [".tiff"] = MediaCategory.Image,
        [".heic"] = MediaCategory.Image, [".heif"] = MediaCategory.Image, [".avif"] = MediaCategory.Image, [".jxl"] = MediaCategory.Image,
        [".svg"] = MediaCategory.Image, [".psd"] = MediaCategory.Image, [".ico"] = MediaCategory.Image, [".tga"] = MediaCategory.Image,
        [".dds"] = MediaCategory.Image, [".exr"] = MediaCategory.Image, [".hdr"] = MediaCategory.Image, [".pbm"] = MediaCategory.Image,
        [".pgm"] = MediaCategory.Image, [".ppm"] = MediaCategory.Image, [".apng"] = MediaCategory.Image, [".jp2"] = MediaCategory.Image,
        [".cr2"] = MediaCategory.Image, [".cr3"] = MediaCategory.Image, [".nef"] = MediaCategory.Image, [".arw"] = MediaCategory.Image,
        [".dng"] = MediaCategory.Image, [".orf"] = MediaCategory.Image, [".rw2"] = MediaCategory.Image, [".raf"] = MediaCategory.Image,
        [".eps"] = MediaCategory.Image, [".ai"] = MediaCategory.Image,
        // documents
        [".pdf"] = MediaCategory.Document, [".docx"] = MediaCategory.Document, [".doc"] = MediaCategory.Document, [".odt"] = MediaCategory.Document,
        [".rtf"] = MediaCategory.Document, [".txt"] = MediaCategory.Document, [".md"] = MediaCategory.Document, [".markdown"] = MediaCategory.Document,
        [".html"] = MediaCategory.Document, [".htm"] = MediaCategory.Document, [".epub"] = MediaCategory.Document, [".xlsx"] = MediaCategory.Document,
        [".xls"] = MediaCategory.Document, [".ods"] = MediaCategory.Document, [".csv"] = MediaCategory.Document, [".pptx"] = MediaCategory.Document,
        [".ppt"] = MediaCategory.Document, [".odp"] = MediaCategory.Document, [".tex"] = MediaCategory.Document, [".rst"] = MediaCategory.Document,
        [".wpd"] = MediaCategory.Document, [".pages"] = MediaCategory.Document, [".numbers"] = MediaCategory.Document, [".key"] = MediaCategory.Document
    };

    /// <summary>Images FFmpeg cannot reliably decode; ImageMagick handles these.</summary>
    public static readonly HashSet<string> ImageMagickPreferredInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".svg", ".psd", ".ico", ".eps", ".ai", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".orf", ".rw2", ".raf", ".jp2", ".exr", ".hdr"
    };

    public static readonly HashSet<string> AnimatedImageInputs = new(StringComparer.OrdinalIgnoreCase) { ".gif", ".apng", ".webp" };

    public static MediaCategory Classify(string path) =>
        Categories.TryGetValue(Path.GetExtension(path), out var category) ? category : MediaCategory.Unknown;

    public static DocumentFlavor ClassifyDocument(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => DocumentFlavor.Pdf,
        ".xlsx" or ".xls" or ".ods" or ".csv" or ".numbers" => DocumentFlavor.Spreadsheet,
        ".pptx" or ".ppt" or ".odp" or ".key" => DocumentFlavor.Presentation,
        ".md" or ".markdown" or ".html" or ".htm" or ".rst" or ".tex" or ".epub" => DocumentFlavor.Markup,
        ".docx" or ".doc" or ".odt" or ".rtf" or ".txt" or ".wpd" or ".pages" => DocumentFlavor.Text,
        _ => DocumentFlavor.None
    };

    public static IReadOnlyList<string> AllKnownExtensions => Categories.Keys.ToArray();

    public static IEnumerable<string> ExtensionsFor(MediaCategory category) =>
        Categories.Where(pair => pair.Value == category).Select(pair => pair.Key);
}
