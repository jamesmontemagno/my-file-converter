using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LocalMorph.Core.Engines;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;
using Xunit;

namespace LocalMorph.Core.Tests;

public sealed class AppleEncoderTests
{
    private static readonly FfmpegCapabilities Apple = new(
        new HashSet<string> { "h264_videotoolbox", "hevc_videotoolbox", "aac" }, new HashSet<string>(), [], "test");

    [Theory]
    [InlineData("mp4-h264", "h264_videotoolbox")]
    [InlineData("mp4-h265", "hevc_videotoolbox")]
    public void Apple_software_requires_explicit_bitrate_without_quality_or_generic_pass_flags(string format, string encoder)
    {
        var options = new ConversionOptions { UseHardwareEncoder = false, VideoBitrateKbps = 2000 };
        var arguments = FfmpegEngine.BuildArguments(TestData.Video(), TestData.Format(format), options, Apple, "output.mp4");
        var command = string.Join(' ', arguments);
        Assert.Contains("-c:v " + encoder, command);
        Assert.Contains("-require_sw 1 -b:v 2000k", command);
        Assert.DoesNotContain("-q:v", arguments);
        Assert.DoesNotContain("-crf", arguments);
        Assert.DoesNotContain("-pass", arguments);
        Assert.DoesNotContain("-constant_bit_rate", arguments);
        Assert.NotNull(FfmpegEngine.ValidateConfiguration(TestData.Video(), TestData.Format(format),
            options with { VideoBitrateKbps = null }, Apple));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(200001)]
    public void Invalid_explicit_bitrate_is_rejected(int bitrate)
    {
        Assert.NotNull(new ConversionOptions { VideoBitrateKbps = bitrate }.Validate(TestData.Format("mp4-h264")));
    }

    [Fact]
    public void Apple_target_size_replans_to_a_lower_bitrate_without_changing_options()
    {
        var options = new ConversionOptions { TargetSizeMegabytes = 1, UseHardwareEncoder = true };
        var job = new ConversionJob(TestData.Video(duration: 4, audio: false), TestData.Format("mp4-h264"), options, "output.mp4", EngineKind.Ffmpeg);
        var tools = new ToolInventory(new Dictionary<ToolKind, ToolInfo>
        {
            [ToolKind.Ffmpeg] = new(ToolKind.Ffmpeg, "ffmpeg", null, ToolSource.Bundled)
        }, Apple);
        var plan = new FfmpegEngine().Plan(job, tools, "work");
        var step = Assert.Single(plan.Steps);
        var retry = plan.RetryOversizedOutput!(1_500_000);
        Assert.NotNull(retry);
        var original = step.StartInfo!.ArgumentList.ToList();
        var adjusted = Assert.Single(retry.Steps).StartInfo!.ArgumentList.ToList();
        Assert.DoesNotContain("-pass", original);
        Assert.Contains("-require_sw", original);
        var originalBitrate = int.Parse(original[original.IndexOf("-b:v") + 1].TrimEnd('k'));
        var adjustedBitrate = int.Parse(adjusted[adjusted.IndexOf("-b:v") + 1].TrimEnd('k'));
        Assert.InRange(adjustedBitrate, 100, originalBitrate - 1);
        Assert.Same(options, job.Options);
        Assert.Null(plan.RetryOversizedOutput(long.MaxValue));
    }

    [Fact]
    public void Existing_x264_target_size_stays_two_pass_without_apple_retries()
    {
        var job = new ConversionJob(TestData.Video(), TestData.Format("mp4-h264"),
            new ConversionOptions { TargetSizeMegabytes = 25 }, "output.mp4", EngineKind.Ffmpeg);
        var plan = new FfmpegEngine().Plan(job, new ToolInventory(new Dictionary<ToolKind, ToolInfo>
        {
            [ToolKind.Ffmpeg] = new(ToolKind.Ffmpeg, "ffmpeg", null, ToolSource.Path)
        }, TestData.SoftwareOnly), "work");
        Assert.Equal(2, plan.Steps.Count);
        Assert.Contains("-pass 1", plan.Steps[0].Describe());
        Assert.Contains("-pass 2", plan.Steps[1].Describe());
        Assert.Null(plan.RetryOversizedOutput);
    }
}

