using LocalMorph.Core.Formats;
using LocalMorph.Core.Files;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;
using Xunit;

namespace LocalMorph.Core.Tests;

[Collection(nameof(FfmpegIntegrationCollection))]
public sealed class QueueFileAccessTests
{
    private sealed class Resource(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Fact]
    public async Task Authorization_failure_stops_before_creating_output()
    {
        var root = Fixture.Root;
        try
        {
            var source = new SourceFile(Path.Combine(root, "source.wav"), 0, MediaCategory.Audio, default, null);
            var output = Path.Combine(root, "uncreated", "output.wav");
            var job = new ConversionJob(source, FormatCatalog.Find("wav")!, new(), output, EngineKind.Ffmpeg);
            using var queue = new ConversionQueue(Path.Combine(root, "work"))
            {
                AcquireFileAccess = _ => throw new UnauthorizedAccessException("Choose an output folder.")
            };
            queue.Enqueue(job);
            queue.Start();
            await queue.WaitForIdleAsync();
            Assert.Equal(JobState.Failed, job.State);
            Assert.Contains("Choose an output folder", job.Error);
            Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [FfmpegFact]
    public async Task Removing_a_running_row_keeps_job_access_until_cancellation_finishes()
    {
        var root = Fixture.Root;
        try
        {
            var input = await Fixture.MakeAudioAsync(root);
            var source = await SourceInspector.InspectAsync(input, Fixture.Inventory.Value);
            var job = new ConversionJob(source, FormatCatalog.Find("wav")!, new(), Path.Combine(root, "canceled.wav"), EngineKind.Ffmpeg);
            var closed = 0;
            var removed = 0;
            var registry = new FileAccessRegistry([new(root, "folder", true, true)],
                grant => new(grant, new Resource(() => closed++)), _ => { });
            using var workspace = registry.Acquire(input);
            using var queue = new ConversionQueue(Path.Combine(root, "work"))
            {
                Tools = () => Fixture.Inventory.Value,
                AcquireFileAccess = _ => registry.Acquire(root, writeDirectory: true)
            };
            job.Changed += current =>
            {
                if (current.CommandLine is null || Interlocked.Exchange(ref removed, 1) != 0) return;
                queue.Remove(current);
                workspace.Dispose();
                Assert.Equal(0, closed);
            };
            queue.Enqueue(job);
            queue.Start();
            await queue.WaitForIdleAsync();
            Assert.Equal(JobState.Canceled, job.State);
            Assert.Equal(1, closed);
            Assert.Empty(queue.Jobs);
            Assert.Empty(Directory.GetFiles(root, ".localmorph-*"));
            Assert.False(File.Exists(job.OutputPath));
            Assert.True(File.Exists(input));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [FfmpegFact]
    public async Task Job_lease_outlives_terminal_notification_and_final_cleanup()
    {
        var root = Fixture.Root;
        try
        {
            var input = await Fixture.MakeAudioAsync(root);
            var source = await SourceInspector.InspectAsync(input, Fixture.Inventory.Value);
            var job = new ConversionJob(source, FormatCatalog.Find("wav")!, new(), Path.Combine(root, "result.wav"), EngineKind.Ffmpeg);
            var closed = 0;
            var terminalWhileHeld = false;
            job.Changed += current =>
            {
                if (current.IsTerminal) terminalWhileHeld = closed == 0;
            };
            using var queue = new ConversionQueue(Path.Combine(root, "work"))
            {
                Tools = () => Fixture.Inventory.Value,
                AcquireFileAccess = _ => new Resource(() =>
                {
                    Assert.True(job.IsTerminal);
                    Assert.Equal(job.OutputPath, job.WorkingOutputPath);
                    Assert.Empty(Directory.GetFiles(root, ".localmorph-*"));
                    closed++;
                })
            };
            queue.Enqueue(job);
            queue.Start();
            await queue.WaitForIdleAsync();
            Assert.Equal(JobState.Completed, job.State);
            Assert.True(terminalWhileHeld);
            Assert.Equal(1, closed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
