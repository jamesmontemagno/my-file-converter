using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;

namespace LocalMorph.App.Services;

/// <summary>Owns the tool inventory, the conversion queue, and the history. Lives for the app's lifetime.</summary>
public sealed class ConversionService : IDisposable
{
    private readonly AppSettings settings;
    private ToolInventory tools = ToolInventory.Empty;
    private Task<ToolInventory>? refresh;

    public ConversionService(AppSettings settings)
    {
        this.settings = settings;
        var workDirectory = Path.Combine(FileSystem.Current.CacheDirectory, "work");
        Queue = new ConversionQueue(workDirectory, settings.MaxParallel) { Tools = () => Tools };
        if (Distribution.IsMacStoreBuild) Queue.AcquireFileAccess = FileAccessService.AcquireJob;
        History = new ConversionHistory(Path.Combine(FileSystem.Current.AppDataDirectory, "history.json"));
        Queue.JobChanged += OnJobChanged;
    }

    public ConversionQueue Queue { get; }
    public ConversionHistory History { get; }
    public ToolInventory Tools => tools;
    public bool IsRefreshing => refresh is { IsCompleted: false };

    public event Action<ToolInventory>? ToolsChanged;
    public event Action<HistoryEntry>? HistoryAdded;

    /// <summary>Finds tools on this device. The first pass is quick (no hardware probing) so the UI can light up fast; the second verifies hardware encoders.</summary>
    public Task<ToolInventory> RefreshToolsAsync(bool verifyHardware = true, CancellationToken token = default)
    {
        if (refresh is { IsCompleted: false }) return refresh;
        refresh = Task.Run(() => RefreshCoreAsync(verifyHardware, token), token);
        return refresh;
    }

    private async Task<ToolInventory> RefreshCoreAsync(bool verifyHardware, CancellationToken token)
    {
        var quick = await DiscoverToolsAsync(verifyHardware: false, token);
        tools = quick;
        ToolsChanged?.Invoke(quick);

        if (verifyHardware && quick.HasFfmpeg)
        {
            var verified = await DiscoverToolsAsync(verifyHardware: true, token);
            tools = verified;
            LogInventory(verified);
            ToolsChanged?.Invoke(verified);
            return verified;
        }

        LogInventory(quick);
        return quick;
    }

    private static void LogInventory(ToolInventory inventory)
    {
        var tools = inventory.Tools.Values.Select(tool => $"{tool.Kind} {tool.ShortVersion} [{tool.Source}] {tool.Path}");
        var hardware = string.Join(", ", inventory.Ffmpeg.WorkingHardwareEncoders.Select(encoder => encoder.Encoder));
        LocalMorph.Core.Diagnostics.DiagnosticLog.Info(
            $"Tools: {(inventory.Tools.Count == 0 ? "none found" : string.Join("; ", tools))}. FFmpeg encoders: {inventory.Ffmpeg.Encoders.Count}; hardware: {(hardware.Length == 0 ? "none" : hardware)}");
    }

    private static Task<ToolInventory> DiscoverToolsAsync(bool verifyHardware, CancellationToken token)
    {
#if MACCATALYST
        if (Distribution.IsMacStoreBuild)
        {
            var bundle = Platforms.MacCatalyst.MacFileAccess.CanonicalPath(Foundation.NSBundle.MainBundle.BundlePath);
            var directory = Path.Combine(bundle, "Contents", "MacOS");
            if (!string.Equals(directory, Platforms.MacCatalyst.MacFileAccess.CanonicalPath(directory), StringComparison.Ordinal))
                throw new IOException("The bundled media-tool directory was redirected. Repair or update LocalMorph.");
            return ToolInventory.DiscoverBundledMediaAsync(
                directory, verifyHardware, token);
        }
#endif
        return ToolInventory.DiscoverAsync(verifyHardware: verifyHardware, token: token);
    }

    private void OnJobChanged(ConversionJob job)
    {
        if (!job.IsTerminal || job.Result is null || job.CompletedOutputs.Count == 0) return;
        var entry = new HistoryEntry(
            DateTimeOffset.Now,
            job.Source.Path,
            job.Result.OutputPath,
            job.Format.Id,
            job.Format.DisplayName,
            job.Inputs.Sum(input => input.SizeBytes),
            job.Result.OutputBytes,
            job.Result.Elapsed.TotalSeconds,
            job.Engine.ToString(),
            job.Inputs.Select(input => input.Path).ToArray(), job.CompletedOutputs, job.Operation, job.RequestedOptions, job.State);
        History.Add(entry);
        HistoryAdded?.Invoke(entry);
    }

    public void ApplySettings() => Queue.MaxParallel = settings.MaxParallel;

    public void Dispose()
    {
        Queue.JobChanged -= OnJobChanged;
        Queue.Dispose();
    }
}
