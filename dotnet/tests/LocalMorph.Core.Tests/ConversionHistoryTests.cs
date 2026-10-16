using LocalMorph.Core.Jobs;
using Xunit;

namespace LocalMorph.Core.Tests;

public sealed class ConversionHistoryTests : IDisposable
{
    private readonly string root = Fixture.Root;
    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task Corrupt_history_is_reported_without_overwriting_it_on_load()
    {
        var path = Path.Combine(root, "history.json");
        await File.WriteAllTextAsync(path, "not json");
        var history = new ConversionHistory(path);
        Assert.Empty(history.Entries);
        Assert.Contains("Could not load", history.PersistenceError);
        Assert.Equal("not json", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Failed_save_reports_an_error_and_preserves_previous_history()
    {
        var path = Path.Combine(root, "history.json");
        var history = new ConversionHistory(path);
        history.Add(Entry());
        var previous = await File.ReadAllTextAsync(path);
        Directory.CreateDirectory(path + ".tmp");
        string? error = null;
        history.PersistenceFailed += message => error = message;
        history.Add(Entry());
        Assert.Contains("could not be saved", error);
        Assert.Equal(error, history.PersistenceError);
        Assert.Equal(previous, await File.ReadAllTextAsync(path));
        Directory.Delete(path + ".tmp");
        history.Clear();
        Assert.Null(history.PersistenceError);
    }

    [Fact]
    public async Task Removing_partial_multi_output_history_keeps_every_result()
    {
        var outputs = new[] { Path.Combine(root, "one.mp4"), Path.Combine(root, "two.mp4") };
        foreach (var output in outputs) await File.WriteAllTextAsync(output, "result");
        var path = Path.Combine(root, "history.json");
        var history = new ConversionHistory(path);
        history.Add(Entry() with
        {
            InputPaths = ["input-a.mov", "input-b.mov"],
            Outputs = outputs.Select(output => new OutputResult(output, 6)).ToArray(),
            Operation = new(MediaOperation.Split, 1),
            State = JobState.Canceled
        });
        history = new ConversionHistory(path);
        var restored = Assert.Single(history.Entries);
        Assert.Equal(2, restored.AllInputs.Count);
        Assert.Equal(outputs, restored.AllOutputs.Select(output => output.Path));
        Assert.Contains("partial results", restored.OperationLabel);
        history.Remove(restored);
        Assert.Empty(new ConversionHistory(path).Entries);
        Assert.All(outputs, output => Assert.True(File.Exists(output)));
    }

    private static HistoryEntry Entry() =>
        new(DateTimeOffset.UtcNow, "input.mov", "output.mp4", "mp4-h264", "MP4", 20, 10, 1, "Ffmpeg");
}
