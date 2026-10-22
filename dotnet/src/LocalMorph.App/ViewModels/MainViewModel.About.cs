using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalMorph.App.Services;
using LocalMorph.Core.Diagnostics;
using LocalMorph.Core.Support;

namespace LocalMorph.App.ViewModels;

public partial class MainViewModel
{
    public string AppVersion => Distribution.CurrentVersionDisplay;
    public string? AppBuild => DeviceInfo.Current.Platform == DevicePlatform.MacCatalyst ? AppInfo.Current.BuildString : null;
    public string FeedbackEnvironmentSummary => $"Included in your draft: LocalMorph {AppVersion}{(AppBuild is null ? "" : $", build {AppBuild}")}, {FeedbackOperatingSystem}. No files or logs are attached.";

    public IReadOnlyList<ChoiceOption<FeedbackKind>> FeedbackTypes { get; } =
        [new("Bug", FeedbackKind.Bug), new("Feature request", FeedbackKind.FeatureRequest)];

    [ObservableProperty]
    public partial ChoiceOption<FeedbackKind>? SelectedFeedbackType { get; set; }

    [ObservableProperty]
    public partial string FeedbackTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FeedbackDetails { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? FeedbackError { get; set; }

    [ObservableProperty]
    public partial string? FeedbackStatus { get; set; }

    [ObservableProperty]
    public partial string? AboutLinkError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFeedbackCommand), nameof(OpenPrivacyPolicyCommand), nameof(OpenWebsiteCommand), nameof(OpenProjectCommand))]
    public partial bool IsOpeningAboutLink { get; set; }

    private bool CanOpenAboutLink() => !IsOpeningAboutLink;

    private static string FeedbackOperatingSystem
    {
        get
        {
#if MACCATALYST
            return $"macOS {Foundation.NSProcessInfo.ProcessInfo.OperatingSystemVersionString}";
#elif WINDOWS
            return $"Windows {DeviceInfo.Current.VersionString}";
#else
            return $"{DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString}";
#endif
        }
    }

    partial void OnSelectedFeedbackTypeChanged(ChoiceOption<FeedbackKind>? value) => ClearFeedbackMessages();
    partial void OnFeedbackTitleChanged(string value) => ClearFeedbackMessages();
    partial void OnFeedbackDetailsChanged(string value) => ClearFeedbackMessages();

    private void ClearFeedbackMessages()
    {
        FeedbackError = null;
        FeedbackStatus = null;
    }

    [RelayCommand(CanExecute = nameof(CanOpenAboutLink))]
    private async Task OpenFeedbackAsync()
    {
        ClearFeedbackMessages();
        if (SelectedFeedbackType is not { } type)
        {
            FeedbackError = "Choose Bug or Feature request.";
            return;
        }

        var draft = FeedbackIssue.Create(type.Value, FeedbackTitle, FeedbackDetails, new(AppVersion, AppBuild, FeedbackOperatingSystem));
        if (draft.Url is not { } url)
        {
            FeedbackError = draft.Error;
            return;
        }

        FeedbackError = await OpenAboutLinkAsync(url, "GitHub");
        if (FeedbackError is null) FeedbackStatus = "GitHub opened. Review your draft and submit it there. Your draft is still here.";
    }

    [RelayCommand(CanExecute = nameof(CanOpenAboutLink))]
    private async Task OpenPrivacyPolicyAsync() =>
        AboutLinkError = await OpenAboutLinkAsync(new("https://localmorph.com/#/privacy"), "the privacy policy");

    [RelayCommand(CanExecute = nameof(CanOpenAboutLink))]
    private async Task OpenWebsiteAsync() =>
        AboutLinkError = await OpenAboutLinkAsync(new("https://localmorph.com"), "the website");

    [RelayCommand(CanExecute = nameof(CanOpenAboutLink))]
    private async Task OpenProjectAsync() =>
        AboutLinkError = await OpenAboutLinkAsync(new("https://github.com/jamesmontemagno/my-file-converter"), "the GitHub project");

    private async Task<string?> OpenAboutLinkAsync(Uri url, string destination)
    {
        IsOpeningAboutLink = true;
        try
        {
            if (await Launcher.Default.OpenAsync(url)) return null;
            DiagnosticLog.Warn($"Could not open {destination}: the launcher returned false.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException
            or System.Runtime.InteropServices.COMException
#if MACCATALYST
            or Foundation.NSErrorException
#endif
            )
        {
            DiagnosticLog.Warn($"Could not open {destination}: {ex.GetType().Name} ({ex.HResult:X8}).");
        }
        finally
        {
            IsOpeningAboutLink = false;
        }

        return $"Could not open {destination}. Check your default browser and try again.";
    }
}
