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
        refresh = RefreshCoreAsync(verifyHardware, token);
        return refresh;
    }

    private async Task<ToolInventory> RefreshCoreAsync(bool verifyHardware, CancellationToken token)
    {
        var quick = await ToolInventory.DiscoverAsync(verifyHardware: false, token: token);
        tools = quick;
        ToolsChanged?.Invoke(quick);

        if (verifyHardware && quick.HasFfmpeg)
        {
            var verified = await ToolInventory.DiscoverAsync(verifyHardware: true, token: token);
            tools = verified;
            ToolsChanged?.Invoke(verified);
            return verified;
        }

        return quick;
    }

    private void OnJobChanged(ConversionJob job)
    {
        if (job.State != JobState.Completed || job.Result is null) return;
        var entry = new HistoryEntry(
            DateTimeOffset.Now,
            job.Source.Path,
            job.Result.OutputPath,
            job.Format.Id,
            job.Format.DisplayName,
            job.Source.SizeBytes,
            job.Result.OutputBytes,
            job.Result.Elapsed.TotalSeconds,
            job.Engine.ToString());
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
