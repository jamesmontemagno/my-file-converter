using LocalMorph.Core.Jobs;
using Xunit;

namespace LocalMorph.Core.Tests;

public sealed class FileImportTests : IDisposable
{
    private readonly string root = Fixture.Root;

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public async Task Duplicates_do_not_consume_the_import_limit()
    {
        var paths = Enumerable.Range(0, FileImport.MaxFiles + 2).Select(index => Path.Combine(root, $"{index}.mp4")).ToArray();
        foreach (var path in paths) await File.WriteAllTextAsync(path, "");
        var result = await FileImport.ScanAsync(paths, [paths[0]]);
        Assert.Equal(FileImport.MaxFiles, result.Paths.Count);
        Assert.Equal(paths[0], Assert.Single(result.AlreadyQueued));
        Assert.True(result.LimitReached);
        Assert.DoesNotContain(paths[0], result.Paths);
    }

    [Fact]
    public async Task Folder_subtitles_are_sidecars_but_subtitle_only_folders_are_importable()
    {
        var subtitle = Path.Combine(root, "captions.srt");
        var video = Path.Combine(root, "video.mp4");
        await File.WriteAllTextAsync(subtitle, "");
        Assert.Equal(subtitle, Assert.Single((await FileImport.ScanAsync([root], [])).Paths));
        await File.WriteAllTextAsync(video, "");
        Assert.Equal(video, Assert.Single((await FileImport.ScanAsync([root], [])).Paths));
        var duplicate = await FileImport.ScanAsync([root], [video]);
        Assert.Empty(duplicate.Paths);
        Assert.Equal(video, Assert.Single(duplicate.AlreadyQueued));
    }

    [Fact]
    public async Task Missing_and_unsupported_inputs_are_reported()
    {
        var unknown = Path.Combine(root, "unknown.xyz");
        await File.WriteAllTextAsync(unknown, "");
        var result = await FileImport.ScanAsync([unknown, Path.Combine(root, "missing.mp4")], []);
        Assert.Empty(result.Paths);
        Assert.Equal(1, result.Unsupported);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task Cancellation_is_not_reported_as_an_empty_success()
    {
        using var token = new CancellationTokenSource();
        token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileImport.ScanAsync([root], [], token.Token));
    }

    [Fact]
    public async Task Exactly_two_thousand_files_do_not_report_overflow()
    {
        var paths = Enumerable.Range(0, FileImport.MaxFiles).Select(index => Path.Combine(root, $"{index}.wav")).ToArray();
        foreach (var path in paths) await File.WriteAllTextAsync(path, "");
        var result = await FileImport.ScanAsync(paths, []);
        Assert.Equal(FileImport.MaxFiles, result.Paths.Count);
        Assert.False(result.LimitReached);
    }

    [Fact]
    public async Task Recursion_includes_depth_eight_but_excludes_depth_nine()
    {
        var directory = root;
        string? lastIncluded = null;
        for (var depth = 0; depth <= FileImport.MaxDepth + 1; depth++)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "video.mp4");
            await File.WriteAllTextAsync(path, "");
            if (depth == FileImport.MaxDepth) lastIncluded = path;
            directory = Path.Combine(directory, "child");
        }
        var result = await FileImport.ScanAsync([root], []);
        Assert.Equal(FileImport.MaxDepth + 1, result.Paths.Count);
        Assert.Contains(lastIncluded!, result.Paths);
        Assert.Equal(1, result.ExcludedFolders);
    }
}
