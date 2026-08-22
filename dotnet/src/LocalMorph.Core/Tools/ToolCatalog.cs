namespace LocalMorph.Core.Tools;

public enum ToolKind
{
    Ffmpeg,
    Ffprobe,
    ImageMagick,
    LibreOffice,
    Pandoc,
    Ghostscript
}

public enum ToolSource
{
    Bundled,
    Path,
    KnownLocation
}

public sealed record ToolInfo(ToolKind Kind, string Path, string? Version, ToolSource Source)
{
    public string ShortVersion => ToolCatalog.ShortenVersion(Kind, Version);
}

public sealed record ToolDescriptor(
    ToolKind Kind,
    string DisplayName,
    string Purpose,
    string[] ExecutableNames,
    string WingetId,
    string BrewFormula,
    string WebsiteUrl,
    bool IsCore);

public static class ToolCatalog
{
    public static readonly IReadOnlyList<ToolDescriptor> All =
    [
        new(ToolKind.Ffmpeg, "FFmpeg", "Video, audio, and image conversion",
            OperatingSystem.IsWindows() ? ["ffmpeg.exe"] : ["ffmpeg"],
            "Gyan.FFmpeg", "ffmpeg", "https://ffmpeg.org/download.html", IsCore: true),
        new(ToolKind.Ffprobe, "FFprobe", "Media inspection (ships with FFmpeg)",
            OperatingSystem.IsWindows() ? ["ffprobe.exe"] : ["ffprobe"],
            "Gyan.FFmpeg", "ffmpeg", "https://ffmpeg.org/download.html", IsCore: true),
        new(ToolKind.ImageMagick, "ImageMagick", "HEIC, SVG, PSD, RAW photos, and PDF pages",
            OperatingSystem.IsWindows() ? ["magick.exe"] : ["magick"],
            "ImageMagick.ImageMagick", "imagemagick", "https://imagemagick.org/script/download.php", IsCore: false),
        new(ToolKind.LibreOffice, "LibreOffice", "Word, Excel, PowerPoint, and OpenDocument files",
            OperatingSystem.IsWindows() ? ["soffice.exe", "soffice.com"] : ["soffice"],
            "TheDocumentFoundation.LibreOffice", "--cask libreoffice", "https://www.libreoffice.org/download/", IsCore: false),
        new(ToolKind.Pandoc, "Pandoc", "Markdown, HTML, EPUB, and rich text documents",
            OperatingSystem.IsWindows() ? ["pandoc.exe"] : ["pandoc"],
            "JohnMacFarlane.Pandoc", "pandoc", "https://pandoc.org/installing.html", IsCore: false),
        new(ToolKind.Ghostscript, "Ghostscript", "PDF compression and PDF to image",
            OperatingSystem.IsWindows() ? ["gswin64c.exe", "gswin32c.exe", "gs.exe"] : ["gs"],
            "ArtifexSoftware.GhostScript", "ghostscript", "https://ghostscript.com/releases/gsdnld.html", IsCore: false)
    ];

    public static ToolDescriptor Get(ToolKind kind) => All.First(descriptor => descriptor.Kind == kind);

    public static string InstallCommand(ToolKind kind)
    {
        var descriptor = Get(kind);
        if (OperatingSystem.IsWindows()) return $"winget install --id {descriptor.WingetId} -e";
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()) return $"brew install {descriptor.BrewFormula}";
        return $"sudo apt install {descriptor.BrewFormula.Replace("--cask ", string.Empty)}";
    }

    public static string ShortenVersion(ToolKind kind, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "version unavailable";
        var text = version.Trim();
        switch (kind)
        {
            case ToolKind.Ffmpeg:
            case ToolKind.Ffprobe:
                // "ffmpeg version 7.1-full_build-www.gyan.dev Copyright ..." -> "7.1"
                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[1] == "version")
                {
                    var raw = parts[2];
                    var dash = raw.IndexOf('-');
                    return dash > 0 ? raw[..dash] : raw;
                }
                return text;
            case ToolKind.ImageMagick:
                // "Version: ImageMagick 7.1.1-43 Q16-HDRI x64 ..." -> "7.1.1-43"
                var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var index = Array.IndexOf(tokens, "ImageMagick");
                return index >= 0 && index + 1 < tokens.Length ? tokens[index + 1] : text;
            case ToolKind.LibreOffice:
                // "LibreOffice 24.8.4.2 ..." -> "24.8.4.2"
                var office = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return office.Length >= 2 ? office[1] : text;
            case ToolKind.Pandoc:
                // "pandoc 3.6.2" -> "3.6.2"
                var pandoc = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return pandoc.Length >= 2 ? pandoc[1] : text;
            default:
                return text.Length > 40 ? text[..40] : text;
        }
    }
}
