using System.Diagnostics;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;
using Xunit;

namespace LocalMorph.Core.Tests;

/// <summary>Runs only when FFmpeg is installed on the machine executing the tests.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (!Fixture.HasFfmpeg) Skip = "FFmpeg is not installed on this machine.";
    }
}

public static class Fixture
{
    public static readonly Lazy<ToolInventory> Inventory = new(() => ToolInventory.DiscoverAsync(verifyHardware: false).GetAwaiter().GetResult());
    public static bool HasFfmpeg => Inventory.Value.HasFfmpeg && Inventory.Value.Has(ToolKind.Ffprobe);

    public static string Root
    {
        get
        {
            var path = Path.Combine(Path.GetTempPath(), "localmorph-integration", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static async Task<string> MakeVideoAsync(string directory, string name = "sample.mp4", double seconds = 2, bool audio = true)
    {
        var path = Path.Combine(directory, name);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=size=640x360:rate=30:duration={seconds}" };
        if (audio) args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={seconds}"]);
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-g", "15", "-pix_fmt", "yuv420p"]);
        if (audio) args.AddRange(["-c:a", "aac", "-shortest"]);
        args.Add(path);
        await RunFfmpegAsync(args);
        return path;
    }

    public static async Task<string> MakeAudioAsync(string directory, string name = "tone.wav", double seconds = 2)
    {
        var path = Path.Combine(directory, name);
        await RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency=660:sample_rate=44100:duration={seconds}", "-ac", "2", path]);
        return path;
    }

    public static async Task<string> MakeImageAsync(string directory, string name = "picture.png")
    {
        var path = Path.Combine(directory, name);
        await RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=800x600:rate=1:duration=1", "-vf", "scale=801:601", "-pix_fmt", "rgb24", "-frames:v", "1", "-update", "1", path]);
        return path;
    }

    private static async Task RunFfmpegAsync(IEnumerable<string> args)
    {
        var startInfo = new ProcessStartInfo(Inventory.Value.PathFor(ToolKind.Ffmpeg)!) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"fixture ffmpeg failed: {error}");
    }

    public static async Task<ConversionJob> ConvertAsync(string sourcePath, string formatId, ConversionOptions? options = null, string? outputDirectory = null, TimeSpan? timeout = null)
    {
        var tools = Inventory.Value;
        var source = await SourceInspector.InspectAsync(sourcePath, tools);
        var format = FormatCatalog.Find(formatId)!;
        var engine = FormatCatalog.ResolveEngine(format, sourcePath, tools) ?? throw new InvalidOperationException($"No engine for {formatId}");
        var output = OutputNaming.BuildOutputPath(sourcePath, format, outputDirectory, "-out", OverwritePolicy.Rename);
        var job = new ConversionJob(source, format, options ?? new ConversionOptions(), output, engine);

        using var queue = new ConversionQueue(Path.Combine(Path.GetDirectoryName(sourcePath)!, "work"), maxParallel: 1) { Tools = () => tools };
        queue.Enqueue(job);
        queue.Start();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(90));
        await queue.WaitForIdleAsync(cts.Token);
        return job;
    }
}

public sealed class FfmpegIntegrationTests
{
    [FfmpegFact]
    public async Task Inspector_reads_video_metadata()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeVideoAsync(root);
        var source = await SourceInspector.InspectAsync(video, Fixture.Inventory.Value);

        Assert.Equal(MediaCategory.Video, source.Category);
        Assert.Equal(640, source.Media!.Width);
        Assert.Equal(360, source.Media.Height);
        Assert.InRange(source.DurationSeconds!.Value, 1.8, 2.3);
        Assert.True(source.HasAudio);
        Assert.Equal("h264", source.Media.VideoCodec);
        Assert.Equal("aac", source.Media.AudioCodec);
        Assert.Contains("640×360", source.Summary);
    }

    [FfmpegFact]
    public async Task Inspector_treats_png_as_image_not_video()
    {
        var root = Fixture.Root;
        var image = await Fixture.MakeImageAsync(root);
        var source = await SourceInspector.InspectAsync(image, Fixture.Inventory.Value);
        Assert.Equal(MediaCategory.Image, source.Category);
        Assert.Equal(801, source.Media!.Width);
    }

    [FfmpegFact]
    public async Task Video_to_mp4_with_resize_reports_progress_and_completes()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeVideoAsync(root, seconds: 3);
        var progress = new List<double>();
        var tools = Fixture.Inventory.Value;
        var source = await SourceInspector.InspectAsync(video, tools);
        var format = FormatCatalog.Find("mp4-h264")!;
        var job = new ConversionJob(source, format, new ConversionOptions { TargetHeight = 240, Speed = EncodingSpeed.Fast, UseHardwareEncoder = false }, Path.Combine(root, "small.mp4"), EngineKind.Ffmpeg);
        job.Changed += j => progress.Add(j.Progress);

