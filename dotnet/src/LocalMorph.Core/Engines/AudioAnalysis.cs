using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Engines;

public static partial class AudioAnalysis
{
    public static async Task<ConversionOptions> PrepareAsync(ConversionJob job, ToolInventory tools, CancellationToken token)
    {
        var options = job.RequestedOptions;
        if (!options.MeasureLoudness && !options.TrimSilence) return options;
        if (options.Validate(job.Format) is { } issue) throw new InvalidOperationException(issue);
        if ((!job.Source.HasAudio && string.IsNullOrWhiteSpace(options.ReplacementAudioPath)) || options.RemoveAudio) throw new InvalidOperationException("The selected source has no audio to analyze.");
        var ffmpeg = tools.PathFor(ToolKind.Ffmpeg) ?? throw new InvalidOperationException("FFmpeg is required for audio analysis.");
        var source = job.Source;
        if (!string.IsNullOrWhiteSpace(options.ReplacementAudioPath))
        {
            source = await SourceInspector.InspectAsync(options.ReplacementAudioPath, tools, token);
            var duration = (options.TrimEndSeconds ?? job.Source.DurationSeconds ?? throw new InvalidOperationException("Audio analysis needs a known duration.")) - (options.TrimStartSeconds ?? 0);
            options = options with { TrimStartSeconds = null, TrimEndSeconds = Math.Min(source.DurationSeconds ?? duration, duration), AudioTrack = 0 };
        }
        var selected = options.AudioTrack ?? 0;
        var tracks = options.KeepAllAudioTracks && job.Format.Supports(FormatFeatures.MultiAudio) && options.ReplacementAudioPath is null
            ? new[] { selected }.Concat(Enumerable.Range(0, source.AudioTracks.Count).Where(track => track != selected)).ToArray()
            : [selected];
        if (options.TrimSilence)
        {
            if (!tools.Ffmpeg.MayHaveFilter("silencedetect")) throw new InvalidOperationException("This FFmpeg build lacks silencedetect.");
            job.ReportProgress(0, "Detecting leading and trailing silence");
            var scan = options with { PlaybackSpeed = 1, VolumePercent = 100, Reverse = false, FadeInSeconds = 0, FadeOutSeconds = 0, AudioDelayMilliseconds = 0 };
            var log = await AnalyzeAsync(ffmpeg, source, scan, selected,
                $"silencedetect=noise={N(options.SilenceThresholdDb)}dB:d={N(options.MinimumSilenceSeconds)}", job, token);
            var duration = (options.TrimEndSeconds ?? source.DurationSeconds ?? throw new InvalidOperationException("Silence trimming needs a known duration.")) - (options.TrimStartSeconds ?? 0);
            var bounds = SilenceBounds(log, duration);
            var start = options.TrimStartSeconds ?? 0;
            options = options with { TrimStartSeconds = start + bounds.Start, TrimEndSeconds = start + bounds.End };
            job.AppendLog($"Audible range: {N(options.TrimStartSeconds.Value)} to {N(options.TrimEndSeconds.Value)} seconds.");
        }
        if (options.MeasureLoudness)
        {
            if (!tools.Ffmpeg.MayHaveFilter("loudnorm")) throw new InvalidOperationException("This FFmpeg build lacks loudnorm.");
            var measurements = new List<LoudnessMeasurement>();
            foreach (var track in tracks)
            {
                job.ReportProgress(0, $"Measuring loudness · audio track {track + 1}");
                var log = await AnalyzeAsync(ffmpeg, source, options, track,
                    $"loudnorm=I={N(options.TargetLoudness)}:TP={N(options.TruePeak)}:LRA=11:print_format=json", job, token);
                measurements.Add(ParseMeasurement(log, track));
            }
            options = options with { LoudnessMeasurements = measurements };
        }
        // Replacement audio starts at output time zero; source-video trim values remain unchanged.
        if (!string.IsNullOrWhiteSpace(options.ReplacementAudioPath))
            options = options with { TrimStartSeconds = job.RequestedOptions.TrimStartSeconds, TrimEndSeconds = job.RequestedOptions.TrimEndSeconds };
        return options;
    }

    public static LoudnessMeasurement ParseMeasurement(string log, int track)
    {
        var start = log.LastIndexOf('{');
        var end = log.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("FFmpeg returned no loudness measurements. See the job diagnostics.");
        using var json = JsonDocument.Parse(log[start..(end + 1)]);
        double Read(string name)
        {
            if (!double.TryParse(json.RootElement.GetProperty(name).GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new InvalidOperationException("No measurable audible content was found. Choose a different track or disable measured loudness.");
            return value;
        }
        return new(track, Read("input_i"), Read("input_tp"), Read("input_lra"), Read("input_thresh"), Read("target_offset"));
    }

    public static (double Start, double End) SilenceBounds(string log, double duration)
    {
        var start = 0d;
        var end = duration;
        double? open = null;
        foreach (Match match in SilenceEvent().Matches(log))
        {
            var value = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (match.Groups[1].Value == "start") open = value;
            else if (open is { } from)
            {
                if (from <= 0.05) start = Math.Min(value, duration);
                if (value >= duration - 0.05) end = from;
                open = null;
            }
        }
        if (open is { } trailing) end = trailing;
        if (end - start < 0.05) throw new InvalidOperationException("No audible content remains after trimming edge silence.");
        return (start, end);
    }

    private static async Task<string> AnalyzeAsync(string ffmpeg, SourceFile source, ConversionOptions options, int track, string filter, ConversionJob job, CancellationToken token)
    {
        var analysis = options with { MeasureLoudness = false, NormalizeAudio = false, TrimSilence = false, ToneMapToSdr = false, TargetSizeMegabytes = null, AudioTrack = track, KeepAllAudioTracks = false };
        var args = FfmpegEngine.BuildArguments(source, FormatCatalog.Find("wav")!, analysis, FfmpegCapabilities.Empty, CommandLine.NullDevice).ToList();
        args[args.IndexOf("-loglevel") + 1] = "info";
        var index = args.IndexOf("-af");
        var filters = new List<string>();
        // Output -t truncates after filters; look-ahead analysis must see only the selected range.
        if (FfmpegEngine.AudioRangeFilter(source, options) is { } inputRange) filters.Add(inputRange);
        if (index >= 0) filters.Add(args[index + 1]);
        if (FfmpegEngine.AudioRangeFilter(source, options, options.PlaybackSpeed) is { } outputRange) filters.Add(outputRange);
        if (options.Channels != ChannelMode.Source) filters.Add($"aformat=channel_layouts={(options.Channels == ChannelMode.Mono ? "mono" : "stereo")}");
        filters.Add(filter);
        if (index >= 0) args[index + 1] = string.Join(",", filters);
        else args.InsertRange(args.Count - 1, ["-af", string.Join(",", filters)]);
        args.InsertRange(args.Count - 1, ["-f", "null"]);
        var start = CommandLine.Create(ffmpeg, args);
        var command = CommandLine.Describe(start);
        job.AnalysisCommands.Add(command);
        job.CommandLine = string.Join("\n", job.AnalysisCommands);
        job.AppendLog("$ " + command);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Could not start FFmpeg audio analysis.");
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(token));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var log = await stderr;
        job.AppendLog(log);
        if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg audio analysis failed. See the job diagnostics.");
        return log;
    }

    private static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    [GeneratedRegex(@"silence_(start|end):\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex SilenceEvent();
}
