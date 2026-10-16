using System.Runtime.InteropServices;
using LocalMorph.Core.Updates;

namespace LocalMorph.App.Services;

public sealed record UpdateCheckResult(UpdateInfo? Update, string? Error = null);

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
        get => !Distribution.IsStoreBuild && preferences.Get("update-auto-check", true);
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

    public async Task<UpdateCheckResult> CheckAsync(bool force = false, CancellationToken token = default)
    {
        if (Distribution.IsStoreBuild || !force && !IsDue) return new(null);
        try
        {
            var xml = await Http.GetStringAsync(FeedUrl, token);
            System.Xml.Linq.XDocument.Parse(xml);
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            var update = AppcastReader.Parse(xml, arch);
            if (update is null) return new(null, "The update feed contains no usable releases. Try again later.");
            LastCheck = DateTimeOffset.UtcNow;
            if (!AppcastReader.IsNewer(update, CurrentVersion)) return new(null);
            if (!force && string.Equals(SkippedVersion, update.DisplayVersion, StringComparison.OrdinalIgnoreCase)) return new(null);
            return new(update);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Xml.XmlException)
        {
            return new(null, $"Could not check for updates: {ex.Message}");
        }
    }

    public static string InstallHint => Distribution.IsStoreBuild ? "Updates are managed by Microsoft Store." : OperatingSystem.IsWindows()
        ? "winget upgrade Refractored.LocalMorph"
        : "brew upgrade --cask localmorph";
}
