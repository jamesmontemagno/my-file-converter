using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using Xunit;

namespace LocalMorph.Core.Tests;

/// <summary>Runs the track, subtitle, edit, and effect tools through a real FFmpeg.</summary>
[Collection(nameof(FfmpegIntegrationCollection))]
public sealed class FfmpegMediaToolsIntegrationTests
{
    private static void AssertCompleted(ConversionJob job) =>
        Assert.True(job.State == JobState.Completed && File.Exists(job.OutputPath), $"{job.Format.Id} failed: {job.Error}");

    [FfmpegFact]
    public async Task Inspector_lists_audio_and_subtitle_tracks()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeMultiTrackVideoAsync(root);
        var source = await SourceInspector.InspectAsync(video, Fixture.Inventory.Value);

        Assert.Equal(2, source.AudioTracks.Count);
        Assert.Single(source.SubtitleTracks);
        Assert.Equal("spa", source.AudioTracks[1].Language);
        Assert.Equal("Commentary", source.AudioTracks[1].Title);
        Assert.Equal(1, source.AudioTracks[1].Channels);
        Assert.Contains("2 audio tracks", source.Summary);
    }

    [FfmpegFact]
    public async Task Swapping_the_audio_track_keeps_only_the_chosen_one()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeMultiTrackVideoAsync(root);

        var job = await Fixture.ConvertAsync(video, "mp4-copy", new ConversionOptions { AudioTrack = 1 });
        AssertCompleted(job);
        var channels = await Fixture.ProbeAsync(job.OutputPath, "-select_streams", "a", "-show_entries", "stream=channels", "-of", "csv=p=0");
        Assert.Equal("1", channels);
    }

    [FfmpegFact]
    public async Task Keeping_all_tracks_makes_the_chosen_one_first_and_default()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeMultiTrackVideoAsync(root);

        var job = await Fixture.ConvertAsync(video, "mkv-copy", new ConversionOptions { AudioTrack = 1, KeepAllAudioTracks = true });
        AssertCompleted(job);
        var tracks = await Fixture.ProbeAsync(job.OutputPath, "-select_streams", "a", "-show_entries", "stream=channels:stream_disposition=default", "-of", "csv=p=0");
        Assert.Equal(["1,1", "2,0"], tracks.Split('\n', StringSplitOptions.TrimEntries));
        var subtitles = await Fixture.ProbeAsync(job.OutputPath, "-select_streams", "s", "-show_entries", "stream=codec_name", "-of", "csv=p=0");
        Assert.Equal("subrip", subtitles);
    }

    [FfmpegFact]
    public async Task Replacing_audio_uses_the_new_soundtrack()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeVideoAsync(root, seconds: 3);
        var music = await Fixture.MakeAudioAsync(root, "new mix.wav", seconds: 5);

        var job = await Fixture.ConvertAsync(video, "mp4-copy", new ConversionOptions { ReplacementAudioPath = music });
        AssertCompleted(job);
        var probe = await SourceInspector.InspectAsync(job.OutputPath, Fixture.Inventory.Value);
        Assert.True(probe.HasAudio);
        Assert.Equal(44100, probe.Media!.SampleRate);
        Assert.InRange(probe.DurationSeconds!.Value, 2.5, 3.6);
    }

    [FfmpegFact]
    public async Task Soft_subtitles_can_be_added_to_mp4()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeVideoAsync(root);
        var captions = await Fixture.MakeSubtitlesAsync(root);

        var job = await Fixture.ConvertAsync(video, "mp4-copy", new ConversionOptions { ExternalSubtitlePath = captions });
        AssertCompleted(job);
        var codec = await Fixture.ProbeAsync(job.OutputPath, "-select_streams", "s", "-show_entries", "stream=codec_name", "-of", "csv=p=0");
        Assert.Equal("mov_text", codec);
    }

    [FfmpegFact]
    public async Task Burning_subtitles_from_an_awkward_path_works()
    {
        // Some FFmpeg builds (e.g. Homebrew's default formula) ship without libass; LocalMorph then refuses text burn-in up front.
        if (!Fixture.Inventory.Value.Ffmpeg.MayHaveFilter("subtitles")) return;

        var root = Path.Combine(Fixture.Root, "it's [a], test; dir");
        Directory.CreateDirectory(root);
        var video = await Fixture.MakeMultiTrackVideoAsync(root);

        var embedded = await Fixture.ConvertAsync(video, "mp4-h264",
            new ConversionOptions { Subtitles = SubtitleMode.BurnIn, TrimStartSeconds = 1, Speed = EncodingSpeed.Fast, UseHardwareEncoder = false });
        AssertCompleted(embedded);

        var captions = await Fixture.MakeSubtitlesAsync(root, "caption's: one.srt");
        var external = await Fixture.ConvertAsync(video, "webm-vp9",
            new ConversionOptions { Subtitles = SubtitleMode.BurnIn, ExternalSubtitlePath = captions, Speed = EncodingSpeed.Fast, TargetHeight = 180 });
        AssertCompleted(external);
    }

    [FfmpegFact]
    public void Filters_are_discovered()
    {
        var filters = Fixture.Inventory.Value.Ffmpeg.Filters;
        Assert.Contains("scale", filters);
        Assert.Contains("overlay", filters);
    }

    [FfmpegFact]
    public async Task Edits_and_effects_render()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeVideoAsync(root, seconds: 3);

        var job = await Fixture.ConvertAsync(video, "mp4-h264", new ConversionOptions
        {
            CropAspect = "9:16", FlipHorizontal = true, Deinterlace = true, Denoise = true, Reverse = true,
            FadeInSeconds = 0.5, FadeOutSeconds = 0.5, AudioDelayMilliseconds = 200, VolumePercent = 150,
            Speed = EncodingSpeed.Fast, UseHardwareEncoder = false
        });
        AssertCompleted(job);
        var probe = await SourceInspector.InspectAsync(job.OutputPath, Fixture.Inventory.Value);
        Assert.Equal(202, probe.Media!.Width);
        Assert.Equal(360, probe.Media.Height);
    }

    [FfmpegFact]
    public async Task Subtitles_extract_and_convert()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeMultiTrackVideoAsync(root);

        var srt = await Fixture.ConvertAsync(video, "subtitles-srt");
        AssertCompleted(srt);
        Assert.Contains("Hello from LocalMorph", await File.ReadAllTextAsync(srt.OutputPath));

        var vtt = await Fixture.ConvertAsync(srt.OutputPath, "subtitles-vtt");
        AssertCompleted(vtt);
        Assert.StartsWith("WEBVTT", await File.ReadAllTextAsync(vtt.OutputPath));

        var ass = await Fixture.ConvertAsync(vtt.OutputPath, "subtitles-ass");
        AssertCompleted(ass);
        Assert.Contains("Second line", await File.ReadAllTextAsync(ass.OutputPath));
    }

    [FfmpegFact]
    public async Task Sbv_lrc_and_ttml_extract_convert_and_work_as_external_tracks()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeMultiTrackVideoAsync(root);
        var baseline = await Fixture.ConvertAsync(video, "subtitles-srt");
        AssertCompleted(baseline);
        var expected = SubtitleText.Parse(await File.ReadAllTextAsync(baseline.OutputPath), "srt");
        foreach (var extension in new[] { "sbv", "lrc", "ttml" })
        {
            var extracted = await Fixture.ConvertAsync(video, "subtitles-" + extension);
            AssertCompleted(extracted);
            var cues = SubtitleText.Parse(await File.ReadAllTextAsync(extracted.OutputPath), extension);
            Assert.Equal(expected[0].StartMilliseconds, cues[0].StartMilliseconds);
            Assert.Contains("Hello from LocalMorph", cues[0].Text);

            foreach (var target in new[] { "srt", "vtt", "ass" })
            {
                var converted = await Fixture.ConvertAsync(extracted.OutputPath, "subtitles-" + target);
                AssertCompleted(converted);
                Assert.Contains("Hello from LocalMorph", await File.ReadAllTextAsync(converted.OutputPath));
            }
            var muxed = await Fixture.ConvertAsync(video, "mp4-copy", new ConversionOptions { ExternalSubtitlePath = extracted.OutputPath });
            AssertCompleted(muxed);
            Assert.Equal("mov_text", await Fixture.ProbeAsync(muxed.OutputPath, "-select_streams", "s", "-show_entries", "stream=codec_name", "-of", "csv=p=0"));

            if (Fixture.Inventory.Value.Ffmpeg.MayHaveFilter("subtitles"))
            {
                var burned = await Fixture.ConvertAsync(video, "mp4-h264", new ConversionOptions
                {
                    ExternalSubtitlePath = extracted.OutputPath, Subtitles = SubtitleMode.BurnIn,
                    UseHardwareEncoder = false, Speed = EncodingSpeed.Fast, TargetHeight = 180
                });
                AssertCompleted(burned);
            }
        }
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "work"), "localmorph-*.srt"));
    }

    [FfmpegFact]
    public async Task New_subtitle_formats_preserve_text_through_the_srt_bridge()
    {
        var root = Fixture.Root;
        SubtitleCue[] cues = [new(125, 1500, "A & B < C > D\n\u65e5\u672c\u8a9e")];
        var source = Path.Combine(root, "captions.sbv");
        await File.WriteAllTextAsync(source, SubtitleText.Serialize(cues, "sbv"));
        var converted = await Fixture.ConvertAsync(source, "subtitles-ttml");
        AssertCompleted(converted);
        Assert.Equal(cues, SubtitleText.Parse(await File.ReadAllTextAsync(converted.OutputPath), "ttml"));
        Assert.Equal(SubtitleText.Serialize(cues, "sbv"), await File.ReadAllTextAsync(source));
    }

    [FfmpegFact]
    public async Task Malformed_subtitles_leave_existing_destinations_untouched_and_clean_work_files()
    {
        var root = Fixture.Root;
        var source = Path.Combine(root, "invalid.sbv");
        var destination = Path.Combine(root, "existing.vtt");
        await File.WriteAllTextAsync(source, "not valid captions");
        await File.WriteAllTextAsync(destination, "existing destination");
        var job = new ConversionJob(await SourceInspector.InspectAsync(source, Fixture.Inventory.Value),
            FormatCatalog.Find("subtitles-vtt")!, new(), destination, EngineKind.Ffmpeg)
        { OverwritePolicy = OverwritePolicy.Overwrite };
        await ConversionRunner.RunAsync(job, Fixture.Inventory.Value, Path.Combine(root, "work"));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("Invalid SBV", job.Error);
        Assert.Equal("existing destination", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "work")));
        Assert.Empty(Directory.EnumerateFiles(root, ".localmorph-*"));
    }

    [FfmpegFact]
    public async Task Contact_sheet_and_waveform_are_single_images()
    {
        var root = Fixture.Root;
        var video = await Fixture.MakeMultiTrackVideoAsync(root, seconds: 5);

        var sheet = await Fixture.ConvertAsync(video, "contact-sheet");
        AssertCompleted(sheet);
        var sheetInfo = await SourceInspector.InspectAsync(sheet.OutputPath, Fixture.Inventory.Value);
        Assert.Equal(MediaCategory.Image, sheetInfo.Category);
        Assert.Equal(4 * 480 + 5 * 6, sheetInfo.Media!.Width);

        var wave = await Fixture.ConvertAsync(video, "waveform", new ConversionOptions { AudioTrack = 1 });
        AssertCompleted(wave);
        var waveInfo = await SourceInspector.InspectAsync(wave.OutputPath, Fixture.Inventory.Value);
        Assert.Equal(1920, waveInfo.Media!.Width);

        var audio = await Fixture.MakeAudioAsync(root);
        AssertCompleted(await Fixture.ConvertAsync(audio, "waveform"));
    }
}
