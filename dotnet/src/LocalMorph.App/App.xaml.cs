using Microsoft.Extensions.DependencyInjection;

namespace LocalMorph.App;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new AppShell())
		{
			Width = 960,
			Height = 720,
			MinimumWidth = 960,
			MinimumHeight = 720
		};
	}
}