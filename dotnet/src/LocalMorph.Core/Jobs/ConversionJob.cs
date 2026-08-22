using System.Diagnostics;
using LocalMorph.Core.Formats;

namespace LocalMorph.Core.Jobs;

public enum JobState
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled,
    Skipped
}

public enum OverwritePolicy
{
    Rename,
    Overwrite,
    Skip
}

public sealed record JobResult(string OutputPath, long OutputBytes, TimeSpan Elapsed, string Command);

/// <summary>A single conversion: one source file to one output file. Mutable state is published through <see cref="Changed"/>.</summary>
public sealed class ConversionJob
{
    private readonly object gate = new();
    private JobState state = JobState.Queued;
    private double progress;
    private string statusMessage = "Waiting";
    private readonly Stopwatch stopwatch = new();

    public ConversionJob(SourceFile source, OutputFormat format, ConversionOptions options, string outputPath, EngineKind engine)
    {
        Source = source;
        Format = format;
        Options = options;
        OutputPath = outputPath;
        Engine = engine;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public SourceFile Source { get; }
    public OutputFormat Format { get; }
    public ConversionOptions Options { get; }
    public string OutputPath { get; internal set; }
    public EngineKind Engine { get; }
    public JobResult? Result { get; private set; }
    public string? Error { get; private set; }
    public string LogTail { get; private set; } = string.Empty;
    public string? CommandLine { get; internal set; }
    public TimeSpan Elapsed => stopwatch.Elapsed;
    public double? EstimatedSecondsRemaining { get; private set; }
    public string? CurrentSpeed { get; private set; }

    public JobState State
    {
        get { lock (gate) return state; }
    }

    /// <summary>0–1.</summary>
    public double Progress
    {
        get { lock (gate) return progress; }
    }

    public string StatusMessage
    {
        get { lock (gate) return statusMessage; }
    }

    public bool IsTerminal => State is JobState.Completed or JobState.Failed or JobState.Canceled or JobState.Skipped;
    public bool IsActive => State is JobState.Running;

    public event Action<ConversionJob>? Changed;

    internal CancellationTokenSource Cancellation { get; } = new();

    internal void MarkRunning(string message)
    {
        lock (gate)
        {
            state = JobState.Running;
            statusMessage = message;
            progress = 0;
        }
        stopwatch.Restart();
        Changed?.Invoke(this);
    }

    internal void ReportProgress(double value, string? message = null, double? secondsRemaining = null, string? speed = null)
    {
        lock (gate)
        {
            if (state != JobState.Running) return;
            progress = Math.Clamp(value, 0, 1);
            if (message is not null) statusMessage = message;
            EstimatedSecondsRemaining = secondsRemaining;
            CurrentSpeed = speed;
        }
        Changed?.Invoke(this);
    }

    internal void AppendLog(string line)
    {
        lock (gate)
        {
            var combined = LogTail.Length == 0 ? line : LogTail + "\n" + line;
            LogTail = combined.Length > 16_000 ? combined[^16_000..] : combined;
        }
    }

    internal void MarkCompleted(JobResult result)
    {
        stopwatch.Stop();
        lock (gate)
        {
            state = JobState.Completed;
            progress = 1;
            statusMessage = $"Done in {FormatElapsed(stopwatch.Elapsed)} · {SourceFile.FormatBytes(result.OutputBytes)}";
            Result = result;
            EstimatedSecondsRemaining = null;
        }
        Changed?.Invoke(this);
    }

    internal void MarkFailed(string error)
    {
        stopwatch.Stop();
        lock (gate)
        {
            state = JobState.Failed;
            statusMessage = error;
            Error = error;
            EstimatedSecondsRemaining = null;
        }
        Changed?.Invoke(this);
    }

    internal void MarkCanceled()
    {
        stopwatch.Stop();
        lock (gate)
        {
            state = JobState.Canceled;
            statusMessage = "Canceled";
            EstimatedSecondsRemaining = null;
        }
        Changed?.Invoke(this);
    }

    internal void MarkSkipped(string reason)
    {
        lock (gate)
        {
            state = JobState.Skipped;
            statusMessage = reason;
        }
        Changed?.Invoke(this);
    }

    /// <summary>Marks a job that will never run (wrong format for this file, output exists, missing tool).</summary>
    public void Skip(string reason)
    {
        if (IsTerminal) return;
        Cancellation.Cancel();
        MarkSkipped(reason);
    }

    public void Cancel()
    {
        if (IsTerminal) return;
        Cancellation.Cancel();
        if (State == JobState.Queued) MarkCanceled();
    }

    public static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalSeconds < 1
        ? $"{elapsed.TotalMilliseconds:0} ms"
        : elapsed.TotalMinutes < 1
            ? $"{elapsed.TotalSeconds:0.0} s"
            : elapsed.ToString(@"m\:ss");
}
