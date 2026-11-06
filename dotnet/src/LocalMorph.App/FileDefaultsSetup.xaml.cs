using LocalMorph.App.Services;

namespace LocalMorph.App;

public partial class FileDefaultsSetup : ContentView
{
    private readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CheckBox> checkBoxes = [];

    public event EventHandler? Dismissed;

    public FileDefaultsSetup()
    {
        InitializeComponent();
        var resources = Application.Current!.Resources;
        foreach (var (category, extensions) in FileActivationService.Associations)
        {
#if MACCATALYST
            // The Mac manifest does not declare subtitle handlers.
            if (category == "subtitle") continue;
#endif
            FileTypes.Children.Add(new Label
            {
                Text = category switch
                {
                    "video" => "Video",
                    "audio" => "Audio",
                    "image" => "Images",
                    "document" => "Documents",
                    _ => "Subtitles"
                },
                Style = (Style)resources["SectionTitle"]
            });
            var group = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
            foreach (var extension in extensions)
            {
                var checkBox = new CheckBox { AutomationId = $"DefaultType{extension}" };
                SemanticProperties.SetDescription(checkBox, $"Choose {extension} for default app setup");
                checkBox.CheckedChanged += (_, e) =>
                {
                    if (e.Value) selected.Add(extension);
                    else selected.Remove(extension);
                    ContinueButton.IsEnabled = selected.Count > 0;
                };
                checkBoxes.Add(checkBox);
                var label = new Label
                {
                    Text = extension,
                    Style = (Style)resources["Body"],
                    VerticalOptions = LayoutOptions.Center
                };
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => checkBox.IsChecked = !checkBox.IsChecked;
                label.GestureRecognizers.Add(tap);
                group.Children.Add(new HorizontalStackLayout
                {
                    WidthRequest = 124,
                    Children = { checkBox, label }
                });
            }
            FileTypes.Children.Add(group);
        }

#if WINDOWS
        Instructions.Text = AppInfo.Current.PackagingModel == AppPackagingModel.Packaged
            ? "Open Windows Default Apps settings, find LocalMorph, and choose it for each extension listed above. Leave other extensions unchanged. If Windows shows the general page, search for LocalMorph or an extension."
            : "This development copy is unpackaged and is not registered in Default Apps. Install the packaged version of LocalMorph to choose it as a default. In Windows Settings > Apps > Default apps, find LocalMorph and change only the extensions listed above.";
#elif MACCATALYST
        OpenSettingsButton.IsVisible = false;
        Instructions.Text = "In Finder, select a file with one of the extensions above. Choose File > Get Info, expand Open with, choose LocalMorph (or Other to find it in Applications), then click Change All and confirm. Repeat for each extension you chose. Leave other file types unchanged.";
#else
        OpenSettingsButton.IsVisible = false;
        Instructions.Text = "Use your operating system's file settings to choose LocalMorph for the extensions listed above.";
#endif
    }

    public void Reset()
    {
        foreach (var checkBox in checkBoxes) checkBox.IsChecked = false;
        ShowChoices();
    }

    private void OnContinue(object? sender, EventArgs e)
    {
        if (selected.Count == 0) return;
        SelectedTypes.Text = string.Join(", ", selected.Order(StringComparer.OrdinalIgnoreCase));
        Choices.IsVisible = SkipButton.IsVisible = ContinueButton.IsVisible = false;
        Guide.IsVisible = BackButton.IsVisible = FinishButton.IsVisible = true;
        StepLabel.Text = "STEP 2 OF 2";
        Heading.Text = "Confirm in your operating system";
        BackButton.Focus();
    }

    private void OnBack(object? sender, EventArgs e) => ShowChoices();

    private void ShowChoices()
    {
        Choices.IsVisible = SkipButton.IsVisible = ContinueButton.IsVisible = true;
        Guide.IsVisible = BackButton.IsVisible = FinishButton.IsVisible = Error.IsVisible = false;
        StepLabel.Text = "WELCOME / STEP 1 OF 2";
        Heading.Text = "Your files. Your choice.";
        SkipButton.Focus();
    }

    private void OnDismiss(object? sender, EventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);

    private async void OnOpenSettings(object? sender, EventArgs e)
    {
        Error.IsVisible = false;
        OpenSettingsButton.IsEnabled = false;
        try
        {
            var uri = "ms-settings:defaultapps";
#if WINDOWS
            if (AppInfo.Current.PackagingModel == AppPackagingModel.Packaged &&
                OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                var appId = Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App";
                uri += "?registeredAUMID=" + Uri.EscapeDataString(appId);
            }
#endif
            if (!await Launcher.Default.OpenAsync(new Uri(uri)))
                throw new InvalidOperationException("Windows could not open Default Apps settings. Open Settings > Apps > Default apps manually.");
        }
        catch (Exception ex)
        {
            Error.Text = $"Could not open default app settings: {ex.Message}";
            Error.IsVisible = true;
            LocalMorph.Core.Diagnostics.DiagnosticLog.Error("Could not open default app settings", ex);
        }
        finally { OpenSettingsButton.IsEnabled = true; }
    }
}
