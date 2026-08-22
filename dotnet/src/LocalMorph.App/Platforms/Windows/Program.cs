using System.Runtime.InteropServices;
using LocalMorph.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using WinRT;

namespace LocalMorph.App.WinUI;

/// <summary>
/// Custom entry point so LocalMorph is single-instance: a second launch (Explorer "Open with",
/// double-click, command line) forwards its files to the running window and exits.
/// </summary>
public static class Program
{
	private const string InstanceKey = "LocalMorph.Main";

	[STAThread]
	private static int Main(string[] args)
	{
		ComWrappersSupport.InitializeComWrappers();

		var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
		var main = AppInstance.FindOrRegisterForKey(InstanceKey);
		if (!main.IsCurrent)
		{
			// We hold the foreground right now (the user just launched us), so grant it to the main
			// instance before handing over the activation; otherwise its window can't come to the front.
			AllowSetForegroundWindow((int)main.ProcessId);
			main.RedirectActivationToAsync(activation).AsTask().GetAwaiter().GetResult();
			return 0;
		}

		main.Activated += OnRedirectedActivation;

		Microsoft.UI.Xaml.Application.Start(_ =>
		{
			var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
			SynchronizationContext.SetSynchronizationContext(context);
			new App();
		});

		return 0;
	}

	private static void OnRedirectedActivation(object? sender, AppActivationArguments args) =>
		FileActivationService.Open(ExtractPaths(args));

	/// <summary>Paths from a file activation (packaged) or a launch activation's command line (unpackaged).</summary>
	public static IEnumerable<string> ExtractPaths(AppActivationArguments args)
	{
		switch (args.Kind)
		{
			case ExtendedActivationKind.File when args.Data is IFileActivatedEventArgs file:
				foreach (var item in file.Files)
				{
					if (item is Windows.Storage.IStorageItem storage && !string.IsNullOrWhiteSpace(storage.Path)) yield return storage.Path;
				}
				break;
			case ExtendedActivationKind.Launch when args.Data is ILaunchActivatedEventArgs launch:
				foreach (var path in SplitCommandLine(launch.Arguments)) yield return path;
				break;
		}
	}

	private static IEnumerable<string> SplitCommandLine(string? commandLine)
	{
		if (string.IsNullOrWhiteSpace(commandLine)) yield break;
		var argv = CommandLineToArgvW(commandLine, out var count);
		if (argv == IntPtr.Zero) yield break;
		try
		{
			// ILaunchActivatedEventArgs.Arguments is the full command line, so argv[0] is the executable.
			var self = Environment.ProcessPath;
			for (var i = 0; i < count; i++)
			{
				var value = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size));
				if (string.IsNullOrWhiteSpace(value)) continue;
				if (i == 0 && (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) continue;
				if (self is not null && string.Equals(Path.GetFullPath(value), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase)) continue;
				if (File.Exists(value) || Directory.Exists(value)) yield return value;
			}
		}
		finally
		{
			LocalFree(argv);
		}
	}

	[DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

	[DllImport("kernel32.dll")]
	private static extern IntPtr LocalFree(IntPtr hMem);

	[DllImport("user32.dll")]
	private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
