using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;
using Xunit;

namespace LocalMorph.Core.Tests;

public sealed class BatchPreflightTests : IDisposable
{
    private readonly string root = Fixture.Root;
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void Mixed_batch_counts_only_applicable_inputs()
    {
        var video = TestData.Video(Path.Combine(root, "video.mov"));
        var audio = TestData.Audio(Path.Combine(root, "audio.flac"));
        var plan = Prepare([new(video, new()), new(audio, new())]);
        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal(1, plan.RunnableCount);
        Assert.True(plan.Entries[0].CanRun);
        Assert.Contains("does not apply", plan.Entries[1].Issue);
    }

    [Fact]
    public void Invalid_options_and_missing_tools_are_reported_before_execution()
    {
        var video = TestData.Video(Path.Combine(root, "video.mov"));
        var invalid = Prepare([new(video, new() { TargetSizeMegabytes = double.NaN })]);
        Assert.Equal(0, invalid.RunnableCount);
        Assert.NotNull(invalid.Entries[0].Issue);
        var missing = BatchPlanner.Prepare([new(video, new())], TestData.Format("mp4-h264"), new(),
            ToolInventory.Empty, root, "-out", OverwritePolicy.Rename, [video.Path]);
        Assert.Contains("tool or decoder", missing.Entries[0].Issue);
        Assert.Equal(0, missing.RunnableCount);
    }

    [Fact]
    public void Overwrite_and_duplicate_stems_never_reserve_a_queued_source()
    {
        var first = TestData.Video(Path.Combine(root, "clip.mov"));
        var second = TestData.Video(Path.Combine(root, "clip.mp4"));
        var plan = Prepare([new(first, new()), new(second, new())], OverwritePolicy.Overwrite, "");
        Assert.Equal(2, plan.RunnableCount);
        Assert.Equal(2, plan.Entries.Select(entry => entry.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(plan.Entries, entry => entry.OutputPath == first.Path || entry.OutputPath == second.Path);
    }

    [Fact]
    public async Task Skip_policy_is_counted_consistently_with_execution()
    {
        var video = TestData.Video(Path.Combine(root, "video.mov"));
        await File.WriteAllTextAsync(Path.Combine(root, "video-out.mp4"), "existing result");
        var plan = Prepare([new(video, new())], OverwritePolicy.Skip);
        Assert.Equal(0, plan.RunnableCount);
        Assert.Contains("already exists", plan.Entries[0].Issue);
    }

    [Fact]
    public void Split_without_duration_is_not_presented_as_runnable()
    {
        var source = TestData.Video(Path.Combine(root, "video.mov")) with { Media = null };
        var plan = BatchPlanner.Prepare([new(source, new())], TestData.Format("mp4-h264"),
            new(MediaOperation.Split, 1), TestData.Tools(), root, "-out", OverwritePolicy.Rename, [source.Path]);
        Assert.Equal(0, plan.RunnableCount);
        Assert.Contains("duration", plan.Entries[0].Issue, StringComparison.OrdinalIgnoreCase);
    }

    private BatchPreflight Prepare(ConversionInput[] inputs, OverwritePolicy policy = OverwritePolicy.Rename, string suffix = "-out") =>
        BatchPlanner.Prepare(inputs, TestData.Format("mp4-h264"), new(), TestData.Tools(), root, suffix, policy, inputs.Select(input => input.Source.Path));
}