public sealed class AppleFfmpegFactAttribute : FactAttribute
{
    public AppleFfmpegFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            Skip = "Requires an Apple Silicon Mac.";
        else if (!Fixture.HasFfmpeg || !Fixture.Inventory.Value.Ffmpeg.HasEncoder("h264_videotoolbox")
                 || !Fixture.Inventory.Value.Ffmpeg.HasEncoder("hevc_videotoolbox"))
            Skip = "Requires FFmpeg with Apple VideoToolbox encoders.";
    }
}

[Collection(nameof(FfmpegIntegrationCollection))]
public sealed class AppleEncoderIntegrationTests
{
    private static ToolInventory AppleTools()
    {
        var tools = Fixture.Inventory.Value;
        return new ToolInventory(tools.Tools, new FfmpegCapabilities(
            tools.Ffmpeg.Encoders.Where(encoder => encoder is not ("libx264" or "libx265")).ToHashSet(),
            tools.Ffmpeg.HardwareAccelerations, [], tools.Ffmpeg.Version, tools.Ffmpeg.Filters));
    }

    private static async Task<string> SourceAsync(string root)
    {
        var path = Path.Combine(root, "apple-source.mp4");
        await Fixture.RunFfmpegAsync(["-v", "error", "-nostdin", "-f", "lavfi", "-i",
            "testsrc2=size=640x360:rate=30:duration=4", "-c:v", "mpeg4", "-an", path]);
        return path;
    }

    [AppleFfmpegFact]
    public async Task Apple_software_h264_and_hevc_produce_valid_outputs_through_the_shared_queue()
    {
        var root = Fixture.Root;
        var tools = AppleTools();
        var path = await SourceAsync(root);
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var source = await SourceInspector.InspectAsync(path, tools);
        foreach (var (format, codec) in new[] { ("mp4-h264", "h264"), ("mp4-h265", "hevc") })
        {
            var job = new ConversionJob(source, TestData.Format(format),
                new ConversionOptions { UseHardwareEncoder = false, VideoBitrateKbps = 1000 },
                Path.Combine(root, codec + ".mp4"), EngineKind.Ffmpeg);
            await ConversionRunner.RunAsync(job, tools, Path.Combine(root, "work"));
            Assert.True(job.State == JobState.Completed, job.Error + "\n" + job.LogTail);
            var result = await SourceInspector.InspectAsync(job.OutputPath, tools);
            Assert.Equal(codec, result.Media!.VideoCodec);
            Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        }
    }

    [AppleFfmpegFact]
    public async Task Apple_target_size_commits_only_within_the_decimal_megabyte_limit()
    {
        var root = Fixture.Root;
        var tools = AppleTools();
        var path = await SourceAsync(root);
        var source = await SourceInspector.InspectAsync(path, tools);
        var job = new ConversionJob(source, TestData.Format("mp4-h264"),
            new ConversionOptions { TargetSizeMegabytes = 0.2, RemoveAudio = true },
            Path.Combine(root, "limited.mp4"), EngineKind.Ffmpeg);
        await ConversionRunner.RunAsync(job, tools, Path.Combine(root, "work"));
        Assert.True(job.State == JobState.Completed, job.Error + "\n" + job.LogTail);
        Assert.InRange(job.Result!.OutputBytes, 1, 200_000);
        Assert.DoesNotContain("-pass ", job.CommandLine);
        Assert.InRange(job.CommandLine!.Split("-require_sw").Length - 1, 1, 3);
        Assert.Equal("h264", (await SourceInspector.InspectAsync(job.OutputPath, tools)).Media!.VideoCodec);
    }

    [AppleFfmpegFact]
    public async Task Apple_oversize_failure_preserves_the_existing_destination_and_removes_staging()
    {
        var root = Fixture.Root;
        var tools = AppleTools();
        var path = await SourceAsync(root);
        var source = await SourceInspector.InspectAsync(path, tools);
        var output = Path.Combine(root, "existing.mp4");
        await File.WriteAllTextAsync(output, "existing destination");
        var job = new ConversionJob(source, TestData.Format("mp4-h264"),
            new ConversionOptions { TargetSizeMegabytes = 0.052, RemoveAudio = true }, output, EngineKind.Ffmpeg)
        { OverwritePolicy = OverwritePolicy.Overwrite };
        await ConversionRunner.RunAsync(job, tools, Path.Combine(root, "work"));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("exceeds", job.Error);
        Assert.Equal("existing destination", await File.ReadAllTextAsync(output));
        Assert.False(File.Exists(Path.Combine(root, $".localmorph-{job.Id:N}.mp4")));
        Assert.Empty(job.CompletedOutputs);
    }
}
