using System.Diagnostics;
using System.Globalization;
using LocalMorph.Bridge;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Engines;

public static class MediaOperations
{
    public static string? JoinIssue(IReadOnlyList<SourceFile> inputs, OutputFormat format)
    {
        if (inputs.Count < 2) return "Join needs at least two clips in queue order.";
        if (format.VideoCodec != "copy") return "Choose MP4 or MKV Remux for a lossless join. LocalMorph never silently re-encodes incompatible clips.";
        var first = inputs[0];
        if (first.Category != MediaCategory.Video || first.Media?.Streams is not { Count: > 0 } streams)
            return "Join needs inspected video clips with known streams.";
        foreach (var input in inputs)
        {
            if (input.DurationSeconds is not > 0 || input.Media?.Streams is not { } other || input.Category != MediaCategory.Video)
                return $"{input.FileName}: inspect the clip before joining.";
            if (input.Path.Contains('\n') || input.Path.Contains('\r')) return "Join cannot use a filename containing a newline.";
            if (streams.Count != other.Count) return $"{input.FileName}: stream counts differ. Export matching clips before joining.";
            for (var index = 0; index < streams.Count; index++)
            {
                var left = streams[index];
                var right = other[index];
                if (left.Signature is null || right.Signature is null) return $"{input.FileName}: stream {index + 1} has no compatibility signature. Refresh Tools to inspect it again.";
                if (left.Type != right.Type || left.Codec != right.Codec || left.Channels != right.Channels || left.ChannelLayout != right.ChannelLayout || left.IsAttachedPicture != right.IsAttachedPicture)
                    return $"{input.FileName}: stream {index + 1} differs in codec, stream order, or audio layout.";
                if (left.Signature != right.Signature)
                    return $"{input.FileName}: stream {index + 1} differs in codec initialization, pixel format, geometry, frame/time base, sample format/rate, rotation, or color metadata.";
                if (left.Signature.ExtraDataSize is > 0 && left.Signature.ExtraDataHash is null)
                    return $"{input.FileName}: codec initialization could not be verified.";
                if (left.Type == MediaStreamType.Other || left.IsAttachedPicture)
                    return $"{input.FileName}: attachments and data streams are not supported by lossless join.";
            }
        }
        return null;
    }

    public static ConversionPlan JoinPlan(ConversionJob job, string ffmpeg, string workDirectory)
    {
        if (JoinIssue(job.Inputs, job.Format) is { } issue) throw new InvalidOperationException(issue);
        var list = Path.Combine(workDirectory, $"localmorph-{job.Id:N}.ffconcat");
        var args = new List<string> { "-hide_banner", "-y", "-nostdin", "-loglevel", "error", "-progress", "pipe:1", "-nostats",
            "-f", "concat", "-safe", "0", "-i", list, "-map", "0", "-c", "copy", "-map_chapters", "-1" };
        if (job.Options.StripMetadata) args.AddRange(["-map_metadata", "-1"]);
        args.Add(job.WorkingOutputPath);
        var duration = job.Inputs.Sum(input => input.DurationSeconds!.Value);
        return new ConversionPlan
        {
            Steps =
            [
                new EngineStep
                {
                    Label = "Preparing ordered clips", ProgressEnd = 0,
                    Summary = "Create a private ordered concat list. Copy compatible streams without chapter merging.",
                    Execute = async (_, token) =>
                    {
                        var lines = job.Inputs.Select(input => "file '" + Path.GetFullPath(input.Path).Replace('\\', '/').Replace("'", "'\\''") + "'");
                        await File.WriteAllLinesAsync(list, lines.Prepend("ffconcat version 1.0"), token);
                    }
                },
                new EngineStep { Label = "Joining losslessly · chapters excluded", StartInfo = CommandLine.Create(ffmpeg, args),
                    ParseStdout = line => FfmpegEngine.ParseProgress(line, (long)(duration * 1_000_000)) }
            ],
            Cleanup = () => { if (File.Exists(list)) File.Delete(list); }
        };
    }

