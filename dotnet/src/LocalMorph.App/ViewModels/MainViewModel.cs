using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Maui.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalMorph.App.Services;
using LocalMorph.Core.Engines;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;

namespace LocalMorph.App.ViewModels;

public enum WorkspaceView
{
    Convert,
    History,
    Tools
}

public sealed record ChoiceOption<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

public partial class MainViewModel : ObservableObject
{
    private const int MaxFilesPerDrop = 500;
    private readonly ConversionService service;
    private readonly AppSettings settings;
    private readonly SemaphoreSlim inspectionGate = new(4);
    private bool applyingPreset;

    public MainViewModel(ConversionService service, AppSettings settings)
    {
        this.service = service;
        this.settings = settings;

        SelectedResolution = ResolutionOptions[0];
        SelectedFrameRate = FrameRateOptions[0];
        SelectedSpeed = SpeedOptions[1];
        SelectedAudioBitrate = AudioBitrateOptions[0];
        SelectedSampleRate = SampleRateOptions[0];
        SelectedChannels = ChannelOptions[0];
        SelectedBitDepth = BitDepthOptions[0];
        SelectedRotation = RotationOptions[0];
        SelectedPlaybackSpeed = PlaybackSpeedOptions[2];

        foreach (var descriptor in ToolCatalog.All.Where(descriptor => descriptor.Kind != ToolKind.Ffprobe))
        {
            Tools.Add(new ToolItemViewModel(descriptor));
        }

        foreach (var entry in service.History.Entries) History.Add(entry);

        OutputDirectory = settings.OutputDirectory;
        OutputSuffix = settings.OutputSuffix;
        SelectedOverwritePolicy = OverwriteOptions.First(option => option.Value == settings.OverwritePolicy);
        MaxParallel = settings.MaxParallel;
        UseHardwareEncoder = settings.UseHardwareEncoder;
        OpenFolderWhenDone = settings.OpenFolderWhenDone;
        StripMetadata = !settings.KeepMetadata;
        Theme = settings.Theme;

        Files.CollectionChanged += (_, _) => OnFilesChanged();
        History.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
        service.ToolsChanged += inventory => MainThread.BeginInvokeOnMainThread(() => ApplyTools(inventory));
        service.HistoryAdded += entry => MainThread.BeginInvokeOnMainThread(() => History.Insert(0, entry));
        service.Queue.Drained += () => MainThread.BeginInvokeOnMainThread(OnQueueDrained);

        RefreshFormats();
    }

    // ---------------------------------------------------------------- collections

    public ObservableCollection<FileItemViewModel> Files { get; } = [];
    public ObservableCollection<FormatGroup> FormatGroups { get; } = [];
    public ObservableCollection<PresetOption> PresetOptions { get; } = [];
    public ObservableCollection<ToolItemViewModel> Tools { get; } = [];
    public ObservableCollection<HistoryEntry> History { get; } = [];

    public IReadOnlyList<ChoiceOption<int?>> ResolutionOptions { get; } =
    [
        new("Original", null), new("2160p · 4K", 2160), new("1440p", 1440), new("1080p · Full HD", 1080), new("720p · HD", 720), new("480p", 480), new("360p", 360), new("240p", 240)
    ];

    public IReadOnlyList<ChoiceOption<int?>> FrameRateOptions { get; } =
    [
        new("Same as source", null), new("24 fps", 24), new("25 fps", 25), new("30 fps", 30), new("50 fps", 50), new("60 fps", 60), new("15 fps", 15), new("10 fps", 10)
    ];

    public IReadOnlyList<ChoiceOption<EncodingSpeed>> SpeedOptions { get; } =
    [
        new("Fast", EncodingSpeed.Fast), new("Balanced", EncodingSpeed.Balanced), new("Best quality", EncodingSpeed.Quality)
    ];

    public IReadOnlyList<ChoiceOption<int?>> AudioBitrateOptions { get; } =
    [
        new("Auto", null), new("64 kbps", 64), new("96 kbps", 96), new("128 kbps", 128), new("160 kbps", 160), new("192 kbps", 192), new("256 kbps", 256), new("320 kbps", 320)
    ];

    public IReadOnlyList<ChoiceOption<int?>> SampleRateOptions { get; } =
    [
        new("Same as source", null), new("22.05 kHz", 22050), new("44.1 kHz", 44100), new("48 kHz", 48000), new("96 kHz", 96000)
    ];

    public IReadOnlyList<ChoiceOption<ChannelMode>> ChannelOptions { get; } =
    [
        new("Same as source", ChannelMode.Source), new("Mono", ChannelMode.Mono), new("Stereo", ChannelMode.Stereo)
    ];

    public IReadOnlyList<ChoiceOption<int>> BitDepthOptions { get; } = [new("16-bit", 16), new("24-bit", 24), new("32-bit", 32)];

    public IReadOnlyList<ChoiceOption<int>> RotationOptions { get; } = [new("None", 0), new("90° right", 90), new("180°", 180), new("90° left", 270)];

    public IReadOnlyList<ChoiceOption<double>> PlaybackSpeedOptions { get; } =
    [
        new("0.5×", 0.5), new("0.75×", 0.75), new("Normal", 1.0), new("1.25×", 1.25), new("1.5×", 1.5), new("2×", 2.0), new("3×", 3.0)
    ];

    public IReadOnlyList<ChoiceOption<OverwritePolicy>> OverwriteOptions { get; } =
    [
        new("Keep both (add a number)", OverwritePolicy.Rename), new("Overwrite existing", OverwritePolicy.Overwrite), new("Skip existing", OverwritePolicy.Skip)
    ];

