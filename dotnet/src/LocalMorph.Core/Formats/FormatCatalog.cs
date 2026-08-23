using LocalMorph.Core.Imaging;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Formats;

public enum EngineKind
{
    Ffmpeg,
    ImageMagick,
    LibreOffice,
    Pandoc,
    Ghostscript,
    /// <summary>In-process Windows Imaging Component decode (HEIC/HEIF via the Store HEIF Image Extensions codec).</summary>
    WindowsImaging
}

[Flags]
public enum FormatFeatures
{
    None = 0,
    Quality = 1 << 0,
    Resolution = 1 << 1,
    FrameRate = 1 << 2,
    EncodingSpeed = 1 << 3,
    AudioTuning = 1 << 4,
    Trim = 1 << 5,
    TargetSize = 1 << 6,
    HardwareAccel = 1 << 7,
    FrameExtract = 1 << 8,
    WavBitDepth = 1 << 9,
    Rotate = 1 << 10,
    RemoveAudio = 1 << 11,
    Lossless = 1 << 12,
    PlaybackSpeed = 1 << 13
}

public sealed record OutputFormat(
    string Id,
    string DisplayName,
    string Extension,
    string Description,
    MediaCategory Category,
    IReadOnlySet<MediaCategory> AcceptsSources,
    FormatFeatures Features,
    EngineKind[] Engines,
    string? VideoCodec = null,
    string? AudioCodec = null,
    string[]? RequiredEncoders = null,
    DocumentFlavor[]? AcceptsDocumentFlavors = null,
    string? Badge = null)
{
    public bool Supports(FormatFeatures feature) => (Features & feature) != 0;
    public string ExtensionWithDot => "." + Extension;
    public string CategoryLabel => Category switch
    {
        MediaCategory.Video => "Video",
        MediaCategory.Audio => "Audio",
        MediaCategory.Image => "Image",
        MediaCategory.Document => "Document",
        _ => "Other"
    };
}

public static class FormatCatalog
{
    private static readonly IReadOnlySet<MediaCategory> FromVideo = new HashSet<MediaCategory> { MediaCategory.Video };
    private static readonly IReadOnlySet<MediaCategory> FromVideoOrAudio = new HashSet<MediaCategory> { MediaCategory.Video, MediaCategory.Audio };
    private static readonly IReadOnlySet<MediaCategory> FromVideoOrImage = new HashSet<MediaCategory> { MediaCategory.Video, MediaCategory.Image };
    private static readonly IReadOnlySet<MediaCategory> FromImage = new HashSet<MediaCategory> { MediaCategory.Image };
    private static readonly IReadOnlySet<MediaCategory> FromDocument = new HashSet<MediaCategory> { MediaCategory.Document };
    private static readonly IReadOnlySet<MediaCategory> FromDocumentOrImage = new HashSet<MediaCategory> { MediaCategory.Document, MediaCategory.Image };

    private const FormatFeatures VideoCommon = FormatFeatures.Quality | FormatFeatures.Resolution | FormatFeatures.FrameRate |
                                               FormatFeatures.EncodingSpeed | FormatFeatures.AudioTuning | FormatFeatures.Trim |
                                               FormatFeatures.Rotate | FormatFeatures.RemoveAudio | FormatFeatures.PlaybackSpeed;
    private const FormatFeatures AudioCommon = FormatFeatures.AudioTuning | FormatFeatures.Trim | FormatFeatures.PlaybackSpeed;
    private const FormatFeatures ImageCommon = FormatFeatures.Quality | FormatFeatures.Resolution | FormatFeatures.Rotate | FormatFeatures.FrameExtract;

