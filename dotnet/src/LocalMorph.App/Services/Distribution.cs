namespace LocalMorph.App.Services;

public static class Distribution
{
#if LOCALMORPH_MAC_STORE
    public static bool IsMacStoreBuild => true;
#else
    public static bool IsMacStoreBuild => false;
#endif

    public static string ToolsDescription => IsMacStoreBuild
        ? "This sandbox workspace uses only bundled FFmpeg and FFprobe. Available formats reflect this build's codecs and filters. External tools are not installed or discovered."
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

    public static Uri? StoreUpdates => OperatingSystem.IsWindows()
        ? new("ms-windows-store://downloadsandupdates")
        : null;

    public static string UpdateDescription => OperatingSystem.IsWindows()
        ? "Updates are managed by Microsoft Store. Open Store updates to check installed apps."
        : "Updates are managed by the Mac App Store. Open App Store and choose Updates.";
}