    // ---------------------------------------------------------------- state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConvertView))]
    [NotifyPropertyChangedFor(nameof(IsHistoryView))]
    [NotifyPropertyChangedFor(nameof(IsToolsView))]
    public partial WorkspaceView View { get; set; } = WorkspaceView.Convert;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    [NotifyPropertyChangedFor(nameof(ShowTrimControls))]
    [NotifyPropertyChangedFor(nameof(ShowFrameControls))]
    public partial FileItemViewModel? SelectedFile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormat))]
    [NotifyPropertyChangedFor(nameof(ShowQuality))]
    [NotifyPropertyChangedFor(nameof(ShowResolution))]
    [NotifyPropertyChangedFor(nameof(ShowFrameRate))]
    [NotifyPropertyChangedFor(nameof(ShowSpeed))]
    [NotifyPropertyChangedFor(nameof(ShowAudio))]
    [NotifyPropertyChangedFor(nameof(ShowBitDepth))]
    [NotifyPropertyChangedFor(nameof(ShowTargetSize))]
    [NotifyPropertyChangedFor(nameof(ShowHardware))]
    [NotifyPropertyChangedFor(nameof(ShowRotate))]
    [NotifyPropertyChangedFor(nameof(ShowRemoveAudio))]
    [NotifyPropertyChangedFor(nameof(ShowLossless))]
    [NotifyPropertyChangedFor(nameof(ShowPlaybackSpeed))]
    [NotifyPropertyChangedFor(nameof(ShowVideoSection))]
    [NotifyPropertyChangedFor(nameof(ShowAudioSection))]
    [NotifyPropertyChangedFor(nameof(ShowAdvancedSection))]
    [NotifyPropertyChangedFor(nameof(ShowStripMetadata))]
    [NotifyPropertyChangedFor(nameof(ShowTrimControls))]
    [NotifyPropertyChangedFor(nameof(ShowFrameControls))]
    [NotifyPropertyChangedFor(nameof(ConvertButtonText))]
    [NotifyPropertyChangedFor(nameof(CanConvert))]
    [NotifyPropertyChangedFor(nameof(OutputExtensionLabel))]
    public partial FormatOption? SelectedFormat { get; set; }

    [ObservableProperty]
    public partial bool IsFormatChooserOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityLabel))]
    public partial int Quality { get; set; } = 80;

    [ObservableProperty] public partial ChoiceOption<int?> SelectedResolution { get; set; }
    [ObservableProperty] public partial ChoiceOption<int?> SelectedFrameRate { get; set; }
    [ObservableProperty] public partial ChoiceOption<EncodingSpeed> SelectedSpeed { get; set; }
    [ObservableProperty] public partial ChoiceOption<int?> SelectedAudioBitrate { get; set; }
    [ObservableProperty] public partial ChoiceOption<int?> SelectedSampleRate { get; set; }
    [ObservableProperty] public partial ChoiceOption<ChannelMode> SelectedChannels { get; set; }
    [ObservableProperty] public partial ChoiceOption<int> SelectedBitDepth { get; set; }
    [ObservableProperty] public partial ChoiceOption<int> SelectedRotation { get; set; }
    [ObservableProperty] public partial ChoiceOption<double> SelectedPlaybackSpeed { get; set; }
    [ObservableProperty] public partial ChoiceOption<OverwritePolicy> SelectedOverwritePolicy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetSizeLabel))]
    public partial bool UseTargetSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetSizeLabel))]
    public partial string TargetSizeText { get; set; } = "25";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HardwareLabel))]
    public partial bool UseHardwareEncoder { get; set; }

    [ObservableProperty] public partial bool RemoveAudio { get; set; }
    [ObservableProperty] public partial bool Lossless { get; set; }
    [ObservableProperty] public partial bool StripMetadata { get; set; }
    [ObservableProperty] public partial bool NormalizeAudio { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputDirectoryLabel))]
    [NotifyPropertyChangedFor(nameof(HasCustomOutputDirectory))]
    public partial string OutputDirectory { get; set; } = string.Empty;

    [ObservableProperty] public partial string OutputSuffix { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxParallelLabel))]
    public partial int MaxParallel { get; set; } = 2;

    [ObservableProperty] public partial bool OpenFolderWhenDone { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    public partial string? ValidationMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConvertButtonText))]
    [NotifyPropertyChangedFor(nameof(CanConvert))]
    [NotifyPropertyChangedFor(nameof(CanCancelAll))]
    public partial bool IsConverting { get; set; }

    [ObservableProperty]
    public partial string BatchStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRefreshingTools { get; set; }

    [ObservableProperty]
    public partial string EngineStatus { get; set; } = "Looking for FFmpeg…";

    [ObservableProperty]
    public partial string EngineDetail { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFfmpeg))]
    public partial ToolInventory Inventory { get; set; } = ToolInventory.Empty;

    [ObservableProperty]
    public partial string CommandPreview { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCommandPreviewOpen { get; set; }

    [ObservableProperty]
    public partial bool IsAdvancedOpen { get; set; }

    [ObservableProperty]
    public partial bool IsOutputOpen { get; set; }

    [ObservableProperty]
    public partial bool IsDragOver { get; set; }

    [ObservableProperty]
    public partial string? Toast { get; set; }

    // ---------------------------------------------------------------- derived

    public bool IsConvertView => View == WorkspaceView.Convert;
    public bool IsHistoryView => View == WorkspaceView.History;
    public bool IsToolsView => View == WorkspaceView.Tools;
    public bool HasFiles => Files.Count > 0;
    public bool HasNoFiles => Files.Count == 0;
    public bool HasSelectedFile => SelectedFile is not null;
    public bool HasFormat => SelectedFormat is not null;
    public bool HasFfmpeg => Inventory.HasFfmpeg;
    public bool HasHistory => History.Count > 0;
    public bool HasFailed => Files.Any(file => file.IsFailed || file.IsCanceled) && !IsConverting;
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool HasCustomOutputDirectory => !string.IsNullOrWhiteSpace(OutputDirectory);
    public string OutputDirectoryLabel => HasCustomOutputDirectory ? OutputDirectory : "Next to each source file";
    public string OutputExtensionLabel => SelectedFormat?.Extension ?? string.Empty;
    public string QualityLabel => Quality >= 95 ? $"{Quality} · near lossless" : Quality >= 80 ? $"{Quality} · high" : Quality >= 60 ? $"{Quality} · balanced" : $"{Quality} · small file";
    public string TargetSizeLabel => UseTargetSize && double.TryParse(TargetSizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var megabytes) ? $"Fit in {megabytes:0.#} MB" : "Target file size";
    public string MaxParallelLabel => MaxParallel == 1 ? "1 at a time" : $"{MaxParallel} at a time";
    public string HardwareLabel => Inventory.Ffmpeg.WorkingHardwareEncoders.Count == 0
        ? "No hardware encoder detected"
        : UseHardwareEncoder ? $"Using {Inventory.Ffmpeg.HardwareSummary}" : $"{Inventory.Ffmpeg.HardwareSummary} available";

    private bool Supports(FormatFeatures feature) => SelectedFormat?.Format.Supports(feature) == true;
    public bool ShowQuality => Supports(FormatFeatures.Quality) && !(UseTargetSize && ShowTargetSize);
    public bool ShowResolution => Supports(FormatFeatures.Resolution);
    public bool ShowFrameRate => Supports(FormatFeatures.FrameRate);
    public bool ShowSpeed => Supports(FormatFeatures.EncodingSpeed);
    public bool ShowAudio => Supports(FormatFeatures.AudioTuning) && !RemoveAudio;
    public bool ShowBitDepth => Supports(FormatFeatures.WavBitDepth);
    public bool ShowTargetSize => Supports(FormatFeatures.TargetSize);
    public bool ShowHardware => Supports(FormatFeatures.HardwareAccel) && Inventory.Ffmpeg.WorkingHardwareEncoders.Count > 0;
    public bool ShowRotate => Supports(FormatFeatures.Rotate);
    public bool ShowRemoveAudio => Supports(FormatFeatures.RemoveAudio);
    public bool ShowLossless => Supports(FormatFeatures.Lossless);
    public bool ShowPlaybackSpeed => Supports(FormatFeatures.PlaybackSpeed);
    public bool ShowStripMetadata => SelectedFormat?.Format.Engines.Contains(EngineKind.Ffmpeg) == true || SelectedFormat?.Format.Engines.Contains(EngineKind.ImageMagick) == true;
    public bool ShowVideoSection => ShowResolution || ShowFrameRate || ShowSpeed || ShowQuality || ShowTargetSize;
    public bool ShowAudioSection => ShowAudio || ShowBitDepth;
    public bool ShowAdvancedSection => ShowHardware || ShowRotate || ShowRemoveAudio || ShowLossless || ShowPlaybackSpeed || ShowStripMetadata;
    public bool ShowTrimControls => SelectedFile?.HasTimeline == true && Supports(FormatFeatures.Trim);
    public bool ShowFrameControls => SelectedFile?.IsVideo == true && Supports(FormatFeatures.FrameExtract);
    public bool CanConvert => HasFiles && HasFormat && SelectedFormat!.IsAvailable && !IsConverting && Files.Any(file => file.IsPending && file.Source is not null && Applies(SelectedFormat.Format, file));
    public bool CanCancelAll => IsConverting;
    public string ConvertButtonText
    {
        get
        {
            if (IsConverting) return "Converting…";
            var pending = Files.Count(file => file.IsPending);
            var done = Files.Count(file => file.IsCompleted);
            if (pending == 0 && done > 0) return "All done";
            if (done > 0 && pending > 0) return pending == 1 ? "Convert 1 remaining" : $"Convert {pending} remaining";
            return pending switch { 0 => "Convert", 1 => "Convert 1 file", _ => $"Convert {pending} files" };
        }
    }

    public string FilesSummary
    {
        get
        {
            if (Files.Count == 0) return string.Empty;
            var groups = Files.GroupBy(file => file.CategoryLabel).Select(group => $"{group.Count()} {group.Key.ToLowerInvariant()}{(group.Count() == 1 ? string.Empty : group.Key.EndsWith('s') ? string.Empty : "s")}");
            var bytes = Files.Sum(file => file.Source?.SizeBytes ?? 0);
            return $"{Files.Count} file{(Files.Count == 1 ? string.Empty : "s")} · {string.Join(", ", groups)} · {SourceFile.FormatBytes(bytes)}";
        }
    }

    // ---------------------------------------------------------------- lifecycle

    public async Task InitializeAsync()
    {
        IsRefreshingTools = true;
        try
        {
            await service.RefreshToolsAsync(verifyHardware: true);
        }
        finally
        {
            IsRefreshingTools = false;
        }
    }

    private void ApplyTools(ToolInventory inventory)
    {
        Inventory = inventory;
        foreach (var tool in Tools) tool.Apply(inventory.Get(tool.Kind));
        if (Tools.FirstOrDefault(tool => tool.Kind == ToolKind.Ffmpeg) is { } ffmpegTool)
        {
            ffmpegTool.Extra = inventory.HasFfmpeg
                ? $"{inventory.Ffmpeg.Encoders.Count} encoders · {inventory.Ffmpeg.HardwareSummary}"
                : null;
        }

        if (inventory.HasFfmpeg)
        {
            var version = inventory.Get(ToolKind.Ffmpeg)!.ShortVersion;
            EngineStatus = $"FFmpeg {version}";
            var extras = Tools.Where(tool => tool.IsInstalled && tool.Kind != ToolKind.Ffmpeg).Select(tool => tool.Name).ToList();
            EngineDetail = string.Join(" · ", new[] { inventory.Ffmpeg.HardwareSummary }.Concat(extras));
        }
        else
        {
            EngineStatus = "FFmpeg not found";
            EngineDetail = "Install FFmpeg to convert video and audio";
        }

        OnPropertyChanged(nameof(HasFfmpeg));
        OnPropertyChanged(nameof(ShowHardware));
        OnPropertyChanged(nameof(HardwareLabel));
        OnPropertyChanged(nameof(ShowAdvancedSection));
        RefreshFormats();
        UpdateCommandPreview();

        // Files added before ffprobe was found still need metadata.
        foreach (var file in Files.Where(file => file.Source is null || file.Source.Media is null && file.Category is MediaCategory.Video or MediaCategory.Audio or MediaCategory.Image))
        {
            _ = InspectAsync(file);
        }
    }

    // ---------------------------------------------------------------- files

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        try
        {
            var results = await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = "Choose files to convert" });
            await AddPathsAsync(results.Select(result => result.FullPath));
        }
        catch (Exception ex)
        {
            ShowToast($"Could not open the file picker: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        try
        {
            var result = await FolderPicker.Default.PickAsync(CancellationToken.None);
            if (!result.IsSuccessful || result.Folder is null) return;
            await AddPathsAsync([result.Folder.Path]);
        }
        catch (Exception ex)
        {
            ShowToast($"Could not open the folder picker: {ex.Message}");
        }
    }

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var expanded = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    expanded.AddRange(Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 })
                        .Where(file => SourceClassifier.Classify(file) != MediaCategory.Unknown)
                        .Take(MaxFilesPerDrop));
                }
                catch (Exception ex)
                {
                    ShowToast($"Could not read {Path.GetFileName(path)}: {ex.Message}");
                }
            }
            else if (File.Exists(path))
            {
                expanded.Add(path);
            }
        }

        var existing = Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<FileItemViewModel>();
        foreach (var path in expanded.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFilesPerDrop))
        {
            if (!existing.Add(path)) continue;
            var item = new FileItemViewModel(path);
            item.RemoveRequested += RemoveFile;
            item.SelectRequested += file => SelectedFile = file;
            item.PropertyChanged += OnFilePropertyChanged;
            Files.Add(item);
            added.Add(item);
        }

        if (added.Count == 0)
        {
            if (expanded.Count > 0) ShowToast("Those files are already in the queue.");
            return;
        }

        SelectedFile ??= added[0];
        View = WorkspaceView.Convert;
        await Task.WhenAll(added.Select(InspectAsync));
    }

    private async Task InspectAsync(FileItemViewModel item)
    {
        await inspectionGate.WaitAsync();
        try
        {
            var source = await SourceInspector.InspectAsync(item.Path, Inventory);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                item.ApplySource(source);
                RefreshFormats();
                if (ReferenceEquals(item, SelectedFile)) UpdateCommandPreview();
                OnPropertyChanged(nameof(FilesSummary));
                OnPropertyChanged(nameof(CanConvert));
            });
        }
        catch (Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(() => item.MarkInspectionFailed($"Could not inspect: {ex.Message}"));
        }
        finally
        {
            inspectionGate.Release();
        }
    }

    private void RemoveFile(FileItemViewModel item)
    {
        if (item.Job is { } job && !job.IsTerminal) service.Queue.Remove(job);
        item.PropertyChanged -= OnFilePropertyChanged;
        var index = Files.IndexOf(item);
        Files.Remove(item);
        if (ReferenceEquals(SelectedFile, item))
        {
            SelectedFile = Files.Count == 0 ? null : Files[Math.Clamp(index, 0, Files.Count - 1)];
        }
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        foreach (var item in Files.Where(file => file.IsCompleted).ToList()) RemoveFile(item);
        service.Queue.ClearFinished();
    }

    [RelayCommand]
    private void ClearAll()
    {
        service.Queue.CancelAll();
        foreach (var item in Files.ToList()) RemoveFile(item);
        service.Queue.ClearFinished();
    }

    [RelayCommand]
    private void RetryFailed()
    {
        foreach (var item in Files.Where(file => file.IsFailed || file.IsCanceled)) item.ResetForRetry();
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(ConvertButtonText));
    }

    private void OnFilesChanged()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasNoFiles));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(FilesSummary));
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(ConvertButtonText));
        RefreshFormats();
        UpdateBatchStatus();
    }

    private void OnFilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(FileItemViewModel.State):
                IsConverting = Files.Any(file => file.IsRunning || file.IsQueued);
                OnPropertyChanged(nameof(CanConvert));
                OnPropertyChanged(nameof(ConvertButtonText));
                OnPropertyChanged(nameof(HasFailed));
                UpdateBatchStatus();
                break;
            case nameof(FileItemViewModel.Progress):
                UpdateBatchStatus();
                break;
            case nameof(FileItemViewModel.TrimStartSeconds):
            case nameof(FileItemViewModel.TrimEndSeconds):
            case nameof(FileItemViewModel.FrameTimeSeconds):
                if (ReferenceEquals(sender, SelectedFile)) UpdateCommandPreview();
                break;
            case nameof(FileItemViewModel.Category):
                RefreshFormats();
                break;
        }
    }

    partial void OnSelectedFileChanged(FileItemViewModel? oldValue, FileItemViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        UpdateCommandPreview();
    }

    private void UpdateBatchStatus()
    {
        var total = Files.Count(file => file.State is not null);
        if (total == 0)
        {
            BatchStatus = string.Empty;
            return;
        }

        var done = Files.Count(file => file.IsCompleted);
        var failed = Files.Count(file => file.IsFailed);
        var running = Files.Where(file => file.IsRunning).ToList();
        if (IsConverting)
        {
            var overall = Files.Where(file => file.State is not null).Average(file => file.IsCompleted ? 1 : file.Progress);
            BatchStatus = $"{done} of {total} done · {overall * 100:0}% overall · {running.Count} running";
        }
        else
        {
            BatchStatus = failed > 0 ? $"{done} converted · {failed} failed" : done > 0 ? $"{done} converted" : string.Empty;
        }
    }

    private void OnQueueDrained()
    {
        IsConverting = Files.Any(file => file.IsRunning || file.IsQueued);
        OnPropertyChanged(nameof(HasFailed));
        UpdateBatchStatus();
        var done = Files.Count(file => file.IsCompleted);
        var failed = Files.Count(file => file.IsFailed);
        if (done > 0 && failed == 0) ShowToast(done == 1 ? "Conversion finished." : $"All {done} conversions finished.");
        else if (failed > 0) ShowToast($"{done} finished, {failed} failed. Select a file to see why.");

        if (OpenFolderWhenDone && Files.FirstOrDefault(file => file.IsCompleted && file.OutputPath is not null) is { OutputPath: { } output })
        {
            PlatformActions.RevealInFolder(output);
        }
    }

    // ---------------------------------------------------------------- formats & presets

    private void RefreshFormats()
    {
        var sources = Files.Where(file => file.Category != MediaCategory.Unknown).Select(file => (file.Category, file.Flavor)).Distinct().ToList();
        var shared = sources.Count == 0
            ? FormatCatalog.All.Where(format => format.AcceptsSources.Contains(MediaCategory.Video)).ToList()
            : FormatCatalog.ForSources(sources).ToList();
        // A mixed batch (e.g. video + audio + image) may share nothing; offer everything any file can become
        // and skip the files a format does not apply to at conversion time.
        IsMixedBatch = sources.Count > 1 && shared.Count == 0;
        var candidates = IsMixedBatch ? sources.SelectMany(source => FormatCatalog.ForSource(source.Category, source.Flavor)).Distinct().ToList() : shared;
        var formats = candidates.Select(format => new FormatOption(format, Inventory)).ToList();

        var previous = SelectedFormat?.Id;
        FormatGroups.Clear();
        foreach (var group in formats.GroupBy(option => option.CategoryLabel).OrderBy(group => GroupOrder(group.Key)))
        {
            FormatGroups.Add(new FormatGroup(group.Key, group.OrderByDescending(option => option.IsAvailable).ThenByDescending(option => option.HasBadge)));
        }

        var all = FormatGroups.SelectMany(group => group).ToList();
        var primaryCategory = sources.Select(source => source.Category).FirstOrDefault();
        var remembered = primaryCategory != MediaCategory.Unknown ? settings.LastFormatFor(primaryCategory.ToString()) : null;
        var next = all.FirstOrDefault(option => option.Id == previous)
                   ?? all.FirstOrDefault(option => option.Id == remembered && option.IsAvailable)
                   ?? all.FirstOrDefault(option => option.IsAvailable && option.HasBadge)
                   ?? all.FirstOrDefault(option => option.IsAvailable)
                   ?? all.FirstOrDefault();
        if (!ReferenceEquals(next, SelectedFormat)) SelectedFormat = next;

        var categories = sources.Select(source => source.Category).Distinct().ToList();
        PresetOptions.Clear();
        List<MediaCategory> presetSource = categories.Count == 0 ? [MediaCategory.Video] : IsMixedBatch ? categories.Take(1).ToList() : categories;
        var presets = IsMixedBatch ? categories.SelectMany(category => Presets.For(category)).Distinct() : Presets.For(presetSource);
        foreach (var preset in presets)
        {
            var option = all.FirstOrDefault(candidate => candidate.Id == preset.FormatId);
            var available = option?.IsAvailable == true;
            PresetOptions.Add(new PresetOption(preset, available, available ? null : option is null ? "Not valid for this mix of files" : option.EngineLabel));
        }
        OnPropertyChanged(nameof(HasPresets));
        OnPropertyChanged(nameof(ApplicabilityLabel));
    }

    public bool HasPresets => PresetOptions.Count > 0;

    [ObservableProperty]
    public partial bool IsMixedBatch { get; set; }

    /// <summary>How many queued files the selected format can actually convert.</summary>
    public string ApplicabilityLabel
    {
        get
        {
            if (SelectedFormat is null || Files.Count == 0) return string.Empty;
            var applicable = Files.Count(file => Applies(SelectedFormat.Format, file));
            return applicable == Files.Count ? string.Empty : $"Applies to {applicable} of {Files.Count} files · others will be skipped";
        }
    }

    private static bool Applies(OutputFormat format, FileItemViewModel file) =>
        format.AcceptsSources.Contains(file.Category) &&
        (file.Category != MediaCategory.Document || format.AcceptsDocumentFlavors is null || format.AcceptsDocumentFlavors.Contains(file.Flavor));

    private static int GroupOrder(string label) => label switch { "Video" => 0, "Audio" => 1, "Image" => 2, "Document" => 3, _ => 4 };

    partial void OnSelectedFormatChanged(FormatOption? value)
    {
        if (value is null) return;
        if (!applyingPreset)
        {
            foreach (var preset in PresetOptions) preset.IsActive = false;
        }

        // Files skipped only because the previous format didn't apply get another chance.
        foreach (var file in Files.Where(file => file.State == JobState.Skipped && Applies(value.Format, file))) file.ResetForRetry();

        var primary = Files.Select(file => file.Category).FirstOrDefault(category => category != MediaCategory.Unknown);
        if (primary != MediaCategory.Unknown && value.IsAvailable) settings.SetLastFormatFor(primary.ToString(), value.Id);
        if (!value.Format.Supports(FormatFeatures.TargetSize)) UseTargetSize = false;
        if (!value.Format.Supports(FormatFeatures.RemoveAudio)) RemoveAudio = false;
        if (!value.Format.Supports(FormatFeatures.Lossless)) Lossless = false;
        if (!value.Format.Supports(FormatFeatures.Rotate)) SelectedRotation = RotationOptions[0];
        if (!value.Format.Supports(FormatFeatures.PlaybackSpeed)) SelectedPlaybackSpeed = PlaybackSpeedOptions[2];
        IsFormatChooserOpen = false;
        ValidationMessage = null;
        OnPropertyChanged(nameof(ApplicabilityLabel));
        UpdateCommandPreview();
    }

    [RelayCommand]
    private void ChooseFormat(FormatOption option)
    {
        SelectedFormat = option;
        IsFormatChooserOpen = false;
    }

    [RelayCommand]
    private void ToggleFormatChooser() => IsFormatChooserOpen = !IsFormatChooserOpen;

    [RelayCommand]
    private void ApplyPreset(PresetOption option)
    {
        if (!option.IsAvailable) return;
        var preset = option.Preset;
        var format = FormatGroups.SelectMany(group => group).FirstOrDefault(candidate => candidate.Id == preset.FormatId);
        if (format is null) return;

        applyingPreset = true;
        try
        {
            SelectedFormat = format;
            var o = preset.Options;
            Quality = o.Quality;
            SelectedResolution = ResolutionOptions.FirstOrDefault(choice => choice.Value == o.TargetHeight) ?? ResolutionOptions[0];
            SelectedFrameRate = FrameRateOptions.FirstOrDefault(choice => choice.Value == o.FrameRate) ?? FrameRateOptions[0];
            SelectedSpeed = SpeedOptions.First(choice => choice.Value == o.Speed);
            SelectedAudioBitrate = AudioBitrateOptions.FirstOrDefault(choice => choice.Value == o.AudioBitrateKbps) ?? AudioBitrateOptions[0];
            SelectedSampleRate = SampleRateOptions.FirstOrDefault(choice => choice.Value == o.SampleRate) ?? SampleRateOptions[0];
            SelectedChannels = ChannelOptions.First(choice => choice.Value == o.Channels);
            SelectedBitDepth = BitDepthOptions.FirstOrDefault(choice => choice.Value == o.WavBitDepth) ?? BitDepthOptions[0];
            SelectedRotation = RotationOptions[0];
            SelectedPlaybackSpeed = PlaybackSpeedOptions[2];
            UseTargetSize = o.TargetSizeMegabytes is not null;
            if (o.TargetSizeMegabytes is { } megabytes) TargetSizeText = megabytes.ToString("0.#", CultureInfo.InvariantCulture);
            RemoveAudio = o.RemoveAudio;
            Lossless = o.Lossless;
            foreach (var candidate in PresetOptions) candidate.IsActive = ReferenceEquals(candidate, option);
        }
        finally
        {
            applyingPreset = false;
        }

        UpdateCommandPreview();
        ShowToast($"Preset applied: {preset.Name}");
    }

    // ---------------------------------------------------------------- settings reactions

    partial void OnQualityChanged(int value) => SettingsTouched();
    partial void OnSelectedResolutionChanged(ChoiceOption<int?> value) => SettingsTouched();
    partial void OnSelectedFrameRateChanged(ChoiceOption<int?> value) => SettingsTouched();
    partial void OnSelectedSpeedChanged(ChoiceOption<EncodingSpeed> value) => SettingsTouched();
    partial void OnSelectedAudioBitrateChanged(ChoiceOption<int?> value) => SettingsTouched();
    partial void OnSelectedSampleRateChanged(ChoiceOption<int?> value) => SettingsTouched();
    partial void OnSelectedChannelsChanged(ChoiceOption<ChannelMode> value) => SettingsTouched();
    partial void OnSelectedBitDepthChanged(ChoiceOption<int> value) => SettingsTouched();
    partial void OnSelectedRotationChanged(ChoiceOption<int> value) => SettingsTouched();
    partial void OnSelectedPlaybackSpeedChanged(ChoiceOption<double> value) => SettingsTouched();
    partial void OnTargetSizeTextChanged(string value) => SettingsTouched();
    partial void OnLosslessChanged(bool value) => SettingsTouched();
    partial void OnNormalizeAudioChanged(bool value) => SettingsTouched();
    partial void OnOutputSuffixChanged(string value)
    {
        settings.OutputSuffix = value;
        SettingsTouched();
    }

    partial void OnUseTargetSizeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowQuality));
        SettingsTouched();
    }

    partial void OnRemoveAudioChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAudio));
        OnPropertyChanged(nameof(ShowAudioSection));
        SettingsTouched();
    }

    partial void OnUseHardwareEncoderChanged(bool value)
    {
        settings.UseHardwareEncoder = value;
        SettingsTouched();
    }

    partial void OnStripMetadataChanged(bool value)
    {
        settings.KeepMetadata = !value;
        SettingsTouched();
    }

    partial void OnSelectedOverwritePolicyChanged(ChoiceOption<OverwritePolicy> value) => settings.OverwritePolicy = value.Value;

    partial void OnMaxParallelChanged(int value)
    {
        settings.MaxParallel = value;
        service.ApplySettings();
    }

    partial void OnOpenFolderWhenDoneChanged(bool value) => settings.OpenFolderWhenDone = value;

    private void SettingsTouched()
    {
        if (!applyingPreset)
        {
            foreach (var preset in PresetOptions) preset.IsActive = false;
        }
        ValidationMessage = null;
        UpdateCommandPreview();
    }

    private ConversionOptions BuildSharedOptions()
    {
        double? targetSize = UseTargetSize && ShowTargetSize && double.TryParse(TargetSizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var megabytes) && megabytes > 0
            ? megabytes
            : null;

        return new ConversionOptions
        {
            Quality = Quality,
            Speed = SelectedSpeed.Value,
            TargetHeight = SelectedResolution.Value,
            FrameRate = SelectedFrameRate.Value,
            AudioBitrateKbps = SelectedAudioBitrate.Value,
            SampleRate = SelectedSampleRate.Value,
            Channels = SelectedChannels.Value,
            WavBitDepth = SelectedBitDepth.Value,
            UseHardwareEncoder = UseHardwareEncoder,
            TargetSizeMegabytes = targetSize,
            Rotation = SelectedRotation.Value,
            RemoveAudio = RemoveAudio,
            StripMetadata = StripMetadata,
            Lossless = Lossless,
            PlaybackSpeed = SelectedPlaybackSpeed.Value,
            NormalizeAudio = NormalizeAudio
        };
    }

    private void UpdateCommandPreview()
    {
        if (SelectedFile?.Source is not { } source || SelectedFormat is not { } format)
        {
            CommandPreview = "Add a file and choose a format to see the exact command LocalMorph will run.";
            return;
        }

        var engine = FormatCatalog.ResolveEngine(format.Format, source.Path, Inventory);
        if (engine is null)
        {
            CommandPreview = $"{format.DisplayName} needs {string.Join(" or ", format.MissingTools.Select(kind => ToolCatalog.Get(kind).DisplayName))}. Open Tools to install it.";
            return;
        }

        try
        {
            var options = SelectedFile.ApplyPerFileOptions(BuildSharedOptions(), format.Format);
            var outputPath = Path.Combine(HasCustomOutputDirectory ? OutputDirectory : Path.GetDirectoryName(source.Path) ?? string.Empty, OutputNaming.Sanitize(Path.GetFileNameWithoutExtension(source.Path) + OutputSuffix) + format.Extension);
            var job = new ConversionJob(source, format.Format, options, outputPath, engine.Value);
            var plan = EngineRegistry.Get(engine.Value).Plan(job, Inventory, Path.Combine(FileSystem.Current.CacheDirectory, "work"));
            CommandPreview = plan.Describe();
            plan.Cleanup?.Invoke();
        }
        catch (Exception ex)
        {
            CommandPreview = ex.Message;
        }
    }

    // ---------------------------------------------------------------- output

    [RelayCommand]
    private async Task PickOutputDirectoryAsync()
    {
        try
        {
            var result = await FolderPicker.Default.PickAsync(CancellationToken.None);
            if (!result.IsSuccessful || result.Folder is null) return;
            OutputDirectory = result.Folder.Path;
            settings.OutputDirectory = OutputDirectory;
            UpdateCommandPreview();
        }
        catch (Exception ex)
        {
            ShowToast($"Could not choose a folder: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ResetOutputDirectory()
    {
        OutputDirectory = string.Empty;
        settings.OutputDirectory = string.Empty;
        UpdateCommandPreview();
    }

    [RelayCommand]
    private void OpenOutputDirectory()
    {
        var target = HasCustomOutputDirectory ? OutputDirectory : Files.FirstOrDefault(file => file.OutputPath is not null)?.OutputPath ?? SelectedFile?.Path;
        if (target is not null) PlatformActions.RevealInFolder(target);
    }

    // ---------------------------------------------------------------- conversion

    [RelayCommand]
    private void ConvertAll()
    {
        if (SelectedFormat is not { } format) return;
        if (!format.IsAvailable)
        {
            ValidationMessage = $"{format.DisplayName} needs {string.Join(" or ", format.MissingTools.Select(kind => ToolCatalog.Get(kind).DisplayName))}. Open Tools to install it.";
            return;
        }

        var shared = BuildSharedOptions();
        if (shared.Validate(format.Format) is { } problem)
        {
            ValidationMessage = problem;
            return;
        }

        if (HasCustomOutputDirectory && !Directory.Exists(OutputDirectory))
        {
            ValidationMessage = "The output folder no longer exists. Choose another one.";
            return;
        }

        ValidationMessage = null;
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var policy = SelectedOverwritePolicy.Value;
        var queued = 0;

        foreach (var item in Files.Where(file => file.IsPending && file.Source is not null).ToList())
        {
            var source = item.Source!;
            if (!Applies(format.Format, item))
            {
                var notApplicable = new ConversionJob(source, format.Format, shared, source.Path, EngineKind.Ffmpeg);
                item.AttachJob(notApplicable);
                notApplicable.Skip($"Skipped · {format.DisplayName} doesn't apply to {item.CategoryLabel.ToLowerInvariant()} files");
                continue;
            }

            var engine = FormatCatalog.ResolveEngine(format.Format, source.Path, Inventory);
            var job = engine is null
                ? null
                : new ConversionJob(source, format.Format, item.ApplyPerFileOptions(shared, format.Format),
                    OutputNaming.BuildOutputPath(source.Path, format.Format, HasCustomOutputDirectory ? OutputDirectory : null, OutputSuffix, policy, reserved), engine.Value);

            if (job is null)
            {
                var placeholder = new ConversionJob(source, format.Format, shared, source.Path, EngineKind.Ffmpeg);
                item.AttachJob(placeholder);
                placeholder.Skip($"Needs {string.Join(" or ", format.MissingTools.Select(kind => ToolCatalog.Get(kind).DisplayName))}");
                continue;
            }

            if (policy == OverwritePolicy.Skip && File.Exists(Path.Combine(Path.GetDirectoryName(job.OutputPath)!, OutputNaming.Sanitize(Path.GetFileNameWithoutExtension(source.Path) + OutputSuffix) + format.Extension)))
            {
                item.AttachJob(job);
                job.Skip("Skipped · output already exists");
                continue;
            }

            item.AttachJob(job);
            service.Queue.Enqueue(job);
            queued++;
        }

        if (queued == 0)
        {
            ShowToast("Nothing to convert.");
            return;
        }

        service.Queue.MaxParallel = MaxParallel;
        service.Queue.Start();
        IsConverting = true;
        UpdateBatchStatus();
    }

    [RelayCommand]
    private void CancelAll()
    {
        service.Queue.CancelAll();
        ShowToast("Canceling…");
    }

    // ---------------------------------------------------------------- trim helpers (called from the page with the player position)

    public void SetTrimStart(double seconds)
    {
        if (SelectedFile is not { HasTimeline: true } file) return;
        file.TrimStartSeconds = Math.Clamp(seconds, 0, Math.Max(0, file.TrimEndSeconds - 0.1));
    }

    public void SetTrimEnd(double seconds)
    {
        if (SelectedFile is not { HasTimeline: true } file) return;
        file.TrimEndSeconds = Math.Clamp(seconds, file.TrimStartSeconds + 0.1, file.DurationSeconds);
    }

    public void SetFrameTime(double seconds)
    {
        if (SelectedFile is not { IsVideo: true } file) return;
        file.FrameTimeSeconds = Math.Clamp(seconds, 0, file.DurationSeconds);
        ShowToast($"Frame at {SourceFile.FormatDuration(seconds)} will be saved.");
    }

    // ---------------------------------------------------------------- views, tools, history

    [RelayCommand] private void ShowConvert() => View = WorkspaceView.Convert;
    [RelayCommand] private void ShowHistory() => View = WorkspaceView.History;
    [RelayCommand] private void ShowTools() => View = WorkspaceView.Tools;
    [RelayCommand] private void ToggleAdvanced() => IsAdvancedOpen = !IsAdvancedOpen;
    [RelayCommand] private void ToggleOutput() => IsOutputOpen = !IsOutputOpen;
    [RelayCommand] private void ToggleCommandPreview() => IsCommandPreviewOpen = !IsCommandPreviewOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeIcon))]
    [NotifyPropertyChangedFor(nameof(ThemeLabel))]
    public partial AppTheme Theme { get; set; }

    public string ThemeIcon => Theme switch { AppTheme.Light => Icons.Sun, AppTheme.Dark => Icons.Moon, _ => Icons.ThemeAuto };
    public string ThemeLabel => Theme switch { AppTheme.Light => "Light theme", AppTheme.Dark => "Dark theme", _ => "Follow system theme" };

    [RelayCommand]
    private void CycleTheme()
    {
        Theme = Theme switch { AppTheme.Unspecified => AppTheme.Light, AppTheme.Light => AppTheme.Dark, _ => AppTheme.Unspecified };
    }

    partial void OnThemeChanged(AppTheme value)
    {
        settings.Theme = value;
        if (Application.Current is { } app) app.UserAppTheme = value;
    }

    [RelayCommand]
    private async Task RefreshToolsAsync()
    {
        if (IsRefreshingTools) return;
        IsRefreshingTools = true;
        try
        {
            await service.RefreshToolsAsync(verifyHardware: true);
            ShowToast(Inventory.HasFfmpeg ? "Tools refreshed." : "FFmpeg still not found. Install it, then refresh again.");
        }
        finally
        {
            IsRefreshingTools = false;
        }
    }

    [RelayCommand]
    private async Task CopyCommandPreviewAsync()
    {
        await Clipboard.Default.SetTextAsync(CommandPreview);
        ShowToast("Command copied.");
    }

    [RelayCommand]
    private async Task OpenHistoryOutputAsync(HistoryEntry entry)
    {
        if (!await PlatformActions.OpenAsync(entry.OutputPath)) ShowToast("That file no longer exists.");
    }

    [RelayCommand]
    private void RevealHistoryOutput(HistoryEntry entry)
    {
        if (!File.Exists(entry.OutputPath) || !PlatformActions.RevealInFolder(entry.OutputPath)) ShowToast("That file no longer exists.");
    }

    [RelayCommand]
    private async Task ReconvertFromHistoryAsync(HistoryEntry entry)
    {
        if (!File.Exists(entry.SourcePath))
        {
            ShowToast("The original file no longer exists.");
            return;
        }
        await AddPathsAsync([entry.SourcePath]);
        var format = FormatGroups.SelectMany(group => group).FirstOrDefault(option => option.Id == entry.FormatId);
        if (format is not null) SelectedFormat = format;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        service.History.Clear();
        History.Clear();
        OnPropertyChanged(nameof(HasHistory));
    }

    private CancellationTokenSource? toastCts;

    private void ShowToast(string message)
    {
        toastCts?.Cancel();
        toastCts = new CancellationTokenSource();
        var token = toastCts.Token;
        Toast = message;
        _ = Task.Delay(3500, token).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() => { if (!token.IsCancellationRequested) Toast = null; }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }
}
