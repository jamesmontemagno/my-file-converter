using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace LocalMorph.Bridge;

public sealed class JobStore
{
    private readonly ConcurrentDictionary<Guid, JobRecord> jobs = new();

    public JobRecord Create(string directory, string outputPath, string outputName)
    {
        var record = new JobRecord(Guid.NewGuid(), directory, outputPath, outputName);
        if (!jobs.TryAdd(record.Id, record)) throw new InvalidOperationException("Unable to create job.");
        record.Publish();
        return record;
    }

    public bool TryGet(Guid id, out JobRecord? record) => jobs.TryGetValue(id, out record);

    public bool Cancel(Guid id)
    {
        if (!jobs.TryGetValue(id, out var record)) return false;
        record.Cancel();
        return true;
    }

    public async Task CleanupExpiredAsync(TimeSpan ttl, CancellationToken cancellationToken)
    {
        foreach (var (id, record) in jobs)
        {
            if (record.TerminalAt is { } terminal && DateTimeOffset.UtcNow - terminal >= ttl && jobs.TryRemove(id, out _))
            {
                try
                {
                    await DeleteDirectoryAsync(record.Directory, cancellationToken);
                }
                catch (IOException)
                {
                    jobs.TryAdd(id, record);
                }
                catch (UnauthorizedAccessException)
                {
                    jobs.TryAdd(id, record);
                }
            }
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        foreach (var record in jobs.Values) record.Cancel();
        await Task.WhenAll(jobs.Values.Select(record => record.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default)));
        foreach (var record in jobs.Values) await DeleteDirectoryAsync(record.Directory, cancellationToken);
        jobs.Clear();
    }

    private static Task DeleteDirectoryAsync(string directory, CancellationToken cancellationToken) =>
        Task.Run(() => { if (Directory.Exists(directory)) Directory.Delete(directory, true); }, cancellationToken);
}

public sealed class JobRecord
{
    private readonly object gate = new();
    private readonly List<Channel<JobView>> subscribers = [];

    internal JobRecord(Guid id, string directory, string outputPath, string outputName)
    {
        Id = id;
        Directory = directory;
        OutputPath = outputPath;
        OutputName = outputName;
        Cancellation = new CancellationTokenSource();
        View = new JobView(id, JobStatus.Queued, null, null);
    }

    public Guid Id { get; }
    public string Directory { get; }
    public string OutputPath { get; }
    public string OutputName { get; }
    public CancellationTokenSource Cancellation { get; }
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public DateTimeOffset? TerminalAt { get; private set; }
    public JobView View { get; private set; }

    public void Transition(JobStatus status, int? progress = null, string? error = null)
    {
        lock (gate)
        {
            if (!IsTransitionAllowed(View.Status, status)) return;
            View = View with { Status = status, ProgressPercent = progress ?? View.ProgressPercent, Error = error };
            if (IsTerminal(status))
            {
                TerminalAt = DateTimeOffset.UtcNow;
                Completion.TrySetResult();
            }
            Publish();
        }
    }

    public void Progress(int? percent)
    {
        lock (gate)
        {
            if (View.Status != JobStatus.Running) return;
            View = View with { ProgressPercent = percent ?? View.ProgressPercent };
            Publish();
        }
    }

    public void Cancel()
    {
        lock (gate)
        {
            if (IsTerminal(View.Status)) return;
            if (View.Status == JobStatus.Queued)
            {
                View = View with { Status = JobStatus.Canceled };
                TerminalAt = DateTimeOffset.UtcNow;
                Completion.TrySetResult();
                Publish();
            }
            Cancellation.Cancel();
        }
    }

