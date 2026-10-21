using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Maui.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalMorph.App.Services;
using LocalMorph.Core.Engines;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Imaging;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;

namespace LocalMorph.App.ViewModels;

public enum WorkspaceView
{
    Convert,
    History,
    Tools,
    About
}

public sealed record ChoiceOption<T>(string Label, T Value)
{
    public override string ToString() => Label;
}

public partial class MainViewModel : ObservableObject
{
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly ConversionService service;
    private readonly AppSettings settings;
    private readonly SemaphoreSlim inspectionGate = new(4);
    private int inspectionsPending;
    private bool applyingPreset;
    private bool addingFiles;
    private bool refreshingMetadata;
    private readonly SemaphoreSlim importGate = new(1);
    private CancellationTokenSource? importCancellation;
    private BatchPreflight? preflight;
    private readonly Dictionary<string, IDisposable> workspaceAccess = new(StringComparer.Ordinal);
    private IDisposable? outputAccess;
    private bool NeedsOutputAuthorization => Distribution.IsMacStoreBuild && outputAccess is null;
    private bool isClosing;
    private Task? closing;
    private CancellationTokenSource inspectionCancellation = new();
    private TaskCompletionSource? inspectionsStopped;

    private const string IdleImportSummary = "Drop more files here, or use Add files";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConvert), nameof(ShowImportStatus))]
    public partial bool IsImporting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowImportStatus))]
    public partial string ImportSummary { get; set; } = IdleImportSummary;

    /// <summary>The empty-state drop zone already invites drops; only show the strip once it adds information.</summary>
    public bool ShowImportStatus => HasFiles || IsImporting || ImportSummary != IdleImportSummary;

    public MainViewModel(ConversionService service, AppSettings settings)
    {
        this.service = service;
        this.settings = settings;
        SelectedFeedbackType = FeedbackTypes[0];
        SelectedOperation = OperationOptions[0];
        SelectedSplitMode = SplitModeOptions[0];

        SelectedResolution = ResolutionOptions[0];
        SelectedFrameRate = FrameRateOptions[0];
        SelectedSpeed = SpeedOptions[1];
        SelectedAudioBitrate = AudioBitrateOptions[0];
        SelectedSampleRate = SampleRateOptions[0];
        SelectedChannels = ChannelOptions[0];
        SelectedBitDepth = BitDepthOptions[0];
        SelectedRotation = RotationOptions[0];
        SelectedPlaybackSpeed = PlaybackSpeedOptions[2];
        SelectedSubtitleMode = SubtitleModeOptions[0];
        SelectedCrop = CropOptions[0];
        SelectedFadeIn = FadeOptions[0];
        SelectedFadeOut = FadeOptions[0];
        SelectedVolume = VolumeOptions[DefaultVolumeIndex];
        SelectedAudioDelay = AudioDelayOptions[DefaultAudioDelayIndex];

        foreach (var descriptor in ToolCatalog.All.Where(descriptor => Distribution.IsMacStoreBuild
            ? descriptor.Kind is ToolKind.Ffmpeg or ToolKind.Ffprobe or ToolKind.AppleNative : descriptor.Kind != ToolKind.Ffprobe))
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

        Files.CollectionChanged += (_, _) => { if (!addingFiles) OnFilesChanged(); };
        History.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
        service.ToolsChanged += inventory => MainThread.BeginInvokeOnMainThread(() => ApplyTools(inventory));
        service.HistoryAdded += entry => MainThread.BeginInvokeOnMainThread(() => History.Insert(0, entry));
        service.History.PersistenceFailed += message => MainThread.BeginInvokeOnMainThread(() => ReportError(message));
        service.Queue.Drained += () => MainThread.BeginInvokeOnMainThread(async () => await OnQueueDrainedAsync());

        RefreshFormats();
    }

    // ---------------------------------------------------------------- collections

    public ObservableCollection<FileItemViewModel> Files { get; } = [];
    public ObservableCollection<FormatGroup> FormatGroups { get; } = [];
    public ObservableCollection<PresetOption> PresetOptions { get; } = [];
    public ObservableCollection<ToolItemViewModel> Tools { get; } = [];
    public ObservableCollection<HistoryEntry> History { get; } = [];
    public IReadOnlyList<ChoiceOption<MediaOperation>> OperationOptions { get; } =
        [new("Convert files", MediaOperation.Convert), new("Join compatible clips", MediaOperation.Join), new("Split into clips", MediaOperation.Split)];
    public IReadOnlyList<ChoiceOption<SplitMode>> SplitModeOptions { get; } =
        [new("Accurate · re-encode", SplitMode.Accurate), new("Fast · source keyframes / stream copy", SplitMode.Fast)];
    [ObservableProperty] public partial ChoiceOption<MediaOperation> SelectedOperation { get; set; }
    [ObservableProperty] public partial ChoiceOption<SplitMode> SelectedSplitMode { get; set; }
    [ObservableProperty] public partial string SegmentSecondsText { get; set; } = "60";
    public bool IsJoin => SelectedOperation?.Value == MediaOperation.Join;
    public bool IsSplit => SelectedOperation?.Value == MediaOperation.Split;
    public string OperationHint => IsJoin
        ? "Joins pending video clips in queue order, without re-encoding. Use the row menus to change order. Streams must match; chapters are excluded."
        : IsSplit ? "Split each pending source into duration-based clips. Accurate mode re-encodes; fast mode copies streams at source keyframes. Completed clips survive a later failure or cancellation."
        : "One format for the whole batch. Trim and track choices apply to the selected file.";
    private OperationRequest Operation => new(SelectedOperation?.Value ?? MediaOperation.Convert, IsSplit ? ParseNumber(SegmentSecondsText) : 60, SelectedSplitMode?.Value ?? SplitMode.Accurate);
    private bool IsReadingFiles => addingFiles || IsImporting || inspectionsPending > 0;

    private BatchPreflight PreparedBatch => preflight ??= SelectedFormat is not { } format ? new([])
        : BatchPlanner.Prepare(Files.Where(file => file.IsPending && file.Source is not null).Select(file => new ConversionInput(file.Source!, file.ApplyPerFileOptions(BuildSharedOptions(), format.Format))).ToArray(),
            format.Format, Operation, Inventory, HasCustomOutputDirectory ? OutputDirectory : null, OutputSuffix, SelectedOverwritePolicy.Value, Files.Select(file => file.Path));

    public string PreflightSummary
    {
        get
        {
            if (IsConverting || SelectedFormat is null) return "";
            if (IsReadingFiles) return "Reading files before checking this batch…";
            if (NeedsOutputAuthorization) return HasCustomOutputDirectory
                ? "Output-folder access will be restored or requested before conversion."
                : "You'll choose an output folder when you convert. LocalMorph only writes where you allow it.";
            var plan = PreparedBatch;
            var excluded = plan.Entries.Where(entry => !entry.CanRun).ToArray();
            return $"{plan.RunnableCount} operation(s) ready · {excluded.Length} will be skipped"
                + (excluded.Length == 0 ? "" : "\n" + string.Join("\n", excluded.Take(3).Select(entry => $"{entry.Inputs[0].FileName}: {entry.Issue}")));
        }
    }

    private void InvalidatePreflight()
    {
        preflight = null;
        if (addingFiles) return;
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(ConvertButtonText));
        OnPropertyChanged(nameof(PreflightSummary));
    }

    partial void OnIsImportingChanged(bool value) => InvalidatePreflight();

    partial void OnSelectedOperationChanged(ChoiceOption<MediaOperation> value)
    {
        OnPropertyChanged(nameof(IsJoin));
        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(OperationHint));
        OnPropertyChanged(nameof(HasPresets));
        if (value.Value == MediaOperation.Join)
            SelectedFormat = FormatGroups.SelectMany(group => group).FirstOrDefault(format => format.Id == "mkv-copy") ?? SelectedFormat;
        RefreshOperationSurfaces();
    }

    partial void OnSelectedSplitModeChanged(ChoiceOption<SplitMode> value) => RefreshOperationSurfaces();
    partial void OnSegmentSecondsTextChanged(string value) => SettingsTouched();

    private void RefreshOperationSurfaces()
    {
        foreach (var name in new[] { nameof(ShowVideoSection), nameof(ShowAudioSection), nameof(ShowTracksSection), nameof(ShowFileTracks), nameof(ShowEditSection), nameof(ShowAdvancedSection), nameof(ShowTrimControls), nameof(ShowFrameControls) })
            OnPropertyChanged(name);
        SettingsTouched();
    }

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

    public IReadOnlyList<ChoiceOption<SubtitleMode>> SubtitleModeOptions { get; } =
    [
        new("Automatic · keep in MKV", SubtitleMode.Auto), new("Keep as selectable tracks", SubtitleMode.Keep), new("Remove all", SubtitleMode.Remove), new("Burn into the picture", SubtitleMode.BurnIn)
    ];

    public IReadOnlyList<ChoiceOption<string>> CropOptions { get; } =
    [
        new("Original shape", CropAspects.None), new("16:9 · Widescreen", "16:9"), new("9:16 · Vertical (Reels, Shorts)", "9:16"), new("1:1 · Square", "1:1"),
        new("4:5 · Portrait feed", "4:5"), new("4:3 · Classic", "4:3"), new("21:9 · Cinema", "21:9")
    ];

    public IReadOnlyList<ChoiceOption<double>> FadeOptions { get; } =
    [
        new("None", 0), new("0.5 s", 0.5), new("1 s", 1), new("2 s", 2), new("3 s", 3), new("5 s", 5)
    ];

    private const int DefaultVolumeIndex = 3;
    public IReadOnlyList<ChoiceOption<int>> VolumeOptions { get; } =
    [
        new("25%", 25), new("50%", 50), new("75%", 75), new("100% · Original", 100), new("125%", 125), new("150%", 150), new("200%", 200), new("300%", 300)
    ];

    private const int DefaultAudioDelayIndex = 5;
    public IReadOnlyList<ChoiceOption<int>> AudioDelayOptions { get; } =
    [
        new("1 s earlier", -1000), new("500 ms earlier", -500), new("250 ms earlier", -250), new("100 ms earlier", -100), new("40 ms earlier", -40),
        new("In sync", 0),
        new("40 ms later", 40), new("100 ms later", 100), new("250 ms later", 250), new("500 ms later", 500), new("1 s later", 1000)
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
    [NotifyPropertyChangedFor(nameof(IsAboutView))]
    [NotifyPropertyChangedFor(nameof(TitleSubtitle))]
    public partial WorkspaceView View { get; set; } = WorkspaceView.Convert;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    [NotifyPropertyChangedFor(nameof(ShowTrimControls))]
    [NotifyPropertyChangedFor(nameof(ShowFrameControls))]
    [NotifyPropertyChangedFor(nameof(ShowAudioTrackPicker))]
    [NotifyPropertyChangedFor(nameof(ShowReplaceAudio))]
    [NotifyPropertyChangedFor(nameof(ShowSubtitleTrackPicker))]
    [NotifyPropertyChangedFor(nameof(ShowExternalSubtitle))]
    [NotifyPropertyChangedFor(nameof(ShowFileTracks))]
    public partial FileItemViewModel? SelectedFile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormat))]
    [NotifyPropertyChangedFor(nameof(ShowQuality))]
    [NotifyPropertyChangedFor(nameof(ShowResolution))]
    [NotifyPropertyChangedFor(nameof(ShowFrameRate))]
    [NotifyPropertyChangedFor(nameof(ShowSpeed))]
    [NotifyPropertyChangedFor(nameof(ShowAudio))]
    [NotifyPropertyChangedFor(nameof(ShowTrimSilence))]
    [NotifyPropertyChangedFor(nameof(ShowBitDepth))]
    [NotifyPropertyChangedFor(nameof(ShowTargetSize))]
    [NotifyPropertyChangedFor(nameof(ShowHardware))]
    [NotifyPropertyChangedFor(nameof(ShowRotate))]
    [NotifyPropertyChangedFor(nameof(ShowRemoveAudio))]
    [NotifyPropertyChangedFor(nameof(ShowLossless))]
    [NotifyPropertyChangedFor(nameof(ShowPlaybackSpeed))]
    [NotifyPropertyChangedFor(nameof(ShowVideoSection))]
    [NotifyPropertyChangedFor(nameof(ShowVideoBitrate))]
    [NotifyPropertyChangedFor(nameof(TargetSizeHint))]
    [NotifyPropertyChangedFor(nameof(ShowAudioSection))]
    [NotifyPropertyChangedFor(nameof(ShowAdvancedSection))]
    [NotifyPropertyChangedFor(nameof(ShowStripMetadata))]
    [NotifyPropertyChangedFor(nameof(ShowTrimControls))]
    [NotifyPropertyChangedFor(nameof(ShowFrameControls))]
    [NotifyPropertyChangedFor(nameof(ShowAudioTrackPicker))]
    [NotifyPropertyChangedFor(nameof(ShowKeepAllAudio))]
    [NotifyPropertyChangedFor(nameof(ShowReplaceAudio))]
    [NotifyPropertyChangedFor(nameof(ShowSubtitleMode))]
    [NotifyPropertyChangedFor(nameof(ShowSubtitleTrackPicker))]
    [NotifyPropertyChangedFor(nameof(ShowExternalSubtitle))]
    [NotifyPropertyChangedFor(nameof(ShowTracksSection))]
    [NotifyPropertyChangedFor(nameof(ShowFileTracks))]
    [NotifyPropertyChangedFor(nameof(ShowVideoFilters))]
    [NotifyPropertyChangedFor(nameof(ShowEffects))]
    [NotifyPropertyChangedFor(nameof(ShowEditSection))]
    [NotifyPropertyChangedFor(nameof(ConvertButtonText))]
    [NotifyPropertyChangedFor(nameof(CanConvert))]
    [NotifyPropertyChangedFor(nameof(OutputExtensionLabel))]
    public partial FormatOption? SelectedFormat { get; set; }

    [ObservableProperty]
    public partial bool IsFormatChooserOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityLabel))]
    public partial int Quality { get; set; } = 80;

    [ObservableProperty] public partial string VideoBitrateText { get; set; } = "4000";

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
    [NotifyPropertyChangedFor(nameof(ShowSubtitleTrackPicker))]
    [NotifyPropertyChangedFor(nameof(ShowExternalSubtitle))]
    [NotifyPropertyChangedFor(nameof(ShowFileTracks))]
    [NotifyPropertyChangedFor(nameof(SubtitleModeHint))]
    public partial ChoiceOption<SubtitleMode> SelectedSubtitleMode { get; set; }

    [ObservableProperty] public partial ChoiceOption<string> SelectedCrop { get; set; }
    [ObservableProperty] public partial ChoiceOption<double> SelectedFadeIn { get; set; }
    [ObservableProperty] public partial ChoiceOption<double> SelectedFadeOut { get; set; }
    [ObservableProperty] public partial ChoiceOption<int> SelectedVolume { get; set; }
    [ObservableProperty] public partial ChoiceOption<int> SelectedAudioDelay { get; set; }
    [ObservableProperty] public partial bool KeepAllAudioTracks { get; set; }
    [ObservableProperty] public partial bool FlipHorizontal { get; set; }
    [ObservableProperty] public partial bool FlipVertical { get; set; }
    [ObservableProperty] public partial bool Deinterlace { get; set; }
    [ObservableProperty] public partial bool Denoise { get; set; }
    [ObservableProperty] public partial bool Reverse { get; set; }

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
    [ObservableProperty] public partial bool MeasureLoudness { get; set; }
    [ObservableProperty] public partial string TargetLoudnessText { get; set; } = "-16";
    [ObservableProperty] public partial string TruePeakText { get; set; } = "-1.5";
    [ObservableProperty] public partial bool TrimSilence { get; set; }
    [ObservableProperty] public partial string SilenceThresholdText { get; set; } = "-50";
    [ObservableProperty] public partial bool ToneMapToSdr { get; set; }

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

    /// <summary>Shown when queued HEIC/HEIF photos lack a reliable decoder (Windows HEIF codec or ImageMagick).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDecoderHint))]
    public partial string? DecoderHint { get; set; }

    [ObservableProperty]
    public partial string DecoderHintAction { get; set; } = "Open Tools";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConvertButtonText))]
    [NotifyPropertyChangedFor(nameof(CanConvert))]
    [NotifyPropertyChangedFor(nameof(CanCancelAll))]
    [NotifyPropertyChangedFor(nameof(TitleSubtitle))]
    public partial bool IsConverting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleSubtitle))]
    public partial string BatchStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRefreshingTools { get; set; }

    [ObservableProperty]
    public partial string EngineStatus { get; set; } = "Looking for FFmpeg…";

    [ObservableProperty]
    public partial string EngineDetail { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFfmpeg))]
    [NotifyPropertyChangedFor(nameof(HasMediaTools))]
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
    public bool IsAboutView => View == WorkspaceView.About;
    public bool HasFiles => Files.Count > 0;
    public bool HasNoFiles => Files.Count == 0;

    /// <summary>Short context shown next to the app name in the title bar.</summary>
    public string TitleSubtitle => View switch
    {
        WorkspaceView.History => "History",
        WorkspaceView.Tools => "Tools",
        WorkspaceView.About => "About",
        _ when IsConverting => BatchStatus,
        _ when Files.Count > 0 => $"{Files.Count} file{(Files.Count == 1 ? string.Empty : "s")} queued",
        _ => "Local file conversion"
    };
    public bool HasSelectedFile => SelectedFile is not null;
    public bool HasFormat => SelectedFormat is not null;
    public bool HasFfmpeg => Inventory.HasFfmpeg;
    public bool HasMediaTools => Inventory.HasMediaTools;
    public string ToolsDescription => Distribution.ToolsDescription;
    public string FfmpegSetupInstructions => Distribution.IsMacStoreBuild ? Distribution.BundledToolRepair : OperatingSystem.IsWindows()
        ? @"FFmpeg and FFprobe are required for media conversion and inspection. Use Install to open WinGet, or Website for manual setup. Keep both executables in C:\ffmpeg\bin, then select Refresh."
        : "FFmpeg and FFprobe are required for media conversion and inspection. Use Install to open your package manager, or Website for manual setup. Keep both executables on PATH, then select Refresh.";
    public bool HasHistory => History.Count > 0;
    public bool HasFailed => Files.Any(file => file.IsFailed || file.IsCanceled) && !IsConverting;
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);
    public bool HasDecoderHint => !string.IsNullOrWhiteSpace(DecoderHint);
    public bool HasCustomOutputDirectory => !string.IsNullOrWhiteSpace(OutputDirectory);
    public string OutputDirectoryLabel => HasCustomOutputDirectory ? OutputDirectory
        : Distribution.IsMacStoreBuild ? "Choose an authorized output folder" : "Next to each source file";
    public bool IsMacStoreBuild => Distribution.IsMacStoreBuild;
    [ObservableProperty] public partial string? BundledLicenseText { get; set; }
    public string OutputExtensionLabel => SelectedFormat?.Extension ?? string.Empty;
    public string QualityLabel => Quality >= 95 ? $"{Quality} · near lossless" : Quality >= 80 ? $"{Quality} · high" : Quality >= 60 ? $"{Quality} · balanced" : $"{Quality} · small file";
    public string TargetSizeLabel => UseTargetSize && double.TryParse(TargetSizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var megabytes) ? $"Fit in {megabytes:0.#} MB" : "Target file size";
    public string MaxParallelLabel => MaxParallel == 1 ? "1 at a time" : $"{MaxParallel} at a time";
    public string HardwareLabel => Inventory.Ffmpeg.WorkingHardwareEncoders.Count == 0
        ? "No hardware encoder detected"
        : UseHardwareEncoder ? $"Using {Inventory.Ffmpeg.HardwareSummary}" : $"{Inventory.Ffmpeg.HardwareSummary} available";

    private bool Supports(FormatFeatures feature) => !IsJoin && SelectedFormat?.Format.Supports(feature) == true;
    private bool UsesAppleSoftware => !IsJoin && Inventory.Ffmpeg.UsesAppleSoftwareEncoder(
        SelectedFormat?.Format.VideoCodec, UseHardwareEncoder && !(UseTargetSize && ShowTargetSize));
    public bool ShowVideoBitrate => UsesAppleSoftware && !(UseTargetSize && ShowTargetSize);
    public string TargetSizeHint => UsesAppleSoftware
        ? "Apple software encoding, with up to three bitrate-adjusted attempts. Oversized results are never committed."
        : "Two-pass encode sized to fit. Great for Discord (25 MB), email, and chat.";
    public bool ShowQuality => Supports(FormatFeatures.Quality) && !(UseTargetSize && ShowTargetSize) && !ShowVideoBitrate;
    public bool ShowResolution => Supports(FormatFeatures.Resolution);
    public bool ShowFrameRate => Supports(FormatFeatures.FrameRate);
    public bool ShowSpeed => Supports(FormatFeatures.EncodingSpeed) && !UsesAppleSoftware;
    public bool ShowAudio => Supports(FormatFeatures.AudioTuning) && !RemoveAudio;
    public bool ShowBitDepth => Supports(FormatFeatures.WavBitDepth);
    public bool ShowTargetSize => Supports(FormatFeatures.TargetSize);
    public bool ShowHardware => Supports(FormatFeatures.HardwareAccel) && Inventory.Ffmpeg.WorkingHardwareEncoders.Count > 0;
    public bool ShowRotate => Supports(FormatFeatures.Rotate);
    public bool ShowRemoveAudio => Supports(FormatFeatures.RemoveAudio);
    public bool ShowLossless => Supports(FormatFeatures.Lossless);
    public bool ShowPlaybackSpeed => Supports(FormatFeatures.PlaybackSpeed);
    public bool ShowStripMetadata => SelectedFormat?.Format.Engines.Contains(EngineKind.Ffmpeg) == true || SelectedFormat?.Format.Engines.Contains(EngineKind.ImageMagick) == true;
    private bool SelectedIsVideo => SelectedFile?.IsVideo == true;
    private bool BurnsSubtitles => SelectedSubtitleMode.Value == SubtitleMode.BurnIn && Supports(FormatFeatures.BurnSubtitles);
    public bool ShowAudioTrackPicker => Supports(FormatFeatures.AudioTracks) && SelectedFile?.HasMultipleAudioTracks == true && !(RemoveAudio && ShowRemoveAudio) && SelectedFile.HasReplacementAudio != true;
    public bool ShowKeepAllAudio => Supports(FormatFeatures.MultiAudio) && Files.Any(file => file.HasMultipleAudioTracks) && !(RemoveAudio && ShowRemoveAudio);
    public bool ShowReplaceAudio => Supports(FormatFeatures.MultiAudio) && SelectedIsVideo && !(RemoveAudio && ShowRemoveAudio);
    public bool ShowSubtitleMode => Supports(FormatFeatures.Subtitles) || Supports(FormatFeatures.BurnSubtitles);
    public bool ShowSubtitleTrackPicker => SelectedFile?.HasSubtitleTracks == true && (Supports(FormatFeatures.SubtitleTrack) || BurnsSubtitles && SelectedFile.HasExternalSubtitle != true);
    public bool ShowExternalSubtitle => SelectedIsVideo && ShowSubtitleMode && SelectedSubtitleMode.Value != SubtitleMode.Remove;
    public bool ShowTracksSection => ShowKeepAllAudio || ShowSubtitleMode;
    public bool ShowFileTracks => ShowAudioTrackPicker || ShowReplaceAudio || ShowSubtitleTrackPicker || ShowExternalSubtitle;
    public bool ShowVideoFilters => Supports(FormatFeatures.VideoFilters);
    public bool ShowEffects => Supports(FormatFeatures.Effects);
    public bool ShowEditSection => ShowVideoFilters || ShowEffects;
    public bool ShowTrimSilence => ShowAudio && SelectedFormat?.Format.Category == MediaCategory.Audio;
    public string SubtitleModeHint => SelectedSubtitleMode.Value switch
    {
        SubtitleMode.Keep => "Text subtitles become selectable tracks. MP4 and MOV skip picture-based (PGS/VobSub) tracks.",
        SubtitleMode.Remove => "Every subtitle track is dropped from the output.",
        SubtitleMode.BurnIn => "The chosen track (or added file) is drawn permanently into the picture. Requires a re-encode.",
        _ => "MKV keeps every subtitle track; other containers leave them out."
    };
    public bool ShowVideoSection => ShowResolution || ShowFrameRate || ShowSpeed || ShowQuality || ShowTargetSize || ShowVideoBitrate;
    public bool ShowAudioSection => ShowAudio || ShowBitDepth;
    public bool ShowAdvancedSection => ShowHardware || ShowRotate || ShowRemoveAudio || ShowLossless || ShowPlaybackSpeed || ShowStripMetadata;
    public bool ShowTrimControls => SelectedFile?.HasTimeline == true && Supports(FormatFeatures.Trim);
    public bool ShowFrameControls => SelectedFile?.IsVideo == true && Supports(FormatFeatures.FrameExtract);
    public bool CanConvert => HasFiles && HasFormat && SelectedFormat!.IsAvailable && !IsConverting && !service.Queue.IsRunning && !IsReadingFiles
        && (NeedsOutputAuthorization
            ? Files.Any(file => file.IsPending && file.Source is not null) : PreparedBatch.RunnableCount > 0);
    public bool CanCancelAll => IsConverting;
    public string ConvertButtonText
    {
        get
        {
            if (IsConverting) return "Converting…";
            if (IsReadingFiles) return "Reading files…";
            var pending = PreparedBatch.RunnableCount;
            if (IsJoin) return $"Join {PreparedBatch.Entries.FirstOrDefault()?.Inputs.Count ?? 0} clips";
            if (IsSplit) return $"Split {pending} file(s)";
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

        if (service.History.PersistenceError is { } historyError) ReportError(historyError);
    }

    private void ApplyTools(ToolInventory inventory)
    {
        Inventory = inventory;
        NotifyEncoderOptionsChanged();
        InvalidatePreflight();
        foreach (var tool in Tools) tool.Apply(inventory.Get(tool.Kind));
        if (Tools.FirstOrDefault(tool => tool.Kind == ToolKind.Ffmpeg) is { } ffmpegTool)
        {
            var filters = inventory.Ffmpeg.Filters;
            var extras = new List<string> { $"{inventory.Ffmpeg.Encoders.Count} encoders", inventory.Ffmpeg.HardwareSummary };
            if (filters.Contains("subtitles")) extras.Add("subtitle burn-in");
            if (filters.Contains("zscale") && filters.Contains("tonemap")) extras.Add("HDR to SDR");
            ffmpegTool.Extra = inventory.HasFfmpeg ? string.Join(" · ", extras) : null;
        }

        if (inventory.HasFfmpeg && !inventory.HasMediaTools)
        {
            EngineStatus = "FFprobe not found";
            EngineDetail = Distribution.IsMacStoreBuild ? Distribution.BundledToolRepair : "Install FFmpeg with FFprobe, then refresh Tools";
        }
        else if (inventory.HasFfmpeg)
        {
            var version = inventory.Get(ToolKind.Ffmpeg)!.ShortVersion;
            EngineStatus = $"FFmpeg {version}";
            var extras = Tools.Where(tool => tool.IsInstalled && tool.Kind != ToolKind.Ffmpeg).Select(tool => tool.Name).ToList();
            EngineDetail = string.Join(" · ", new[] { inventory.Ffmpeg.HardwareSummary }.Concat(extras));
        }
        else
        {
            EngineStatus = "FFmpeg not found";
            EngineDetail = Distribution.IsMacStoreBuild ? Distribution.BundledToolRepair : "Install FFmpeg to convert video and audio";
        }

        OnPropertyChanged(nameof(HasFfmpeg));
        OnPropertyChanged(nameof(ShowHardware));
        OnPropertyChanged(nameof(HardwareLabel));
        OnPropertyChanged(nameof(ShowAdvancedSection));
        RefreshFormats();
        UpdateCommandPreview();
        UpdateDecoderHint();

        _ = RefreshMissingMetadataAsync();
    }

    private async Task RefreshMissingMetadataAsync()
    {
        if (isClosing || refreshingMetadata || !Inventory.Has(ToolKind.Ffprobe)) return;
        refreshingMetadata = true;
        await importGate.WaitAsync();
        try
        {
            var files = Files.Where(file => file.IsPending && (file.Source is null ||
                file.Source.Media is null && file.Category is MediaCategory.Video or MediaCategory.Audio or MediaCategory.Image)).ToArray();
            if (files.Length == 0) return;
            using var cancellation = new CancellationTokenSource();
            importCancellation = cancellation;
            IsImporting = true;
            var summary = ImportSummary;
            ImportSummary = "Reading media details…";
            try
            {
                await Task.WhenAll(files.Select(file => InspectAsync(file, cancellation.Token)));
                ImportSummary = summary;
            }
            catch (OperationCanceledException) { ImportSummary = "Inspection canceled. Use Retry on files without details."; }
            catch (Exception ex) { ReportError($"Could not refresh file details: {ex.Message}"); }
            finally
            {
                importCancellation = null;
                OnFilesChanged();
                IsImporting = false;
            }
        }
        finally
        {
            refreshingMetadata = false;
            importGate.Release();
        }
    }

    // ---------------------------------------------------------------- files

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        try
        {
#if MACCATALYST
            if (Distribution.IsMacStoreBuild)
            {
                await AddPathsAsync(await Platforms.MacCatalyst.MacFileAccess.PickAsync(folder: false, multiple: true));
                return;
            }
#endif
            var results = await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = "Choose files to convert" });
            if (results is not null) await AddPathsAsync(results.Select(result => result.FullPath));
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
#if MACCATALYST
            if (Distribution.IsMacStoreBuild)
            {
                await AddPathsAsync(await Platforms.MacCatalyst.MacFileAccess.PickAsync(folder: true, multiple: false));
                return;
            }
