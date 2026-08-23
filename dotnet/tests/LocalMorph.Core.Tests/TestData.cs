using LocalMorph.Bridge;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Tests;

internal static class TestData
{
    public static readonly FfmpegCapabilities SoftwareOnly = new(
        new HashSet<string> { "libx264", "libx265", "libvpx-vp9", "libsvtav1", "libaom-av1", "aac", "libmp3lame", "libopus", "libvorbis", "flac", "alac", "libwebp", "libwebp_anim", "apng", "gif", "png", "mjpeg", "tiff", "bmp", "prores_ks", "mpeg4" },
        new HashSet<string>(),
        [],
        "ffmpeg version 7.1");

    public static readonly FfmpegCapabilities WithNvenc = new(
        SoftwareOnly.Encoders.Concat(["h264_nvenc", "hevc_nvenc"]).ToHashSet(),
        new HashSet<string> { "cuda", "d3d11va" },
        [new HardwareEncoder("h264", "h264_nvenc", HardwareVendor.Nvidia, "NVIDIA NVENC"), new HardwareEncoder("hevc", "hevc_nvenc", HardwareVendor.Nvidia, "NVIDIA NVENC")],
        "ffmpeg version 7.1");

    public static ToolInventory Tools(FfmpegCapabilities? capabilities = null, params ToolKind[] extra)
    {
        var tools = new Dictionary<ToolKind, ToolInfo>
        {
            [ToolKind.Ffmpeg] = new(ToolKind.Ffmpeg, @"C:\tools\ffmpeg.exe", "ffmpeg version 7.1", ToolSource.Path),
            [ToolKind.Ffprobe] = new(ToolKind.Ffprobe, @"C:\tools\ffprobe.exe", "ffprobe version 7.1", ToolSource.Path)
        };
        foreach (var kind in extra) tools[kind] = new ToolInfo(kind, $@"C:\tools\{kind}.exe", "1.0", ToolSource.Path);
        return new ToolInventory(tools, capabilities ?? SoftwareOnly);
    }

    public static SourceFile Video(string path = @"C:\media\clip.mov", double duration = 60, int width = 1920, int height = 1080, bool audio = true) =>
        new(path, 50_000_000, MediaCategory.Video, DocumentFlavor.None,
            new SourceMediaInfo(SourceMediaKind.Video, duration, width, height, 29.97, audio ? 48000 : null, audio ? 2 : null, "h264", audio ? "aac" : null, 6_000_000, "mov,mp4,m4a"));

    public static SourceFile Audio(string path = @"C:\media\song.flac") =>
        new(path, 30_000_000, MediaCategory.Audio, DocumentFlavor.None,
            new SourceMediaInfo(SourceMediaKind.Audio, 200, null, null, null, 44100, 2, null, "flac", 900_000, "flac"));

    public static SourceFile Image(string path = @"C:\media\photo.png", int width = 4000, int height = 3000) =>
        new(path, 8_000_000, MediaCategory.Image, DocumentFlavor.None,
            new SourceMediaInfo(SourceMediaKind.Image, null, width, height, null, null, null, "png", null, null, "png_pipe"));

    public static SourceFile Document(string path = @"C:\docs\report.docx") =>
        new(path, 120_000, MediaCategory.Document, SourceClassifier.ClassifyDocument(path), null);

    public static OutputFormat Format(string id) => FormatCatalog.Find(id) ?? throw new InvalidOperationException(id);
}