    public static readonly IReadOnlyList<OutputFormat> All =
    [
        // ---- Video ----
        new("mp4-h264", "MP4 · H.264", "mp4", "Plays everywhere. Best default for sharing.", MediaCategory.Video, FromVideoOrImage,
            VideoCommon | FormatFeatures.TargetSize | FormatFeatures.HardwareAccel, [EngineKind.Ffmpeg], "h264", "aac", ["libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_videotoolbox"], Badge: "Popular"),
        new("mp4-h265", "MP4 · H.265 / HEVC", "mp4", "About half the size of H.264 at the same quality. Newer devices only.", MediaCategory.Video, FromVideo,
            VideoCommon | FormatFeatures.TargetSize | FormatFeatures.HardwareAccel, [EngineKind.Ffmpeg], "hevc", "aac", ["libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_videotoolbox"]),
        new("mp4-av1", "MP4 · AV1", "mp4", "Smallest files, royalty-free. Slow to encode without hardware.", MediaCategory.Video, FromVideo,
            VideoCommon | FormatFeatures.HardwareAccel, [EngineKind.Ffmpeg], "av1", "aac", ["libsvtav1", "libaom-av1", "av1_nvenc", "av1_qsv", "av1_amf"]),
        new("mkv-h264", "MKV · H.264", "mkv", "Flexible container that keeps subtitles and chapters.", MediaCategory.Video, FromVideo,
            VideoCommon | FormatFeatures.TargetSize | FormatFeatures.HardwareAccel, [EngineKind.Ffmpeg], "h264", "aac", ["libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_videotoolbox"]),
        new("mkv-copy", "MKV · Remux (no re-encode)", "mkv", "Change the container only. Instant and lossless.", MediaCategory.Video, FromVideo,
            FormatFeatures.Trim | FormatFeatures.RemoveAudio, [EngineKind.Ffmpeg], "copy", "copy", Badge: "Lossless"),
        new("mp4-copy", "MP4 · Remux (no re-encode)", "mp4", "Repackage H.264/HEVC streams into MP4 without quality loss.", MediaCategory.Video, FromVideo,
            FormatFeatures.Trim | FormatFeatures.RemoveAudio, [EngineKind.Ffmpeg], "copy", "copy", Badge: "Lossless"),
        new("mov-h264", "MOV · H.264", "mov", "QuickTime container for Apple workflows.", MediaCategory.Video, FromVideo,
            VideoCommon | FormatFeatures.TargetSize | FormatFeatures.HardwareAccel, [EngineKind.Ffmpeg], "h264", "aac", ["libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_videotoolbox"]),
        new("mov-prores", "MOV · ProRes 422", "mov", "Editing-grade intermediate for Final Cut, Premiere, and Resolve.", MediaCategory.Video, FromVideo,
            FormatFeatures.Resolution | FormatFeatures.FrameRate | FormatFeatures.Trim | FormatFeatures.Rotate | FormatFeatures.RemoveAudio, [EngineKind.Ffmpeg], "prores", "pcm_s16le", ["prores_ks", "prores"], Badge: "Pro"),
        new("webm-vp9", "WebM · VP9", "webm", "Open web format, great for browsers and embeds.", MediaCategory.Video, FromVideoOrImage,
            VideoCommon, [EngineKind.Ffmpeg], "vp9", "libopus", ["libvpx-vp9"]),
        new("webm-av1", "WebM · AV1", "webm", "Open and tiny. Slow to encode.", MediaCategory.Video, FromVideo,
            VideoCommon, [EngineKind.Ffmpeg], "av1", "libopus", ["libsvtav1", "libaom-av1"]),
        new("avi-mpeg4", "AVI · MPEG-4", "avi", "Legacy format for older players and devices.", MediaCategory.Video, FromVideo,
            FormatFeatures.Quality | FormatFeatures.Resolution | FormatFeatures.FrameRate | FormatFeatures.AudioTuning | FormatFeatures.Trim | FormatFeatures.RemoveAudio, [EngineKind.Ffmpeg], "mpeg4", "libmp3lame", ["mpeg4"]),
        new("gif", "Animated GIF", "gif", "Short loops with an optimized palette.", MediaCategory.Video, FromVideoOrImage,
            FormatFeatures.Resolution | FormatFeatures.FrameRate | FormatFeatures.Trim | FormatFeatures.Rotate | FormatFeatures.PlaybackSpeed, [EngineKind.Ffmpeg], "gif"),
        new("webp-anim", "Animated WebP", "webp", "Much smaller than GIF with full color.", MediaCategory.Video, FromVideo,
            FormatFeatures.Quality | FormatFeatures.Resolution | FormatFeatures.FrameRate | FormatFeatures.Trim | FormatFeatures.Rotate, [EngineKind.Ffmpeg], "libwebp_anim", RequiredEncoders: ["libwebp_anim", "libwebp"]),
        new("apng", "Animated PNG", "apng", "Lossless animation with transparency.", MediaCategory.Video, FromVideo,
            FormatFeatures.Resolution | FormatFeatures.FrameRate | FormatFeatures.Trim | FormatFeatures.Rotate, [EngineKind.Ffmpeg], "apng", RequiredEncoders: ["apng"]),

        // ---- Audio ----
        new("mp3", "MP3", "mp3", "Universal compatibility.", MediaCategory.Audio, FromVideoOrAudio,
            AudioCommon, [EngineKind.Ffmpeg], null, "libmp3lame", ["libmp3lame"], Badge: "Popular"),
        new("m4a-aac", "M4A · AAC", "m4a", "Apple-friendly, better quality than MP3 at the same bitrate.", MediaCategory.Audio, FromVideoOrAudio,
            AudioCommon, [EngineKind.Ffmpeg], null, "aac", ["aac", "aac_at", "libfdk_aac"]),
        new("wav", "WAV", "wav", "Uncompressed PCM for editing.", MediaCategory.Audio, FromVideoOrAudio,
            AudioCommon | FormatFeatures.WavBitDepth, [EngineKind.Ffmpeg], null, "pcm", Badge: "Lossless"),
        new("flac", "FLAC", "flac", "Lossless compression, about half the size of WAV.", MediaCategory.Audio, FromVideoOrAudio,
            AudioCommon, [EngineKind.Ffmpeg], null, "flac", ["flac"], Badge: "Lossless"),
        new("alac", "M4A · ALAC", "m4a", "Apple Lossless for iTunes and Music.", MediaCategory.Audio, FromVideoOrAudio,
            AudioCommon, [EngineKind.Ffmpeg], null, "alac", ["alac"], Badge: "Lossless"),
        new("ogg-vorbis", "OGG · Vorbis", "ogg", "Open format for games and web audio.", MediaCategory.Audio, FromVideoOrAudio,
            AudioCommon, [EngineKind.Ffmpeg], null, "libvorbis", ["libvorbis"]),
        new("opus", "Opus", "opus", "Best quality per kilobit for voice and music.", MediaCategory.Audio, FromVideoOrAudio,
            AudioCommon, [EngineKind.Ffmpeg], null, "libopus", ["libopus"]),
        new("aiff", "AIFF", "aiff", "Uncompressed audio for Apple and pro audio apps.", MediaCategory.Audio, FromVideoOrAudio,
            AudioCommon | FormatFeatures.WavBitDepth, [EngineKind.Ffmpeg], null, "pcm_be", Badge: "Lossless"),
        new("audio-copy", "Extract audio (no re-encode)", "m4a", "Pull the original audio stream out of a video untouched.", MediaCategory.Audio, FromVideo,
            FormatFeatures.Trim, [EngineKind.Ffmpeg], null, "copy", Badge: "Lossless"),

        // ---- Image ----
        new("png", "PNG", "png", "Lossless with transparency.", MediaCategory.Image, FromVideoOrImage,
            ImageCommon, [EngineKind.Ffmpeg, EngineKind.ImageMagick], "png", Badge: "Lossless"),
        new("jpg", "JPEG", "jpg", "Small photos, no transparency.", MediaCategory.Image, FromVideoOrImage,
            ImageCommon, [EngineKind.Ffmpeg, EngineKind.ImageMagick], "mjpeg", Badge: "Popular"),
        new("webp", "WebP", "webp", "Modern web images, 25–35% smaller than JPEG.", MediaCategory.Image, FromVideoOrImage,
            ImageCommon | FormatFeatures.Lossless, [EngineKind.Ffmpeg, EngineKind.ImageMagick], "libwebp", RequiredEncoders: ["libwebp"]),
        new("avif", "AVIF", "avif", "Next-gen image format with excellent compression.", MediaCategory.Image, FromVideoOrImage,
            ImageCommon | FormatFeatures.Lossless, [EngineKind.Ffmpeg, EngineKind.ImageMagick], "libaom-av1", RequiredEncoders: ["libaom-av1"]),
        new("jxl", "JPEG XL", "jxl", "High quality, supports lossless JPEG recompression.", MediaCategory.Image, FromVideoOrImage,
            ImageCommon | FormatFeatures.Lossless, [EngineKind.Ffmpeg, EngineKind.ImageMagick], "libjxl", RequiredEncoders: ["libjxl"]),
        new("gif-still", "GIF (still)", "gif", "256-color single frame.", MediaCategory.Image, FromImage,
            FormatFeatures.Resolution | FormatFeatures.Rotate, [EngineKind.Ffmpeg, EngineKind.ImageMagick], "gif"),
        new("bmp", "BMP", "bmp", "Uncompressed Windows bitmap.", MediaCategory.Image, FromVideoOrImage,
            FormatFeatures.Resolution | FormatFeatures.Rotate | FormatFeatures.FrameExtract, [EngineKind.Ffmpeg, EngineKind.ImageMagick], "bmp"),
        new("tiff", "TIFF", "tiff", "Print and archival quality.", MediaCategory.Image, FromVideoOrImage,
            FormatFeatures.Resolution | FormatFeatures.Rotate | FormatFeatures.FrameExtract, [EngineKind.Ffmpeg, EngineKind.ImageMagick], "tiff", Badge: "Lossless"),
        new("ico", "ICO (Windows icon)", "ico", "Multi-size icon for apps and favicons.", MediaCategory.Image, FromImage,
            FormatFeatures.None, [EngineKind.ImageMagick, EngineKind.Ffmpeg], "bmp"),
        new("heic", "HEIC", "heic", "Apple's high-efficiency photo format.", MediaCategory.Image, FromImage,
            FormatFeatures.Quality | FormatFeatures.Resolution | FormatFeatures.Rotate, [EngineKind.ImageMagick]),
        new("pdf-image", "PDF (from image)", "pdf", "Wrap an image into a single-page PDF.", MediaCategory.Document, FromImage,
            FormatFeatures.Quality | FormatFeatures.Resolution, [EngineKind.ImageMagick]),

        // ---- Documents ----
        // Pandoc needs an external PDF engine (wkhtmltopdf/LaTeX) that we do not manage, so PDF is LibreOffice-only.
        new("pdf", "PDF", "pdf", "Fixed layout for sharing and printing.", MediaCategory.Document, FromDocument,
            FormatFeatures.None, [EngineKind.LibreOffice], AcceptsDocumentFlavors: [DocumentFlavor.Text, DocumentFlavor.Spreadsheet, DocumentFlavor.Presentation, DocumentFlavor.Markup], Badge: "Popular"),
        new("pdf-compress", "PDF · Compressed", "pdf", "Shrink a PDF by downsampling images.", MediaCategory.Document, FromDocument,
            FormatFeatures.Quality, [EngineKind.Ghostscript], AcceptsDocumentFlavors: [DocumentFlavor.Pdf]),
        new("docx", "Word (DOCX)", "docx", "Editable Microsoft Word document.", MediaCategory.Document, FromDocument,
            FormatFeatures.None, [EngineKind.LibreOffice, EngineKind.Pandoc], AcceptsDocumentFlavors: [DocumentFlavor.Text, DocumentFlavor.Markup]),
        new("odt", "OpenDocument Text (ODT)", "odt", "Open standard word processing file.", MediaCategory.Document, FromDocument,
            FormatFeatures.None, [EngineKind.LibreOffice, EngineKind.Pandoc], AcceptsDocumentFlavors: [DocumentFlavor.Text, DocumentFlavor.Markup]),
        new("rtf", "Rich Text (RTF)", "rtf", "Widely compatible formatted text.", MediaCategory.Document, FromDocument,
            FormatFeatures.None, [EngineKind.LibreOffice, EngineKind.Pandoc], AcceptsDocumentFlavors: [DocumentFlavor.Text, DocumentFlavor.Markup]),
        new("html", "HTML", "html", "Web page.", MediaCategory.Document, FromDocument,
            FormatFeatures.None, [EngineKind.Pandoc, EngineKind.LibreOffice], AcceptsDocumentFlavors: [DocumentFlavor.Text, DocumentFlavor.Markup, DocumentFlavor.Spreadsheet]),
        new("md", "Markdown", "md", "Plain text with lightweight formatting.", MediaCategory.Document, FromDocument,
            FormatFeatures.None, [EngineKind.Pandoc], AcceptsDocumentFlavors: [DocumentFlavor.Text, DocumentFlavor.Markup]),
        new("epub", "EPUB", "epub", "E-book for readers and tablets.", MediaCategory.Document, FromDocument,
            FormatFeatures.None, [EngineKind.Pandoc, EngineKind.LibreOffice], AcceptsDocumentFlavors: [DocumentFlavor.Text, DocumentFlavor.Markup]),
        new("txt", "Plain text", "txt", "Just the words.", MediaCategory.Document, FromDocument,
            FormatFeatures.None, [EngineKind.LibreOffice, EngineKind.Pandoc], AcceptsDocumentFlavors: [DocumentFlavor.Text, DocumentFlavor.Markup]),
        new("xlsx", "Excel (XLSX)", "xlsx", "Editable Microsoft Excel workbook.", MediaCategory.Document, FromDocument,
            FormatFeatures.None, [EngineKind.LibreOffice], AcceptsDocumentFlavors: [DocumentFlavor.Spreadsheet]),
        new("ods", "OpenDocument Spreadsheet (ODS)", "ods", "Open standard spreadsheet.", MediaCategory.Document, FromDocument,
            FormatFeatures.None, [EngineKind.LibreOffice], AcceptsDocumentFlavors: [DocumentFlavor.Spreadsheet]),
        new("csv", "CSV", "csv", "Comma-separated values (first sheet).", MediaCategory.Document, FromDocument,
            FormatFeatures.None, [EngineKind.LibreOffice], AcceptsDocumentFlavors: [DocumentFlavor.Spreadsheet]),
        new("pptx", "PowerPoint (PPTX)", "pptx", "Editable Microsoft PowerPoint deck.", MediaCategory.Document, FromDocument,
            FormatFeatures.None, [EngineKind.LibreOffice], AcceptsDocumentFlavors: [DocumentFlavor.Presentation]),
        new("odp", "OpenDocument Presentation (ODP)", "odp", "Open standard presentation.", MediaCategory.Document, FromDocument,
            FormatFeatures.None, [EngineKind.LibreOffice], AcceptsDocumentFlavors: [DocumentFlavor.Presentation]),
        new("doc-png", "PNG (first page)", "png", "Render the first page as an image.", MediaCategory.Image, FromDocument,
            FormatFeatures.Resolution, [EngineKind.LibreOffice, EngineKind.ImageMagick, EngineKind.Ghostscript], AcceptsDocumentFlavors: [DocumentFlavor.Text, DocumentFlavor.Spreadsheet, DocumentFlavor.Presentation, DocumentFlavor.Pdf]),
        new("doc-jpg", "JPEG (first page)", "jpg", "Render the first page as a photo-style image.", MediaCategory.Image, FromDocument,
            FormatFeatures.Quality | FormatFeatures.Resolution, [EngineKind.LibreOffice, EngineKind.ImageMagick, EngineKind.Ghostscript], AcceptsDocumentFlavors: [DocumentFlavor.Text, DocumentFlavor.Spreadsheet, DocumentFlavor.Presentation, DocumentFlavor.Pdf])
    ];

