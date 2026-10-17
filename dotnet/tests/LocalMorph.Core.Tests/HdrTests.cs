using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using LocalMorph.Core.Engines;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;
using Xunit;

namespace LocalMorph.Core.Tests;

[Collection(nameof(FfmpegIntegrationCollection))]
public sealed class HdrTests : IDisposable
{
    private readonly string root = Fixture.Root;
    public void Dispose() => Directory.Delete(root, recursive: true);

    [FfmpegFact]
    public async Task Pq_and_hlg_are_tone_mapped_and_tagged_as_bt709()
    {
        foreach (var transfer in new[] { "smpte2084", "arib-std-b67" })
        {
            var source = Path.Combine(root, transfer + ".mkv");
            await Fixture.RunFfmpegAsync(["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=24:d=0.5",
                "-vf", $"zscale=matrixin=bt709:transferin=bt709:primariesin=bt709:transfer={transfer}:primaries=bt2020:matrix=bt2020nc,format=yuv420p10le",
                "-c:v", "ffv1", "-color_trc", transfer, "-color_primaries", "bt2020", "-colorspace", "bt2020nc", source]);
            var input = await SourceInspector.InspectAsync(source, Fixture.Inventory.Value);
            Assert.True(input.Media!.IsHdr);
            Assert.Equal("yuv420p10le", input.Media.PixelFormat);
            var job = await Fixture.ConvertAsync(source, "mp4-h264", new ConversionOptions { ToneMapToSdr = true, UseHardwareEncoder = false, Quality = 100 });
            Assert.True(job.State == JobState.Completed, job.Error + "\n" + job.LogTail);
            var output = await SourceInspector.InspectAsync(job.OutputPath, Fixture.Inventory.Value);
            Assert.Equal("bt709", output.Media!.ColorTransfer);
            Assert.Equal("bt709", output.Media.ColorPrimaries);
            Assert.Equal("bt709", output.Media.ColorSpace);
            Assert.False(output.Media.IsHdr);
            Assert.Contains("tonemap=tonemap=mobius", job.CommandLine);
            var untouched = await Fixture.ConvertAsync(source, "mp4-h264", new ConversionOptions { UseHardwareEncoder = false, Quality = 100 });
            Assert.Equal(JobState.Completed, untouched.State);
            Assert.True(Math.Abs(await LumaAsync(job.OutputPath) - await LumaAsync(untouched.OutputPath)) > 5,
                "Tone mapping must materially transform luminance, not merely change color tags.");
        }
    }

    [Fact]
    public void Unknown_transfer_is_rejected_and_known_sdr_is_not_tone_mapped()
    {
        var source = TestData.Video();
        var options = new ConversionOptions { ToneMapToSdr = true };
        Assert.Throws<InvalidOperationException>(() => FfmpegEngine.BuildArguments(source, TestData.Format("mp4-h264"), options, TestData.SoftwareOnly, "out.mp4"));
        var sdr = source with { Media = source.Media! with { ColorTransfer = "bt709" } };
        var args = FfmpegEngine.BuildArguments(sdr, TestData.Format("mp4-h264"), options, TestData.SoftwareOnly, "out.mp4");
        Assert.DoesNotContain(args, argument => argument.Contains("tonemap="));
    }

    private static async Task<double> LumaAsync(string path)
    {
        using var process = Process.Start(CommandLine.Create(Fixture.Inventory.Value.PathFor(ToolKind.Ffmpeg)!,
            ["-hide_banner", "-i", path, "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-frames:v", "1", "-an", "-f", "null", CommandLine.NullDevice]))!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(output, error, process.WaitForExitAsync());
        Assert.Equal(0, process.ExitCode);
        var match = Regex.Match(await error, @"lavfi.signalstats.YAVG=([0-9.]+)");
        Assert.True(match.Success, await error);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
