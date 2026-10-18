using LocalMorph.Core.Files;
using Xunit;

namespace LocalMorph.Core.Tests;

public sealed class FileAccessRegistryTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LocalMorph grants"));
    private static FileAccessGrant Folder(string? path = null, bool writable = true) => new(path ?? Root, "bookmark", true, writable);
    private sealed class Resource(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Fact]
    public void Invalid_saved_records_are_not_loaded_as_usable_permissions()
    {
        Assert.Throws<ArgumentException>(() => new FileAccessRegistry(
            [Folder() with { Bookmark = "" }], _ => throw new InvalidOperationException(), _ => { }));
        Assert.Throws<ArgumentException>(() => new FileAccessRegistry(
            [Folder("relative")], _ => throw new InvalidOperationException(), _ => { }));
    }

    [Fact]
    public void Saved_paths_are_normalized_before_resolution_and_matching()
    {
        var registry = new FileAccessRegistry([Folder(Path.Combine(Root, "."))], grant =>
        {
            Assert.Equal(Root, grant.Path);
            return new(grant, new Resource(() => { }));
        }, _ => { });
        using var scope = registry.Acquire(Path.Combine(Root, "file"));
    }

    [Fact]
    public void Independent_consumers_share_scope_until_last_release()
    {
        var opened = 0;
        var closed = 0;
        var registry = new FileAccessRegistry([Folder()], grant =>
        {
            opened++;
            return new(grant, new Resource(() => closed++));
        }, _ => { });
        var preview = registry.Acquire(Path.Combine(Root, "source.mp4"));
        var job = registry.Acquire(Path.Combine(Root, "out"), writeDirectory: true);
        preview.Dispose();
        preview.Dispose();
        Assert.Equal(0, closed);
        Assert.Equal(1, opened);
        job.Dispose();
        Assert.Equal(1, closed);
        using var next = registry.Acquire(Root);
        Assert.Equal(2, opened);
    }

    [Fact]
    public void Matching_is_case_sensitive_and_respects_directory_boundaries()
    {
        Assert.True(FileAccessRegistry.Covers(Folder(), Path.Combine(Root, "clips", "file.mp4")));
        Assert.False(FileAccessRegistry.Covers(Folder(), Root + "-other/file.mp4"));
        Assert.False(FileAccessRegistry.Covers(Folder(), Root.ToUpperInvariant() + "/file.mp4"));
        Assert.False(FileAccessRegistry.Covers(Folder(), Path.Combine(Root, "..", "outside.mp4")));
        Assert.True(FileAccessRegistry.Covers(Folder(Path.GetPathRoot(Root)), Root));
    }

    [Fact]
    public void Selecting_a_file_never_grants_sibling_output_access()
    {
        var path = Path.Combine(Root, "source.mp4");
        var registry = new FileAccessRegistry([new(path, "file", false, false)],
            grant => new(grant, new Resource(() => { })), _ => { });
        using var input = registry.Acquire(path);
        Assert.Throws<UnauthorizedAccessException>(() => registry.Acquire(Root, writeDirectory: true));
        Assert.Throws<UnauthorizedAccessException>(() => registry.Acquire(path, writeDirectory: true));
        Assert.Throws<UnauthorizedAccessException>(() => registry.Acquire(Path.Combine(Root, "sibling.mp4")));
    }

    [Fact]
    public void Read_only_directory_does_not_authorize_writing()
    {
        var registry = new FileAccessRegistry([Folder(writable: false)],
            grant => new(grant, new Resource(() => { })), _ => { });
        using var read = registry.Acquire(Path.Combine(Root, "input"));
        Assert.Throws<UnauthorizedAccessException>(() => registry.Acquire(Root, writeDirectory: true));
    }

    [Fact]
    public void Reauthorization_keeps_old_read_scope_separate_from_new_write_scope()
    {
        var closed = 0;
        var opened = 0;
        var registry = new FileAccessRegistry([Folder(writable: false)], grant =>
        {
            opened++;
            return new(grant, new Resource(() => closed++));
        }, _ => { });
        var old = registry.Acquire(Root);
        registry.Register([Folder() with { Bookmark = "new" }]);
        using var writable = registry.Acquire(Root, writeDirectory: true);
        Assert.Equal(2, opened);
        old.Dispose();
        Assert.Equal(1, closed);
        using var second = registry.Acquire(Root, writeDirectory: true);
        Assert.Equal(2, opened);
    }

    [Fact]
    public void Choosing_a_folder_reauthorizes_its_files_instead_of_reusing_revoked_narrow_grants()
    {
        var source = Path.Combine(Root, "source.mp4");
        IReadOnlyList<FileAccessGrant> saved = [];
        var registry = new FileAccessRegistry([new(source, "revoked", false, false)], grant =>
        {
            if (grant.Bookmark == "revoked") throw new UnauthorizedAccessException("Revoked");
            return new(grant, new Resource(() => { }));
        }, records => saved = records);
        Assert.Throws<UnauthorizedAccessException>(() => registry.Acquire(source));
        registry.Register([Folder() with { Bookmark = "new folder" }]);
        using var access = registry.Acquire(source);
        Assert.Equal(Root, Assert.Single(saved).Path);
    }

    [Fact]
    public void Batch_registration_persists_once_and_survives_registry_recreation()
    {
        IReadOnlyList<FileAccessGrant> saved = [];
        var writes = 0;
        var registry = new FileAccessRegistry([], grant => new(grant, new Resource(() => { })), records =>
        {
            writes++;
            saved = records;
        });
        registry.Register([Folder(), new(Path.Combine(Root, "..", "another.mp4"), "other", false, false)]);
        Assert.Equal(1, writes);
        Assert.Equal(2, saved.Count);
        var restored = new FileAccessRegistry(saved, grant => new(grant, new Resource(() => { })), _ => { });
        using var access = restored.Acquire(Path.Combine(Root, "output"), writeDirectory: true);
    }

    [Fact]
    public void Failed_persistence_never_publishes_a_new_grant()
    {
        var registry = new FileAccessRegistry([], _ => throw new InvalidOperationException("Must not open"),
            _ => throw new IOException("Disk full"));
        Assert.Throws<IOException>(() => registry.Register([Folder()]));
        Assert.Throws<UnauthorizedAccessException>(() => registry.Acquire(Root));
    }

    [Fact]
    public void Stale_bookmarks_are_refreshed_once_and_shared()
    {
        IReadOnlyList<FileAccessGrant>? saved = null;
        var opened = 0;
        var registry = new FileAccessRegistry([Folder()], grant =>
        {
            opened++;
            return new(grant with { Bookmark = "refreshed" }, new Resource(() => { }));
        }, records => saved = records);
        using var first = registry.Acquire(Root);
        using var second = registry.Acquire(Path.Combine(Root, "file"));
        Assert.Equal("refreshed", Assert.Single(saved!).Bookmark);
        Assert.Equal(1, opened);
    }

    [Fact]
    public void Failed_stale_refresh_releases_native_scope()
    {
        var closed = 0;
        var registry = new FileAccessRegistry([Folder()],
            grant => new(grant with { Bookmark = "refreshed" }, new Resource(() => closed++)),
            _ => throw new IOException("Cannot save"));
        Assert.Throws<IOException>(() => registry.Acquire(Root));
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Moved_or_revoked_grants_fail_explicitly_without_leaking()
    {
        var closed = 0;
        var moved = new FileAccessRegistry([Folder()],
            grant => new(grant with { Path = Root + "-moved" }, new Resource(() => closed++)), _ => { });
        Assert.Throws<UnauthorizedAccessException>(() => moved.Acquire(Root));
        Assert.Equal(1, closed);
        var revoked = new FileAccessRegistry([Folder()], _ => throw new UnauthorizedAccessException("Revoked"), _ => { });
        Assert.Throws<UnauthorizedAccessException>(() => revoked.Acquire(Root));
    }
}