#endif
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
        if (isClosing) return;
        var requested = paths.ToArray();
        await importGate.WaitAsync();
        if (isClosing) { importGate.Release(); return; }
        using var cancellation = new CancellationTokenSource();
        importCancellation = cancellation;
        IsImporting = true;
        ImportSummary = "Scanning files…";
        var added = new List<FileItemViewModel>();
        try
        {
            using var access = await FileAccessService.RequestAsync(requested);
            if (access is null) { ImportSummary = "Import canceled. No files were added."; return; }
            Func<string, string>? canonicalize = null;
#if MACCATALYST
            if (Distribution.IsMacStoreBuild) canonicalize = Platforms.MacCatalyst.MacFileAccess.CanonicalPath;
#endif
            var imported = await FileImport.ScanAsync(requested, Files.Select(file => file.Path), cancellation.Token, canonicalize);
            addingFiles = true;
            foreach (var path in imported.AlreadyQueued)
            {
                RetainWorkspaceAccess(path);
                if (Files.FirstOrDefault(file => string.Equals(file.Path, path, PathComparison)) is { IsTerminal: true } previous)
                    previous.ResetForRetry();
            }
            foreach (var path in imported.Paths)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                RetainWorkspaceAccess(path);
                var item = new FileItemViewModel(path);
                item.RemoveRequested += RemoveFile;
                item.SelectRequested += file => SelectedFile = file;
                item.RetryRequested += file => InspectAsync(file);
                item.MoveRequested += MoveFile;
                item.FeedbackRequested += ReportError;
                item.PropertyChanged += OnFilePropertyChanged;
                Files.Add(item);
                added.Add(item);
                SelectedFile ??= item;
                if (added.Count % 50 == 0) await Task.Yield();
            }
            addingFiles = false;
            OnFilesChanged();
            if (Distribution.IsMacStoreBuild) OnPropertyChanged(nameof(SelectedFile));
            ImportSummary = imported.Summary;
            View = WorkspaceView.Convert;
            await Task.WhenAll(added.Select(file => InspectAsync(file, cancellation.Token)));
        }
        catch (OperationCanceledException)
        {
            foreach (var file in added.Where(file => file.Source is null && file.IsInspecting))
                file.MarkInspectionFailed("Import canceled. Use Retry to inspect this file.");
            ImportSummary = "Import canceled. Files already added remain in the queue.";
        }
        catch (Exception ex) { ReportError($"Could not import files: {ex.Message}"); }
        finally
        {
            addingFiles = false;
            OnFilesChanged();
            IsImporting = false;
            OnPropertyChanged(nameof(CanConvert));
            importCancellation = null;
            importGate.Release();
        }
    }

    [RelayCommand]
    private void CancelImport() => importCancellation?.Cancel();

    public void ReportError(string message)
    {
        LocalMorph.Core.Diagnostics.DiagnosticLog.Warn(message);
        ValidationMessage = message;
        ShowToast(message);
    }

    private async Task InspectAsync(FileItemViewModel item, CancellationToken token = default)
    {
        if (isClosing) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, inspectionCancellation.Token);
        token = linked.Token;
        inspectionsPending++;
        InvalidatePreflight();
        var acquired = false;
        try
        {
            await inspectionGate.WaitAsync(token);
            acquired = true;
            item.IsInspecting = true;
            using var access = FileAccessService.Acquire(item.Path);
            var source = await SourceInspector.InspectAsync(item.Path, Inventory, token);
            token.ThrowIfCancellationRequested();
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (isClosing || !Files.Contains(item)) return;
                item.ApplySource(source);
                if (ReferenceEquals(item, SelectedFile)) UpdateCommandPreview();
            });
        }
        catch (OperationCanceledException)
        {
            await MainThread.InvokeOnMainThreadAsync(() => item.MarkInspectionFailed("Inspection canceled. Use Retry to inspect this file."));
            if (isClosing) return;
            throw;
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(() => item.MarkInspectionFailed($"Could not inspect: {ex.Message}"));
        }
        finally
        {
            if (acquired) inspectionGate.Release();
            inspectionsPending--;
            if (inspectionsPending == 0) inspectionsStopped?.TrySetResult();
            if (!IsReadingFiles && !isClosing)
            {
                RefreshFormats();
                InvalidatePreflight();
                OnPropertyChanged(nameof(FilesSummary));
            }
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
            SelectedFile = addingFiles || Files.Count == 0 ? null : Files[Math.Clamp(index, 0, Files.Count - 1)];
        }
    }

    private void BatchFileChanges(Action update)
    {
        addingFiles = true;
        try { update(); }
        finally
        {
            addingFiles = false;
            SelectedFile ??= Files.FirstOrDefault();
            OnFilesChanged();
        }
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        if (IsReadingFiles) { ReportError("Wait for file inspection or cancel the import before clearing files."); return; }
        BatchFileChanges(() => { foreach (var item in Files.Where(file => file.IsCompleted).ToArray()) RemoveFile(item); });
        service.Queue.ClearFinished();
    }

    [RelayCommand]
    private async Task ClearAllAsync()
    {
        if (IsConverting && !await Shell.Current.DisplayAlertAsync("Cancel and clear queue?", "Running conversions will be canceled. Source files and completed results will not be deleted.", "Cancel and clear", "Keep converting")) return;
        if (IsReadingFiles) { ReportError("Wait for file inspection or cancel the import before clearing files."); return; }
        service.Queue.CancelAll();
        BatchFileChanges(() => { foreach (var item in Files.ToArray()) RemoveFile(item); });
        service.Queue.ClearFinished();
    }

    [RelayCommand]
    private void RetryFailed()
    {
        if (IsReadingFiles) { ReportError("Wait for file inspection or cancel the import before retrying files."); return; }
        BatchFileChanges(() => { foreach (var item in Files.Where(file => file.IsFailed || file.IsCanceled)) item.ResetForRetry(); });
    }

    private void OnFilesChanged()
    {
        PruneWorkspaceAccess();
        InvalidatePreflight();
        for (var index = 0; index < Files.Count; index++) { Files[index].QueueIndex = index; Files[index].QueueCount = Files.Count; }
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasNoFiles));
        OnPropertyChanged(nameof(ShowImportStatus));
        OnPropertyChanged(nameof(TitleSubtitle));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(FilesSummary));
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(ConvertButtonText));
        RefreshFormats();
        UpdateBatchStatus();
        UpdateDecoderHint();
        OnTracksChanged();
    }

    private ToolKind? missingDecoder;

    /// <summary>HEIC/HEIF photos decode through the Windows HEIF codec (or ImageMagick); FFmpeg alone is unreliable for them.</summary>
    private void UpdateDecoderHint()
    {
        missingDecoder = Files
            .SelectMany(file => FormatCatalog.MissingDecodersFor(file.Path, Inventory))
            .Cast<ToolKind?>()
            .FirstOrDefault();

        if (missingDecoder is not { } kind)
        {
            DecoderHint = null;
            return;
        }

        var count = Files.Count(file => PlatformImageCodec.IsHeif(file.Path));
        var photos = count == 1 ? "This HEIC photo needs" : $"{count} HEIC photos need";
        if (Distribution.IsMacStoreBuild)
        {
            DecoderHint = $"{photos} a decoder not included in this media bundle. External tool installs cannot extend this build.";
            DecoderHintAction = "Review bundled tools";
            return;
        }
        if (kind == ToolKind.WindowsHeif)
        {
            DecoderHint = $"{photos} the free Windows HEIF Image Extensions codec to convert reliably (or ImageMagick).";
            DecoderHintAction = "Get from Store";
        }
        else
        {
            DecoderHint = $"{photos} {ToolCatalog.Get(kind).DisplayName} to convert reliably.";
            DecoderHintAction = "Open Tools";
        }
    }

    [RelayCommand]
    private void ResolveDecoderHint()
    {
        if (missingDecoder == ToolKind.WindowsHeif && PlatformActions.LaunchInstaller(ToolKind.WindowsHeif))
        {
            ShowToast("Install the codec, then refresh Tools.");
            return;
        }

        View = WorkspaceView.Tools;
    }

    private void OnFilePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileItemViewModel.State) or nameof(FileItemViewModel.Source) or nameof(FileItemViewModel.TrimStartSeconds)
            or nameof(FileItemViewModel.TrimEndSeconds) or nameof(FileItemViewModel.SelectedAudioTrack) or nameof(FileItemViewModel.SelectedSubtitleTrack)
            or nameof(FileItemViewModel.ReplacementAudioPath) or nameof(FileItemViewModel.ExternalSubtitlePath))
            InvalidatePreflight();
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
            case nameof(FileItemViewModel.SelectedAudioTrack):
            case nameof(FileItemViewModel.SelectedSubtitleTrack):
                if (ReferenceEquals(sender, SelectedFile)) UpdateCommandPreview();
                break;
            case nameof(FileItemViewModel.ReplacementAudioPath):
            case nameof(FileItemViewModel.ExternalSubtitlePath):
                PruneWorkspaceAccess();
                OnTracksChanged();
                if (ReferenceEquals(sender, SelectedFile)) UpdateCommandPreview();
                break;
            case nameof(FileItemViewModel.AudioTrackChoices):
            case nameof(FileItemViewModel.SubtitleTrackChoices):
                OnTracksChanged();
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
        var jobs = Files.Select(file => file.Job).OfType<ConversionJob>().Distinct().ToArray();
        var total = jobs.Length;
        if (total == 0)
        {
            BatchStatus = string.Empty;
            return;
        }

        var done = jobs.Count(job => job.State == JobState.Completed);
        var failed = jobs.Count(job => job.State == JobState.Failed);
        var running = jobs.Count(job => job.IsActive);
        if (IsConverting)
        {
            var overall = jobs.Average(job => job.State == JobState.Completed ? 1 : job.Progress);
            BatchStatus = $"{done} of {total} done · {overall * 100:0}% overall · {running} running";
        }
        else
        {
            var canceled = jobs.Count(job => job.State == JobState.Canceled);
            var skipped = jobs.Count(job => job.State == JobState.Skipped);
            BatchStatus = $"{done} completed · {failed} failed · {canceled} canceled · {skipped} skipped · {jobs.Sum(job => job.CompletedOutputs.Count)} result(s)";
        }
    }

    private async Task OnQueueDrainedAsync()
    {
        if (isClosing) return;
        await service.Queue.WaitForIdleAsync();
        if (isClosing) return;
        IsConverting = Files.Any(file => file.IsRunning || file.IsQueued);
        OnPropertyChanged(nameof(CanConvert));
        OnPropertyChanged(nameof(ConvertButtonText));
        OnPropertyChanged(nameof(HasFailed));
        UpdateBatchStatus();
        var done = Files.Count(file => file.IsCompleted);
        var failed = Files.Count(file => file.IsFailed);
        ShowToast(BatchStatus);

        if (OpenFolderWhenDone && Files.FirstOrDefault(file => file.IsCompleted && file.OutputPath is not null) is { OutputPath: { } output })
        {
            if (!await PlatformActions.RevealInFolderAsync(output)) ReportError("Could not open the completed results folder.");
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
        var built = presets.Select(preset =>
        {
            var option = all.FirstOrDefault(candidate => candidate.Id == preset.FormatId);
            var available = option?.IsAvailable == true;
            var description = preset.Options.TargetSizeMegabytes is not null
                && Inventory.Ffmpeg.UsesAppleSoftwareEncoder(preset.Format.VideoCodec, useHardware: false)
                ? "Apple software encoding with up to three bitrate-adjusted attempts; oversized results are rejected."
                : null;
            return new PresetOption(preset, available, available ? null : option is null ? "Not valid for this mix of files" : option.EngineLabel, description);
        });
        // Usable presets lead; unavailable ones stay visible (with their reason) at the end.
        foreach (var preset in built.OrderBy(preset => preset.IsAvailable ? 0 : 1)) PresetOptions.Add(preset);
        OnPropertyChanged(nameof(HasPresets));
        OnPropertyChanged(nameof(ApplicabilityLabel));
    }

    public bool HasPresets => !IsJoin && PresetOptions.Count > 0;

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

    private static int GroupOrder(string label) => label switch { "Video" => 0, "Audio" => 1, "Image" => 2, "Document" => 3, "Subtitles" => 4, _ => 5 };

    private void OnTracksChanged()
    {
        OnPropertyChanged(nameof(ShowAudioTrackPicker));
        OnPropertyChanged(nameof(ShowKeepAllAudio));
        OnPropertyChanged(nameof(ShowReplaceAudio));
        OnPropertyChanged(nameof(ShowSubtitleTrackPicker));
        OnPropertyChanged(nameof(ShowExternalSubtitle));
        OnPropertyChanged(nameof(ShowTracksSection));
        OnPropertyChanged(nameof(ShowFileTracks));
    }

    // ---------------------------------------------------------------- tracks

    private static readonly FilePickerFileType AudioFileTypes = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.WinUI] = [".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".wma", ".aif", ".aiff", ".ac3", ".mka"],
        [DevicePlatform.MacCatalyst] = ["public.audio"],
        [DevicePlatform.Android] = ["audio/*"]
    });

    private static readonly FilePickerFileType SubtitleFileTypes = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.WinUI] = [".srt", ".vtt", ".ass", ".ssa"],
        [DevicePlatform.MacCatalyst] = ["public.item"],
        [DevicePlatform.Android] = ["*/*"]
    });

    [RelayCommand]
    private async Task PickReplacementAudioAsync()
    {
        if (SelectedFile is not { } file) return;
        try
        {
            var path = await PickAttachmentAsync("Choose the new soundtrack", AudioFileTypes);
            if (path is null || !Files.Contains(file)) return;
            if (SourceClassifier.Classify(path) is not (MediaCategory.Audio or MediaCategory.Video))
            {
                ShowToast($"{Path.GetFileName(path)} isn't an audio file.");
                return;
            }
            RetainWorkspaceAccess(path);
            file.ReplacementAudioPath = path;
        }
        catch (Exception ex)
        {
            ShowToast($"Could not open the file picker: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task PickExternalSubtitleAsync()
    {
        if (SelectedFile is not { } file) return;
        try
        {
            var path = await PickAttachmentAsync("Choose a subtitle file", SubtitleFileTypes);
            if (path is null || !Files.Contains(file)) return;
            if (SourceClassifier.Classify(path) != MediaCategory.Subtitle)
            {
                ShowToast("Subtitle files must be .srt, .vtt, .ass, or .ssa.");
                return;
            }
            RetainWorkspaceAccess(path);
            file.ExternalSubtitlePath = path;
        }
        catch (Exception ex)
        {
            ShowToast($"Could not open the file picker: {ex.Message}");
        }
    }

    private static async Task<string?> PickAttachmentAsync(string title, FilePickerFileType types)
    {
#if MACCATALYST
        if (Distribution.IsMacStoreBuild)
            return (await Platforms.MacCatalyst.MacFileAccess.PickAsync(folder: false, multiple: false)).FirstOrDefault();
#endif
        return (await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = title, FileTypes = types }))?.FullPath;
    }

    private void RetainWorkspaceAccess(string path)
    {
        if (!Distribution.IsMacStoreBuild) return;
        var access = FileAccessService.Acquire(path);
        workspaceAccess.TryGetValue(path, out var previous);
        workspaceAccess[path] = access;
        previous?.Dispose();
    }

    private void PruneWorkspaceAccess()
    {
        var needed = Files.SelectMany(file => new[] { file.Path, file.ReplacementAudioPath, file.ExternalSubtitlePath })
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var path in workspaceAccess.Keys.Where(path => !needed.Contains(path)).ToArray())
        {
            workspaceAccess[path].Dispose();
            workspaceAccess.Remove(path);
        }
    }

    public Task CloseAsync() => closing ??= CloseCoreAsync();

    private async Task CloseCoreAsync()
    {
        isClosing = true;
        inspectionsStopped = inspectionsPending == 0 ? null : new(TaskCreationOptions.RunContinuationsAsynchronously);
        importCancellation?.Cancel();
        inspectionCancellation.Cancel();
        service.Queue.CancelAll();
        await Task.WhenAll(service.Queue.WaitForIdleAsync(), inspectionsStopped?.Task ?? Task.CompletedTask);
        await importGate.WaitAsync();
        try
        {
            foreach (var access in workspaceAccess.Values) access.Dispose();
            workspaceAccess.Clear();
            outputAccess?.Dispose();
            outputAccess = null;
        }
        finally { importGate.Release(); }
    }

    public async Task ResumeAsync()
    {
        if (closing is null) return;
        await closing;
        closing = null;
        inspectionCancellation.Dispose();
        inspectionCancellation = new();
        inspectionsStopped = null;
        isClosing = false;
        var failures = new List<string>();
        foreach (var path in Files.SelectMany(file => new[] { file.Path, file.ReplacementAudioPath, file.ExternalSubtitlePath })
            .OfType<string>().Distinct(StringComparer.Ordinal))
        {
            try { RetainWorkspaceAccess(path); }
            catch (Exception ex) { failures.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        IsConverting = service.Queue.IsRunning;
        OnFilesChanged();
        UpdateCommandPreview();
        if (failures.Count > 0) ReportError($"{failures.Count} workspace item(s) need to be selected again. {string.Join("; ", failures.Take(3))}");
    }

    [RelayCommand]
    private async Task ForgetFilePermissionsAsync()
    {
        if (!Distribution.IsMacStoreBuild) return;
        if (IsReadingFiles || IsConverting || service.Queue.IsRunning)
        {
            ReportError("Wait for inspection and conversions to finish before forgetting permissions.");
            return;
        }
        if (!await Shell.Current.DisplayAlertAsync("Forget file permissions?",
            "Clear the workspace and saved file/folder permissions. Your files and history will not be deleted. Select files and an output folder again to authorize access.", "Forget", "Cancel")) return;
        if (IsReadingFiles || IsConverting || service.Queue.IsRunning) { ReportError("Work started while confirming. Wait before forgetting permissions."); return; }
        try
        {
            BatchFileChanges(() => { foreach (var file in Files.ToArray()) RemoveFile(file); });
            ResetOutputDirectory();
#if MACCATALYST
            Platforms.MacCatalyst.MacFileAccess.Forget();
#endif
            ShowToast("Saved file permissions forgotten.");
        }
        catch (Exception ex) { ReportError($"Could not forget file permissions: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task CopyDiagnosticLogAsync()
    {
        var text = LocalMorph.Core.Diagnostics.DiagnosticLog.ReadAll();
        if (text.Length == 0) { ShowToast("The diagnostic log is empty."); return; }
        await Clipboard.Default.SetTextAsync(text);
        ShowToast("Diagnostic log copied. It includes file names and paths; review before sharing.");
    }

    [RelayCommand]
    private async Task ShowDiagnosticLogAsync()
    {
        var path = LocalMorph.Core.Diagnostics.DiagnosticLog.FilePath;
        if (path is null) { ReportError("The diagnostic log is unavailable on this device."); return; }
        if (!File.Exists(path)) LocalMorph.Core.Diagnostics.DiagnosticLog.Info("Diagnostic log opened.");
        if (!await PlatformActions.RevealOwnFileAsync(path)) ReportError($"Could not show the log. It is at {path}");
    }

    [RelayCommand]
    private async Task ShowBundledLicensesAsync()
    {
        if (!Distribution.IsMacStoreBuild) return;
        try
        {
            string? directory = null;
#if MACCATALYST
            directory = Path.Combine(Foundation.NSBundle.MainBundle.BundlePath, "Contents", "Resources", "FFmpeg");
#endif
            if (directory is null) throw new PlatformNotSupportedException("Bundled Mac media notices require Mac Catalyst.");
            var thirdParty = Path.Combine(directory, "..", "ThirdPartyNotices.txt");
            var componentLicenses = Path.Combine(directory, "licenses");
            var paths = (File.Exists(thirdParty) ? new[] { thirdParty } : [])
                .Concat(new[] { "FFmpeg-proof-NOTICE.txt", "LICENSE.md", "COPYING.LGPLv2.1" }.Select(name => Path.Combine(directory, name)))
                .Concat(Directory.Exists(componentLicenses)
                    ? Directory.GetFiles(componentLicenses).Order(StringComparer.Ordinal)
                    : []);
            BundledLicenseText = string.Join("\n\n", await Task.WhenAll(paths.Select(async path =>
                $"── {Path.GetFileName(path)} ──\n{await File.ReadAllTextAsync(path)}")));
        }
        catch (Exception ex) { ReportError($"Bundled media notices could not be read. Repair or update this build: {ex.Message}"); }
    }

    partial void OnSelectedFormatChanged(FormatOption? value)
    {
        InvalidatePreflight();
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
        if (!value.Format.Supports(FormatFeatures.BurnSubtitles) && SelectedSubtitleMode.Value == SubtitleMode.BurnIn) SelectedSubtitleMode = SubtitleModeOptions[0];
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
            ApplyOptions(preset.Options);
            foreach (var candidate in PresetOptions) candidate.IsActive = ReferenceEquals(candidate, option);
        }
        finally
        {
            applyingPreset = false;
        }

        UpdateCommandPreview();
        ShowToast($"Preset applied: {preset.Name}");
    }

    private void ApplyOptions(ConversionOptions options)
    {
        Quality = options.Quality;
        VideoBitrateText = (options.VideoBitrateKbps ?? 4000).ToString(CultureInfo.InvariantCulture);
        SelectedResolution = ResolutionOptions.FirstOrDefault(choice => choice.Value == options.TargetHeight) ?? ResolutionOptions[0];
        SelectedFrameRate = FrameRateOptions.FirstOrDefault(choice => choice.Value == options.FrameRate) ?? FrameRateOptions[0];
        SelectedSpeed = SpeedOptions.First(choice => choice.Value == options.Speed);
        SelectedAudioBitrate = AudioBitrateOptions.FirstOrDefault(choice => choice.Value == options.AudioBitrateKbps) ?? AudioBitrateOptions[0];
        SelectedSampleRate = SampleRateOptions.FirstOrDefault(choice => choice.Value == options.SampleRate) ?? SampleRateOptions[0];
        SelectedChannels = ChannelOptions.First(choice => choice.Value == options.Channels);
        SelectedBitDepth = BitDepthOptions.First(choice => choice.Value == options.WavBitDepth);
        SelectedRotation = RotationOptions.First(choice => choice.Value == options.Rotation);
        SelectedPlaybackSpeed = PlaybackSpeedOptions.First(choice => choice.Value == options.PlaybackSpeed);
        UseTargetSize = options.TargetSizeMegabytes is not null;
        if (options.TargetSizeMegabytes is { } megabytes) TargetSizeText = megabytes.ToString("0.###", CultureInfo.InvariantCulture);
        UseHardwareEncoder = options.UseHardwareEncoder;
        RemoveAudio = options.RemoveAudio;
        StripMetadata = options.StripMetadata;
        Lossless = options.Lossless;
        NormalizeAudio = options.NormalizeAudio;
        MeasureLoudness = options.MeasureLoudness;
        TargetLoudnessText = options.TargetLoudness.ToString(CultureInfo.InvariantCulture);
        TruePeakText = options.TruePeak.ToString(CultureInfo.InvariantCulture);
        TrimSilence = options.TrimSilence;
        SilenceThresholdText = options.SilenceThresholdDb.ToString(CultureInfo.InvariantCulture);
        ToneMapToSdr = options.ToneMapToSdr;
        SelectedCrop = CropOptions.FirstOrDefault(choice => choice.Value == (options.CropAspect ?? CropAspects.None)) ?? CropOptions[0];
        SelectedSubtitleMode = SubtitleModeOptions.First(choice => choice.Value == options.Subtitles);
        SelectedFadeIn = FadeOptions.First(choice => choice.Value == options.FadeInSeconds);
        SelectedFadeOut = FadeOptions.First(choice => choice.Value == options.FadeOutSeconds);
        SelectedVolume = VolumeOptions.First(choice => choice.Value == options.VolumePercent);
        SelectedAudioDelay = AudioDelayOptions.First(choice => choice.Value == options.AudioDelayMilliseconds);
        KeepAllAudioTracks = options.KeepAllAudioTracks;
        FlipHorizontal = options.FlipHorizontal;
        FlipVertical = options.FlipVertical;
        Deinterlace = options.Deinterlace;
        Denoise = options.Denoise;
        Reverse = options.Reverse;
    }

    // ---------------------------------------------------------------- settings reactions

    partial void OnQualityChanged(int value) => SettingsTouched();
    partial void OnVideoBitrateTextChanged(string value) => SettingsTouched();
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
    partial void OnMeasureLoudnessChanged(bool value) => SettingsTouched();
    partial void OnTargetLoudnessTextChanged(string value) => SettingsTouched();
    partial void OnTruePeakTextChanged(string value) => SettingsTouched();
    partial void OnTrimSilenceChanged(bool value) => SettingsTouched();
    partial void OnSilenceThresholdTextChanged(string value) => SettingsTouched();
    partial void OnToneMapToSdrChanged(bool value) => SettingsTouched();
    partial void OnSelectedSubtitleModeChanged(ChoiceOption<SubtitleMode> value) => SettingsTouched();
    partial void OnSelectedCropChanged(ChoiceOption<string> value) => SettingsTouched();
    partial void OnSelectedFadeInChanged(ChoiceOption<double> value) => SettingsTouched();
    partial void OnSelectedFadeOutChanged(ChoiceOption<double> value) => SettingsTouched();
    partial void OnSelectedVolumeChanged(ChoiceOption<int> value) => SettingsTouched();
    partial void OnSelectedAudioDelayChanged(ChoiceOption<int> value) => SettingsTouched();
    partial void OnKeepAllAudioTracksChanged(bool value) => SettingsTouched();
    partial void OnFlipHorizontalChanged(bool value) => SettingsTouched();
    partial void OnFlipVerticalChanged(bool value) => SettingsTouched();
    partial void OnDeinterlaceChanged(bool value) => SettingsTouched();
    partial void OnDenoiseChanged(bool value) => SettingsTouched();
    partial void OnReverseChanged(bool value) => SettingsTouched();
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
        OnTracksChanged();
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

    partial void OnSelectedOverwritePolicyChanged(ChoiceOption<OverwritePolicy> value)
    {
        settings.OverwritePolicy = value.Value;
        InvalidatePreflight();
    }
    partial void OnOutputDirectoryChanged(string value) => InvalidatePreflight();

    partial void OnMaxParallelChanged(int value)
    {
        settings.MaxParallel = value;
        service.ApplySettings();
    }

    partial void OnOpenFolderWhenDoneChanged(bool value) => settings.OpenFolderWhenDone = value;

    private void SettingsTouched()
    {
        NotifyEncoderOptionsChanged();
        InvalidatePreflight();
        if (!applyingPreset)
        {
            foreach (var preset in PresetOptions) preset.IsActive = false;
        }
        ValidationMessage = null;
        UpdateCommandPreview();
    }

    private void NotifyEncoderOptionsChanged()
    {
        OnPropertyChanged(nameof(ShowVideoBitrate));
        OnPropertyChanged(nameof(ShowQuality));
        OnPropertyChanged(nameof(ShowSpeed));
        OnPropertyChanged(nameof(ShowVideoSection));
        OnPropertyChanged(nameof(TargetSizeHint));
    }

    private ConversionOptions BuildSharedOptions()
    {
        if (IsJoin) return new ConversionOptions { StripMetadata = StripMetadata };
        // An enabled-but-unparseable target size becomes a negative sentinel so Validate() rejects it
        // instead of silently falling back to quality mode.
        double? targetSize = !(UseTargetSize && ShowTargetSize) ? null
            : double.TryParse(TargetSizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var megabytes) && megabytes > 0 ? megabytes
            : -1;

        return new ConversionOptions
        {
            Quality = Quality,
            Speed = SelectedSpeed.Value,
            TargetHeight = SelectedResolution.Value,
            FrameRate = SelectedFrameRate.Value,
            AudioBitrateKbps = SelectedAudioBitrate.Value,
            VideoBitrateKbps = ShowVideoBitrate
                ? int.TryParse(VideoBitrateText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var videoBitrate) ? videoBitrate : -1
                : null,
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
            NormalizeAudio = NormalizeAudio,
            MeasureLoudness = MeasureLoudness && ShowAudio,
            TargetLoudness = MeasureLoudness && ShowAudio ? ParseNumber(TargetLoudnessText) : -16,
            TruePeak = MeasureLoudness && ShowAudio ? ParseNumber(TruePeakText) : -1.5,
            TrimSilence = TrimSilence && ShowTrimSilence,
            SilenceThresholdDb = TrimSilence && ShowTrimSilence ? ParseNumber(SilenceThresholdText) : -50,
            ToneMapToSdr = ToneMapToSdr && ShowVideoFilters,
            KeepAllAudioTracks = KeepAllAudioTracks,
            Subtitles = SelectedSubtitleMode.Value,
            CropAspect = string.IsNullOrEmpty(SelectedCrop.Value) ? null : SelectedCrop.Value,
            FlipHorizontal = FlipHorizontal,
            FlipVertical = FlipVertical,
            Deinterlace = Deinterlace,
            Denoise = Denoise,
            Reverse = Reverse,
            FadeInSeconds = SelectedFadeIn.Value,
            FadeOutSeconds = SelectedFadeOut.Value,
            VolumePercent = SelectedVolume.Value,
            AudioDelayMilliseconds = SelectedAudioDelay.Value
        };
    }

    private static double ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;

    private void UpdateCommandPreview()
    {
        if (SelectedFile?.Source is not { } source || SelectedFormat is not { } format)
        {
            CommandPreview = "Add a file and choose a format to see the exact command LocalMorph will run.";
            return;
        }

        if (NeedsOutputAuthorization)
        {
            CommandPreview = "Authorize an output folder to preview the final conversion command.";
            return;
        }
        var engine = FormatCatalog.ResolveEngine(format.Format, source.Path, Inventory);
        if (engine is null)
        {
            CommandPreview = Distribution.IsMacStoreBuild ? $"{format.DisplayName} is unavailable in this bundled-tool configuration."
                : $"{format.DisplayName} needs {string.Join(" or ", format.MissingTools.Select(kind => ToolCatalog.Get(kind).DisplayName))}. Open Tools to install it.";
            return;
        }

        try
        {
            var options = SelectedFile.ApplyPerFileOptions(BuildSharedOptions(), format.Format);
            if (IsJoin)
            {
                CommandPreview = "Check all pending clips for matching stream signatures, write their queue order to a private concat list, then copy the streams into one result. Chapters are excluded.";
                return;
            }
            if (IsSplit)
            {
                CommandPreview = $"Plan {Operation.SegmentSeconds:0.###}-second clips in {Operation.SplitMode} mode, then commit each completed clip separately. Fast boundaries come from source keyframes; resolved commands appear in diagnostics.";
                return;
            }
            if (options.MeasureLoudness || options.TrimSilence)
            {
                CommandPreview = (options.TrimSilence ? "1. Detect leading/trailing silence (internal pauses are kept).\n" : "")
                    + (options.MeasureLoudness ? $"Measure each retained audio track at {options.TargetLoudness:0.#} LUFS / {options.TruePeak:0.#} dBTP.\n" : "")
                    + "Render using the measured values, then safely commit the result. Copy diagnostics after conversion for the resolved commands.";
                return;
            }
            var outputPath = Path.Combine(HasCustomOutputDirectory ? OutputDirectory : Path.GetDirectoryName(source.Path) ?? string.Empty, OutputNaming.Sanitize(Path.GetFileNameWithoutExtension(source.Path) + OutputSuffix) + format.Extension);
            var job = new ConversionJob(source, format.Format, options, outputPath, engine.Value);
            var plan = EngineRegistry.Get(engine.Value).Plan(job, Inventory, Path.Combine(FileSystem.Current.CacheDirectory, "work"));
            CommandPreview = plan.Describe();
            try { plan.Cleanup?.Invoke(); } catch { }
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
#if MACCATALYST
            if (Distribution.IsMacStoreBuild)
            {
                var path = (await Platforms.MacCatalyst.MacFileAccess.PickAsync(folder: true, multiple: false)).FirstOrDefault();
                if (path is null) return;
                var access = FileAccessService.Acquire(path, writeDirectory: true);
                outputAccess?.Dispose();
                outputAccess = access;
                OutputDirectory = path;
                settings.OutputDirectory = path;
                UpdateCommandPreview();
                return;
            }
#endif
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
        outputAccess?.Dispose();
        outputAccess = null;
        OutputDirectory = string.Empty;
        settings.OutputDirectory = string.Empty;
        UpdateCommandPreview();
    }

    [RelayCommand]
    private async Task OpenOutputDirectoryAsync()
    {
        var target = HasCustomOutputDirectory ? OutputDirectory : Files.FirstOrDefault(file => file.OutputPath is not null)?.OutputPath ?? SelectedFile?.Path;
        if (target is not null && !await PlatformActions.RevealInFolderAsync(target)) ReportError("Could not open the output folder.");
    }

    // ---------------------------------------------------------------- conversion

    [RelayCommand]
    private async Task ConvertAllAsync()
    {
        if (isClosing || IsReadingFiles || IsConverting || service.Queue.IsRunning) return;
        var requestLifetime = inspectionCancellation.Token;
        if (SelectedFormat is not { } format) { ReportError("Choose an output format first."); return; }
        if (Distribution.IsMacStoreBuild)
        {
            try
            {
                if (HasCustomOutputDirectory && outputAccess is null)
                {
                    try { outputAccess = FileAccessService.Acquire(OutputDirectory, writeDirectory: true); }
                    catch (UnauthorizedAccessException)
                    {
                        if (!await Shell.Current.DisplayAlertAsync("Authorize output folder",
                            "Choose the folder to use for results. A saved path alone does not grant write access.", "Choose folder", "Cancel")) return;
                    }
                }
                if (outputAccess is null) await PickOutputDirectoryAsync();
                if (outputAccess is null) return;
            }
            catch (Exception ex) { ReportError($"Could not authorize the output folder: {ex.Message}"); return; }
            if (isClosing || requestLifetime.IsCancellationRequested) return;
            if (IsReadingFiles || IsConverting) { ReportError("The workspace changed while choosing a folder. Try converting again."); return; }
            format = SelectedFormat ?? format;
            InvalidatePreflight();
            UpdateCommandPreview();
        }
        if (HasCustomOutputDirectory && !Directory.Exists(OutputDirectory))
        {
            ReportError("The output folder no longer exists. Choose another one.");
            return;
        }
        preflight = null;
        var plan = PreparedBatch;
        var protectedPaths = Files.Select(file => file.Path).ToArray();
        ValidationMessage = null;
        var queued = 0;
        foreach (var entry in plan.Entries)
        {
            var job = new ConversionJob(entry.Inputs[0], format.Format, entry.Options, entry.OutputPath, entry.Engine)
            { Inputs = entry.Inputs, Operation = Operation, OverwritePolicy = SelectedOverwritePolicy.Value, ProtectedInputPaths = protectedPaths };
            foreach (var source in entry.Inputs)
                Files.First(file => string.Equals(file.Path, source.Path, PathComparison)).AttachJob(job);
            if (entry.Issue is { } issue) { job.Skip("Skipped · " + issue); continue; }
            service.Queue.Enqueue(job);
            queued++;
        }

        if (queued == 0)
        {
            ReportError("Nothing is ready to run. Review the preflight summary.");
            return;
        }

        service.Queue.MaxParallel = MaxParallel;
        service.Queue.Start();
        IsConverting = true;
        UpdateBatchStatus();
    }

    private void MoveFile(FileItemViewModel item, int offset)
    {
        if (IsConverting) { ReportError("Wait for the current batch before reordering inputs."); return; }
        var index = Files.IndexOf(item);
        var target = index + offset;
        if (index >= 0 && target >= 0 && target < Files.Count) Files.Move(index, target);
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
    [RelayCommand] private void ShowAbout() => View = WorkspaceView.About;
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
            ShowToast(Inventory.HasMediaTools ? "Tools refreshed." : Distribution.IsMacStoreBuild
                ? Distribution.BundledToolRepair : "FFmpeg or FFprobe is still missing. Install both, then select Refresh.");
        }
        catch (Exception ex) { ReportError($"Could not refresh conversion tools: {ex.Message}"); }
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
        var path = await PlatformActions.ChooseOutputAsync(entry.AllOutputs);
        if (path is not null && !await PlatformActions.OpenAsync(path)) ReportError("That result no longer exists or could not be opened.");
    }

    [RelayCommand]
    private async Task RevealHistoryOutputAsync(HistoryEntry entry)
    {
        var path = await PlatformActions.ChooseOutputAsync(entry.AllOutputs);
        if (path is not null && !await PlatformActions.RevealInFolderAsync(path)) ReportError("That result no longer exists or could not be revealed.");
    }

    [RelayCommand] private async Task CopyHistoryOutputsAsync(HistoryEntry entry) => await Clipboard.Default.SetTextAsync(string.Join(Environment.NewLine, entry.AllOutputs.Select(output => output.Path)));
    [RelayCommand] private async Task CopyHistorySourcesAsync(HistoryEntry entry) => await Clipboard.Default.SetTextAsync(string.Join(Environment.NewLine, entry.AllInputs));
    [RelayCommand]
    private void RemoveHistoryEntry(HistoryEntry entry)
    {
        service.History.Remove(entry);
        History.Remove(entry);
    }

    [RelayCommand]
    private async Task ReconvertFromHistoryAsync(HistoryEntry entry)
    {
        if (IsConverting || IsReadingFiles) { ReportError("Wait for the current batch or file inspection before loading a previous operation."); return; }
        var operation = entry.Operation ?? new();
        if (!Enum.IsDefined(operation.Kind) || !Enum.IsDefined(operation.SplitMode))
        {
            ReportError("This history entry uses an unsupported operation. Add the source files manually.");
            return;
        }
        var options = entry.Options ?? ConversionOptions.Default;
        using var access = await RequestHistoryAccessAsync(entry.AllInputs.Concat(
            new[] { options.ReplacementAudioPath, options.ExternalSubtitlePath }.OfType<string>()));
        if (access is null) return;
        if (entry.AllInputs.Any(path => !File.Exists(path)))
        {
            ReportError("One or more original files no longer exist. This operation cannot be reconstructed.");
            return;
        }
        if (Files.Count > 0 && !await Shell.Current.DisplayAlertAsync("Load previous operation?", "Replace the current queue with the original inputs, in their saved order? Source and output files will not be deleted.", "Load", "Cancel")) return;
        if (IsConverting || IsReadingFiles) { ReportError("The queue changed while confirming. Wait for the current work before loading history."); return; }
        BatchFileChanges(() => { foreach (var file in Files.ToArray()) RemoveFile(file); });
        service.Queue.ClearFinished();
        await AddPathsAsync(entry.AllInputs);
        SelectedOperation = OperationOptions.First(choice => choice.Value == operation.Kind);
        SelectedSplitMode = SplitModeOptions.First(choice => choice.Value == operation.SplitMode);
        SegmentSecondsText = operation.SegmentSeconds.ToString(CultureInfo.InvariantCulture);
        var row = Files.FirstOrDefault(file => string.Equals(file.Path, entry.SourcePath, PathComparison));
        if (row is not null) SelectedFile = row;
        View = WorkspaceView.Convert;
        var format = FormatGroups.SelectMany(group => group).FirstOrDefault(option => option.Id == entry.FormatId);
        if (format is not null) SelectedFormat = format;
        else ShowToast($"{entry.FormatName} isn't available right now; pick another format.");
        ApplyOptions(options);
        foreach (var file in Files)
        {
            file.TrimStartSeconds = options.TrimStartSeconds ?? 0;
            file.TrimEndSeconds = options.TrimEndSeconds ?? file.DurationSeconds;
            file.SelectedAudioTrack = file.AudioTrackChoices.FirstOrDefault(choice => choice.Value == (options.AudioTrack ?? 0));
            file.SelectedSubtitleTrack = file.SubtitleTrackChoices.FirstOrDefault(choice => choice.Value == (options.SubtitleTrack ?? 0));
            if (options.ReplacementAudioPath is { } audio) RetainWorkspaceAccess(audio);
            file.ReplacementAudioPath = options.ReplacementAudioPath;
            if (options.ExternalSubtitlePath is { } subtitle) RetainWorkspaceAccess(subtitle);
            file.ExternalSubtitlePath = options.ExternalSubtitlePath;
        }
    }

    private async Task<IDisposable?> RequestHistoryAccessAsync(IEnumerable<string> paths)
    {
        try { return await FileAccessService.RequestAsync(paths); }
        catch (Exception ex) { ReportError($"Could not restore access for this operation: {ex.Message}"); return null; }
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
