using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using Xunit;

namespace LocalMorph.Core.Tests;

[Collection(nameof(FfmpegIntegrationCollection))]
public sealed class OutputSafetyTests
{
    [FfmpegFact]
    public async Task Failed_overwrite_preserves_existing_destination()
    {
        var root = Fixture.Root;
        try
        {
            var input = Path.Combine(root, "broken.mp4");
            var output = Path.Combine(root, "existing.mp4");
            await File.WriteAllTextAsync(input, "not a video");
            await File.WriteAllTextAsync(output, "previous result");
            var source = await SourceInspector.InspectAsync(input, Fixture.Inventory.Value);
            var job = new ConversionJob(source, FormatCatalog.Find("mp4-h264")!, new(), output, EngineKind.Ffmpeg)
            { OverwritePolicy = OverwritePolicy.Overwrite };

            await ConversionRunner.RunAsync(job, Fixture.Inventory.Value, Path.Combine(root, "work"));

            Assert.Equal(JobState.Failed, job.State);
            Assert.Equal("previous result", await File.ReadAllTextAsync(output));
            Assert.Empty(Directory.GetFiles(root, ".localmorph-*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [FfmpegFact]
    public async Task Canceled_overwrite_preserves_existing_destination()
    {
        var root = Fixture.Root;
        try
        {
            var input = await Fixture.MakeAudioAsync(root);
            var output = Path.Combine(root, "existing.wav");
            await File.WriteAllTextAsync(output, "previous result");
            var source = await SourceInspector.InspectAsync(input, Fixture.Inventory.Value);
            var job = new ConversionJob(source, FormatCatalog.Find("wav")!, new(), output, EngineKind.Ffmpeg)
            { OverwritePolicy = OverwritePolicy.Overwrite };
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await ConversionRunner.RunAsync(job, Fixture.Inventory.Value, Path.Combine(root, "work"), cancellation.Token);

            Assert.Equal(JobState.Canceled, job.State);
            Assert.Equal("previous result", await File.ReadAllTextAsync(output));
            Assert.Empty(Directory.GetFiles(root, ".localmorph-*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [FfmpegFact]
    public async Task Successful_overwrite_commits_valid_output()
    {
        var root = Fixture.Root;
        try
        {
            var input = await Fixture.MakeAudioAsync(root);
            var output = Path.Combine(root, "existing.wav");
            await File.WriteAllTextAsync(output, "previous result");
            var source = await SourceInspector.InspectAsync(input, Fixture.Inventory.Value);
            var job = new ConversionJob(source, FormatCatalog.Find("wav")!, new(), output, EngineKind.Ffmpeg)
            { OverwritePolicy = OverwritePolicy.Overwrite };

            await ConversionRunner.RunAsync(job, Fixture.Inventory.Value, Path.Combine(root, "work"));

            Assert.Equal(JobState.Completed, job.State);
            Assert.Equal(output, job.Result!.OutputPath);
            Assert.True(job.Result.OutputBytes > 100);
            Assert.Empty(Directory.GetFiles(root, ".localmorph-*"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