    public static IReadOnlyList<(double Start, double End)> Segments(double start, double end, double seconds, IReadOnlyList<double>? keyframes = null)
    {
        if (!double.IsFinite(seconds) || seconds < 0.1 || seconds > 86_400) throw new InvalidOperationException("Clip duration must be between 0.1 and 86400 seconds.");
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start) throw new InvalidOperationException("Splitting needs a known, nonempty source range.");
        if ((end - start) / seconds > 10_000) throw new InvalidOperationException("This would create more than 10,000 clips. Choose a longer clip duration.");
        var points = new List<double> { start };
        for (var next = start + seconds; next < end - 0.000001; next += seconds)
        {
            var boundary = keyframes is null ? next : keyframes.FirstOrDefault(value => value >= next && value < end, double.NaN);
            if (double.IsNaN(boundary)) break;
            if (boundary > points[^1] + 0.000001) points.Add(boundary);
        }
        points.Add(end);
        return points.Zip(points.Skip(1), (from, to) => (from, to)).ToArray();
    }

    public static string? SplitIssue(SourceFile source, OutputFormat format, ConversionOptions options, OperationRequest operation)
    {
        if (source.Category is not (MediaCategory.Video or MediaCategory.Audio)) return "Split needs an audio or video source.";
        if (source.DurationSeconds is not > 0) return "Split needs a known source duration.";
        if (options.TrimSilence) return "Trim edge silence in a separate conversion before splitting.";
        var fast = operation.SplitMode == SplitMode.Fast;
        var copies = format.VideoCodec == "copy" || format.AudioCodec == "copy";
        if (fast != copies) return fast ? "Fast splitting needs a Remux / stream-copy output." : "Accurate splitting needs an encoded audio or video format, not Remux.";
        if (format.Category is not (MediaCategory.Video or MediaCategory.Audio) || !format.Supports(FormatFeatures.Trim)) return "Choose an audio or video output that supports trimming.";
        try { Segments(options.TrimStartSeconds ?? 0, Math.Min(options.TrimEndSeconds ?? double.MaxValue, source.DurationSeconds.Value), operation.SegmentSeconds); }
        catch (InvalidOperationException ex) { return ex.Message; }
        return null;
    }

    public static async Task SplitAsync(ConversionJob job, ToolInventory tools, string workDirectory, CancellationToken externalToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cancellation.Token, externalToken);
        var token = linked.Token;
        job.MarkRunning("Planning split");
        try
        {
            var options = job.RequestedOptions;
            if (job.Inputs.Count != 1) throw new InvalidOperationException("Each split job takes one source.");
            if (options.Validate(job.Format) is { } issue) throw new InvalidOperationException(issue);
            if (SplitIssue(job.Source, job.Format, options, job.Operation) is { } splitIssue) throw new InvalidOperationException(splitIssue);
            var fast = job.Operation.SplitMode == SplitMode.Fast;
            var start = options.TrimStartSeconds ?? 0;
            var end = Math.Min(options.TrimEndSeconds ?? double.MaxValue, job.Source.DurationSeconds ?? throw new InvalidOperationException("Split needs a known source duration."));
            IReadOnlyList<double>? keys = null;
            if (fast && job.Source.HasVideoStream)
            {
                keys = await KeyframesAsync(job, tools, token);
                if (start > 0) start = keys.FirstOrDefault(value => value >= start, double.NaN);
                if (!double.IsFinite(start) || start >= end) throw new InvalidOperationException("There are no video keyframes in the selected range.");
            }
            var segments = Segments(start, end, job.Operation.SegmentSeconds, keys);
            var paths = new List<string>();
            var reserved = job.Inputs.Select(input => input.Path).Concat(job.ProtectedInputPaths).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < segments.Count; index++)
            {
                var stem = Path.GetFileNameWithoutExtension(job.OutputPath) + $"-{index + 1:D3}";
                var path = Path.Combine(Path.GetDirectoryName(job.OutputPath)!, stem + Path.GetExtension(job.OutputPath));
                if (job.OverwritePolicy == OverwritePolicy.Skip && File.Exists(path))
                {
                    job.MarkSkipped($"Skipped · split destination {Path.GetFileName(path)} already exists");
                    return;
                }
                var suffix = 2;
                var candidate = path;
                while (reserved.Contains(Path.GetFullPath(candidate)) || (job.OverwritePolicy != OverwritePolicy.Overwrite && File.Exists(candidate)))
                    candidate = Path.Combine(Path.GetDirectoryName(path)!, $"{stem} ({suffix++}){Path.GetExtension(path)}");
                reserved.Add(Path.GetFullPath(candidate));
                paths.Add(candidate);
            }
            job.PlannedOutputs = paths;
            for (var index = 0; index < segments.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var segment = segments[index];
                var clipOptions = options with { TrimStartSeconds = segment.Start, TrimEndSeconds = segment.End };
                var child = new ConversionJob(job.Source, job.Format, clipOptions, paths[index], job.Engine)
                { OverwritePolicy = job.OverwritePolicy, ProtectedInputPaths = job.ProtectedInputPaths };
                var current = index;
                child.Changed += changed => job.ReportProgress((current + changed.Progress) / segments.Count, $"Clip {current + 1} of {segments.Count} · {changed.StatusMessage}");
                await ConversionRunner.RunAsync(child, tools, workDirectory, token);
                job.AppendLog(child.LogTail);
                job.AnalysisCommands.Add(child.CommandLine ?? "No command was resolved.");
                job.CommandLine = string.Join("\n", job.AnalysisCommands);
                if (child.State == JobState.Canceled) throw new OperationCanceledException(token);
                if (child.State != JobState.Completed) throw new InvalidOperationException($"Clip {index + 1} failed: {child.Error}");
                foreach (var output in child.CompletedOutputs) job.RecordOutput(output);
            }
            job.MarkCompleted(job.Result!);
        }
        catch (OperationCanceledException) { job.MarkCanceled(); }
        catch (Exception ex)
        {
            job.MarkFailed(ex.Message + (job.CompletedOutputs.Count > 0 ? $" · {job.CompletedOutputs.Count} completed clips kept." : ""));
        }
    }

    private static async Task<IReadOnlyList<double>> KeyframesAsync(ConversionJob job, ToolInventory tools, CancellationToken token)
    {
        var ffprobe = tools.PathFor(ToolKind.Ffprobe) ?? throw new InvalidOperationException("Fast splitting needs ffprobe to locate source keyframes.");
        var start = CommandLine.Create(ffprobe, ["-v", "error", "-select_streams", "v:0", "-skip_frame", "nokey", "-show_entries", "frame=best_effort_timestamp_time", "-of", "csv=p=0", job.Source.Path]);
        job.AnalysisCommands.Add(CommandLine.Describe(start));
        job.ReportProgress(0, "Locating source keyframes");
        using var process = Process.Start(start) ?? throw new IOException("Could not start keyframe analysis.");
        var error = process.StandardError.ReadToEndAsync(token);
        var keys = new SortedSet<double>();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
                if (double.TryParse(line.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time) && double.IsFinite(time) && time >= 0) keys.Add(time);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new InvalidOperationException("Keyframe analysis failed: " + await error);
            if (keys.Count == 0) throw new InvalidOperationException("No source keyframes were found.");
            return keys.ToArray();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }
}
