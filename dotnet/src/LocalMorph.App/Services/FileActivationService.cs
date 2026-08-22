using System.Collections.Concurrent;
using LocalMorph.Core.Formats;

namespace LocalMorph.App.Services;

/// <summary>
/// Funnels files that arrive from the OS ("Open with", Finder/Explorer double-click, a second
/// instance's command line) into the running app. Paths are buffered until the UI subscribes so
/// nothing is lost during startup.
/// </summary>
public static class FileActivationService
{
    private static readonly ConcurrentQueue<string> Pending = new();
    private static readonly object Gate = new();
    private static Func<IReadOnlyList<string>, Task>? handler;

    public static event Action? Activated;

    public static void Open(IEnumerable<string> paths)
    {
        var list = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(path => Directory.Exists(path) || File.Exists(path) && SourceClassifier.Classify(path) != MediaCategory.Unknown)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Func<IReadOnlyList<string>, Task>? current;
        lock (Gate)
        {
            current = handler;
            if (current is null)
            {
                foreach (var path in list) Pending.Enqueue(path);
                return;
            }
        }

        Activated?.Invoke();
        if (list.Count > 0) Dispatch(current, list);
    }

    public static void Subscribe(Func<IReadOnlyList<string>, Task> onFiles)
    {
        List<string> buffered;
        lock (Gate)
        {
            handler = onFiles;
            buffered = [];
            while (Pending.TryDequeue(out var path)) buffered.Add(path);
        }

        if (buffered.Count > 0) Dispatch(onFiles, buffered);
    }

    private static void Dispatch(Func<IReadOnlyList<string>, Task> target, IReadOnlyList<string> paths) =>
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try { await target(paths); }
            catch { }
        });

    /// <summary>Extensions the desktop app registers as a handler for, grouped as the OS manifests want them.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Associations = new Dictionary<string, string[]>
    {
        ["video"] = [".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp", ".ogv", ".vob", ".mxf"],
        ["audio"] = [".mp3", ".wav", ".aac", ".m4a", ".flac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif", ".amr", ".ac3", ".mka", ".caf"],
        ["image"] = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".jxl", ".svg", ".psd", ".ico", ".tga", ".dds", ".exr"],
        ["document"] = [".pdf", ".docx", ".doc", ".odt", ".rtf", ".md", ".markdown", ".html", ".htm", ".epub", ".xlsx", ".xls", ".ods", ".csv", ".pptx", ".ppt", ".odp"]
    };
}
