using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Maui.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalMorph.Bridge;
using Microsoft.Maui.ApplicationModel;

namespace LocalMorph.App.ViewModels;

public partial class MainPageViewModel : ObservableObject
{
    private const string OutputDirectoryPreferenceKey = "output-directory";

    public MainPageViewModel()
    {
        OutputFileName = "converted-output";
        SetOutputFormats(["MP4 video", "WebM video", "GIF image", "MP3 audio", "WAV audio"]);
        SelectedFormat = "MP4 video";
        Quality = 80;
        DurationSeconds = 30;
        TrimStartSeconds = 0;
        TrimEndSeconds = 15;
        ScrubPosition = 0;
        OutputDirectoryPath = Preferences.Default.Get(OutputDirectoryPreferenceKey, string.Empty);
        UpdatePreview();
    }

    public ObservableCollection<string> OutputFormats { get; } = [];
    public ObservableCollection<string> ResolutionOptions { get; } = [];

    public string[] EncodingSpeeds { get; } = ["fast", "balanced", "quality"];
    public string[] FrameRates { get; } = ["", "24", "30", "60"];
    public string[] AudioBitrates { get; } = ["", "64", "96", "128", "192", "256", "320"];
    public string[] SampleRates { get; } = ["", "22050", "44100", "48000"];
    public string[] ChannelModes { get; } = ["source", "mono", "stereo"];
    public string[] WavBitDepths { get; } = ["16", "24", "32"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedFile))]
    [NotifyPropertyChangedFor(nameof(CanStartConversion))]
    [NotifyPropertyChangedFor(nameof(RouteSourceLabel))]
    [NotifyPropertyChangedFor(nameof(SourceStepState))]
    [NotifyPropertyChangedFor(nameof(TuneStepState))]
    public partial string SelectedFileDisplay { get; set; } = "No file selected";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartConversion))]
    public partial string SelectedFilePath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputFormatMetric))]
    public partial string? SelectedFormat { get; set; } = "MP4 video";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceDetails))]
    [NotifyPropertyChangedFor(nameof(IsVideoSource))]
    [NotifyPropertyChangedFor(nameof(IsAudioSource))]
    [NotifyPropertyChangedFor(nameof(IsImageSource))]
    [NotifyPropertyChangedFor(nameof(ShowMediaPreview))]
    [NotifyPropertyChangedFor(nameof(ShowPreviewPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowTimingSettings))]
    [NotifyPropertyChangedFor(nameof(ShowAdvancedSettings))]
    public partial SourceMediaKind SourceKind { get; set; } = SourceMediaKind.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceDetails))]
    public partial int? SourceWidth { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceDetails))]
    public partial int? SourceHeight { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAudioSettings))]
    [NotifyPropertyChangedFor(nameof(ShowAdvancedSettings))]
    public partial bool SourceHasAudio { get; set; }

    [ObservableProperty]
    public partial string SelectedResolution { get; set; } = "Original";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputSummary))]
    public partial string OutputFileName { get; set; } = "converted-output";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QualityMetric))]
    public partial int Quality { get; set; } = 80;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClipDurationMetric))]
    public partial double TrimStartSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClipDurationMetric))]
    public partial double TrimEndSeconds { get; set; } = 15;

    [ObservableProperty]
    public partial double ScrubPosition { get; set; }

    [ObservableProperty]
    public partial double DurationSeconds { get; set; } = 30;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressRatio))]
    [NotifyPropertyChangedFor(nameof(ProgressMetric))]
    [NotifyPropertyChangedFor(nameof(ConvertStepState))]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartConversion))]
    [NotifyPropertyChangedFor(nameof(ProgressMetric))]
    [NotifyPropertyChangedFor(nameof(ConvertStepState))]
    [NotifyPropertyChangedFor(nameof(ConvertButtonText))]
    public partial bool IsConverting { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready to convert";

    [ObservableProperty]
    public partial bool HasConversionError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConvertButtonText))]
    public partial bool IsConversionComplete { get; set; }

    [ObservableProperty]
    public partial bool HasWizardValidation { get; set; }

    [ObservableProperty]
    public partial string WizardValidationText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineMetric))]
    public partial string FfmpegStatus { get; set; } = "Checking local FFmpeg...";

    [ObservableProperty]
    public partial string CommandPreview { get; set; } = "Select a file to preview the generated FFmpeg command.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputSummary))]
    public partial string CurrentOutputExtension { get; set; } = ".mp4";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputLocationDisplay))]
    [NotifyPropertyChangedFor(nameof(RouteOutputLocation))]
    public partial string OutputDirectoryPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStepOne))]
    [NotifyPropertyChangedFor(nameof(IsStepTwo))]
    [NotifyPropertyChangedFor(nameof(IsStepThree))]
    [NotifyPropertyChangedFor(nameof(WizardProgress))]
    [NotifyPropertyChangedFor(nameof(WizardStepLabel))]
    public partial int CurrentStep { get; set; } = 1;

    public bool HasSelectedFile => SelectedFileDisplay != "No file selected";
    public bool CanStartConversion => HasSelectedFile && !IsConverting;
    public string RouteSourceLabel => HasSelectedFile ? SelectedFileDisplay : "Choose a source";
    public string OutputSummary => $"{(string.IsNullOrWhiteSpace(OutputFileName) ? "converted-output" : OutputFileName)}{CurrentOutputExtension}";
    public string OutputLocationDisplay => string.IsNullOrWhiteSpace(OutputDirectoryPath) ? "Beside the source file" : OutputDirectoryPath;
    public string RouteOutputLocation => string.IsNullOrWhiteSpace(OutputDirectoryPath) ? "Saved beside the source" : $"Saved to {Path.GetFileName(OutputDirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}";
    public string OutputFormatMetric => SelectedFormat?.Replace(" video", string.Empty).Replace(" image", string.Empty).Replace(" audio", string.Empty).ToUpperInvariant() ?? "CHOOSE";
    public string QualityMetric => $"{Quality}%";
    public string ClipDurationMetric => $"{Math.Max(0, TrimEndSeconds - TrimStartSeconds):0.0}s";
    public string EngineMetric => FfmpegStatus.StartsWith("FFmpeg resolved", StringComparison.Ordinal) ? "READY" : "CHECKING";
    public double ProgressRatio => Math.Clamp(ProgressPercent / 100d, 0d, 1d);
    public string ProgressMetric => IsConverting ? $"{ProgressPercent:0}%" : ProgressPercent >= 100 ? "DONE" : "IDLE";
    public string ConvertButtonText => IsConverting ? "Converting..." : IsConversionComplete ? "Convert again" : "Convert locally";
    public string SourceStepState => HasSelectedFile ? "DONE" : "NOW";
    public string TuneStepState => HasSelectedFile ? "NOW" : "NEXT";
    public string ConvertStepState => IsConverting ? "LIVE" : ProgressPercent >= 100 ? "DONE" : "NEXT";
    public bool IsStepOne => CurrentStep == 1;
    public bool IsStepTwo => CurrentStep == 2;
    public bool IsStepThree => CurrentStep == 3;
    public bool IsVideoSource => SourceKind == SourceMediaKind.Video;
    public bool IsAudioSource => SourceKind == SourceMediaKind.Audio;
    public bool IsImageSource => SourceKind == SourceMediaKind.Image;
    public bool ShowMediaPreview => IsVideoSource || IsAudioSource;
    public bool ShowPreviewPlaceholder => SourceKind == SourceMediaKind.Unknown;
    public bool IsVideoOutput => SelectedFormat is "MP4 video" or "WebM video";
    public bool IsAudioOutput => SelectedFormat is "MP3 audio" or "WAV audio";
    public bool IsImageOutput => SelectedFormat is "GIF image" or "PNG image" or "JPEG image" or "WebP image";
    public bool ShowQualitySettings => IsVideoOutput || IsImageOutput;
    public bool ShowVideoSettings => IsVideoOutput;
    public bool ShowResolutionSettings => IsVideoOutput || IsImageOutput;
    public bool ShowAudioSettings => IsAudioOutput || IsVideoOutput && SourceHasAudio;
    public bool ShowWavBitDepth => SelectedFormat == "WAV audio";
    public bool ShowTimingSettings => IsVideoSource || IsAudioSource;
    public bool ShowAdvancedSettings => ShowTimingSettings || ShowAudioSettings;
    public string SourceDetails
    {
        get
        {
            var kind = SourceKind.ToString().ToUpperInvariant();
            var resolution = SourceWidth is { } width && SourceHeight is { } height ? $" | {width} x {height}" : string.Empty;
            var duration = ShowTimingSettings ? $" | {DurationSeconds:0.0}s" : string.Empty;
            return $"{kind}{resolution}{duration}";
        }
    }
    public double WizardProgress => CurrentStep / 3d;
    public string WizardStepLabel => $"STEP {CurrentStep} OF 3";

    [ObservableProperty]
    public partial string SelectedEncodingSpeed { get; set; } = "balanced";

    [ObservableProperty]
    public partial string SelectedFrameRate { get; set; } = "30";

    [ObservableProperty]
    public partial string SelectedAudioBitrate { get; set; } = "192";

    [ObservableProperty]
    public partial string SelectedSampleRate { get; set; } = "48000";

    [ObservableProperty]
    public partial string SelectedChannelMode { get; set; } = "source";

    [ObservableProperty]
    public partial string SelectedWavBitDepth { get; set; } = "16";

    partial void OnSelectedFormatChanged(string? value)
    {
        CurrentOutputExtension = value switch
        {
            "MP4 video" => ".mp4",
            "WebM video" => ".webm",
            "GIF image" => ".gif",
            "PNG image" => ".png",
            "JPEG image" => ".jpg",
            "WebP image" => ".webp",
            "MP3 audio" => ".mp3",
            "WAV audio" => ".wav",
            _ => ".mp4"
        };

        ApplyFormatDefaults(value);
        OnPropertyChanged(nameof(IsVideoOutput));
        OnPropertyChanged(nameof(IsAudioOutput));
        OnPropertyChanged(nameof(IsImageOutput));
        OnPropertyChanged(nameof(ShowQualitySettings));
        OnPropertyChanged(nameof(ShowVideoSettings));
        OnPropertyChanged(nameof(ShowResolutionSettings));
        OnPropertyChanged(nameof(ShowAudioSettings));
        OnPropertyChanged(nameof(ShowWavBitDepth));
        OnPropertyChanged(nameof(ShowAdvancedSettings));
        UpdatePreview();
    }

    partial void OnQualityChanged(int value) => UpdatePreview();
    partial void OnTrimStartSecondsChanged(double value) => UpdatePreview();
    partial void OnTrimEndSecondsChanged(double value) => UpdatePreview();
    partial void OnScrubPositionChanged(double value) => UpdatePreview();
    partial void OnOutputFileNameChanged(string value) => UpdatePreview();
    partial void OnSelectedEncodingSpeedChanged(string value) => UpdatePreview();
    partial void OnSelectedFrameRateChanged(string value) => UpdatePreview();
    partial void OnSelectedAudioBitrateChanged(string value) => UpdatePreview();
    partial void OnSelectedSampleRateChanged(string value) => UpdatePreview();
    partial void OnSelectedChannelModeChanged(string value) => UpdatePreview();
    partial void OnSelectedWavBitDepthChanged(string value) => UpdatePreview();
    partial void OnSelectedResolutionChanged(string value) => UpdatePreview();

    partial void OnSelectedFileDisplayChanged(string value)
    {
        if (HasSelectedFile)
        {
            ClearWizardValidation();
        }
    }

    [RelayCommand]
    private async Task PickFileAsync()
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Choose a video, audio, or image file",
            });

            if (result is null)
            {
                return;
            }

            OutputFileName = CreateOutputName(result.FullPath);
            SelectedFileDisplay = Path.GetFileName(result.FullPath);
            var ffmpeg = FfmpegResolver.Resolve();
            var mediaInfo = ffmpeg is null ? null : await MediaProbe.ProbeAsync(ffmpeg.Path, result.FullPath);
            ApplySourceInfo(mediaInfo ?? InferMediaInfo(result.FullPath));
            SelectedFilePath = result.FullPath;
            StatusText = "File ready to convert";
            ScrubPosition = 0;
            UpdatePreview();
        }
        catch (Exception ex)
        {
            StatusText = $"File selection failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task PickOutputDirectoryAsync()
    {
        try
        {
            var result = await FolderPicker.Default.PickAsync(CancellationToken.None);
            if (!result.IsSuccessful || result.Folder is null)
            {
                return;
            }

            OutputDirectoryPath = result.Folder.Path;
            Preferences.Default.Set(OutputDirectoryPreferenceKey, OutputDirectoryPath);
            StatusText = "Output location saved";
        }
        catch (Exception ex)
        {
            StatusText = $"Output location selection failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void GoToStep(int step)
    {
        if (step < 1 || step > 3 || step > 1 && !HasSelectedFile)
        {
            ShowWizardValidation("Choose a source file before moving to another step.");
            return;
        }

        ClearWizardValidation();
        CurrentStep = step;
    }

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep == 1 && !HasSelectedFile)
        {
            ShowWizardValidation("Choose a source file before continuing to conversion settings.");
            return;
        }

        if (CurrentStep == 2 && ValidateSettings() is { } validationMessage)
        {
            ShowWizardValidation(validationMessage);
            return;
        }

        ClearWizardValidation();
        CurrentStep = Math.Min(3, CurrentStep + 1);
    }

    [RelayCommand]
    private void PreviousStep()
    {
        ClearWizardValidation();
        CurrentStep = Math.Max(1, CurrentStep - 1);
    }

    [RelayCommand]
    private void StartOver()
    {
        SelectedFilePath = string.Empty;
        SelectedFileDisplay = "No file selected";
        SourceKind = SourceMediaKind.Unknown;
        SourceWidth = null;
        SourceHeight = null;
        SourceHasAudio = false;
        ProgressPercent = 0;
        IsConversionComplete = false;
        HasConversionError = false;
        StatusText = "Ready to convert";
        ScrubPosition = 0;
        ClearWizardValidation();
        CurrentStep = 1;
        SetOutputFormats(["MP4 video", "WebM video", "GIF image", "MP3 audio", "WAV audio"]);
        UpdatePreview();
    }

    private void ApplySourceInfo(SourceMediaInfo mediaInfo)
    {
        SourceKind = mediaInfo.Kind;
        SourceWidth = mediaInfo.Width;
        SourceHeight = mediaInfo.Height;
        SourceHasAudio = mediaInfo.Channels is > 0 || mediaInfo.Kind == SourceMediaKind.Audio;
        DurationSeconds = mediaInfo.DurationSeconds is > 0 ? mediaInfo.DurationSeconds.Value : mediaInfo.Kind == SourceMediaKind.Image ? 1 : 30;
        TrimStartSeconds = 0;
        TrimEndSeconds = DurationSeconds;

        ResolutionOptions.Clear();
        ResolutionOptions.Add(mediaInfo.Width is { } width && mediaInfo.Height is { } height
            ? $"Original ({width} x {height})"
            : "Original");
        if (mediaInfo.Height is { } sourceHeight)
        {
            foreach (var targetHeight in new[] { 2160, 1440, 1080, 720, 480, 360, 240 }.Where(candidate => candidate < sourceHeight))
            {
                ResolutionOptions.Add($"{targetHeight}p");
            }
        }
        SelectedResolution = ResolutionOptions[0];

        SetOutputFormats(mediaInfo.Kind switch
        {
            SourceMediaKind.Video => ["MP4 video", "WebM video", "GIF image", "MP3 audio", "WAV audio"],
            SourceMediaKind.Audio => ["MP3 audio", "WAV audio"],
            SourceMediaKind.Image => ["PNG image", "JPEG image", "WebP image", "GIF image"],
            _ => ["MP4 video", "WebM video", "GIF image", "MP3 audio", "WAV audio"]
        });

        if (mediaInfo.SampleRate is 22050 or 44100 or 48000)
        {
            SelectedSampleRate = mediaInfo.SampleRate.Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void SetOutputFormats(IReadOnlyList<string> formats)
    {
        OutputFormats.Clear();
        foreach (var format in formats) OutputFormats.Add(format);
        SelectedFormat = formats[0];
    }

    private void ApplyFormatDefaults(string? format)
    {
        Quality = format switch
        {
            "JPEG image" or "WebP image" => 85,
            "GIF image" => 75,
            _ => 80
        };
        SelectedEncodingSpeed = "balanced";
        SelectedFrameRate = string.Empty;
        SelectedAudioBitrate = format == "MP3 audio" ? "192" : string.Empty;
        SelectedChannelMode = "source";
        if (ResolutionOptions.Count > 0) SelectedResolution = ResolutionOptions[0];
    }

    private static SourceMediaInfo InferMediaInfo(string path)
    {
        var extension = Path.GetExtension(path);
        var kind = extension.ToLowerInvariant() switch
        {
            ".mp3" or ".wav" or ".aac" or ".m4a" or ".flac" or ".ogg" or ".opus" => SourceMediaKind.Audio,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff" or ".heic" or ".heif" or ".avif" => SourceMediaKind.Image,
            ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" or ".m4v" or ".wmv" => SourceMediaKind.Video,
            _ => SourceMediaKind.Unknown
        };
        return new SourceMediaInfo(kind, null, null, null, null, null, kind == SourceMediaKind.Audio ? 2 : null);
    }

    private static string CreateOutputName(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var sanitized = new string(stem.Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '-').ToArray()).Trim('-', '.');
        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "converted-output";
        if (sanitized.Length > 118) sanitized = sanitized[..118];
        return $"{sanitized}-converted";
    }

    private string? ValidateSettings()
    {
        var outputName = string.IsNullOrWhiteSpace(OutputFileName) ? "converted-output" : OutputFileName;
        if (outputName.Length > 128)
        {
            return "Use an output name with 128 characters or fewer.";
        }

        if (outputName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            return "Use only letters, numbers, periods, underscores, or hyphens in the output name.";
        }

        if (ShowTimingSettings && (!double.IsFinite(TrimStartSeconds) || TrimStartSeconds < 0))
        {
            return "Trim start must be zero or a positive number.";
        }

        if (ShowTimingSettings && (!double.IsFinite(TrimEndSeconds) || TrimEndSeconds <= TrimStartSeconds))
        {
            return "Trim end must be later than trim start.";
        }

        return BuildRequest().IsValid() ? null : "Review the selected format and tuning options before continuing.";
    }

    private void ShowWizardValidation(string message)
    {
        WizardValidationText = message;
        HasWizardValidation = true;
    }

    private void ClearWizardValidation()
    {
        HasWizardValidation = false;
        WizardValidationText = string.Empty;
    }

    [RelayCommand]
    private async Task ConvertAsync()
    {
        if (IsConverting)
        {
            return;
        }

        HasConversionError = false;
        IsConversionComplete = false;
        IsConverting = true;
        ProgressPercent = 0;
        StatusText = "Checking conversion setup...";
        await Task.Yield();

        try
        {
            if (string.IsNullOrWhiteSpace(SelectedFilePath) || !File.Exists(SelectedFilePath))
            {
                HasConversionError = true;
                StatusText = "The selected source file is no longer available. Go back and choose it again.";
                return;
            }

            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacCatalyst())
            {
                HasConversionError = true;
                StatusText = "Local conversion is currently supported on Windows and macOS desktop targets only.";
                return;
            }

            var ffmpeg = FfmpegResolver.Resolve();
            if (ffmpeg is null)
            {
                HasConversionError = true;
                StatusText = "FFmpeg was not found. Install FFmpeg on PATH or bundle it with LocalMorph, then try again.";
                return;
            }

            var request = BuildRequest();
            if (!request.IsValid())
            {
                HasConversionError = true;
                StatusText = "These settings cannot be converted together. Go back and adjust the conversion options.";
                return;
            }

            var outputDirectory = string.IsNullOrWhiteSpace(OutputDirectoryPath)
                ? Path.GetDirectoryName(SelectedFilePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : OutputDirectoryPath;
            if (!Directory.Exists(outputDirectory))
            {
                HasConversionError = true;
                StatusText = "The selected output folder is no longer available. Go back and choose another folder.";
                return;
            }

            var outputPath = Path.Combine(outputDirectory, $"{OutputFileName}{CurrentOutputExtension}");
            if (File.Exists(outputPath)) File.Delete(outputPath);

            StatusText = "Starting FFmpeg...";
            long? duration = null;
            try
            {
                duration = await ProbeDurationAsync(ffmpeg.Path, SelectedFilePath, request, CancellationToken.None);
            }
            catch
            {
                duration = null;
            }

            using var process = new Process { StartInfo = Ffmpeg.BuildCommand(ffmpeg.Path, SelectedFilePath, outputPath, request), EnableRaisingEvents = true };
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (string.IsNullOrWhiteSpace(eventArgs.Data)) return;
                var update = Ffmpeg.ParseProgress(eventArgs.Data, duration);
                if (update is null) return;
                if (update.Completed)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        ProgressPercent = 100;
                        StatusText = "Finalizing output...";
                    });
                    return;
                }

                if (update.Percent is { } percent)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        ProgressPercent = percent;
                        StatusText = $"Converting {Path.GetFileName(SelectedFilePath)} ({percent}%)";
                    });
                }
            };

            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    MainThread.BeginInvokeOnMainThread(() => StatusText = eventArgs.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0 && File.Exists(outputPath))
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ProgressPercent = 100;
                    IsConversionComplete = true;
                    StatusText = $"Converted successfully: {Path.GetFileName(outputPath)}";
                });
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    HasConversionError = true;
                    StatusText = "Conversion failed. Try a different format or adjust the conversion settings.";
                });
            }
        }
        catch (Exception ex)
        {
            HasConversionError = true;
            StatusText = $"Conversion could not start: {ex.Message}";
        }
        finally
        {
            IsConverting = false;
        }
    }

    [RelayCommand]
    private void UpdatePreview()
    {
        var ffmpeg = FfmpegResolver.Resolve();
        FfmpegStatus = ffmpeg is null
            ? "FFmpeg not discovered in the app bundle or PATH yet."
            : $"FFmpeg resolved: {ffmpeg.Path} ({ffmpeg.Version ?? "version unavailable"})";

        var outputFileName = string.IsNullOrWhiteSpace(OutputFileName) ? "converted-output" : OutputFileName;
        if (string.IsNullOrWhiteSpace(SelectedFilePath) || SelectedFileDisplay == "No file selected")
        {
            CommandPreview = "Select a file to preview the generated FFmpeg command.";
            return;
        }

        var request = BuildRequest(outputFileName);
        if (!request.IsValid())
        {
            CommandPreview = "The selected settings are not valid for the current output format. Adjust the conversion tuning and try again.";
            return;
        }

        var command = Ffmpeg.BuildCommand(ffmpeg?.Path ?? "ffmpeg", SelectedFilePath, $"{outputFileName}{CurrentOutputExtension}", request);
        CommandPreview = Ffmpeg.DescribeCommand(command);
        if (!IsConverting)
        {
            StatusText = "Ready to convert";
        }
    }

    private ConversionRequest BuildRequest(string? outputNameOverride = null)
    {
        var mime = SelectedFormat switch
        {
            "MP4 video" => "video/mp4",
            "WebM video" => "video/webm",
            "GIF image" => "image/gif",
            "PNG image" => "image/png",
            "JPEG image" => "image/jpeg",
            "WebP image" => "image/webp",
            "MP3 audio" => "audio/mpeg",
            "WAV audio" => "audio/wav",
            _ => "video/mp4"
        };

        var outputName = string.IsNullOrWhiteSpace(outputNameOverride)
            ? (string.IsNullOrWhiteSpace(OutputFileName) ? "converted-output" : OutputFileName)
            : outputNameOverride;

        return new ConversionRequest
        {
            TargetMime = mime,
            OutputName = outputName,
            MediaType = mime switch
            {
                "image/gif" or "image/png" or "image/jpeg" or "image/webp" => "image",
                "audio/mpeg" or "audio/wav" => "audio",
                _ => "video"
            },
            Quality = ShowQualitySettings ? Quality : null,
            Image = new ImageOptions
            {
                Width = null,
                Height = IsImageOutput ? ParseResolutionHeight(SelectedResolution) : null,
                KeepAspectRatio = true
            },
            Media = new MediaOptions
            {
                TrimStart = ShowTimingSettings ? TrimStartSeconds : null,
                TrimEnd = ShowTimingSettings ? TrimEndSeconds : null,
                ChannelMode = SelectedChannelMode,
                VideoEncodingSpeed = SelectedEncodingSpeed,
                VideoFrameRate = TryParseInt(SelectedFrameRate),
                VideoHeight = IsVideoOutput ? ParseResolutionHeight(SelectedResolution) : null,
                AudioBitrate = TryParseInt(SelectedAudioBitrate),
                AudioSampleRate = TryParseInt(SelectedSampleRate),
                WavBitDepth = TryParseInt(SelectedWavBitDepth) ?? 16
            }
        };
    }

    private static int? ParseResolutionHeight(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("Original", StringComparison.Ordinal)) return null;
        return int.TryParse(value.TrimEnd('p'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            ? height
            : null;
    }

    private static int? TryParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private static async Task<long?> ProbeDurationAsync(string ffmpeg, string input, ConversionRequest request, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacCatalyst())
        {
            return null;
        }

        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (!File.Exists(ffprobe)) return null;

        using var process = new Process { StartInfo = new ProcessStartInfo(ffprobe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }, EnableRaisingEvents = true };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", input })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start()) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(timeout.Token));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            return null;
        }

        var text = await stdoutTask;
        if (process.ExitCode != 0 || !double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0 || !double.IsFinite(seconds))
        {
            return null;
        }

        var start = request.Media.TrimStart ?? 0;
        var end = request.Media.TrimEnd ?? seconds;
        var effective = Math.Max(0, end - start);
        return effective > 0 ? (long)(effective * 1_000_000) : null;
    }
}