        await ConversionRunner.RunAsync(job, tools, Path.Combine(root, "work"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.True(File.Exists(job.OutputPath));
        Assert.True(job.Result!.OutputBytes > 0);
        Assert.Contains(1.0, progress);
        var probe = await SourceInspector.InspectAsync(job.OutputPath, tools);
        Assert.Equal(240, probe.Media!.Height);
        Assert.Equal(426, probe.Media.Width);
        Assert.True(probe.HasAudio);
    }

    [FfmpegFact]
    public async Task Video_to_gif_mp3_and_frame_jpeg()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeVideoAsync(root);

        var gif = await Fixture.ConvertAsync(video, "gif", new ConversionOptions { TargetHeight = 180, FrameRate = 10 });
        Assert.Equal(JobState.Completed, gif.State);
        Assert.EndsWith(".gif", gif.OutputPath);

        var mp3 = await Fixture.ConvertAsync(video, "mp3", new ConversionOptions { AudioBitrateKbps = 128 });
        Assert.Equal(JobState.Completed, mp3.State);
        var mp3Info = await SourceInspector.InspectAsync(mp3.OutputPath, Fixture.Inventory.Value);
        Assert.Equal(MediaCategory.Audio, mp3Info.Category);
        Assert.Equal("mp3", mp3Info.Media!.AudioCodec);

        var frame = await Fixture.ConvertAsync(video, "jpg", new ConversionOptions { FrameTimeSeconds = 1.0, Quality = 85 });
        Assert.Equal(JobState.Completed, frame.State);
        var frameInfo = await SourceInspector.InspectAsync(frame.OutputPath, Fixture.Inventory.Value);
        Assert.Equal(MediaCategory.Image, frameInfo.Category);
        Assert.Equal(640, frameInfo.Media!.Width);
    }

