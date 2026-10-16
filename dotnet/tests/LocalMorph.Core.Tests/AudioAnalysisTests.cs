using System.Diagnostics;
using LocalMorph.Core.Engines;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;
using Xunit;

namespace LocalMorph.Core.Tests;

public sealed class AudioAnalysisTests : IDisposable
{
    private readonly string root = Fixture.Root;
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Silence_bounds_keep_internal_pauses()
    {
        var bounds = AudioAnalysis.SilenceBounds("silence_start: 0\nsilence_end: 1\nsilence_start: 2\nsilence_end: 3\nsilence_start: 4\nsilence_end: 5", 5);
        Assert.Equal(1, bounds.Start);
        Assert.Equal(4, bounds.End);
        Assert.Throws<InvalidOperationException>(() => AudioAnalysis.SilenceBounds("silence_start: 0\nsilence_end: 5", 5));
    }

    [FfmpegFact]
    public async Task Measured_normalization_meets_loudness_and_peak_limits()
    {
        var source = await Fixture.MakeAudioAsync(root, seconds: 5);
        var job = await Fixture.ConvertAsync(source, "flac", new ConversionOptions { MeasureLoudness = true });
        Assert.Equal(JobState.Completed, job.State);
        var output = await SourceInspector.InspectAsync(job.OutputPath, Fixture.Inventory.Value);
        Assert.Equal(44100, output.Media!.SampleRate);
        var log = await RunAsync(["-hide_banner", "-i", job.OutputPath, "-af", "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-f", "null", CommandLine.NullDevice]);
        var measured = AudioAnalysis.ParseMeasurement(log, 0);
        Assert.InRange(measured.Integrated, -16.5, -15.5);
        Assert.True(measured.Peak <= -1.3, $"True peak exceeded ceiling: {measured.Peak}");
        Assert.Contains("measured_I=", job.CommandLine);
        Assert.Contains("print_format=json", job.CommandLine);
    }

    [FfmpegFact]
    public async Task Every_retained_audio_track_gets_its_own_measurement()
    {
        var source = await Fixture.MakeMultiTrackVideoAsync(root);
        var job = await Fixture.ConvertAsync(source, "mkv-h264", new ConversionOptions { MeasureLoudness = true, KeepAllAudioTracks = true, AudioTrack = 1, UseHardwareEncoder = false });
        Assert.True(job.State == JobState.Completed, job.Error + "\n" + job.LogTail);
        Assert.Equal([1, 0], job.Options.LoudnessMeasurements!.Select(measurement => measurement.Track));
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal("48000", await Fixture.ProbeAsync(job.OutputPath, "-select_streams", $"a:{index}", "-show_entries", "stream=sample_rate", "-of", "default=noprint_wrappers=1:nokey=1"));
            var log = await RunAsync(["-hide_banner", "-i", job.OutputPath, "-map", $"0:a:{index}", "-af", "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-f", "null", CommandLine.NullDevice]);
            var measured = AudioAnalysis.ParseMeasurement(log, index);
            Assert.InRange(measured.Integrated, -16.5, -15.5);
            Assert.True(measured.Peak <= -1.3);
        }
    }

    [FfmpegFact]
    public async Task Silence_trimming_removes_only_the_edges()
    {
        var source = Path.Combine(root, "pauses.wav");
        await RunAsync(["-hide_banner", "-y", "-f", "lavfi", "-i", "aevalsrc=exprs='if(between(t,1,2)+between(t,3,4),0.2*sin(2*PI*440*t),0)':s=48000:d=5", source]);
        var job = await Fixture.ConvertAsync(source, "flac", new ConversionOptions { TrimSilence = true, MeasureLoudness = true, VolumePercent = 200, SampleRate = 32000, Channels = ChannelMode.Mono, PlaybackSpeed = 0.5 });
        Assert.True(job.State == JobState.Completed, job.Error + "\n" + job.LogTail);
        var output = await SourceInspector.InspectAsync(job.OutputPath, Fixture.Inventory.Value);
        Assert.InRange(output.DurationSeconds!.Value, 5.95, 6.06);
        var log = await RunAsync(["-hide_banner", "-i", job.OutputPath, "-af", "silencedetect=noise=-50dB:d=0.5", "-f", "null", CommandLine.NullDevice]);
        Assert.Contains("silence_start:", log);
        Assert.Contains("silence_end:", log);
        Assert.Equal(32000, output.Media!.SampleRate);
        Assert.Equal(1, output.Media.Channels);
        var measured = AudioAnalysis.ParseMeasurement(await RunAsync(["-hide_banner", "-i", job.OutputPath, "-af", "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-f", "null", CommandLine.NullDevice]), 0);
        Assert.InRange(measured.Integrated, -16.5, -15.5);
        Assert.True(measured.Peak <= -1.3);
    }

    [FfmpegFact]
    public async Task All_silent_audio_reports_no_audible_content()
    {
        var source = Path.Combine(root, "silent.wav");
        await RunAsync(["-hide_banner", "-y", "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "1", source]);
        var job = await Fixture.ConvertAsync(source, "flac", new ConversionOptions { TrimSilence = true });
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("No audible content", job.Error);
        Assert.False(File.Exists(job.OutputPath));
    }

    [FfmpegFact]
    public async Task Replacement_audio_is_measured_from_output_zero_even_when_video_is_trimmed_and_has_no_audio()
    {
        var source = await Fixture.MakeVideoAsync(root, seconds: 6, audio: false);
        var replacement = Path.Combine(root, "replacement.wav");
        await Fixture.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "aevalsrc=exprs='if(lt(t,3),0.02,0.2)*sin(2*PI*440*t)':s=48000:d=6", replacement]);
        var job = await Fixture.ConvertAsync(source, "mkv-h264", new ConversionOptions
        {
            ReplacementAudioPath = replacement, MeasureLoudness = true, UseHardwareEncoder = false,
            TrimStartSeconds = 3, TrimEndSeconds = 6
        });
        Assert.True(job.State == JobState.Completed, job.Error + "\n" + job.LogTail);
        var output = await SourceInspector.InspectAsync(job.OutputPath, Fixture.Inventory.Value);
        Assert.InRange(output.DurationSeconds!.Value, 2.98, 3.06);
        Assert.Equal(48000, output.Media!.SampleRate);
        var measured = AudioAnalysis.ParseMeasurement(await RunAsync(["-hide_banner", "-i", job.OutputPath, "-af", "loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json", "-f", "null", CommandLine.NullDevice]), 0);
        Assert.InRange(measured.Integrated, -16.5, -15.5);
        Assert.True(measured.Peak <= -1.3);
    }

    private static async Task<string> RunAsync(IEnumerable<string> args)
    {
        using var process = Process.Start(CommandLine.Create(Fixture.Inventory.Value.PathFor(ToolKind.Ffmpeg)!, args))!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, error, process.WaitForExitAsync());
        Assert.True(process.ExitCode == 0, await error);
        return await error;
    }
}
