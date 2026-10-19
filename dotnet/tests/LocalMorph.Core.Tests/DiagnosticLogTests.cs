using LocalMorph.Core.Diagnostics;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using Xunit;

namespace LocalMorph.Core.Tests;

[CollectionDefinition(nameof(DiagnosticLogCollection), DisableParallelization = true)]
public sealed class DiagnosticLogCollection;

[Collection(nameof(DiagnosticLogCollection))]
public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "localmorph-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        DiagnosticLog.Configure(null);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Writes_entries_with_level_and_exception()
    {
        DiagnosticLog.Configure(directory);
        DiagnosticLog.Info("started");
        DiagnosticLog.Error("boom", new InvalidOperationException("bad state"));

        var text = DiagnosticLog.ReadAll();
        Assert.Contains("[INFO] started", text);
        Assert.Contains("[ERROR] boom", text);
        Assert.Contains("bad state", text);
    }

    [Fact]
    public void Rotates_when_the_file_exceeds_the_cap()
    {
        DiagnosticLog.Configure(directory, maxBytesPerFile: 4_096);
        for (var i = 0; i < 100; i++) DiagnosticLog.Info($"line {i} " + new string('x', 80));

        Assert.True(File.Exists(Path.Combine(directory, DiagnosticLog.PreviousFileName)));
        Assert.True(new FileInfo(DiagnosticLog.FilePath!).Length <= 4_096);
        Assert.Contains("line 99", DiagnosticLog.ReadAll());
    }

    [Fact]
    public void Unconfigured_log_is_a_no_op()
    {
        DiagnosticLog.Configure(null);
        DiagnosticLog.Error("ignored");
        Assert.Equal(string.Empty, DiagnosticLog.ReadAll());
    }

    [Fact]
    public async Task Failed_conversion_records_source_format_and_reason()
    {
        DiagnosticLog.Configure(directory);
        var source = TestData.Video(path: Path.Combine(directory, "missing.mov"));
        var job = new ConversionJob(source, TestData.Format("mp4-h264"), new ConversionOptions(), Path.Combine(directory, "out.mp4"), EngineKind.Ffmpeg);

        await ConversionRunner.RunAsync(job, TestData.Tools(), Path.Combine(directory, "work"));

        Assert.Equal(JobState.Failed, job.State);
        var text = DiagnosticLog.ReadAll();
        Assert.Contains("[ERROR] Conversion failed", text);
        Assert.Contains("missing.mov", text);
        Assert.Contains("format: mp4-h264", text);
        Assert.False(File.ReadAllBytes(DiagnosticLog.FilePath!).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
    }
}

public sealed class FailureDescriptionTests
{
    [Fact]
    public void Missing_decoder_beats_the_generic_trailing_error()
    {
        const string log = """
            $ ffmpeg -i photo.png out.jpg
              Stream #0:0: Video: png, none, 25 fps
            [vist#0:0/png @ 0x81b014180] Decoding requested, but no decoder found for: png
            Error opening output file out.jpg.
            Error opening output files: Invalid argument
            """;

        var message = ConversionRunner.DescribeToolFailure(log, 234);

        Assert.Contains("can't read PNG input", message);
        Assert.DoesNotContain("Invalid argument", message);
    }

    [Fact]
    public void Falls_back_to_the_last_error_line()
    {
        var message = ConversionRunner.DescribeToolFailure("frame=1\nSomething failed badly\n", 1);
        Assert.Equal("Something failed badly", message);
    }
}
