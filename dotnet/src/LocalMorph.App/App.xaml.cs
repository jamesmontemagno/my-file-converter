using LocalMorph.App.Services;
using LocalMorph.App.ViewModels;

namespace LocalMorph.App;

public partial class App : Application
{
	private readonly MainViewModel viewModel;

	public App(AppSettings settings, MainViewModel viewModel)
	{
		InitializeComponent();
		this.viewModel = viewModel;
		UserAppTheme = settings.Theme;
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell())
		{
			Title = "LocalMorph",
			Width = 1280,
			Height = 860,
			MinimumWidth = 1000,
			MinimumHeight = 680
		};

#if WINDOWS || MACCATALYST
		// Native title bar carries the brand once: app icon, name, and a live subtitle. Interactive
		// chrome (tabs, theme, status) lives in the page so it stays hit-testable and UIA-visible.
		var titleBar = new TitleBar { Title = "LocalMorph", Icon = "localmorph_logo.png", HeightRequest = 40, BackgroundColor = Colors.Transparent, BindingContext = viewModel };
		titleBar.SetBinding(TitleBar.SubtitleProperty, nameof(MainViewModel.TitleSubtitle));
		titleBar.SetAppThemeColor(TitleBar.ForegroundColorProperty, Color.FromArgb("#0B1220"), Color.FromArgb("#F3F6FA"));
		window.TitleBar = titleBar;
#endif
#if WINDOWS
		window.HandlerChanged += (_, _) => ApplyBackdrop(window);
#endif
		return window;
	}

#if WINDOWS
	/// <summary>Mica keeps the window feeling native on Windows 11; older builds fall back to the themed page color.</summary>
	private static void ApplyBackdrop(Window window)
	{
		if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native) return;
		if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
		{
			native.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
		}
	}
#endif
}
