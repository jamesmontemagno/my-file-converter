using LocalMorph.Core.Jobs;

namespace LocalMorph.App.Services;

/// <summary>Typed access to persisted user preferences.</summary>
public sealed class AppSettings
{
    private readonly IPreferences preferences;

    public AppSettings(IPreferences preferences) => this.preferences = preferences;

    public string OutputDirectory
    {
        get => preferences.Get("output-directory", string.Empty);
        set => preferences.Set("output-directory", value);
    }

    public string OutputSuffix
    {
        get => preferences.Get("output-suffix", string.Empty);
        set => preferences.Set("output-suffix", value);
    }

    public OverwritePolicy OverwritePolicy
    {
        get => Enum.TryParse<OverwritePolicy>(preferences.Get("overwrite-policy", nameof(OverwritePolicy.Rename)), out var policy) ? policy : OverwritePolicy.Rename;
        set => preferences.Set("overwrite-policy", value.ToString());
    }

    public int MaxParallel
    {
        get => Math.Clamp(preferences.Get("max-parallel", Math.Clamp(Environment.ProcessorCount / 4, 1, 4)), 1, 8);
        set => preferences.Set("max-parallel", Math.Clamp(value, 1, 8));
    }

    public bool UseHardwareEncoder
    {
        get => preferences.Get("use-hardware", true);
        set => preferences.Set("use-hardware", value);
    }

    public bool OpenFolderWhenDone
    {
        get => preferences.Get("open-folder-when-done", false);
        set => preferences.Set("open-folder-when-done", value);
    }

    public bool KeepMetadata
    {
        get => preferences.Get("keep-metadata", true);
        set => preferences.Set("keep-metadata", value);
    }

    public string? LastFormatFor(string category) => preferences.Get($"last-format-{category}", string.Empty) is { Length: > 0 } id ? id : null;
    public void SetLastFormatFor(string category, string formatId) => preferences.Set($"last-format-{category}", formatId);
}
