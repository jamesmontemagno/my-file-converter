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
                Process.Start(new ProcessStartInfo("/usr/bin/open", $"-R \"{path}\"") { UseShellExecute = false });
                return true;
            }

            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (folder is null) return false;
            Process.Start(new ProcessStartInfo("xdg-open", $"\"{folder}\"") { UseShellExecute = false });
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
                var script = $"tell application \"Terminal\" to do script \"{command.Replace("\"", "\\\"")}\"";
                Process.Start(new ProcessStartInfo("/usr/bin/osascript", $"-e '{script}' -e 'tell application \"Terminal\" to activate'") { UseShellExecute = false });
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
