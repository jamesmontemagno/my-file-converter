using System.ComponentModel;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using LocalMorph.App.Services;
using LocalMorph.App.ViewModels;
using LocalMorph.Core.Jobs;

namespace LocalMorph.App;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel viewModel;
    private FileItemViewModel? previewed;
    private bool initialized;
    private bool previewExpanded;

    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        BindingContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (initialized) return;
        initialized = true;
        // Files passed on the command line ("Open with LocalMorph", drag onto the exe, scripts) plus
        // anything the OS hands us later through file activation / a redirected second instance.
        FileActivationService.Open(Environment.GetCommandLineArgs().Skip(1));
        FileActivationService.Subscribe(async paths =>
        {
            await viewModel.AddPathsAsync(paths);
            viewModel.View = WorkspaceView.Convert;
        });
        FileActivationService.Failed += viewModel.ReportError;
        try { await viewModel.InitializeAsync(); }
        catch (Exception ex) { viewModel.ReportError($"Could not discover conversion tools: {ex.Message}"); }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedFile) or nameof(MainViewModel.View))
        {
            UpdatePreview();
        }
    }

    // ------------------------------------------------------------ preview

    private async void OnTogglePreviewSize(object? sender, EventArgs e)
    {
        previewExpanded = !previewExpanded;
        QueueRow.Height = new GridLength(previewExpanded ? 1 : 2, GridUnitType.Star);
        PreviewRow.Height = new GridLength(previewExpanded ? 4 : 3, GridUnitType.Star);
        PreviewSizeButton.Text = previewExpanded ? "Collapse preview" : "Expand preview";
        await ResetPreviewScrollAsync();
    }

    private async Task ResetPreviewScrollAsync()
    {
        try { await PreviewInspector.ScrollToAsync(0, 0, animated: false); }
        catch (Exception ex) { viewModel.ReportError($"Could not show the top of the preview: {ex.Message}"); }
    }

    private void OnPreviewInspectorSizeChanged(object? sender, EventArgs e)
    {
        if (PreviewInspector.Width <= 0 || PreviewInspector.Height <= 0) return;
        PreviewSurface.HeightRequest = Math.Max(120, Math.Min(PreviewInspector.Width * 9 / 16, PreviewInspector.Height - 32));
    }

    private void UpdatePreview()
    {
        var file = viewModel.IsConvertView ? viewModel.SelectedFile : null;
        if (ReferenceEquals(file, previewed)) return;
        previewed = file;

        Player.Stop();
        Player.Source = null;
        PreviewImage.Source = null;
        PositionLabel.Text = string.Empty;

        if (file is null || !File.Exists(file.Path)) return;
        _ = ResetPreviewScrollAsync();

        if (file.IsImage)
        {
            Player.IsVisible = false;
            PreviewImage.IsVisible = true;
            PreviewImage.Source = ImageSource.FromFile(file.Path);
        }
        else if (file.IsVideo || file.IsAudio)
        {
            PreviewImage.IsVisible = false;
            Player.IsVisible = true;
            try
            {
                Player.Source = MediaSource.FromFile(file.Path);
            }
            catch (Exception ex)
            {
                viewModel.ReportError($"Preview is unavailable for this file: {ex.Message}");
                Player.IsVisible = false;
            }
        }
        else
        {
            Player.IsVisible = false;
            PreviewImage.IsVisible = false;
        }
    }

    private void OnPlayerPositionChanged(object? sender, MediaPositionChangedEventArgs e)
    {
        if (viewModel.SelectedFile is { HasTimeline: true })
        {
            PositionLabel.Text = $"· at {SourceFile.FormatDuration(e.Position.TotalSeconds)}";
        }
    }

    private double CurrentPositionSeconds => Player.Position.TotalSeconds;

    private void OnSetTrimStart(object? sender, EventArgs e) => viewModel.SetTrimStart(CurrentPositionSeconds);

    private void OnSetTrimEnd(object? sender, EventArgs e) => viewModel.SetTrimEnd(CurrentPositionSeconds);

    private void OnUseCurrentFrame(object? sender, EventArgs e) => viewModel.SetFrameTime(CurrentPositionSeconds);

    // Sliders bind Value one-way: with a two-way binding, re-targeting Maximum during a BindingContext
    // switch clamps the old value and writes it back into the *previous* file's trim points.
    private void OnTrimStartSliderChanged(object? sender, ValueChangedEventArgs e)
    {
        if (sender is Slider { BindingContext: FileItemViewModel file } && ReferenceEquals(file, viewModel.SelectedFile) && Math.Abs(file.TrimStartSeconds - e.NewValue) > 0.01)
        {
            file.TrimStartSeconds = Math.Min(e.NewValue, Math.Max(0, file.TrimEndSeconds - 0.1));
        }
    }

    private void OnTrimEndSliderChanged(object? sender, ValueChangedEventArgs e)
    {
        if (sender is Slider { BindingContext: FileItemViewModel file } && ReferenceEquals(file, viewModel.SelectedFile) && Math.Abs(file.TrimEndSeconds - e.NewValue) > 0.01)
        {
            file.TrimEndSeconds = Math.Max(e.NewValue, file.TrimStartSeconds + 0.1);
        }
    }

    private void OnFrameSliderChanged(object? sender, ValueChangedEventArgs e)
    {
        if (sender is Slider { BindingContext: FileItemViewModel file } && ReferenceEquals(file, viewModel.SelectedFile) && Math.Abs(file.FrameTimeSeconds - e.NewValue) > 0.01)
        {
            file.FrameTimeSeconds = e.NewValue;
        }
    }

    // ------------------------------------------------------------ drag & drop

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        viewModel.IsDragOver = true;
#if WINDOWS
        if (e.PlatformArgs?.DragEventArgs is { } args)
        {
            args.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            args.DragUIOverride.Caption = "Add to LocalMorph";
            args.DragUIOverride.IsCaptionVisible = true;
        }
#endif
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => viewModel.IsDragOver = false;

    private async void OnDrop(object? sender, DropEventArgs e)
    {
        viewModel.IsDragOver = false;
        var paths = new List<string>();
        try
        {
#if WINDOWS
            if (e.PlatformArgs?.DragEventArgs.DataView is { } dataView && dataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await dataView.GetStorageItemsAsync();
                paths.AddRange(items.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)));
            }
#elif MACCATALYST
            if (e.PlatformArgs?.DropSession is { } session && session.CanLoadObjects(new ObjCRuntime.Class(typeof(Foundation.NSUrl))))
            {
                var completion = new TaskCompletionSource<Foundation.INSItemProviderReading[]>();
                session.LoadObjects(new ObjCRuntime.Class(typeof(Foundation.NSUrl)), items => completion.TrySetResult(items));
                var items = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
                paths.AddRange(items.OfType<Foundation.NSUrl>().Select(url => url.Path).Where(path => !string.IsNullOrWhiteSpace(path))!);
            }
#endif
            if (paths.Count == 0 && e.Data.Properties.TryGetValue("FileNames", out var fileNames) && fileNames is IEnumerable<string> names)
            {
                paths.AddRange(names);
            }
            if (paths.Count == 0 && await e.Data.GetTextAsync() is { Length: > 0 } text)
            {
                paths.AddRange(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => line.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(line, UriKind.Absolute, out var uri) ? uri.LocalPath : line)
                    .Where(path => File.Exists(path) || Directory.Exists(path)));
            }
        }
        catch (Exception ex)
        {
            viewModel.ReportError($"Could not read the dropped files: {ex.Message}");
        }

        if (paths.Count > 0) await viewModel.AddPathsAsync(paths);
    }
}
