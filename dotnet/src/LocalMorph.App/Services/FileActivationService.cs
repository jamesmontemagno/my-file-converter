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
    private static readonly ConcurrentQueue<string> PendingErrors = new();
    private static readonly object Gate = new();
    private static Func<IReadOnlyList<string>, Task>? handler;

    public static event Action? Activated;
    public static event Action<string>? Failed;

    public static void Open(IEnumerable<string> paths)
    {
        var list = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
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
        if (list.Count > 0) Dispatch(list);
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

        if (buffered.Count > 0) Dispatch(buffered);
        while (PendingErrors.TryDequeue(out var message)) ReportFailure(message);
    }

    public static void Suspend()
    {
        lock (Gate) handler = null;
    }

    public static void ReportFailure(string message)
    {
        lock (Gate)
        {
            if (handler is null) { PendingErrors.Enqueue(message); return; }
        }
        MainThread.BeginInvokeOnMainThread(() => Failed?.Invoke(message));
    }

    private static void Dispatch(IReadOnlyList<string> paths) =>
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            Func<IReadOnlyList<string>, Task>? target;
            lock (Gate)
            {
                target = handler;
                if (target is null)
                {
                    foreach (var path in paths) Pending.Enqueue(path);
                    return;
                }
            }
            try { await target(paths); }
            catch (Exception ex) { Failed?.Invoke($"Could not open activated files: {ex.Message}"); }
        });

    /// <summary>Extensions the desktop app registers as a handler for, grouped as the OS manifests want them.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Associations = new Dictionary<string, string[]>
    {
        ["video"] = [".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp", ".ogv", ".vob", ".mxf"],
        ["audio"] = [".mp3", ".wav", ".aac", ".m4a", ".flac", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif", ".amr", ".ac3", ".mka", ".caf"],
        ["image"] = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".jxl", ".svg", ".psd", ".ico", ".tga", ".dds", ".exr"],
        ["document"] = [".pdf", ".docx", ".doc", ".odt", ".rtf", ".md", ".markdown", ".html", ".htm", ".epub", ".xlsx", ".xls", ".ods", ".csv", ".pptx", ".ppt", ".odp"],
        ["subtitle"] = [".srt", ".vtt", ".ass", ".ssa"]
    };
}
