using LocalMorph.Core.Formats;

namespace LocalMorph.Core.Jobs;

public sealed record FileImportResult(
    IReadOnlyList<string> Paths, IReadOnlyList<string> AlreadyQueued,
    int Unsupported, int ExcludedFolders, bool LimitReached, IReadOnlyList<string> Errors)
{
    public string Summary => $"{Paths.Count:N0} added · {AlreadyQueued.Count:N0} already queued · {Unsupported:N0} unsupported · {ExcludedFolders:N0} folders excluded"
        + (LimitReached ? " · 2,000-file import limit reached; remaining files were not scanned" : "")
        + (Errors.Count > 0 ? $" · {string.Join("; ", Errors)}" : "");
}

public static class FileImport
{
    public const int MaxFiles = 2000;
    public const int MaxDepth = 8;

    public static Task<FileImportResult> ScanAsync(IEnumerable<string> paths, IEnumerable<string> existing, CancellationToken token = default)
    {
        var roots = paths.ToArray();
        var queued = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.Run(() => Scan(roots, queued, token), token);
    }

    private static FileImportResult Scan(IEnumerable<string> roots, ISet<string> existing, CancellationToken token)
    {
        var accepted = new List<string>();
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var unsupported = 0;
        var excluded = 0;
        var limit = false;

        void Add(string path)
        {
            token.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path);
            if (!seen.Add(path)) return;
            if (existing.Contains(path)) { duplicates.Add(path); return; }
            if (SourceClassifier.Classify(path) == MediaCategory.Unknown) { unsupported++; return; }
            if (accepted.Count == MaxFiles) { limit = true; return; }
            accepted.Add(path);
        }

        void Report(string path, Exception ex)
        {
            if (errors.Count < 5) errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
        }

        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (limit) break;
            if (Directory.Exists(root))
            {
                var pending = new Stack<(string Path, int Depth)>();
                var subtitles = new List<string>();
                var hasPrimaryContent = false;
                pending.Push((root, 0));
                while (pending.TryPop(out var directory) && !limit)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var path in Directory.EnumerateFiles(directory.Path))
                        {
                            token.ThrowIfCancellationRequested();
                            if (SourceClassifier.Classify(path) == MediaCategory.Subtitle)
                            {
                                if (subtitles.Count <= MaxFiles) subtitles.Add(path);
                            }
                            else
                            {
                                if (SourceClassifier.Classify(path) != MediaCategory.Unknown) hasPrimaryContent = true;
                                Add(path);
                            }
                            if (limit) break;
                        }
                        if (limit) break;
                        foreach (var child in Directory.EnumerateDirectories(directory.Path))
                        {
                            token.ThrowIfCancellationRequested();
                            if (directory.Depth >= MaxDepth || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) { excluded++; continue; }
                            pending.Push((child, directory.Depth + 1));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        excluded++;
                        Report(directory.Path, ex);
                    }
                }
                if (!hasPrimaryContent)
                    foreach (var subtitle in subtitles) { Add(subtitle); if (limit) break; }
            }
            else if (File.Exists(root)) Add(root);
            else Report(root, new FileNotFoundException("File or folder no longer exists."));
        }

        return new FileImportResult(accepted, duplicates.ToArray(), unsupported, excluded, limit, errors);
    }
}
