using System.Text.Json;
using Foundation;
using LocalMorph.Core.Files;
using UIKit;
using UniformTypeIdentifiers;

namespace LocalMorph.App.Platforms.MacCatalyst;

public static class MacFileAccess
{
    private static Lazy<FileAccessRegistry> registry = new(CreateRegistry);
    private static readonly Lazy<string[]> privateRoots = new(() =>
        new[] { FileSystem.Current.AppDataDirectory, FileSystem.Current.CacheDirectory, NSBundle.MainBundle.BundlePath }
            .Select(CanonicalPath).ToArray());
    private static string StorePath => Path.Combine(FileSystem.Current.AppDataDirectory, "file-access.json");

    public static IDisposable Acquire(string path, bool writeDirectory)
    {
        path = CanonicalPath(path);
        if (IsPrivatePath(path))
        {
            if (writeDirectory && FileAccessRegistry.Covers(new(CanonicalPath(NSBundle.MainBundle.BundlePath), "", true, false), path))
                throw new UnauthorizedAccessException("Choose an output folder outside the application bundle.");
            return new NativeScope(null);
        }
        return registry.Value.Acquire(path, writeDirectory);
    }

    public static IReadOnlyList<string> RegisterUrls(IEnumerable<NSUrl> urls)
    {
        var grants = new List<FileAccessGrant>();
        var paths = new List<string>();
        foreach (var url in urls.Where(url => url.IsFileUrl && !string.IsNullOrWhiteSpace(url.Path)))
        {
            var started = url.StartAccessingSecurityScopedResource();
            try
            {
                var path = CanonicalPath(url.Path!);
                paths.Add(path);
                if (IsPrivatePath(path)) continue;
                var directory = url.HasDirectoryPath || Directory.Exists(path);
                grants.Add(CreateGrant(url, path, directory, directory));
            }
            finally
            {
                if (started) url.StopAccessingSecurityScopedResource();
            }
        }
        if (grants.Count > 0) registry.Value.Register(grants);
        return paths;
    }

    public static async Task<IReadOnlyList<string>> PickAsync(bool folder, bool multiple, bool allowFolders = false)
    {
        var completion = new TaskCompletionSource<NSUrl[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var picker = new UIDocumentPickerViewController(
            folder ? [UTTypes.Folder] : allowFolders ? [UTTypes.Item, UTTypes.Folder] : [UTTypes.Item], asCopy: false)
        {
            AllowsMultipleSelection = multiple
        };
        picker.DidPickDocumentAtUrls += (_, args) => completion.TrySetResult(args.Urls);
        picker.WasCancelled += (_, _) => completion.TrySetResult([]);
        var presenter = Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController()
            ?? throw new InvalidOperationException("The file picker needs an active application window.");
        await presenter.PresentViewControllerAsync(picker, true);
        return RegisterUrls(await completion.Task);
    }

    public static void Forget()
    {
        File.Delete(StorePath);
        registry = new(CreateRegistry);
    }

    private static bool IsPrivatePath(string path) =>
        privateRoots.Value.Any(root => FileAccessRegistry.Covers(new(root, "", true, true), path));

    internal static string CanonicalPath(string path)
    {
        using var value = new NSString(Path.GetFullPath(path));
        using var resolved = value.ResolveSymlinksInPath();
        return resolved.ToString();
    }

    private static FileAccessRegistry CreateRegistry()
    {
        try
        {
            var records = File.Exists(StorePath)
                ? JsonSerializer.Deserialize<FileAccessGrant[]>(File.ReadAllText(StorePath))
                    ?? throw new JsonException("The file-access store is empty.")
                : [];
            return new(records, Open, Persist);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new IOException("Saved file permissions are damaged. Use Forget file permissions in Tools, then select your files and output folder again.", ex);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416",
        Justification = "Apple NSURL.h explicitly supports WithSecurityScope on Mac Catalyst 13.0+. The .NET enum's macOS-only annotation is incomplete.")]
    internal static OpenFileAccessGrant Open(FileAccessGrant grant)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(grant.Bookmark); }
        catch (FormatException ex) { throw new UnauthorizedAccessException("This saved permission is invalid. Select the item again.", ex); }
        using var data = NSData.FromArray(bytes);
        var url = NSUrl.FromBookmarkData(data,
            NSUrlBookmarkResolutionOptions.WithSecurityScope | NSUrlBookmarkResolutionOptions.WithoutUI
                | NSUrlBookmarkResolutionOptions.WithoutMounting,
            null, out var stale, out var error);
        using (error)
        {
            if (url is null) throw new UnauthorizedAccessException($"Saved access could not be restored: {error?.LocalizedDescription ?? "item unavailable"}. Select the item again.");
        }
        var started = url.StartAccessingSecurityScopedResource();
        if (!started && !IsPrivatePath(grant.Path))
        {
            url.Dispose();
            throw new UnauthorizedAccessException("Saved access was revoked or is unavailable. Select the item again.");
        }
        var resource = new NativeScope(url, started);
        try
        {
            if (url.Path is null || !string.Equals(CanonicalPath(url.Path), grant.Path, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The bookmarked item moved. Add it again from its current location.");
            var refreshed = stale ? CreateGrant(url, grant.Path, grant.IsDirectory, grant.CanWrite) : grant;
            return new(refreshed, resource);
        }
        catch
        {
            resource.Dispose();
            throw;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416",
        Justification = "Apple NSURL.h explicitly supports these security-scope flags on Mac Catalyst 13.0+. The .NET enum's macOS-only annotation is incomplete.")]
    internal static FileAccessGrant CreateGrant(NSUrl url, string path, bool directory, bool writable)
    {
        var options = NSUrlBookmarkCreationOptions.WithSecurityScope;
        if (!writable) options |= NSUrlBookmarkCreationOptions.SecurityScopeAllowOnlyReadAccess;
        using var data = url.CreateBookmarkData(options, null, null, out var error);
        using (error)
        {
            if (data is null) throw new UnauthorizedAccessException($"Could not remember access: {error?.LocalizedDescription ?? "bookmark unavailable"}.");
        }
        return new(path, Convert.ToBase64String(data.ToArray()), directory, writable);
    }

    private static void Persist(IReadOnlyList<FileAccessGrant> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var temporary = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(records));
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, StorePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class NativeScope(NSUrl? url, bool started = true) : IDisposable
    {
        private NSUrl? owned = url;
        public void Dispose()
        {
            var release = Interlocked.Exchange(ref owned, null);
            if (release is null) return;
            if (started) release.StopAccessingSecurityScopedResource();
            release.Dispose();
        }
    }
}