    public static OutputFormat? Find(string id) => All.FirstOrDefault(format => format.Id == id);

    /// <summary>Formats that make sense for this source, regardless of which tools are installed.</summary>
    public static IEnumerable<OutputFormat> ForSource(MediaCategory category, DocumentFlavor flavor = DocumentFlavor.None) =>
        All.Where(format => format.AcceptsSources.Contains(category) &&
                            (category != MediaCategory.Document || format.AcceptsDocumentFlavors is null || format.AcceptsDocumentFlavors.Contains(flavor)));

    /// <summary>Formats shared by every source in a batch.</summary>
    public static IEnumerable<OutputFormat> ForSources(IEnumerable<(MediaCategory Category, DocumentFlavor Flavor)> sources)
    {
        var list = sources.Distinct().ToList();
        if (list.Count == 0) return All.Where(format => format.Category != MediaCategory.Document || format.AcceptsSources.Contains(MediaCategory.Image));
        return All.Where(format => list.All(source => ForSource(source.Category, source.Flavor).Contains(format)));
    }

    /// <summary>Picks the engine to run for a given source + format given installed tools, or null when nothing installed can do it.</summary>
    public static EngineKind? ResolveEngine(OutputFormat format, string sourcePath, ToolInventory tools)
    {
        var extension = Path.GetExtension(sourcePath);
        var sourceCategory = SourceClassifier.Classify(sourcePath);
        var flavor = SourceClassifier.ClassifyDocument(sourcePath);

        // HEIC/HEIF photos decode natively through the Windows HEIF codec when it is installed; it is
        // faster than ImageMagick and far more reliable than FFmpeg's HEIF demuxer.
        if (CanUseWindowsImaging(format, sourcePath, tools)) return EngineKind.WindowsImaging;

        // Image inputs FFmpeg cannot decode go to ImageMagick first if it exists.
        var ordered = format.Engines.AsEnumerable();
        if (sourceCategory == MediaCategory.Image && SourceClassifier.ImageMagickPreferredInputs.Contains(extension) && format.Engines.Contains(EngineKind.ImageMagick))
        {
            ordered = format.Engines.OrderByDescending(engine => engine == EngineKind.ImageMagick);
        }

        // Markdown/HTML documents are Pandoc's home turf; office files are LibreOffice's.
        if (sourceCategory == MediaCategory.Document)
        {
            ordered = flavor switch
            {
                DocumentFlavor.Markup when format.Engines.Contains(EngineKind.Pandoc) => format.Engines.OrderByDescending(engine => engine == EngineKind.Pandoc),
                DocumentFlavor.Pdf when format.Engines.Contains(EngineKind.Ghostscript) => format.Engines.OrderByDescending(engine => engine == EngineKind.Ghostscript).ThenByDescending(engine => engine == EngineKind.ImageMagick),
                _ => format.Engines.OrderByDescending(engine => engine == EngineKind.LibreOffice)
            };
        }

        foreach (var engine in ordered)
        {
            if (EngineAvailable(engine, format, tools)) return engine;
        }

        return null;
    }

