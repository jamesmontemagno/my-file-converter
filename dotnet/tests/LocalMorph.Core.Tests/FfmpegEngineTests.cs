using LocalMorph.Core.Engines;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;
using Xunit;

namespace LocalMorph.Core.Tests;

public sealed class FfmpegEngineTests
{
    private static List<string> Build(SourceFile source, string formatId, ConversionOptions? options = null, FfmpegCapabilities? caps = null, string output = @"C:\out\result") =>
        FfmpegEngine.BuildArguments(source, TestData.Format(formatId), options ?? new ConversionOptions(), caps ?? TestData.SoftwareOnly, output + "." + TestData.Format(formatId).Extension).ToList();

    private static string Joined(IReadOnlyList<string> args) => string.Join(' ', args);

    [Fact]
    public void Mp4_h264_software_uses_crf_preset_faststart_and_even_dimensions()
    {
        var args = Build(TestData.Video(), "mp4-h264", new ConversionOptions { Quality = 80, Speed = EncodingSpeed.Quality });
        var text = Joined(args);

        Assert.Contains("-c:v libx264", text);
        Assert.Contains("-preset slow", text);
        Assert.Contains("-crf 22", text);
        Assert.Contains("-pix_fmt yuv420p", text);
        Assert.Contains("-movflags +faststart", text);
        Assert.Contains("-vf scale=trunc(iw/2)*2:trunc(ih/2)*2", text);
        Assert.Contains("-c:a aac -b:a 160k", text);
        Assert.Contains("-map 0:v:0 -map 0:a:0?", text);
        Assert.EndsWith(@"C:\out\result.mp4", args[^1]);
        Assert.Equal("-progress", args[args.IndexOf("pipe:1") - 1]);
    }

    [Fact]
    public void Mp4_h264_prefers_working_hardware_encoder_when_allowed()
    {
        var hardware = Joined(Build(TestData.Video(), "mp4-h264", new ConversionOptions { UseHardwareEncoder = true }, TestData.WithNvenc));
        var software = Joined(Build(TestData.Video(), "mp4-h264", new ConversionOptions { UseHardwareEncoder = false }, TestData.WithNvenc));

        Assert.Contains("-c:v h264_nvenc", hardware);
        Assert.Contains("-rc vbr -cq", hardware);
        Assert.Contains("-c:v libx264", software);
        Assert.DoesNotContain("nvenc", software);
    }

    [Fact]
    public void Hevc_in_mp4_gets_apple_friendly_tag()
    {
        var text = Joined(Build(TestData.Video(), "mp4-h265"));
        Assert.Contains("-c:v libx265", text);
        Assert.Contains("-tag:v hvc1", text);
    }

    [Fact]
    public void Resolution_only_downscales_and_keeps_even_width()
    {
        var downscale = Joined(Build(TestData.Video(height: 1080), "mp4-h264", new ConversionOptions { TargetHeight = 720 }));
        var noUpscale = Joined(Build(TestData.Video(height: 480), "mp4-h264", new ConversionOptions { TargetHeight = 720 }));

        Assert.Contains("scale=-2:720:flags=lanczos", downscale);
        Assert.DoesNotContain(":720", noUpscale);
        Assert.Contains("scale=trunc(iw/2)*2:trunc(ih/2)*2", noUpscale);
    }

    [Fact]
    public void Trim_seeks_on_input_and_limits_duration()
    {
        var args = Build(TestData.Video(), "mp4-h264", new ConversionOptions { TrimStartSeconds = 10, TrimEndSeconds = 25 });
        var ss = args.IndexOf("-ss");
        var i = args.IndexOf("-i");
        Assert.True(ss >= 0 && ss < i, "-ss should precede -i for fast input seeking");
        Assert.Equal("10", args[ss + 1]);
        Assert.Equal("15", args[args.IndexOf("-t") + 1]);
    }

    [Fact]
    public void Remux_copies_streams_without_encoding()
    {
        var text = Joined(Build(TestData.Video(), "mp4-copy", new ConversionOptions { TrimStartSeconds = 5 }));
        Assert.Contains("-c:v copy", text);
        Assert.Contains("-c:a copy", text);
        Assert.Contains("-avoid_negative_ts make_zero", text);
        Assert.DoesNotContain("libx264", text);
        Assert.DoesNotContain("-crf", text);
    }