    public (JobView Current, ChannelReader<JobView> Events) SubscribeWithCurrent()
    {
        lock (gate)
        {
            var events = Channel.CreateBounded<JobView>(new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            if (IsTerminal(View.Status))
            {
                events.Writer.TryComplete();
            }
            else
            {
                subscribers.Add(events);
            }
            return (View, events.Reader);
        }
    }

    public JobView Snapshot() { lock (gate) return View; }
    internal void Publish()
    {
        foreach (var subscriber in subscribers) subscriber.Writer.TryWrite(View);
        if (IsTerminal(View.Status))
        {
            foreach (var subscriber in subscribers) subscriber.Writer.TryComplete();
            subscribers.Clear();
        }
    }

    private static bool IsTerminal(JobStatus status) => status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled;
    private static bool IsTransitionAllowed(JobStatus from, JobStatus to) => (from, to) switch
    {
        (JobStatus.Queued, JobStatus.Running or JobStatus.Canceled) => true,
        (JobStatus.Running, JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled) => true,
        _ => false
    };
}

public sealed class JobRunner(JobStore jobs, FfmpegState ffmpegState, BridgeOptions options) : BackgroundService
{
    private readonly ConcurrentQueue<(JobRecord Job, ConversionRequest Request, string Input)> pending = new();
    private readonly SemaphoreSlim signal = new(0);

    public void Enqueue(JobRecord job, ConversionRequest request, string input)
    {
        pending.Enqueue((job, request, input));
        signal.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await signal.WaitAsync(stoppingToken);
            if (pending.TryDequeue(out var work)) _ = RunAsync(work.Job, work.Request, work.Input, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await jobs.ShutdownAsync(cancellationToken);
        if (Directory.Exists(options.JobRoot)) Directory.Delete(options.JobRoot, true);
        await base.StopAsync(cancellationToken);
    }

    private async Task RunAsync(JobRecord job, ConversionRequest request, string input, CancellationToken stoppingToken)
    {
        var ffmpeg = ffmpegState.Info;
        if (ffmpeg is null || job.Snapshot().Status == JobStatus.Canceled) return;
        job.Transition(JobStatus.Running, 0);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancellation.Token);
        try
        {
            var duration = await ProbeDurationAsync(ffmpeg.Path, input, request, linked.Token);
            using var process = new Process { StartInfo = Ffmpeg.BuildCommand(ffmpeg.Path, input, job.OutputPath, request) };
            if (!process.Start()) throw new InvalidOperationException("FFmpeg could not be started");
            var progress = ReadProgressAsync(process.StandardOutput, duration, job, linked.Token);
            var stderr = ReadBoundedAsync(process.StandardError, linked.Token);
            try { await process.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
                job.Transition(JobStatus.Canceled);
                return;
            }
            await progress;
            var error = await stderr;
            if (process.ExitCode == 0 && new FileInfo(job.OutputPath) is { Exists: true, Length: <= BridgeOptions.MaxFileBytes })
            {
                job.Transition(JobStatus.Completed, 100);
                return;
            }
            if (File.Exists(job.OutputPath)) File.Delete(job.OutputPath);
            job.Transition(JobStatus.Failed, error: error.Length == 0 ? "FFmpeg conversion failed or exceeded the output size limit" : $"FFmpeg conversion failed: {error.Split('\n').LastOrDefault(line => !string.IsNullOrWhiteSpace(line))}");
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            job.Transition(JobStatus.Canceled);
        }
        catch
        {
            job.Transition(JobStatus.Failed, error: "FFmpeg could not be started");
        }
    }

    private static async Task ReadProgressAsync(StreamReader output, long? duration, JobRecord job, CancellationToken token)
    {
        while (await output.ReadLineAsync(token) is { } line)
        {
            if (Ffmpeg.ParseProgress(line, duration) is { } update) job.Progress(update.Percent);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var tail = new StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, token) is { } read and > 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > BridgeOptions.MaxStderrBytes) tail.Remove(0, tail.Length - BridgeOptions.MaxStderrBytes);
        }
        return tail.ToString();
    }

    private static async Task<long?> ProbeDurationAsync(string ffmpeg, string input, ConversionRequest request, CancellationToken token)
    {
        var probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (!File.Exists(probe)) return null;
        using var process = new Process { StartInfo = new ProcessStartInfo(probe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", input })
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();
            return null;
        }
        var text = await stdout;
        if (process.ExitCode != 0 || !double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds <= 0 || !double.IsFinite(seconds)) return null;
        var start = request.Media.TrimStart ?? 0;
        var end = request.Media.TrimEnd;
        var effective = Math.Max(0, (end ?? seconds) - start);
        return effective > 0 ? (long)(effective * 1_000_000) : null;
    }
}

public sealed class JobCleanupService(JobStore jobs, BridgeOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            await jobs.CleanupExpiredAsync(options.JobTtl, stoppingToken);
        }
    }
}
