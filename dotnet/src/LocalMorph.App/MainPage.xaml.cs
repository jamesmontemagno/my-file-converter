using System.ComponentModel;
using CommunityToolkit.Maui.Views;
using LocalMorph.App.ViewModels;

namespace LocalMorph.App;

public partial class MainPage : ContentPage
{
    private readonly MainPageViewModel _viewModel;

    public MainPage()
    {
        InitializeComponent();
        _viewModel = new MainPageViewModel();
        BindingContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainPageViewModel.SelectedFilePath))
        {
            UpdateSelectedPreview();
        }
    }

    private void UpdateSelectedPreview()
    {
        var path = _viewModel.SelectedFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            PreviewMediaElement.Stop();
            PreviewMediaElement.Source = null;
            PreviewImage.Source = null;
            return;
        }

        if (_viewModel.IsImageSource)
        {
            PreviewMediaElement.Stop();
            PreviewMediaElement.Source = null;
            PreviewImage.Source = ImageSource.FromFile(path);
            return;
        }

        PreviewImage.Source = null;
        PreviewMediaElement.Source = MediaSource.FromFile(path);
    }
}