    [Fact]
    public void Remove_audio_adds_an_and_skips_audio_map()
    {
        var text = Joined(Build(TestData.Video(), "mp4-h264", new ConversionOptions { RemoveAudio = true }));
        Assert.Contains(" -an ", text);
        Assert.DoesNotContain("-map 0:a", text);
        Assert.DoesNotContain("-c:a", text);
    }

    [Fact]
    public void Gif_uses_palette_pipeline_and_loops()
    {
        var text = Joined(Build(TestData.Video(), "gif", new ConversionOptions { TargetHeight = 480, FrameRate = 15 }));
        Assert.Contains("fps=15,scale=-1:480:flags=lanczos,split[s0][s1];[s0]palettegen", text);
        Assert.Contains("paletteuse", text);
        Assert.Contains("-loop 0", text);
        Assert.DoesNotContain("-r 15", text);
    }

    [Fact]
    public void Mp3_extraction_drops_video_and_uses_lame()
    {
        var text = Joined(Build(TestData.Video(), "mp3", new ConversionOptions { AudioBitrateKbps = 192, Channels = ChannelMode.Mono }));
        Assert.Contains("-map 0:a:0 -vn", text);
        Assert.Contains("-c:a libmp3lame -b:a 192k", text);
        Assert.Contains("-ac 1", text);
    }

    [Fact]
    public void Mp3_without_bitrate_uses_vbr_quality()
    {
        var text = Joined(Build(TestData.Audio(), "mp3"));
        Assert.Contains("-c:a libmp3lame -q:a 2", text);
    }

    [Fact]
    public void Opus_coerces_unsupported_sample_rates_to_48k()
    {
        var text = Joined(Build(TestData.Audio(), "opus", new ConversionOptions { SampleRate = 44100, AudioBitrateKbps = 96 }));
        Assert.Contains("-c:a libopus -b:a 96k", text);
        Assert.Contains("-ar 48000", text);
        Assert.DoesNotContain("-ar 44100", text);
    }

    [Fact]
    public void Wav_bit_depth_selects_pcm_codec()
    {
        Assert.Contains("-c:a pcm_s24le", Joined(Build(TestData.Audio(), "wav", new ConversionOptions { WavBitDepth = 24 })));
        Assert.Contains("-c:a pcm_s16be", Joined(Build(TestData.Audio(), "aiff")));
    }

    [Fact]
    public void Playback_speed_changes_video_pts_and_audio_tempo()
    {
        var text = Joined(Build(TestData.Video(), "mp4-h264", new ConversionOptions { PlaybackSpeed = 2 }));
        Assert.Contains("setpts=PTS/2", text);
        Assert.Contains("-af atempo=2", text);

        var slow = Joined(Build(TestData.Audio(), "mp3", new ConversionOptions { PlaybackSpeed = 0.25 }));
        Assert.Contains("atempo=0.5,atempo=0.5", slow);
    }

    [Fact]
    public void Frame_extraction_seeks_to_frame_time_and_writes_single_image()
    {
        var args = Build(TestData.Video(), "jpg", new ConversionOptions { FrameTimeSeconds = 12.5, Quality = 90 });
        var text = Joined(args);
        Assert.Equal("12.5", args[args.IndexOf("-ss") + 1]);
        Assert.Contains("-frames:v 1 -update 1", text);
        Assert.Contains("-c:v mjpeg -q:v 5", text);
        Assert.DoesNotContain("-t ", text);
    }

    [Fact]
    public void Image_to_webp_supports_lossless_and_flattening_for_jpeg()
    {
        var webp = Joined(Build(TestData.Image(), "webp", new ConversionOptions { Quality = 100 }));
        Assert.Contains("-c:v libwebp -quality 100", webp);
        Assert.Contains("-lossless 1", webp);

        var jpg = Joined(Build(TestData.Image(), "jpg", new ConversionOptions { Quality = 85, TargetHeight = 1080 }));
        Assert.Contains("split[a][b];[a]drawbox=c=white:t=fill[bg];[bg][b]overlay=format=auto,scale=-1:1080", jpg);
        Assert.Contains("-pix_fmt yuvj420p", jpg);
    }

    [Fact]
    public void Still_image_becomes_looping_video_clip()
    {
        var text = Joined(Build(TestData.Image(), "mp4-h264", new ConversionOptions { TrimEndSeconds = 8 }));
        Assert.Contains("-loop 1 -framerate 30 -i", text);
        Assert.Contains("-t 8", text);
        Assert.Contains(" -an ", text);
    }

