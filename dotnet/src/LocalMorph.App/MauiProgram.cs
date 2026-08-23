using CommunityToolkit.Maui;
using LocalMorph.App.Services;
using LocalMorph.App.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
#if MAUI_DEVFLOW
using Microsoft.Maui.DevFlow.Agent;
#endif

namespace LocalMorph.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.UseMauiCommunityToolkitMediaElement(false)
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
				fonts.AddFont("FluentSystemIcons-Regular.ttf", "FluentIcons");
			});

		builder.Services.AddSingleton(Preferences.Default);
		builder.Services.AddSingleton<AppSettings>();
		builder.Services.AddSingleton<UpdateService>();
		builder.Services.AddSingleton<ConversionService>();
		builder.Services.AddSingleton<MainViewModel>();
		builder.Services.AddSingleton<MainPage>();

#if WINDOWS
		// HEIC/HEIF decode through the Windows Imaging Component (Store "HEIF Image Extensions" codec).
		LocalMorph.Core.Imaging.PlatformImageCodec.Current = new Platforms.Windows.WindowsImageCodec();
#endif

#if MACCATALYST
		// Finder "Open with" / double-click: files arrive as URL contexts on the scene, either at
		// connect time (cold start) or later while the app is running.
		builder.ConfigureLifecycleEvents(events => events.AddiOS(ios => ios
			.SceneWillConnect((scene, session, options) => FileActivationService.Open(UrlPaths(options.UrlContexts)))
			.SceneOpenUrl((scene, contexts) =>
			{
				FileActivationService.Open(UrlPaths(contexts));
				return true;
			})
			.OpenUrl((app, url, options) =>
			{
				if (url.IsFileUrl && url.Path is { } path) FileActivationService.Open([path]);
				return true;
			})));
#endif

#if MAUI_DEVFLOW
		builder.AddMauiDevFlowAgent();
#endif

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

#if MACCATALYST
	private static IEnumerable<string> UrlPaths(Foundation.NSSet<UIKit.UIOpenUrlContext>? contexts)
	{
		if (contexts is null) yield break;
		foreach (var context in contexts)
		{
			if (context.Url.IsFileUrl && context.Url.Path is { Length: > 0 } path) yield return path;
		}
	}
#endif
}
