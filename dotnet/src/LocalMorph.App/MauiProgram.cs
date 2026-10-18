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
#if LOCALMORPH_MAC_SANDBOX_PROOF
			.UseMauiApp<Platforms.MacCatalyst.SandboxProofApp>()
#else
			.UseMauiApp<App>()
#endif
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
			.SceneWillConnect((scene, session, options) => OpenUrls(options.UrlContexts?.Cast<UIKit.UIOpenUrlContext>().Select(context => context.Url) ?? []))
			.SceneOpenUrl((scene, contexts) =>
			{
				OpenUrls(contexts.Cast<UIKit.UIOpenUrlContext>().Select(context => context.Url));
				return true;
			})
			.OpenUrl((app, url, options) =>
			{
				OpenUrls([url]);
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
	private static void OpenUrls(IEnumerable<Foundation.NSUrl> urls)
	{
		try
		{
			FileActivationService.Open(Distribution.IsMacStoreBuild
				? Platforms.MacCatalyst.MacFileAccess.RegisterUrls(urls)
				: urls.Where(url => url.IsFileUrl).Select(url => url.Path).OfType<string>());
		}
		catch (Exception ex) { FileActivationService.ReportFailure($"Could not authorize activated files: {ex.Message}"); }
	}
#endif
}