    [Fact]
    public void Rotation_adds_transpose_filters()
    {
        Assert.Contains("transpose=1", Joined(Build(TestData.Video(), "mp4-h264", new ConversionOptions { Rotation = 90 })));
        Assert.Contains("hflip,vflip", Joined(Build(TestData.Video(), "mp4-h264", new ConversionOptions { Rotation = 180 })));
        Assert.Contains("transpose=2", Joined(Build(TestData.Video(), "mp4-h264", new ConversionOptions { Rotation = 270 })));
    }

    [Fact]
    public void Strip_metadata_removes_tags_and_chapters()
    {
        Assert.Contains("-map_metadata -1 -map_chapters -1", Joined(Build(TestData.Video(), "mkv-h264", new ConversionOptions { StripMetadata = true })));
    }

    [Fact]
    public void Mkv_keeps_subtitles_while_mp4_drops_them()
    {
        Assert.Contains("-map 0:s? -c:s copy", Joined(Build(TestData.Video(), "mkv-h264")));
        Assert.Contains(" -sn ", Joined(Build(TestData.Video(), "mp4-h264")));
    }

    [Fact]
    public void Target_size_plans_two_pass_for_software_h264()
    {
        var job = new ConversionJob(TestData.Video(duration: 100), TestData.Format("mp4-h264"),
            new ConversionOptions { TargetSizeMegabytes = 25, AudioBitrateKbps = 96 }, @"C:\out\small.mp4", EngineKind.Ffmpeg);
        var plan = new FfmpegEngine().Plan(job, TestData.Tools(), Path.GetTempPath());

        Assert.Equal(2, plan.Steps.Count);
        var pass1 = string.Join(' ', plan.Steps[0].StartInfo!.ArgumentList);
        var pass2 = string.Join(' ', plan.Steps[1].StartInfo!.ArgumentList);
        Assert.Contains("-pass 1", pass1);
        Assert.Contains("-an -f null", pass1);
        Assert.Contains("-pass 2", pass2);
        Assert.Contains("-b:a 96k", pass2);
        // 25 MB * 8192 kbit / 100 s = 2048 kbps total; minus audio 96 and 3% safety
        Assert.Contains("-b:v 1890k", pass2);
        Assert.Equal(0.45, plan.Steps[0].ProgressEnd);
    }

    [Fact]
    public void Target_size_forces_software_two_pass_even_with_hardware_encoder()
    {
        var job = new ConversionJob(TestData.Video(duration: 100), TestData.Format("mp4-h264"),
            new ConversionOptions { TargetSizeMegabytes = 25, UseHardwareEncoder = true }, @"C:\out\small.mp4", EngineKind.Ffmpeg);
        var plan = new FfmpegEngine().Plan(job, TestData.Tools(TestData.WithNvenc), Path.GetTempPath());

        Assert.Equal(2, plan.Steps.Count);
        Assert.Contains("libx264", string.Join(' ', plan.Steps[1].StartInfo!.ArgumentList));
        Assert.DoesNotContain("nvenc", string.Join(' ', plan.Steps[1].StartInfo!.ArgumentList));
    }

    [Fact]
    public void Impossible_target_size_is_rejected_with_guidance()
    {
        // 0.5 MB for 100 s leaves ~41 kbps total: below the 100 kbps video floor once audio is counted.
        var job = new ConversionJob(TestData.Video(duration: 100), TestData.Format("mp4-h264"),
            new ConversionOptions { TargetSizeMegabytes = 0.5, AudioBitrateKbps = 64 }, @"C:\out\tiny.mp4", EngineKind.Ffmpeg);
        var error = Assert.Throws<InvalidOperationException>(() => new FfmpegEngine().Plan(job, TestData.Tools(), Path.GetTempPath()));
        Assert.Contains("too small", error.Message);
        Assert.Contains("Use at least", error.Message);
    }

    [Fact]
    public void Aac_falls_back_to_libfdk_when_native_encoder_is_missing()
    {
        var fdkOnly = new FfmpegCapabilities(new HashSet<string> { "libx264", "libfdk_aac" }, new HashSet<string>(), [], null);
        Assert.Contains("-c:a libfdk_aac", Joined(Build(TestData.Video(), "m4a-aac", caps: fdkOnly)));
        Assert.Contains("-c:a aac ", Joined(Build(TestData.Video(), "m4a-aac")));
    }

