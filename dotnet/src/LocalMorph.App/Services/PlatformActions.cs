using System.Diagnostics;
using LocalMorph.Core.Tools;

namespace LocalMorph.App.Services;

/// <summary>Desktop integration that MAUI does not wrap: reveal in Explorer/Finder, launch a terminal installer.</summary>
public static class PlatformActions
{
    public static bool RevealInFolder(string path)
    {
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

    public static async Task<bool> OpenAsync(string path)
    {
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

    /// <summary>Opens a terminal running the package-manager install command so the user can watch and approve it.</summary>
    public static bool LaunchInstaller(ToolKind kind)
    {
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

    public static bool CanLaunchInstaller => OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS();
}
