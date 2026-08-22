using LocalMorph.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace LocalMorph.App.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	public App()
	{
		this.InitializeComponent();
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	protected override void OnLaunched(LaunchActivatedEventArgs args)
	{
		base.OnLaunched(args);

		// Packaged "Open with" on a cold start arrives as a File activation rather than command-line args.
		try
		{
			var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
			if (activation?.Kind == ExtendedActivationKind.File) FileActivationService.Open(Program.ExtractPaths(activation));
		}
		catch
		{
		}

		FileActivationService.Activated += BringToFront;
	}

	private void BringToFront()
	{
		var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
		if (window is null) return;
		window.DispatcherQueue.TryEnqueue(() =>
		{
			var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
			var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
			if (Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id)?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
			{
				presenter.Restore();
			}
			window.Activate();
			SetForegroundWindow(hwnd);
		});
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);
}
