namespace LocalMorph.App.Services;

public static class Distribution
{
#if LOCALMORPH_MAC_STORE
    public static bool IsMacStoreBuild => true;
#else
    public static bool IsMacStoreBuild => false;
#endif

    public static string ToolsDescription => IsMacStoreBuild
        ? "Media converts with the bundled FFmpeg and FFprobe; photos, PDF pages, and text documents use converters built into macOS. Available formats reflect this build. External tools are not installed or discovered."
        : "Install FFmpeg and FFprobe separately for media conversion and inspection. ImageMagick, LibreOffice, Pandoc, and Ghostscript add more formats. Everything runs locally.";

    public static string BundledToolRepair => "A bundled media tool is missing or unusable. Repair or update LocalMorph; installing Homebrew tools will not repair this build.";

    public static string CurrentVersionDisplay
    {
        get
        {
            var version = AppInfo.Current.Version;
            return $"{version.Major}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}";
        }
    }

}
