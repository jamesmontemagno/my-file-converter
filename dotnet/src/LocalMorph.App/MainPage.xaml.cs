using System.ComponentModel;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using LocalMorph.App.ViewModels;
using LocalMorph.Core.Jobs;

namespace LocalMorph.App;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel viewModel;
    private FileItemViewModel? previewed;
    private bool initialized;

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
        await viewModel.InitializeAsync();

        // Files passed on the command line ("Open with LocalMorph", drag onto the exe, scripts).
        var arguments = Environment.GetCommandLineArgs().Skip(1).Where(argument => File.Exists(argument) || Directory.Exists(argument)).ToList();
        if (arguments.Count > 0) await viewModel.AddPathsAsync(arguments);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedFile) or nameof(MainViewModel.View))
        {
            UpdatePreview();
        }
    }

    // ------------------------------------------------------------ preview

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
            catch
            {
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
                var completion = new TaskCompletionSource<Foundation.NSUrl[]>();
                session.LoadObjects<Foundation.NSUrl>(urls => completion.TrySetResult(urls));
                var urls = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
                paths.AddRange(urls.Select(url => url.Path).Where(path => !string.IsNullOrWhiteSpace(path))!);
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
        catch
        {
        }

        if (paths.Count > 0) await viewModel.AddPathsAsync(paths);
    }
}
