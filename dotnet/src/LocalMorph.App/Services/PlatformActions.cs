using System.Diagnostics;
using LocalMorph.Core.Tools;
using LocalMorph.Core.Jobs;

namespace LocalMorph.App.Services;

/// <summary>Desktop integration that MAUI does not wrap: reveal in Explorer/Finder, launch a terminal installer.</summary>
public static class PlatformActions
{
    public static async Task<string?> ChooseOutputAsync(IReadOnlyList<OutputResult> outputs)
    {
        if (outputs.Count == 0) return null;
        if (outputs.Count == 1) return outputs[0].Path;
        var paths = outputs.Select(output => output.Path).ToArray();
        var selected = await Shell.Current.DisplayActionSheetAsync("Choose a result", "Cancel", null, paths);
        return paths.Contains(selected) ? selected : null;
    }

    public static bool RevealInFolder(string path)
    {
        if (Distribution.IsMacStoreBuild) return false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return true;
            }

            if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
            {
                var open = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                open.ArgumentList.Add("-R");
                open.ArgumentList.Add(path);
                Process.Start(open);
                return true;
            }

            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (folder is null) return false;
            var xdg = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            xdg.ArgumentList.Add(folder);
            Process.Start(xdg);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> RevealInFolderAsync(string path)
    {
        if (!Distribution.IsMacStoreBuild) return (File.Exists(path) || Directory.Exists(path)) && RevealInFolder(path);
        try
        {
            using var access = await FileAccessService.RequestAsync([path]);
            if (access is null) return false;
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (folder is null) return false;
            using var folderAccess = await FileAccessService.RequestAsync([folder]);
            if (folderAccess is null) return false;
#if MACCATALYST
            using var url = Foundation.NSUrl.FromFilename(folder);
            return await UIKit.UIApplication.SharedApplication.OpenUrlAsync(url, new UIKit.UIApplicationOpenUrlOptions());
#else
            return false;
#endif
        }
        catch (Exception ex)
        {
            await Shell.Current.DisplayAlertAsync("Could not open folder", ex.Message, "OK");
            return false;
        }
    }

    public static async Task<bool> OpenAsync(string path)
    {
        if (Distribution.IsMacStoreBuild)
        {
            try
            {
                using var access = await FileAccessService.RequestAsync([path]);
                if (access is null || !File.Exists(path)) return false;
#if MACCATALYST
                using var url = Foundation.NSUrl.FromFilename(path);
                return await UIKit.UIApplication.SharedApplication.OpenUrlAsync(url, new UIKit.UIApplicationOpenUrlOptions());
#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                await Shell.Current.DisplayAlertAsync("Could not open file", ex.Message, "OK");
                return false;
            }
        }
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            return await Launcher.Default.OpenAsync(new OpenFileRequest(Path.GetFileName(path), new ReadOnlyFile(path)));
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Opens a terminal running the package-manager install command so the user can watch and approve it. Store codecs open their Microsoft Store page instead.</summary>
    public static bool LaunchInstaller(ToolKind kind)
    {
        if (Distribution.IsMacStoreBuild) return false;
        var descriptor = ToolCatalog.Get(kind);
        if (descriptor.IsStoreCodec) return OpenStorePage(ToolCatalog.StoreUri(descriptor), descriptor.WebsiteUrl);

        var command = ToolCatalog.InstallCommand(kind);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/k title LocalMorph - Installing {ToolCatalog.Get(kind).DisplayName} && {command}") { UseShellExecute = true });
                return true;
            }

            if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
            {
                var osascript = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
                osascript.ArgumentList.Add("-e");
                osascript.ArgumentList.Add($"tell application \"Terminal\" to do script \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
                osascript.ArgumentList.Add("-e");
                osascript.ArgumentList.Add("tell application \"Terminal\" to activate");
                Process.Start(osascript);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    public static bool CanLaunchInstaller => !Distribution.IsMacStoreBuild && (OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS());

    /// <summary>Opens the Microsoft Store app at a product page, falling back to the web store when the ms-windows-store: protocol is unavailable.</summary>
    public static bool OpenStorePage(string storeUri, string webUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(storeUri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo(webUrl) { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