    [Fact]
    public void Av1_prefers_svt_when_available()
    {
        Assert.Contains("-c:v libsvtav1", Joined(Build(TestData.Video(), "mp4-av1")));
        var aomOnly = new FfmpegCapabilities(TestData.SoftwareOnly.Encoders.Where(name => name != "libsvtav1").ToHashSet(), new HashSet<string>(), [], null);
        Assert.Contains("-c:v libaom-av1", Joined(Build(TestData.Video(), "mp4-av1", caps: aomOnly)));
    }

    [Fact]
    public void Animated_gif_to_png_still_takes_one_frame()
    {
        var gif = new SourceFile(@"C:\media\anim.gif", 1, MediaCategory.Image, DocumentFlavor.None,
            new LocalMorph.Bridge.SourceMediaInfo(LocalMorph.Bridge.SourceMediaKind.Image, 2.5, 320, 240, 10, null, null, "gif", null, null, "gif"));
        var args = Build(gif, "png", new ConversionOptions { FrameTimeSeconds = 1.2 });
        var text = Joined(args);
        Assert.Contains("-frames:v 1 -update 1", text);
        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"));
        Assert.Equal("1.2", args[args.IndexOf("-ss") + 1]);
    }

    [Theory]
    [InlineData("aac", "m4a")]
    [InlineData("vorbis", "ogg")]
    [InlineData("opus", "opus")]
    [InlineData("pcm_s16le", "wav")]
    [InlineData("dts", "mka")]
    [InlineData(null, "mka")]
    public void Audio_copy_picks_container_matching_codec(string? codec, string expected) =>
        Assert.Equal(expected, FfmpegEngine.AudioCopyExtension(codec));

    [Fact]
    public void Audio_copy_plan_rewrites_output_extension()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "media", "clip.webm");
        var outDir = Path.Combine(Path.GetTempPath(), "out");
        var source = new SourceFile(sourcePath, 1, MediaCategory.Video, DocumentFlavor.None,
            new LocalMorph.Bridge.SourceMediaInfo(LocalMorph.Bridge.SourceMediaKind.Video, 10, 640, 360, 30, 48000, 2, "vp9", "opus", null, "webm"));
        // The extension is chosen before collision handling so reservations apply to the real name.
        var preferred = OutputNaming.PreferredOutputPath(source.Path, TestData.Format("audio-copy"), outDir, string.Empty, source);
        Assert.Equal(Path.Combine(outDir, "clip.opus"), preferred);
        var job = new ConversionJob(source, TestData.Format("audio-copy"), new ConversionOptions(), preferred, EngineKind.Ffmpeg);
        new FfmpegEngine().Plan(job, TestData.Tools(), Path.GetTempPath());
        Assert.Equal(preferred, job.OutputPath);
    }

    [Fact]
    public void Two_pass_cleanup_tolerates_missing_work_directory()
    {
        var job = new ConversionJob(TestData.Video(duration: 100), TestData.Format("mp4-h264"), new ConversionOptions { TargetSizeMegabytes = 25 }, @"C:\out\small.mp4", EngineKind.Ffmpeg);
        var plan = new FfmpegEngine().Plan(job, TestData.Tools(), Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N")));
        plan.Cleanup!.Invoke();
    }

    [Theory]
    [InlineData("out_time_us=30000000", 0.5)]
    [InlineData("out_time_ms=30000000", 0.5)]
    [InlineData("out_time_us=90000000", 0.995)]
    public void Progress_ratio_is_clamped_and_based_on_duration(string line, double expected)
    {
        var sample = FfmpegEngine.ParseProgress(line, 60_000_000);
        Assert.NotNull(sample);
        Assert.Equal(expected, sample!.Ratio!.Value, 3);
    }

    [Fact]
    public void Progress_end_and_speed_are_recognized()
    {
        Assert.True(FfmpegEngine.ParseProgress("progress=end", null)!.Finished);
        Assert.Equal("3.2×", FfmpegEngine.ParseProgress("speed=3.21x", null)!.Speed);
        Assert.Null(FfmpegEngine.ParseProgress("frame=12", null));
        Assert.Null(FfmpegEngine.ParseProgress("garbage", null));
    }
}