    [FfmpegFact]
    public async Task Trim_and_remux_produce_shorter_clip_without_reencoding()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeVideoAsync(root, seconds: 4);
        var job = await Fixture.ConvertAsync(video, "mkv-copy", new ConversionOptions { TrimStartSeconds = 1, TrimEndSeconds = 3 });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Contains("-c:v copy", job.CommandLine);
        var info = await SourceInspector.InspectAsync(job.OutputPath, Fixture.Inventory.Value);
        Assert.InRange(info.DurationSeconds!.Value, 1.5, 2.6);
        Assert.Equal("h264", info.Media!.VideoCodec);
    }

    [FfmpegFact]
    public async Task Target_size_two_pass_lands_under_budget()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeVideoAsync(root, seconds: 4);
        var job = await Fixture.ConvertAsync(video, "mp4-h264", new ConversionOptions { TargetSizeMegabytes = 0.5, UseHardwareEncoder = false, Speed = EncodingSpeed.Fast, AudioBitrateKbps = 64 });

        Assert.Equal(JobState.Completed, job.State);
        Assert.Contains("-pass 2", job.CommandLine);
        Assert.True(job.Result!.OutputBytes < 0.6 * 1024 * 1024, $"output was {job.Result.OutputBytes} bytes");
    }

    [FfmpegFact]
    public async Task Audio_to_flac_opus_and_wav24()
    {
        var root = Fixture.Root;
        var wav = await Fixture.MakeAudioAsync(root);

        var flac = await Fixture.ConvertAsync(wav, "flac");
        Assert.Equal(JobState.Completed, flac.State);

        var opus = await Fixture.ConvertAsync(wav, "opus", new ConversionOptions { AudioBitrateKbps = 64, Channels = ChannelMode.Mono, SampleRate = 44100 });
        Assert.Equal(JobState.Completed, opus.State);
        var opusInfo = await SourceInspector.InspectAsync(opus.OutputPath, Fixture.Inventory.Value);
        Assert.Equal(1, opusInfo.Media!.Channels);

        var wav24 = await Fixture.ConvertAsync(wav, "wav", new ConversionOptions { WavBitDepth = 24, SampleRate = 48000 });
        Assert.Equal(JobState.Completed, wav24.State);
        var wavInfo = await SourceInspector.InspectAsync(wav24.OutputPath, Fixture.Inventory.Value);
        Assert.Equal(48000, wavInfo.Media!.SampleRate);
        Assert.Equal("pcm_s24le", wavInfo.Media.AudioCodec);
    }

    [FfmpegFact]
    public async Task Image_to_webp_jpg_and_resized_png()
    {
        var root = Fixture.Root;
        var png = await Fixture.MakeImageAsync(root);
        var tools = Fixture.Inventory.Value;

        if (tools.Ffmpeg.HasEncoder("libwebp"))
        {
            var webp = await Fixture.ConvertAsync(png, "webp", new ConversionOptions { Quality = 80 });
            Assert.Equal(JobState.Completed, webp.State);
        }

        var jpg = await Fixture.ConvertAsync(png, "jpg", new ConversionOptions { Quality = 90 });
        Assert.Equal(JobState.Completed, jpg.State);
        var jpgInfo = await SourceInspector.InspectAsync(jpg.OutputPath, tools);
        Assert.Equal(801, jpgInfo.Media!.Width);

        var small = await Fixture.ConvertAsync(png, "png", new ConversionOptions { TargetHeight = 300 });
        Assert.Equal(JobState.Completed, small.State);
        var smallInfo = await SourceInspector.InspectAsync(small.OutputPath, tools);
        Assert.Equal(300, smallInfo.Media!.Height);
    }

    [FfmpegFact]
    public async Task Still_image_becomes_video_clip()
    {
        var root = Fixture.Root;
        var png = await Fixture.MakeImageAsync(root);
        var job = await Fixture.ConvertAsync(png, "mp4-h264", new ConversionOptions { TrimEndSeconds = 2, UseHardwareEncoder = false, Speed = EncodingSpeed.Fast });
        Assert.Equal(JobState.Completed, job.State);
        var info = await SourceInspector.InspectAsync(job.OutputPath, Fixture.Inventory.Value);
        Assert.Equal(MediaCategory.Video, info.Category);
        Assert.InRange(info.DurationSeconds!.Value, 1.8, 2.3);
        Assert.Equal(800, info.Media!.Width);
    }

    [FfmpegFact]
    public async Task Corrupt_input_fails_with_friendly_message_and_no_output()
    {
        var root = Fixture.Root;
        var bogus = Path.Combine(root, "broken.mp4");
        await File.WriteAllBytesAsync(bogus, new byte[4096]);
        var job = await Fixture.ConvertAsync(bogus, "mp3");

        Assert.Equal(JobState.Failed, job.State);
        Assert.False(File.Exists(job.OutputPath));
        Assert.False(string.IsNullOrWhiteSpace(job.Error));
        Assert.DoesNotContain("exited with code", job.Error);
    }

    [FfmpegFact]
    public async Task Cancel_kills_ffmpeg_and_removes_partial_output()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeVideoAsync(root, seconds: 20);
        var tools = Fixture.Inventory.Value;
        var source = await SourceInspector.InspectAsync(video, tools);
        var job = new ConversionJob(source, FormatCatalog.Find("mp4-h265")!, new ConversionOptions { Speed = EncodingSpeed.Quality, UseHardwareEncoder = false }, Path.Combine(root, "slow.mp4"), EngineKind.Ffmpeg);
        job.Changed += j => { if (j.State == JobState.Running && j.Progress > 0.02) j.Cancel(); };

        using var queue = new ConversionQueue(Path.Combine(root, "work")) { Tools = () => tools };
        queue.Enqueue(job);
        queue.Start();
        await queue.WaitForIdleAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);

        Assert.Equal(JobState.Canceled, job.State);
        Assert.False(File.Exists(job.OutputPath));
    }

    [FfmpegFact]
    public async Task Queue_runs_batch_in_parallel_and_reports_drained()
    {
        var root = Fixture.Root;
        var tools = Fixture.Inventory.Value;
        var drained = false;
        using var queue = new ConversionQueue(Path.Combine(root, "work"), maxParallel: 3) { Tools = () => tools };
        queue.Drained += () => drained = true;
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < 4; i++)
        {
            var wav = await Fixture.MakeAudioAsync(root, $"tone{i}.wav");
            var source = await SourceInspector.InspectAsync(wav, tools);
            var format = FormatCatalog.Find("mp3")!;
            var output = OutputNaming.BuildOutputPath(wav, format, null, string.Empty, OverwritePolicy.Rename, reserved);
            queue.Enqueue(new ConversionJob(source, format, new ConversionOptions(), output, EngineKind.Ffmpeg));
        }

        queue.Start();
        await queue.WaitForIdleAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);

        Assert.True(drained);
        Assert.All(queue.Jobs, job => Assert.Equal(JobState.Completed, job.State));
        Assert.Equal(4, queue.Jobs.Select(job => job.OutputPath).Distinct().Count());
    }

    [FfmpegFact]
    public async Task Capabilities_detect_encoders_from_real_ffmpeg()
    {
        var caps = await FfmpegCapabilities.DiscoverAsync(Fixture.Inventory.Value.PathFor(ToolKind.Ffmpeg)!, verifyHardware: true);
        Assert.True(caps.IsAvailable);
        Assert.True(caps.HasEncoder("libx264") || caps.HasEncoder("h264_videotoolbox"));
        Assert.True(caps.HasEncoder("aac"));
        Assert.NotNull(caps.Version);
        // Hardware list may be empty on CI, but anything reported must have passed a test encode.
        Assert.All(caps.WorkingHardwareEncoders, encoder => Assert.Contains(encoder.Encoder, caps.Encoders));
    }
}
