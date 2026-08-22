using System.Collections.Concurrent;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Jobs;

/// <summary>Runs queued jobs with bounded parallelism. Jobs can be cancelled individually or all at once.</summary>
public sealed class ConversionQueue : IDisposable
{
    private readonly ConcurrentQueue<ConversionJob> pending = new();
    private readonly List<ConversionJob> all = [];
    private readonly object gate = new();
    private readonly string workDirectory;
    private CancellationTokenSource stopAll = new();
    private Task? pump;
    private int maxParallel;

    public ConversionQueue(string? workDirectory = null, int maxParallel = 2)
    {
        this.workDirectory = workDirectory ?? Path.Combine(Path.GetTempPath(), "LocalMorph", "work");
        this.maxParallel = Math.Max(1, maxParallel);
    }

    public Func<ToolInventory> Tools { get; set; } = () => ToolInventory.Empty;

    public int MaxParallel
    {
        get => maxParallel;
        set => maxParallel = Math.Clamp(value, 1, 16);
    }

    public IReadOnlyList<ConversionJob> Jobs
    {
        get { lock (gate) return all.ToArray(); }
    }

    public bool IsRunning => pump is { IsCompleted: false };
    public int PendingCount => pending.Count;
    public int ActiveCount { get { lock (gate) return all.Count(job => job.IsActive); } }

    public event Action<ConversionJob>? JobAdded;
    public event Action<ConversionJob>? JobChanged;
    public event Action? Drained;

    public void Enqueue(ConversionJob job)
    {
        lock (gate) all.Add(job);
        job.Changed += OnJobChanged;
        pending.Enqueue(job);
        JobAdded?.Invoke(job);
    }

    public void Remove(ConversionJob job)
    {
        job.Cancel();
        lock (gate) all.Remove(job);
        job.Changed -= OnJobChanged;
    }

    public void ClearFinished()
    {
        lock (gate)
        {
            foreach (var job in all.Where(job => job.IsTerminal).ToList())
            {
                job.Changed -= OnJobChanged;
                all.Remove(job);
            }
        }
    }

    public void Start()
    {
        lock (gate)
        {
            if (IsRunning) return;
            if (stopAll.IsCancellationRequested) stopAll = new CancellationTokenSource();
            pump = Task.Run(() => PumpAsync(stopAll.Token));
        }
    }

    public void CancelAll()
    {
        foreach (var job in Jobs.Where(job => !job.IsTerminal)) job.Cancel();
        while (pending.TryDequeue(out var job)) job.Cancel();
    }

    public async Task WaitForIdleAsync(CancellationToken token = default)
    {
        while (true)
        {
            var current = pump;
            if (current is null || current.IsCompleted) return;
            await Task.WhenAny(current, Task.Delay(Timeout.Infinite, token));
            token.ThrowIfCancellationRequested();
        }
    }

    private async Task PumpAsync(CancellationToken token)
    {
        var running = new List<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                running.RemoveAll(task => task.IsCompleted);
                if (running.Count < maxParallel && pending.TryDequeue(out var job))
                {
                    if (job.IsTerminal) continue;
                    running.Add(RunJobAsync(job, token));
                    continue;
                }

                if (running.Count == 0 && pending.IsEmpty) break;
                if (running.Count == 0) { await Task.Delay(50, token); continue; }
                await Task.WhenAny(running.Concat([Task.Delay(250, token)]));
            }

            await Task.WhenAll(running);
        }
        catch (OperationCanceledException) { }
        finally
        {
            Drained?.Invoke();
        }
    }

    private async Task RunJobAsync(ConversionJob job, CancellationToken token)
    {
        try
        {
            await ConversionRunner.RunAsync(job, Tools(), workDirectory, token);
        }
        catch (Exception ex)
        {
            job.MarkFailed(ex.Message);
        }
    }

    private void OnJobChanged(ConversionJob job) => JobChanged?.Invoke(job);

    public void Dispose()
    {
        CancelAll();
        stopAll.Cancel();
        stopAll.Dispose();
    }
}

/// <summary>Decides where an output goes and how name collisions are handled.</summary>
public static class OutputNaming
{
    public static string Sanitize(string stem)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(stem.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim().Trim('.', '-');
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "converted";
        return cleaned.Length > 180 ? cleaned[..180] : cleaned;
    }

    public static string BuildOutputPath(string sourcePath, OutputFormat format, string? outputDirectory, string suffix, OverwritePolicy policy, ISet<string>? reserved = null)
    {
        var directory = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.GetDirectoryName(sourcePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : outputDirectory;
        var stem = Sanitize(Path.GetFileNameWithoutExtension(sourcePath) + suffix);
        var candidate = Path.Combine(directory, stem + format.ExtensionWithDot);

        // Never clobber the source even when overwriting.
        var collidesWithSource = string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase);
        if (policy == OverwritePolicy.Overwrite && !collidesWithSource && (reserved is null || !reserved.Contains(candidate)))
        {
            reserved?.Add(candidate);
            return candidate;
        }

        var index = 2;
        while (File.Exists(candidate) || collidesWithSource || reserved?.Contains(candidate) == true)
        {
            candidate = Path.Combine(directory, $"{stem} ({index++}){format.ExtensionWithDot}");
            collidesWithSource = false;
        }

        reserved?.Add(candidate);
        return candidate;
    }
}
