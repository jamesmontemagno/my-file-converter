using System.Text.Json;
using LocalMorph.Core.Engines;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using Xunit;

namespace LocalMorph.Core.Tests;

public sealed class MediaOperationsTests : IDisposable
{
    private readonly string root = Fixture.Root;
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Segments_have_no_empty_tail_and_fast_boundaries_are_keyframes()
    {
        Assert.Equal(3, MediaOperations.Segments(0, 3, 1).Count);
        Assert.Single(MediaOperations.Segments(0, 0.4, 1));
        Assert.Equal([(0d, 1.5d), (1.5d, 3d), (3d, 3.2d)], MediaOperations.Segments(0, 3.2, 1, [0, 1.5, 3]));
        Assert.Throws<InvalidOperationException>(() => MediaOperations.Segments(0, 2, double.NaN));
    }

    [FfmpegFact]
    public async Task Join_preserves_order_streams_and_combined_duration()
    {
        var first = Path.Combine(root, "one.mp4");
        var second = Path.Combine(root, "two's clip.mp4");
        foreach (var (path, color, frequency) in new[] { (first, "red", 440), (second, "blue", 880) })
            await Fixture.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", $"color=c={color}:s=320x180:r=30:d=1",
                "-f", "lavfi", "-i", $"sine=frequency={frequency}:sample_rate=48000:duration=1",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "15", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path]);
        var job = await RunAsync([first, second], "mkv-copy", new(MediaOperation.Join));
        Assert.True(job.State == JobState.Completed, job.Error + "\n" + job.LogTail);
        var output = await SourceInspector.InspectAsync(job.OutputPath, Fixture.Inventory.Value);
        Assert.InRange(output.DurationSeconds!.Value, 1.98, 2.12);
        Assert.Equal("h264", output.Media!.VideoCodec);
        Assert.Equal(2, job.Inputs.Count);
        Assert.Single(job.CompletedOutputs);
        foreach (var (time, dominant) in new[] { ("0.5", 0), ("1.5", 2) })
        {
            var pixel = Path.Combine(root, $"pixel-{time}.rgb");
            await Fixture.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-ss", time, "-i", job.OutputPath,
                "-vf", "scale=1:1", "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", pixel]);
            var rgb = await File.ReadAllBytesAsync(pixel);
            Assert.Equal(3, rgb.Length);
            Assert.True(rgb[dominant] > 200 && rgb[2 - dominant] < 50, $"Clip order/color differs at {time}s.");
        }
        using var packetJson = JsonDocument.Parse(await Fixture.ProbeAsync(job.OutputPath, "-show_packets", "-show_entries", "packet=stream_index,pts_time,duration_time", "-of", "json"));
        var packets = packetJson.RootElement.GetProperty("packets").EnumerateArray().ToArray();
        var ranges = Enumerable.Range(0, 2).Select(stream =>
        {
            var times = packets.Where(packet => packet.GetProperty("stream_index").GetInt32() == stream).Select(packet =>
            {
                var start = double.Parse(packet.GetProperty("pts_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                var duration = double.Parse(packet.GetProperty("duration_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                return (Start: start, End: start + duration);
            }).ToArray();
            return (Start: times.Min(time => time.Start), End: times.Max(time => time.End));
        }).ToArray();
        Assert.InRange(Math.Abs(ranges[0].Start - ranges[1].Start), 0, 0.05);
        Assert.InRange(Math.Abs(ranges[0].End - ranges[1].End), 0, 0.05);
        var different = job.Inputs[1] with { Media = job.Inputs[1].Media! with { Streams = job.Inputs[1].Media!.Streams!.Take(1).ToArray() } };
        Assert.Contains("stream counts differ", MediaOperations.JoinIssue([job.Inputs[0], different], job.Format));
        var streams = job.Inputs[1].Media!.Streams!.ToArray();
        streams[0] = streams[0] with { Signature = streams[0].Signature! with { Width = 640 } };
        different = job.Inputs[1] with { Media = job.Inputs[1].Media! with { Streams = streams } };
        Assert.Contains("geometry", MediaOperations.JoinIssue([job.Inputs[0], different], job.Format));
    }

    [FfmpegFact]
    public async Task Accurate_split_has_frame_accurate_durations_and_all_results()
    {
        var source = await Fixture.MakeVideoAsync(root, seconds: 2.4);
        var job = await RunAsync([source], "mp4-h264", new(MediaOperation.Split, 1));
        Assert.True(job.State == JobState.Completed, job.Error + "\n" + job.LogTail);
        Assert.Equal(3, job.CompletedOutputs.Count);
        Assert.Equal(3, job.PlannedOutputs.Count);
        var expected = new[] { 1d, 1d, 0.4 };
        for (var index = 0; index < expected.Length; index++)
        {
            var count = await Fixture.ProbeAsync(job.CompletedOutputs[index].Path, "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0");
            Assert.InRange(int.Parse(count), (int)(expected[index] * 30) - 1, (int)(expected[index] * 30) + 1);
        }
    }

    [FfmpegFact]
    public async Task Fast_split_starts_with_the_selected_source_keyframes()
    {
        var source = await Fixture.MakeVideoAsync(root, seconds: 2.6);
        var job = await RunAsync([source], "mkv-copy", new(MediaOperation.Split, 0.7, SplitMode.Fast));
        Assert.True(job.State == JobState.Completed, job.Error + "\n" + job.LogTail);
        var starts = new[] { 0d, 1d, 1.5, 2.5 };
        Assert.Equal(starts.Length, job.CompletedOutputs.Count);
        for (var index = 0; index < starts.Length; index++)
        {
            var interval = starts[index].ToString(System.Globalization.CultureInfo.InvariantCulture) + "%+0.001";
            var original = await Fixture.ProbeAsync(source, "-select_streams", "v:0", "-read_intervals", interval, "-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=data_hash", "-of", "json");
            var split = await Fixture.ProbeAsync(job.CompletedOutputs[index].Path, "-select_streams", "v:0", "-read_intervals", "0%+0.001", "-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=data_hash", "-of", "json");
            using var input = JsonDocument.Parse(original);
            using var output = JsonDocument.Parse(split);
            Assert.Equal(input.RootElement.GetProperty("packets")[0].GetProperty("data_hash").GetString(),
                output.RootElement.GetProperty("packets")[0].GetProperty("data_hash").GetString());
        }
    }

    [FfmpegFact]
    public async Task Split_overwrite_never_replaces_another_queued_source()
    {
        var source = await Fixture.MakeVideoAsync(root, seconds: 1);
        var protectedPath = Path.Combine(root, "result-001.mp4");
        await File.WriteAllTextAsync(protectedPath, "another input");
        var input = await SourceInspector.InspectAsync(source, Fixture.Inventory.Value);
        var job = new ConversionJob(input, FormatCatalog.Find("mp4-h264")!, new ConversionOptions { UseHardwareEncoder = false }, Path.Combine(root, "result.mp4"), EngineKind.Ffmpeg)
        { Operation = new(MediaOperation.Split, 1), OverwritePolicy = OverwritePolicy.Overwrite, ProtectedInputPaths = [source, protectedPath] };
        await ConversionRunner.RunAsync(job, Fixture.Inventory.Value, Path.Combine(root, "work"));
        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("another input", await File.ReadAllTextAsync(protectedPath));
        Assert.NotEqual(protectedPath, Assert.Single(job.CompletedOutputs).Path);
    }

    [FfmpegFact]
    public async Task Canceling_a_split_keeps_and_reports_completed_clips()
    {
        var source = await Fixture.MakeVideoAsync(root, seconds: 3);
        var job = await CreateAsync([source], "mp4-h264", new(MediaOperation.Split, 1));
        job.Changed += changed => { if (changed.CompletedOutputs.Count == 1) changed.Cancel(); };
        await ConversionRunner.RunAsync(job, Fixture.Inventory.Value, Path.Combine(root, "work"));
        Assert.Equal(JobState.Canceled, job.State);
        Assert.Single(job.CompletedOutputs);
        Assert.True(File.Exists(job.CompletedOutputs[0].Path));
        Assert.Contains("completed clips kept", job.StatusMessage);
        Assert.Empty(Directory.EnumerateFiles(root, ".localmorph-*"));
    }

    [FfmpegFact]
    public async Task A_later_split_commit_failure_keeps_earlier_results()
    {
        var source = await Fixture.MakeVideoAsync(root, seconds: 3);
        var job = await CreateAsync([source], "mp4-h264", new(MediaOperation.Split, 1));
        job.Changed += changed =>
        {
            if (changed.CompletedOutputs.Count == 1) Directory.CreateDirectory(changed.PlannedOutputs[1]);
        };
        await ConversionRunner.RunAsync(job, Fixture.Inventory.Value, Path.Combine(root, "work"));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Single(job.CompletedOutputs);
        Assert.True(File.Exists(job.CompletedOutputs[0].Path));
        Assert.Single(job.Result!.AllOutputs);
        Assert.Empty(Directory.EnumerateFiles(root, ".localmorph-*"));
    }

    [Fact]
    public void Old_history_json_remains_readable()
    {
        var entry = JsonSerializer.Deserialize<HistoryEntry>("""{"CompletedAt":"2025-01-01T00:00:00Z","SourcePath":"old.mp4","OutputPath":"out.mp4","FormatId":"mp4-h264","FormatName":"MP4","SourceBytes":20,"OutputBytes":10,"ElapsedSeconds":1,"Engine":"Ffmpeg"}""")!;
        Assert.Equal("old.mp4", Assert.Single(entry.AllInputs));
        Assert.Equal("out.mp4", Assert.Single(entry.AllOutputs).Path);
        Assert.Null(entry.Operation);
    }

    private async Task<ConversionJob> RunAsync(string[] paths, string formatId, OperationRequest operation)
    {
        var job = await CreateAsync(paths, formatId, operation);
        await ConversionRunner.RunAsync(job, Fixture.Inventory.Value, Path.Combine(root, "work"));
        return job;
    }

    private async Task<ConversionJob> CreateAsync(string[] paths, string formatId, OperationRequest operation)
    {
        var inputs = await Task.WhenAll(paths.Select(path => SourceInspector.InspectAsync(path, Fixture.Inventory.Value)));
        var format = FormatCatalog.Find(formatId)!;
        return new ConversionJob(inputs[0], format, new ConversionOptions { UseHardwareEncoder = false }, Path.Combine(root, "result" + format.ExtensionWithDot), EngineKind.Ffmpeg)
        { Inputs = inputs, Operation = operation };
    }
}
