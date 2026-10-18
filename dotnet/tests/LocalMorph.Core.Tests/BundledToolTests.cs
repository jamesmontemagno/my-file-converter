using LocalMorph.Core.Tools;
using Xunit;

namespace LocalMorph.Core.Tests;

public sealed class BundledToolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LocalMorph-bundle-" + Guid.NewGuid().ToString("N"));

    public BundledToolTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Empty_bundle_never_discovers_host_tools()
    {
        Directory.CreateDirectory(Path.Combine(root, "tools"));
        File.WriteAllText(Path.Combine(root, "tools", "ffmpeg"), "not an approved location");
        var inventory = await ToolInventory.DiscoverBundledMediaAsync(root);
        Assert.Empty(inventory.Tools);
        Assert.False(inventory.HasMediaTools);
    }

    [Fact]
    public void Broken_bundled_tools_fail_instead_of_falling_back()
    {
        File.WriteAllText(Path.Combine(root, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"), "corrupt executable");
        Assert.Throws<IOException>(() => ToolLocator.FindBundledMedia(root));
    }

    [Fact]
    public void Relative_bundle_directories_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => ToolLocator.FindBundledMedia("."));
    }

    [Fact]
    public void Symlinked_bundle_directories_are_rejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var link = Path.Combine(root, "linked-tools");
        var target = Path.Combine(root, "external-tools");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);
        Assert.Throws<IOException>(() => ToolLocator.FindBundledMedia(link));
    }

    [Fact]
    public void Symlinked_tools_are_rejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Path.Combine(root, "external");
        File.WriteAllText(target, "#!/bin/sh\nprintf 'ffmpeg version test\\n'\n");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.CreateSymbolicLink(Path.Combine(root, "ffmpeg"), target);
        Assert.Throws<IOException>(() => ToolLocator.FindBundledMedia(root));
    }

    [Fact]
    public void Only_named_successful_media_executables_are_accepted()
    {
        if (OperatingSystem.IsWindows()) return;
        WriteTool("ffmpeg", "ffmpeg version test", 0);
        WriteTool("ffprobe", "ffprobe version test", 0);
        WriteTool("pandoc", "pandoc 1.0", 0);
        var found = ToolLocator.FindBundledMedia(root);
        Assert.Equal(2, found.Count);
        Assert.All(found.Values, tool => Assert.Equal(ToolSource.Bundled, tool.Source));
        Assert.False(found.ContainsKey(ToolKind.Pandoc));
        WriteTool("ffprobe", "ffprobe version test", 1);
        Assert.Throws<IOException>(() => ToolLocator.FindBundledMedia(root));
        WriteTool("ffprobe", "wrong program", 0);
        Assert.Throws<IOException>(() => ToolLocator.FindBundledMedia(root));
    }

    private void WriteTool(string name, string version, int exit)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The fixture is a Unix executable.");
        var path = Path.Combine(root, name);
        File.WriteAllText(path, $"#!/bin/sh\nprintf '{version}\\n'\nexit {exit}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
