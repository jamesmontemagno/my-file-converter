namespace LocalMorph.App.Services;

public static class Distribution
{
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