    public static bool EngineAvailable(EngineKind engine, OutputFormat format, ToolInventory tools) => engine switch
    {
        EngineKind.Ffmpeg => tools.HasFfmpeg && (format.RequiredEncoders is null || !tools.Ffmpeg.IsAvailable || HasUsableEncoder(format, tools.Ffmpeg)),
        EngineKind.ImageMagick => tools.Has(ToolKind.ImageMagick),
        EngineKind.LibreOffice => tools.Has(ToolKind.LibreOffice),
        EngineKind.Pandoc => tools.Has(ToolKind.Pandoc),
        EngineKind.Ghostscript => tools.Has(ToolKind.Ghostscript),
        EngineKind.WindowsImaging => tools.Has(ToolKind.WindowsHeif),
        _ => false
    };

    /// <summary>True when this HEIC/HEIF source can be written as <paramref name="format"/> by the Windows HEIF codec.</summary>
    public static bool CanUseWindowsImaging(OutputFormat format, string sourcePath, ToolInventory tools) =>
        PlatformImageCodec.IsHeif(sourcePath) &&
        PlatformImageCodec.EncodableFormats.Contains(format.Id) &&
        tools.Has(ToolKind.WindowsHeif);

    /// <summary>
    /// Decoders the user could install to read this source reliably. HEIC/HEIF on Windows wants the Store HEIF codec
    /// (or ImageMagick); FFmpeg is only a best-effort fallback for those files.
    /// </summary>
    public static IEnumerable<ToolKind> MissingDecodersFor(string sourcePath, ToolInventory tools)
    {
        if (!PlatformImageCodec.IsHeif(sourcePath) || tools.Has(ToolKind.ImageMagick) || tools.Has(ToolKind.WindowsHeif)) yield break;
        yield return OperatingSystem.IsWindows() ? ToolKind.WindowsHeif : ToolKind.ImageMagick;
    }

