using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalMorph.App.Services;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;

namespace LocalMorph.App.ViewModels;

/// <summary>One row in the queue: a source file, its inspection results, and (once started) its conversion job.</summary>
public partial class FileItemViewModel : ObservableObject
{
    private ConversionJob? job;

    public FileItemViewModel(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Category = SourceClassifier.Classify(path);
        Flavor = Category == MediaCategory.Document ? SourceClassifier.ClassifyDocument(path) : DocumentFlavor.None;
        Summary = "Inspecting…";
    }

    public string Path { get; }
    public string FileName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon))]
    [NotifyPropertyChangedFor(nameof(IsVideo))]
    [NotifyPropertyChangedFor(nameof(IsAudio))]
    [NotifyPropertyChangedFor(nameof(IsImage))]
    [NotifyPropertyChangedFor(nameof(IsDocument))]
    [NotifyPropertyChangedFor(nameof(IsSubtitle))]
    [NotifyPropertyChangedFor(nameof(ShowsPlaceholder))]
    [NotifyPropertyChangedFor(nameof(HasTimeline))]
    [NotifyPropertyChangedFor(nameof(CategoryLabel))]
    public partial MediaCategory Category { get; set; }

    [ObservableProperty]
    public partial DocumentFlavor Flavor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimeline))]
    [NotifyPropertyChangedFor(nameof(DurationSeconds))]
    [NotifyPropertyChangedFor(nameof(HasAudio))]
    [NotifyPropertyChangedFor(nameof(HasMultipleAudioTracks))]
    [NotifyPropertyChangedFor(nameof(HasSubtitleTracks))]
    [NotifyPropertyChangedFor(nameof(TracksSummary))]
    public partial SourceFile? Source { get; set; }

    /// <summary>The source's audio tracks as picker choices (value = 0-based audio track number).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChoiceOption<int>> AudioTrackChoices { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TracksSummary))]
    public partial ChoiceOption<int>? SelectedAudioTrack { get; set; }

    /// <summary>The source's subtitle tracks as picker choices (value = 0-based subtitle track number).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ChoiceOption<int>> SubtitleTrackChoices { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TracksSummary))]
    public partial ChoiceOption<int>? SelectedSubtitleTrack { get; set; }

    /// <summary>An audio file that replaces the video's soundtrack.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplacementAudio))]
    [NotifyPropertyChangedFor(nameof(ReplacementAudioLabel))]
    [NotifyPropertyChangedFor(nameof(TracksSummary))]
    public partial string? ReplacementAudioPath { get; set; }

    /// <summary>A subtitle file to add as a track or burn into the picture.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExternalSubtitle))]
    [NotifyPropertyChangedFor(nameof(ExternalSubtitleLabel))]
    [NotifyPropertyChangedFor(nameof(TracksSummary))]
    public partial string? ExternalSubtitlePath { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial bool IsInspecting { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueued))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(IsCanceled))]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsTerminal))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(ShowProgress))]
    [NotifyPropertyChangedFor(nameof(StateIcon))]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    public partial JobState? State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    public partial string? OutputPath { get; set; }

    [ObservableProperty]
    public partial string? OutputSummary { get; set; }

    [ObservableProperty]
    public partial string? LogTail { get; set; }

    [ObservableProperty]
    public partial string? CommandLine { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrimSummary))]
    [NotifyPropertyChangedFor(nameof(HasTrim))]
    public partial double TrimStartSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrimSummary))]
    [NotifyPropertyChangedFor(nameof(HasTrim))]
    public partial double TrimEndSeconds { get; set; }

    [ObservableProperty]
    public partial double FrameTimeSeconds { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public bool IsVideo => Category == MediaCategory.Video;
    public bool IsAudio => Category == MediaCategory.Audio;
    public bool IsImage => Category == MediaCategory.Image;
    public bool IsDocument => Category == MediaCategory.Document;
    public bool IsSubtitle => Category == MediaCategory.Subtitle;
    public bool ShowsPlaceholder => IsDocument || IsSubtitle;
    public bool HasMultipleAudioTracks => AudioTrackChoices.Count > 1;
    public bool HasSubtitleTracks => SubtitleTrackChoices.Count > 0;
    public bool HasReplacementAudio => !string.IsNullOrWhiteSpace(ReplacementAudioPath);
    public bool HasExternalSubtitle => !string.IsNullOrWhiteSpace(ExternalSubtitlePath);
    public string ReplacementAudioLabel => HasReplacementAudio ? System.IO.Path.GetFileName(ReplacementAudioPath!) : "Keep the original soundtrack";
    public string ExternalSubtitleLabel => HasExternalSubtitle ? System.IO.Path.GetFileName(ExternalSubtitlePath!) : "No subtitle file added";

    /// <summary>One line describing the streams this file carries and what will happen to them.</summary>
    public string TracksSummary
    {
        get
        {
            if (Source is null || !IsVideo) return string.Empty;
            var parts = new List<string>();
            var audio = Source.AudioTracks.Count;
            parts.Add(audio switch { 0 => "No audio", 1 => "1 audio track", _ => $"{audio} audio tracks" });
            var subtitles = Source.SubtitleTracks.Count;
            if (subtitles > 0) parts.Add(subtitles == 1 ? "1 subtitle track" : $"{subtitles} subtitle tracks");
            if (Source.Media?.ChapterCount is > 0 and var chapters) parts.Add(chapters == 1 ? "1 chapter" : $"{chapters} chapters");
            if (HasReplacementAudio) parts.Add($"audio → {ReplacementAudioLabel}");
            if (HasExternalSubtitle) parts.Add($"+ {ExternalSubtitleLabel}");
            return string.Join(" · ", parts);
        }
    }
    public bool HasTimeline => (IsVideo || IsAudio || Source?.IsAnimatedImage == true) && DurationSeconds > 0;
    public bool HasAudio => Source?.HasAudio ?? (IsAudio || IsVideo);
    public double DurationSeconds => Source?.DurationSeconds ?? 0;
    public bool HasTrim => HasTimeline && (TrimStartSeconds > 0.01 || TrimEndSeconds < DurationSeconds - 0.01);
    public string TrimSummary => HasTrim ? $"{SourceFile.FormatDuration(TrimStartSeconds)} – {SourceFile.FormatDuration(TrimEndSeconds)}" : "Full length";
    public string CategoryLabel => Category switch
    {
        MediaCategory.Video => "Video",
        MediaCategory.Audio => "Audio",
        MediaCategory.Image => "Image",
        MediaCategory.Subtitle => "Subtitles",
        MediaCategory.Document => Flavor switch
        {
            DocumentFlavor.Spreadsheet => "Spreadsheet",
            DocumentFlavor.Presentation => "Presentation",
            DocumentFlavor.Pdf => "PDF",
            _ => "Document"
        },
        _ => "Unknown"
    };

    public string Icon => Category switch
    {
        MediaCategory.Video => Icons.Video,
        MediaCategory.Audio => Icons.Audio,
        MediaCategory.Image => Icons.Image,
        MediaCategory.Subtitle => Icons.DocumentText,
        MediaCategory.Document => Flavor switch
        {
            DocumentFlavor.Spreadsheet => Icons.Table,
            DocumentFlavor.Presentation => Icons.Slides,
            DocumentFlavor.Pdf => Icons.Pdf,
            _ => Icons.Document
        },
        _ => Icons.Question
    };

    public bool IsQueued => State == JobState.Queued;
    public bool IsRunning => State == JobState.Running;
    public bool IsCompleted => State == JobState.Completed;
    public bool IsFailed => State == JobState.Failed;
    public bool IsCanceled => State is JobState.Canceled or JobState.Skipped;
    public bool IsPending => State is null or JobState.Failed or JobState.Canceled or JobState.Skipped;
    public bool IsTerminal => State is JobState.Completed or JobState.Failed or JobState.Canceled or JobState.Skipped;
    public bool CanCancel => State is JobState.Queued or JobState.Running;
    public bool CanRemove => !IsRunning;
    public bool ShowProgress => IsRunning || IsQueued;
    public bool HasOutput => IsCompleted && OutputPath is not null && File.Exists(OutputPath);
    public string ProgressPercent => $"{Progress * 100:0}%";
    public string StateIcon => State switch
    {
        JobState.Completed => Icons.CheckCircle,
        JobState.Failed => Icons.ErrorCircle,
        JobState.Canceled or JobState.Skipped => Icons.Dismiss,
        JobState.Running => Icons.Play,
        JobState.Queued => Icons.Clock,
        _ => string.Empty
    };

    public ConversionJob? Job => job;

    public event Action<FileItemViewModel>? RemoveRequested;
    public event Action<FileItemViewModel>? SelectRequested;

    public void ApplySource(SourceFile source)
    {
        Source = source;
        Category = source.Category;
        Flavor = source.Flavor;
        Summary = source.Summary;
        TrimStartSeconds = 0;
        TrimEndSeconds = source.DurationSeconds ?? 0;
        FrameTimeSeconds = 0;
        AudioTrackChoices = source.AudioTracks.Select(track => new ChoiceOption<int>(track.Describe(), track.TypeIndex)).ToList();
        SelectedAudioTrack = AudioTrackChoices.FirstOrDefault();
        SubtitleTrackChoices = source.SubtitleTracks.Select(track => new ChoiceOption<int>(track.Describe(), track.TypeIndex)).ToList();
        SelectedSubtitleTrack = SubtitleTrackChoices.FirstOrDefault();
        OnPropertyChanged(nameof(HasMultipleAudioTracks));
        OnPropertyChanged(nameof(HasSubtitleTracks));
        OnPropertyChanged(nameof(TracksSummary));
        IsInspecting = false;
        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(HasTrim));
        OnPropertyChanged(nameof(TrimSummary));
    }

    public void MarkInspectionFailed(string reason)
    {
        IsInspecting = false;
        Summary = reason;
    }

    public void AttachJob(ConversionJob newJob)
    {
        if (job is not null) job.Changed -= OnJobChanged;
        job = newJob;
        OutputPath = newJob.OutputPath;
        OutputSummary = null;
        LogTail = null;
        CommandLine = null;
        Progress = 0;
        State = newJob.State;
        StatusMessage = newJob.StatusMessage;
        IsIndeterminate = false;
        newJob.Changed += OnJobChanged;
    }

    public void ResetForRetry()
    {
        if (job is not null) job.Changed -= OnJobChanged;
        job = null;
        State = null;
        Progress = 0;
        StatusMessage = string.Empty;
        OutputPath = null;
        OutputSummary = null;
    }

    private void OnJobChanged(ConversionJob changed)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!ReferenceEquals(changed, job)) return;
            State = changed.State;
            Progress = changed.Progress;
            StatusMessage = changed.StatusMessage;
            IsIndeterminate = changed.State == JobState.Running && changed.Progress <= 0 && changed.Engine != EngineKind.Ffmpeg;
            CommandLine = changed.CommandLine;
            if (changed.IsTerminal)
            {
                LogTail = changed.LogTail;
                if (changed.Result is { } result)
                {
                    OutputPath = result.OutputPath;
                    var ratio = Source is { SizeBytes: > 0 } source ? (double)result.OutputBytes / source.SizeBytes : 1;
                    var change = ratio < 0.995 ? $"{Math.Min(99, (1 - ratio) * 100):0}% smaller" : ratio > 1.005 ? $"{(ratio - 1) * 100:0}% larger" : "same size";
                    OutputSummary = $"{SourceFile.FormatBytes(result.OutputBytes)} · {change} · {ConversionJob.FormatElapsed(result.Elapsed)}";
                }
                OnPropertyChanged(nameof(HasOutput));
            }
        });
    }

    public ConversionOptions ApplyPerFileOptions(ConversionOptions shared, OutputFormat format)
    {
        var options = shared;
        if (format.Supports(FormatFeatures.Trim) && HasTrim)
        {
            options = options with
            {
                TrimStartSeconds = TrimStartSeconds > 0.01 ? TrimStartSeconds : null,
                TrimEndSeconds = TrimEndSeconds < DurationSeconds - 0.01 ? TrimEndSeconds : null
            };
        }
        else
        {
            options = options with { TrimStartSeconds = null, TrimEndSeconds = null };
        }

        if (format.Supports(FormatFeatures.FrameExtract) && IsVideo)
        {
            options = options with { FrameTimeSeconds = FrameTimeSeconds };
        }

        var burnsIn = shared.Subtitles == SubtitleMode.BurnIn && format.Supports(FormatFeatures.BurnSubtitles);
        options = options with
        {
            AudioTrack = format.Supports(FormatFeatures.AudioTracks) && SelectedAudioTrack is { Value: > 0 } audioTrack ? audioTrack.Value : null,
            SubtitleTrack = (format.Supports(FormatFeatures.SubtitleTrack) || burnsIn) && SelectedSubtitleTrack is { } subtitleTrack ? subtitleTrack.Value : null,
            ReplacementAudioPath = IsVideo && format.Supports(FormatFeatures.MultiAudio) && !shared.RemoveAudio ? ReplacementAudioPath : null,
            ExternalSubtitlePath = IsVideo && (format.Supports(FormatFeatures.Subtitles) || burnsIn) ? ExternalSubtitlePath : null
        };

        return options;
    }

    [RelayCommand]
    private void Cancel() => job?.Cancel();

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this);

    [RelayCommand]
    private void Select() => SelectRequested?.Invoke(this);

    [RelayCommand]
    private async Task OpenOutputAsync()
    {
        if (OutputPath is not null) await PlatformActions.OpenAsync(OutputPath);
    }

    [RelayCommand]
    private void RevealOutput()
    {
        if (OutputPath is not null) PlatformActions.RevealInFolder(OutputPath);
    }

    [RelayCommand]
    private void RevealSource() => PlatformActions.RevealInFolder(Path);

    // A picker whose ItemsSource is swapped briefly reports "nothing selected"; keep the real choice instead.
    partial void OnSelectedAudioTrackChanged(ChoiceOption<int>? oldValue, ChoiceOption<int>? newValue)
    {
        if (newValue is null && AudioTrackChoices.Count > 0) SelectedAudioTrack = oldValue ?? AudioTrackChoices[0];
    }

    partial void OnSelectedSubtitleTrackChanged(ChoiceOption<int>? oldValue, ChoiceOption<int>? newValue)
    {
        if (newValue is null && SubtitleTrackChoices.Count > 0) SelectedSubtitleTrack = oldValue ?? SubtitleTrackChoices[0];
    }

    [RelayCommand]
    private void ClearReplacementAudio() => ReplacementAudioPath = null;

    [RelayCommand]
    private void ClearExternalSubtitle() => ExternalSubtitlePath = null;

    [RelayCommand]
    private void ResetTrim()
    {
        TrimStartSeconds = 0;
        TrimEndSeconds = DurationSeconds;
    }
}
