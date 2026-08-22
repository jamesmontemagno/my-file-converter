using LocalMorph.App.Services;

namespace LocalMorph.App;

public partial class App : Application
{
	public App(AppSettings settings)
	{
		InitializeComponent();
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
		var titleBar = new TitleBar { Title = "LocalMorph", Subtitle = "Local file conversion", HeightRequest = 40 };
		titleBar.SetAppThemeColor(TitleBar.BackgroundColorProperty, Color.FromArgb("#F3F5F9"), Color.FromArgb("#0D1117"));
		titleBar.SetAppThemeColor(TitleBar.ForegroundColorProperty, Color.FromArgb("#0B1220"), Color.FromArgb("#F3F6FA"));
		window.TitleBar = titleBar;
#endif
		return window;
	}
}