    /// <summary>A hardware encoder only counts when its test encode succeeded; compiled-in but broken encoders do not make a format available.</summary>
    private static bool HasUsableEncoder(OutputFormat format, FfmpegCapabilities capabilities) =>
        format.RequiredEncoders!.Any(name => IsHardwareEncoderName(name)
            ? capabilities.WorkingHardwareEncoders.Any(encoder => encoder.Encoder == name)
            : capabilities.HasEncoder(name));

    private static bool IsHardwareEncoderName(string name) =>
        name.EndsWith("_nvenc", StringComparison.Ordinal) || name.EndsWith("_qsv", StringComparison.Ordinal) ||
        name.EndsWith("_amf", StringComparison.Ordinal) || name.EndsWith("_videotoolbox", StringComparison.Ordinal);

    /// <summary>Tools the user could install to unlock this format.</summary>
    public static IEnumerable<ToolKind> MissingToolsFor(OutputFormat format, ToolInventory tools) =>
        format.Engines.Where(engine => !EngineAvailable(engine, format, tools)).Select(ToToolKind).Distinct();

    public static ToolKind ToToolKind(EngineKind engine) => engine switch
    {
        EngineKind.Ffmpeg => ToolKind.Ffmpeg,
        EngineKind.ImageMagick => ToolKind.ImageMagick,
        EngineKind.LibreOffice => ToolKind.LibreOffice,
        EngineKind.Pandoc => ToolKind.Pandoc,
        EngineKind.Ghostscript => ToolKind.Ghostscript,
        EngineKind.WindowsImaging => ToolKind.WindowsHeif,
        _ => ToolKind.Ffmpeg
    };
}
