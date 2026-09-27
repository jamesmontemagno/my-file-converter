using LocalMorph.Bridge;
using LocalMorph.Core.Engines;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using Xunit;

namespace LocalMorph.Core.Tests;

/// <summary>Command-line coverage for the track, subtitle, edit, and effect tools layered on top of plain conversion.</summary>
public sealed class FfmpegMediaToolsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("localmorph-tools-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private string TempFile(string name)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private static List<string> Build(SourceFile source, string formatId, ConversionOptions? options = null) =>
        FfmpegEngine.BuildArguments(source, TestData.Format(formatId), options ?? new ConversionOptions(), TestData.SoftwareOnly,
            "/out/result." + TestData.Format(formatId).Extension).ToList();

    private static string Joined(IReadOnlyList<string> args) => string.Join(' ', args);

    [Fact]
    public void Selected_audio_track_is_mapped_and_made_default()
    {
        var text = Joined(Build(TestData.MultiTrackVideo(), "mp4-h264", new ConversionOptions { AudioTrack = 1 }));
        Assert.Contains("-map 0:v:0 -map 0:a:1 -disposition:a:0 default", text);
        Assert.DoesNotContain("0:a:0", text);
    }

    [Fact]
    public void Out_of_range_audio_track_fails_with_a_friendly_message()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(TestData.MultiTrackVideo(), "mp4-h264", new ConversionOptions { AudioTrack = 5 }));
        Assert.Contains("audio track", error.Message);
    }

    [Fact]
    public void Keep_all_tracks_reorders_the_chosen_track_first()
    {
        var text = Joined(Build(TestData.MultiTrackVideo(), "mkv-copy", new ConversionOptions { AudioTrack = 1, KeepAllAudioTracks = true }));
        Assert.Contains("-map 0:a:1 -map 0:a:0 -disposition:a:0 default -disposition:a:1 0", text);
        Assert.Contains("-c:v copy", text);
        Assert.Contains("-c:a copy", text);
    }

    [Fact]
    public void Audio_extraction_uses_the_selected_track_and_its_codec_for_the_extension()
    {
        var source = TestData.MultiTrackVideo();
        var text = Joined(Build(source, "mp3", new ConversionOptions { AudioTrack = 1 }));
        Assert.Contains("-map 0:a:1", text);

        Assert.Equal(".ac3", OutputNaming.ExtensionFor(TestData.Format("audio-copy"), source));
        Assert.Equal(".m4a", OutputNaming.ExtensionFor(TestData.Format("audio-copy"), source, audioTrack: 1));
    }

    [Fact]
    public void Replacement_audio_is_a_second_input_and_shortest_wins()
    {
        var music = TempFile("new mix.wav");
        var args = Build(TestData.Video(), "mp4-copy", new ConversionOptions { ReplacementAudioPath = music, TrimStartSeconds = 5 });
        var text = Joined(args);

        Assert.Equal(args.IndexOf("-i") + 2, args.LastIndexOf("-i"));
        Assert.Equal(music, args[args.LastIndexOf("-i") + 1]);
        Assert.Equal(1, args.Count(arg => arg == "-ss"));
        Assert.Contains("-map 0:v:0 -map 1:a:0 -shortest", text);
        Assert.Contains("-c:v copy", text);
        Assert.Contains("-c:a aac", text);
    }

    [Fact]
    public void Missing_replacement_audio_is_rejected()
    {
        var options = new ConversionOptions { ReplacementAudioPath = Path.Combine(_folder, "missing.mp3") };
        Assert.NotNull(options.Validate(TestData.Format("mp4-copy")));
    }

    [Fact]
    public void Mkv_keeps_every_subtitle_track_by_default()
    {
        var text = Joined(Build(TestData.MultiTrackVideo(), "mkv-copy"));
        Assert.Contains("-map 0:s? -c:s copy", text);
    }

    [Fact]
    public void Mp4_keeps_only_text_subtitles_as_mov_text()
    {
        var text = Joined(Build(TestData.MultiTrackVideo(pictureSubtitles: true), "mp4-h264", new ConversionOptions { Subtitles = SubtitleMode.Keep }));
        Assert.DoesNotContain("0:s:0", text);
        Assert.Contains("-map 0:s:1 -c:s mov_text", text);
    }

    [Fact]
    public void Removing_subtitles_drops_them()
    {
        var text = Joined(Build(TestData.MultiTrackVideo(), "mkv-copy", new ConversionOptions { Subtitles = SubtitleMode.Remove }));
        Assert.Contains("-sn", text);
        Assert.DoesNotContain("0:s", text);
    }

    [Fact]
    public void External_subtitle_file_is_muxed_as_a_soft_track()
    {
        var srt = TempFile("captions.srt");
        var args = Build(TestData.Video(), "mp4-h264", new ConversionOptions { ExternalSubtitlePath = srt });
        var text = Joined(args);
        Assert.Equal(srt, args[args.LastIndexOf("-i") + 1]);
        Assert.Contains("-map 1:s:0 -c:s mov_text", text);
    }

    [Fact]
    public void Burning_embedded_text_subtitles_uses_the_subtitles_filter()
    {
        var text = Joined(Build(TestData.MultiTrackVideo(path: "/media/movie.mkv"), "mp4-h264",
            new ConversionOptions { Subtitles = SubtitleMode.BurnIn, SubtitleTrack = 1, TrimStartSeconds = 10 }));
        Assert.Contains("setpts=PTS+10/TB,subtitles=", text);
        Assert.Contains("si=1", text);
        Assert.Contains("setpts=PTS-STARTPTS", text);
        Assert.Contains("-sn", text);
    }

    [Fact]
    public void Text_burn_in_without_libass_fails_with_a_friendly_message()
    {
        var noLibass = new Tools.FfmpegCapabilities(TestData.SoftwareOnly.Encoders, new HashSet<string>(), [], "ffmpeg version 9.0",
            new HashSet<string> { "scale", "overlay", "crop" });
        var error = Assert.Throws<InvalidOperationException>(() => FfmpegEngine.BuildArguments(TestData.MultiTrackVideo(), TestData.Format("mp4-h264"),
            new ConversionOptions { Subtitles = SubtitleMode.BurnIn }, noLibass, "/out/result.mp4"));
        Assert.Contains("libass", error.Message);

        // Picture subtitles only need the overlay filter, so they still burn in.
        var picture = FfmpegEngine.BuildArguments(TestData.MultiTrackVideo(pictureSubtitles: true), TestData.Format("mp4-h264"),
            new ConversionOptions { Subtitles = SubtitleMode.BurnIn }, noLibass, "/out/result.mp4");
        Assert.Contains("-filter_complex", picture);
    }

    [Fact]
    public void Filter_list_is_parsed()
    {
        const string text = """
            Filters:
              T.. = Timeline support
              .S. = Slice threading
              ..C = Command support
              A = Audio input/output
              V = Video input/output
              N = Dynamic number and/or type of input/output
              | = Source or sink filter
             ... abench            A->A       Benchmark part of a filtergraph.
             TSC overlay           VV->V      Overlay a video source on top of the input.
             ... subtitles         V->V       Render text subtitles onto input video using the libass library.
             ... amovie            |->N       Read audio from a movie source.
            """;
        var filters = Tools.FfmpegCapabilities.ParseFilters(text);
        Assert.Equal(["abench", "overlay", "subtitles", "amovie"], filters.ToArray());
    }

    [Fact]
    public void Burning_picture_subtitles_overlays_them()
    {
        var text = Joined(Build(TestData.MultiTrackVideo(pictureSubtitles: true), "mp4-h264", new ConversionOptions { Subtitles = SubtitleMode.BurnIn, SubtitleTrack = 0 }));
        Assert.Contains("-filter_complex [0:v:0][0:s:0]overlay=eof_action=pass", text);
        Assert.Contains("-map [v]", text);
        Assert.DoesNotContain("-vf", text);
    }

    [Fact]
    public void Filter_values_are_escaped_for_both_parser_levels()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(@"/tmp/it\\\'s\, \[a\]\\:b.srt", FfmpegEngine.EscapeFilterValue("/tmp/it's, [a]:b.srt"));
    }

    [Fact]
    public void Crop_flip_and_cleanup_filters_are_added_in_order()
    {
        var text = Joined(Build(TestData.Video(), "mp4-h264",
            new ConversionOptions { CropAspect = "9:16", FlipHorizontal = true, Deinterlace = true, Denoise = true }));
        Assert.Contains("-vf yadif,hqdn3d,hflip,crop=w='min(iw,ih*9/16)':h='min(ih,iw*16/9)',scale=trunc(iw/2)*2:trunc(ih/2)*2", text);
    }

    [Fact]
    public void Fades_and_reverse_apply_to_video_and_audio()
    {
        var text = Joined(Build(TestData.Video(duration: 60), "mp4-h264",
            new ConversionOptions { FadeInSeconds = 1, FadeOutSeconds = 2, Reverse = true, TrimStartSeconds = 10, TrimEndSeconds = 30 }));
        Assert.Contains("reverse,fade=t=in:st=0:d=1,fade=t=out:st=18:d=2", text);
        Assert.Contains("areverse", text);
        Assert.Contains("afade=t=in:st=0:d=1,afade=t=out:st=18:d=2", text);
    }

    [Fact]
    public void Audio_delay_shifts_or_trims_the_soundtrack()
    {
        var later = Joined(Build(TestData.Video(), "mp4-h264", new ConversionOptions { AudioDelayMilliseconds = 250 }));
        var earlier = Joined(Build(TestData.Video(), "mp4-h264", new ConversionOptions { AudioDelayMilliseconds = -500 }));
        Assert.Contains("adelay=delays=250:all=1", later);
        Assert.Contains("atrim=start=0.5,asetpts=PTS-STARTPTS", earlier);
    }

    [Fact]
    public void Stream_copy_formats_ignore_filters()
    {
        var text = Joined(Build(TestData.Video(), "mp4-copy", new ConversionOptions { CropAspect = "1:1", Reverse = true }));
        Assert.DoesNotContain("crop", text);
        Assert.DoesNotContain("reverse", text);
    }

    [Fact]
    public void Subtitle_extraction_maps_the_selected_track()
    {
        var text = Joined(Build(TestData.MultiTrackVideo(), "subtitles-vtt", new ConversionOptions { SubtitleTrack = 1 }));
        Assert.Contains("-map 0:s:1 -vn -an -dn -c:s webvtt", text);
    }

    [Fact]
    public void Picture_subtitles_cannot_be_extracted_to_text()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(TestData.MultiTrackVideo(pictureSubtitles: true), "subtitles-srt"));
        Assert.Contains("picture-based", error.Message);
    }

    [Fact]
    public void Subtitle_files_convert_between_formats()
    {
        var text = Joined(Build(TestData.Subtitle(), "subtitles-srt"));
        Assert.Contains("-map 0:s:0 -vn -an -dn -c:s srt", text);
    }

    [Fact]
    public void Contact_sheet_samples_sixteen_frames_into_a_grid()
    {
        var text = Joined(Build(TestData.Video(duration: 80), "contact-sheet"));
        Assert.Contains("fps=0.2:round=down", text);
        Assert.Contains("tile=4x4", text);
        Assert.Contains("-frames:v 1", text);
    }

    [Fact]
    public void Waveform_draws_the_selected_audio_track()
    {
        var text = Joined(Build(TestData.MultiTrackVideo(), "waveform", new ConversionOptions { AudioTrack = 1 }));
        Assert.Contains("[0:a:1]aformat=channel_layouts=mono,showwavespic", text);
    }

    [Fact]
    public void Waveform_of_a_silent_video_fails_with_a_friendly_message()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(TestData.Video(audio: false), "waveform"));
        Assert.Contains("no audio", error.Message);
    }

    [Fact]
    public void Probe_describes_tracks_with_language_and_title()
    {
        var tracks = TestData.MultiTrackVideo().AudioTracks;
        Assert.Equal(2, tracks.Count);
        Assert.Contains("Spanish", tracks[1].Describe());
        Assert.Contains("Commentary", tracks[1].Describe());
        Assert.True(TestData.MultiTrackVideo(pictureSubtitles: true).SubtitleTracks[0].IsPictureSubtitle);
    }

    [Fact]
    public void New_presets_point_at_real_formats()
    {
        foreach (var id in new[] { "vertical-9x16", "square-1x1", "mute-video", "contact-sheet", "extract-subtitles", "waveform", "to-srt", "to-vtt" })
        {
            var preset = Presets.All.Single(preset => preset.Id == id);
            Assert.NotNull(FormatCatalog.Find(preset.FormatId));
            Assert.Null(preset.Options.Validate(TestData.Format(preset.FormatId)));
        }
    }
}
