using System.Runtime.InteropServices;
using LocalMorph.Core.Updates;

namespace LocalMorph.App.Services;

/// <summary>Checks localmorph.com for a newer release once a day and remembers what the user skipped.</summary>
public sealed class UpdateService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(20);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly IPreferences preferences;

    public UpdateService(IPreferences preferences) => this.preferences = preferences;

    public static Uri FeedUrl
    {
        get
        {
            // LOCALMORPH_APPCAST_URL lets release engineering point a build at a staging feed.
            if (Environment.GetEnvironmentVariable("LOCALMORPH_APPCAST_URL") is { Length: > 0 } override_ && Uri.TryCreate(override_, UriKind.Absolute, out var custom)) return custom;
            return OperatingSystem.IsWindows()
                ? new Uri("https://localmorph.com/appcast-windows.xml")
                : new Uri("https://localmorph.com/appcast.xml");
        }
    }

    public static Version CurrentVersion => AppcastReader.TryParseVersion(AppInfo.Current.VersionString, out var version) ? version : new Version(0, 0, 0);

    /// <summary>Three-part version for display (the packaged build carries a fourth revision digit).</summary>
    public static string CurrentVersionDisplay
    {
        get
        {
            var version = CurrentVersion;
            return $"{version.Major}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}";
        }
    }

    public bool AutomaticChecks
    {
        get => preferences.Get("update-auto-check", true);
        set => preferences.Set("update-auto-check", value);
    }

    public string? SkippedVersion
    {
        get => preferences.Get("update-skipped", string.Empty) is { Length: > 0 } value ? value : null;
        set => preferences.Set("update-skipped", value ?? string.Empty);
    }

    public DateTimeOffset LastCheck
    {
        get => DateTimeOffset.TryParse(preferences.Get("update-last-check", string.Empty), out var when) ? when : DateTimeOffset.MinValue;
        private set => preferences.Set("update-last-check", value.ToString("O"));
    }

    public bool IsDue => AutomaticChecks && DateTimeOffset.UtcNow - LastCheck > CheckInterval;

    /// <summary>Returns an update newer than the running build, or null. Never throws.</summary>
    public async Task<UpdateInfo?> CheckAsync(bool force = false, CancellationToken token = default)
    {
        if (!force && !IsDue) return null;
        try
        {
            var xml = await Http.GetStringAsync(FeedUrl, token);
            LastCheck = DateTimeOffset.UtcNow;
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            var update = AppcastReader.Parse(xml, arch);
            if (update is null || !AppcastReader.IsNewer(update, CurrentVersion)) return null;
            if (!force && string.Equals(SkippedVersion, update.DisplayVersion, StringComparison.OrdinalIgnoreCase)) return null;
            return update;
        }
        catch
        {
            return null;
        }
    }

    public static string InstallHint => OperatingSystem.IsWindows()
        ? "winget upgrade Refractored.LocalMorph"
        : "brew upgrade --cask localmorph";
}
