using System.Xml;
using LocalMorph.Core.Engines;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;
using Xunit;

namespace LocalMorph.Core.Tests;

public sealed class SubtitleTextTests
{
    [Fact]
    public void Sbv_reads_youtube_timestamps_bom_crlf_and_multiline_text()
    {
        var cue = Assert.Single(SubtitleText.Parse("\uFEFF0:00:01.125,1:02:03.456\r\nHello\r\nWorld\r\n", ".sbv"));
        Assert.Equal(new SubtitleCue(1125, 3723456, "Hello\nWorld"), cue);
    }

    [Fact]
    public void Lrc_reads_metadata_offsets_repeated_timestamps_and_infers_end_times()
    {
        var cues = SubtitleText.Parse("[ar:Artist]\n[offset:125]\n[01:02.50]Last\n[00:01.1][00:03:125]First", ".lrc");
        Assert.Equal(new SubtitleCue(1225, 3250, "First"), cues[0]);
        Assert.Equal(new SubtitleCue(3250, 62625, "First"), cues[1]);
        Assert.Equal(new SubtitleCue(62625, 65625, "Last"), cues[2]);
    }

    [Fact]
    public void Lrc_simultaneous_lines_have_the_same_nonzero_duration()
    {
        var cues = SubtitleText.Parse("[00:01]One\n[00:01]Two\n[00:02]Three", "lrc");
        Assert.Equal(2000, cues[0].EndMilliseconds);
        Assert.Equal(2000, cues[1].EndMilliseconds);
    }

    [Fact]
    public void Ttml_reads_namespaces_entities_breaks_duration_and_inherited_timing()
    {
        var cue = Assert.Single(SubtitleText.Parse("""
            <tt:tt xmlns:tt="http://www.w3.org/ns/ttml">
              <tt:body begin="1s"><tt:div begin="500ms" end="5s">
                <tt:p begin="00:00:00.125" dur="2s">
                  A &amp; <tt:span>B</tt:span><tt:br/>C
                </tt:p>
              </tt:div></tt:body>
            </tt:tt>
            """, "ttml"));
        Assert.Equal(new SubtitleCue(1625, 3625, "A & B\nC"), cue);
    }

    [Theory]
    [InlineData("srt")]
    [InlineData("sbv")]
    [InlineData("ttml")]
    public void Timed_formats_round_trip_text_and_millisecond_precision(string extension)
    {
        SubtitleCue[] cues = [new(1234, 3723456, "A & B < C > D \"quoted\"\n\u65e5\u672c\u8a9e"), new(4000000, 4003000, "Last")];
        Assert.Equal(cues, SubtitleText.Parse(SubtitleText.Serialize(cues, extension), extension));
    }

    [Fact]
    public void Lrc_output_has_start_timestamps_and_flattens_line_breaks()
    {
        var output = SubtitleText.Serialize([new(3723456, 3725000, "First\nSecond")], "lrc");
        Assert.Equal("[62:03.456]First Second\n", output);
    }

    [Theory]
    [InlineData("", "sbv")]
    [InlineData("0:00:01.000,0:00:00.000\nBackwards", "sbv")]
    [InlineData("0:61:01.000,0:62:00.000\nInvalid minutes", "sbv")]
    [InlineData("0:00:00.000,0:00:01.000\nValid\n\nbroken block", "sbv")]
    [InlineData("No timestamps", "lrc")]
    [InlineData("[00:99]Bad time", "lrc")]
    [InlineData("<tt><body><p>Untimed</p></body></tt>", "ttml")]
    [InlineData("<tt><body timeContainer=\"seq\"><p begin=\"0s\" end=\"1s\">Sequential</p></body></tt>", "ttml")]
    [InlineData("<tt><p begin=\"0s\" end=\"1s\"><span begin=\"0s\">Timed span</span></p></tt>", "ttml")]
    [InlineData("<tt xmlns:ttp=\"http://www.w3.org/ns/ttml#parameter\" ttp:timeBase=\"clock\"><p begin=\"0s\" end=\"1s\">Clock</p></tt>", "ttml")]
    public void Invalid_or_unsupported_captions_fail_explicitly(string content, string extension) =>
        Assert.Throws<FormatException>(() => SubtitleText.Parse(content, extension));

    [Fact]
    public void Ttml_cannot_resolve_external_entities() =>
        Assert.Throws<XmlException>(() => SubtitleText.Parse("""
            <!DOCTYPE tt [<!ENTITY external SYSTEM "file:///must-not-read">]>
            <tt><p begin="0s" end="1s">&external;</p></tt>
            """, "ttml"));

    [Fact]
    public void Preview_plans_prepare_source_and_convert_output_without_writing_files()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var source = TestData.Subtitle(Path.Combine(directory, "captions.sbv"));
        var job = new ConversionJob(source, TestData.Format("subtitles-ttml"), new(),
            Path.Combine(directory, "captions.ttml"), EngineKind.Ffmpeg);
        var plan = new FfmpegEngine().Plan(job, TestData.Tools(), directory);
        Assert.Equal(3, plan.Steps.Count);
        Assert.True(plan.Steps[0].IsInProcess);
        Assert.Contains("source.srt", plan.Steps[1].Describe());
        Assert.Contains("output.srt", plan.Steps[1].Describe());
        Assert.True(plan.Steps[2].IsInProcess);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Target_size_retries_recreate_normalized_external_subtitles_after_cleanup()
    {
        var root = Fixture.Root;
        try
        {
            var source = Path.Combine(root, "captions.sbv");
            await File.WriteAllTextAsync(source, "0:00:00.000,0:00:01.000\nHello");
            var capabilities = new FfmpegCapabilities(new HashSet<string> { "h264_videotoolbox", "aac" }, new HashSet<string>(), [], "test");
            var job = new ConversionJob(TestData.Video(duration: 4, audio: false), TestData.Format("mp4-h264"),
                new ConversionOptions { TargetSizeMegabytes = 1, ExternalSubtitlePath = source },
                Path.Combine(root, "output.mp4"), EngineKind.Ffmpeg);
            var plan = new FfmpegEngine().Plan(job, TestData.Tools(capabilities), root);
            await plan.Steps[0].Execute!(job, CancellationToken.None);
            var normalized = Assert.Single(Directory.EnumerateFiles(root, "*-external.srt"));
            var retry = plan.RetryOversizedOutput!(1_500_000);
            Assert.NotNull(retry);
            plan.Cleanup!();
            Assert.False(File.Exists(normalized));
            await retry.Steps[0].Execute!(job, CancellationToken.None);
            Assert.True(File.Exists(normalized));
            Assert.Contains(normalized, retry.Steps[1].StartInfo!.ArgumentList);
            retry.Cleanup!();
            Assert.False(File.Exists(normalized));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Subtitle_conversion_honors_cancellation_without_writing_output()
    {
        var root = Fixture.Root;
        try
        {
            var source = Path.Combine(root, "captions.sbv");
            var destination = Path.Combine(root, "captions.srt");
            await File.WriteAllTextAsync(source, "0:00:00.000,0:00:01.000\nHello");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SubtitleText.ConvertAsync(source, destination, cancellation.Token));
            Assert.False(File.Exists(destination));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
