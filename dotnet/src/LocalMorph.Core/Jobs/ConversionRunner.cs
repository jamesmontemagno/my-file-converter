using System.Diagnostics;
using LocalMorph.Core.Engines;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Jobs;

/// <summary>Executes a job's plan step by step, streaming progress and capturing the tool's stderr for diagnostics.</summary>
public static class ConversionRunner
{
    public static async Task RunAsync(ConversionJob job, ToolInventory tools, string workDirectory, CancellationToken externalToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken, job.Cancellation.Token);
        var token = linked.Token;

        ConversionPlan plan;
        try
        {
            Directory.CreateDirectory(workDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath) ?? ".");
            plan = EngineRegistry.Get(job.Engine).Plan(job, tools, workDirectory);
            job.CommandLine = plan.Describe();
        }
        catch (Exception ex)
        {
            job.MarkFailed($"Could not prepare the conversion: {ex.Message}");
            return;
        }

        job.MarkRunning(plan.Steps[0].Label);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            foreach (var step in plan.Steps)
            {
                token.ThrowIfCancellationRequested();
                job.ReportProgress(step.ProgressStart, step.Label);
                job.AppendLog($"$ {CommandLine.Describe(step.StartInfo)}");
                var exitCode = await RunStepAsync(job, step, token);
                if (exitCode != 0)
                {
                    job.MarkFailed(DescribeFailure(job, exitCode));
                    return;
                }
            }

            if (plan.Finalize is not null) await plan.Finalize(token);

            var output = new FileInfo(job.OutputPath);
            if (!output.Exists || output.Length == 0)
            {
                job.MarkFailed("The tool finished but no output file was written." + LogHint(job));
                return;
            }

            job.MarkCompleted(new JobResult(job.OutputPath, output.Length, stopwatch.Elapsed, job.CommandLine ?? string.Empty));
        }
        catch (OperationCanceledException)
        {
            TryDelete(job.OutputPath);
            job.MarkCanceled();
        }
        catch (Exception ex)
        {
            TryDelete(job.OutputPath);
            job.MarkFailed(ex.Message + LogHint(job));
        }
        finally
        {
            try { plan.Cleanup?.Invoke(); } catch { }
        }
    }

    private static async Task<int> RunStepAsync(ConversionJob job, EngineStep step, CancellationToken token)
    {
        using var process = new Process { StartInfo = step.StartInfo, EnableRaisingEvents = true };
        var span = step.ProgressEnd - step.ProgressStart;
        string? lastSpeed = null;
        var stepStart = DateTime.UtcNow;

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null) return;
            if (step.ParseStdout?.Invoke(eventArgs.Data) is { } sample) Apply(sample);
            else if (step.ParseStdout is null && eventArgs.Data.Length > 0) job.AppendLog(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (string.IsNullOrWhiteSpace(eventArgs.Data)) return;
            if (step.ParseStderr?.Invoke(eventArgs.Data) is { } sample) Apply(sample);
            else job.AppendLog(eventArgs.Data);
        };

        void Apply(ProgressSample sample)
        {
            if (sample.Speed is not null) lastSpeed = sample.Speed;
            if (sample.Ratio is { } ratio)
            {
                var elapsed = (DateTime.UtcNow - stepStart).TotalSeconds;
                double? remaining = ratio > 0.02 ? elapsed * (1 - ratio) / ratio : null;
                var overall = step.ProgressStart + ratio * span;
                var message = step.IsIndeterminate ? step.Label : $"{step.Label} · {overall * 100:0}%";
                if (lastSpeed is not null) message += $" · {lastSpeed}";
                if (remaining is { } seconds && seconds < 36_000) message += $" · {FormatRemaining(seconds)} left";
                job.ReportProgress(overall, message, remaining, lastSpeed);
            }
            else if (sample.Speed is not null && !step.IsIndeterminate)
            {
                job.ReportProgress(job.Progress, null, job.EstimatedSecondsRemaining, lastSpeed);
            }
        }

        if (!process.Start()) throw new InvalidOperationException($"Could not start {Path.GetFileName(step.StartInfo.FileName)}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            throw;
        }

        // Drain async readers so the log tail is complete.
        await Task.Delay(20, CancellationToken.None);
        return process.ExitCode;
    }

    private static string DescribeFailure(ConversionJob job, int exitCode)
    {
        var lines = job.LogTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('$'))
            .ToList();
        var relevant = lines.LastOrDefault(line =>
            line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase)) ?? lines.LastOrDefault();

        var friendly = relevant switch
        {
            null => $"The converter exited with code {exitCode}.",
            _ when relevant.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase) => "This FFmpeg build does not include the required encoder. Try a different format or update FFmpeg.",
            _ when relevant.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) => "Permission denied writing the output. Choose another output folder.",
            _ when relevant.Contains("No space left", StringComparison.OrdinalIgnoreCase) => "The disk is full.",
            _ when relevant.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase) => "The source file appears to be corrupt or is not a media file FFmpeg can read.",
            _ when relevant.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase) => "The source video is incomplete (missing moov atom). It may still be recording or was truncated.",
            _ when relevant.Contains("height not divisible", StringComparison.OrdinalIgnoreCase) || relevant.Contains("width not divisible", StringComparison.OrdinalIgnoreCase) => "The video dimensions are odd-sized for this codec. Pick a resolution preset to fix it.",
            _ when relevant.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase) => "The source contains no usable media stream.",
            _ when relevant.Contains("Stream map", StringComparison.OrdinalIgnoreCase) && relevant.Contains("matches no streams", StringComparison.OrdinalIgnoreCase) => "The source has no audio track to extract.",
            _ => Shorten(relevant, 220)
        };
        return friendly;
    }

    private static string LogHint(ConversionJob job) => string.IsNullOrWhiteSpace(job.LogTail) ? string.Empty : " See the log for details.";

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string FormatRemaining(double seconds) => seconds switch
    {
        < 60 => $"{Math.Ceiling(seconds):0}s",
        < 3600 => $"{Math.Floor(seconds / 60):0}m {seconds % 60:00}s",
        _ => $"{Math.Floor(seconds / 3600):0}h {Math.Floor(seconds % 3600 / 60):00}m"
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
