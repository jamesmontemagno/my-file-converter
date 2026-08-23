using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalMorph.App.Services;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Tools;

namespace LocalMorph.App.ViewModels;

/// <summary>An output format as shown in the picker, with availability based on installed tools.</summary>
public sealed class FormatOption
{
    public FormatOption(OutputFormat format, ToolInventory tools)
    {
        Format = format;
        var missing = FormatCatalog.MissingToolsFor(format, tools).ToList();
        var anyEngine = format.Engines.Any(engine => FormatCatalog.EngineAvailable(engine, format, tools));
        IsAvailable = anyEngine;
        MissingTools = missing;
        EngineLabel = anyEngine
            ? format.Engines.First(engine => FormatCatalog.EngineAvailable(engine, format, tools)).ToString()
            : $"Needs {string.Join(" or ", missing.Select(kind => ToolCatalog.Get(kind).DisplayName))}";
    }

    public OutputFormat Format { get; }
    public bool IsAvailable { get; }
    public IReadOnlyList<ToolKind> MissingTools { get; }
    public string EngineLabel { get; }
    public string Id => Format.Id;
    public string DisplayName => Format.DisplayName;
    public string Description => Format.Description;
    public string Extension => Format.ExtensionWithDot;
    public string CategoryLabel => Format.CategoryLabel;
    public string? Badge => Format.Badge;
    public bool HasBadge => Format.Badge is not null;
    public string PickerLabel => IsAvailable ? $"{Format.DisplayName}  ·  {Format.ExtensionWithDot}" : $"{Format.DisplayName}  ·  {EngineLabel}";
    public override string ToString() => PickerLabel;
}

public sealed class FormatGroup : List<FormatOption>
{
    public FormatGroup(string name, IEnumerable<FormatOption> items) : base(items) => Name = name;
    public string Name { get; }
}

public sealed partial class PresetOption : ObservableObject
{
    public PresetOption(Preset preset, bool isAvailable, string? unavailableReason)
    {
        Preset = preset;
        IsAvailable = isAvailable;
        UnavailableReason = unavailableReason;
    }

    public Preset Preset { get; }
    public bool IsAvailable { get; }
    public string? UnavailableReason { get; }
    public string Name => Preset.Name;
    public string Description => UnavailableReason ?? Preset.Description;
    public string Icon => Preset.Icon;
    public string FormatLabel => Preset.Format.DisplayName;

    [ObservableProperty]
    public partial bool IsActive { get; set; }
}

public sealed partial class ToolItemViewModel : ObservableObject
{
    public ToolItemViewModel(ToolDescriptor descriptor) => Descriptor = descriptor;

    public ToolDescriptor Descriptor { get; }
    public ToolKind Kind => Descriptor.Kind;
    public string Name => Descriptor.DisplayName;
    public string Purpose => Descriptor.Purpose;
    public bool IsCore => Descriptor.IsCore;
    public bool IsStoreCodec => Descriptor.IsStoreCodec;
    public string InstallCommand => ToolCatalog.InstallCommand(Kind);
    /// <summary>Mono text under a missing tool: the package-manager command, or where the codec comes from.</summary>
    public string InstallHint => IsStoreCodec ? "Free from the Microsoft Store" : InstallCommand;
    public string InstallActionLabel => IsStoreCodec ? "Get from Store" : "Install";
    public string WebsiteUrl => Descriptor.WebsiteUrl;
    public bool CanLaunchInstaller => PlatformActions.CanLaunchInstaller;
    public bool CanCopyCommand => !IsStoreCodec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsMissing))]
    [NotifyPropertyChangedFor(nameof(CanRevealPath))]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Version { get; set; }

    [ObservableProperty]
    public partial string? Path { get; set; }

    [ObservableProperty]
    public partial string? Source { get; set; }

    [ObservableProperty]
    public partial string? Extra { get; set; }

    public bool IsMissing => !IsInstalled;
    public bool CanRevealPath => IsInstalled && !IsStoreCodec;
    public string StatusIcon => IsInstalled ? Icons.CheckCircle : Icons.Download;
    public string StatusText => IsInstalled ? $"Installed · {Version}" : IsCore ? "Required · not found" : "Optional · not installed";

    public void Apply(ToolInfo? info)
    {
        IsInstalled = info is not null;
        Version = info?.ShortVersion;
        Path = info?.Path;
        Extra = info?.Notes;
        Source = info?.Source switch
        {
            ToolSource.Bundled => "Bundled with LocalMorph",
            ToolSource.Path => "Found on PATH",
            ToolSource.KnownLocation => "Found in a known install location",
            ToolSource.System => "Provided by Windows",
            _ => null
        };
    }

    [RelayCommand]
    private async Task CopyInstallCommandAsync() => await Clipboard.Default.SetTextAsync(InstallCommand);

    [RelayCommand]
    private void LaunchInstaller() => PlatformActions.LaunchInstaller(Kind);

    [RelayCommand]
    private async Task OpenWebsiteAsync() => await Launcher.Default.OpenAsync(WebsiteUrl);

    [RelayCommand]
    private void RevealPath()
    {
        if (Path is not null) PlatformActions.RevealInFolder(Path);
    }
}
