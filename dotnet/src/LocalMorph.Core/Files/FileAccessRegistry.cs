namespace LocalMorph.Core.Files;

public sealed record FileAccessGrant(string Path, string Bookmark, bool IsDirectory, bool CanWrite);

public sealed record OpenFileAccessGrant(FileAccessGrant Grant, IDisposable Resource);

/// <summary>Shares one native scope per bookmark while operations hold independent leases.</summary>
public sealed class FileAccessRegistry(
    IEnumerable<FileAccessGrant> saved,
    Func<FileAccessGrant, OpenFileAccessGrant> open,
    Action<IReadOnlyList<FileAccessGrant>> persist)
{
    private readonly object gate = new();
    private readonly Dictionary<string, FileAccessGrant> grants = saved.Select(NormalizeGrant)
        .ToDictionary(grant => grant.Path, StringComparer.Ordinal);
    private readonly Dictionary<FileAccessGrant, Active> active = [];

    public void Register(IEnumerable<FileAccessGrant> records)
    {
        lock (gate)
        {
            var updated = new Dictionary<string, FileAccessGrant>(grants, StringComparer.Ordinal);
            foreach (var record in records)
            {
                var replacement = NormalizeGrant(record);
                var path = replacement.Path;
                if (replacement.IsDirectory)
                {
                    foreach (var covered in updated.Values.Where(grant => grant.Path != path && Covers(replacement, grant.Path)
                        && (replacement.CanWrite || !grant.CanWrite)).ToArray())
                        updated.Remove(covered.Path);
                }
                updated[path] = replacement;
            }
            persist(updated.Values.ToArray());
            grants.Clear();
            foreach (var pair in updated) grants.Add(pair.Key, pair.Value);
        }
    }

    public IDisposable Acquire(string path, bool writeDirectory = false)
    {
        path = Normalize(path);
        lock (gate)
        {
            FileAccessGrant? grant = null;
            for (var parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
            {
                if (grants.TryGetValue(parent, out var candidate) && (parent == path || candidate.IsDirectory)
                    && (!writeDirectory || candidate.IsDirectory && candidate.CanWrite))
                {
                    grant = candidate;
                    break;
                }
            }
            if (grant is null) throw new UnauthorizedAccessException($"Access to {path} must be authorized again with the native picker.");
            var key = grant;
            if (!active.TryGetValue(key, out var current))
            {
                var opened = open(grant);
                try
                {
                    if (!string.Equals(Normalize(opened.Grant.Path), grant.Path, StringComparison.Ordinal)
                        || opened.Grant.IsDirectory != grant.IsDirectory || opened.Grant.CanWrite != grant.CanWrite)
                        throw new UnauthorizedAccessException("The bookmarked item moved. Add it again from its current location.");
                    if (opened.Grant != grant)
                    {
                        var updated = grants.Values.Select(item => item == grant ? opened.Grant : item).ToArray();
                        persist(updated);
                        grants[grant.Path] = opened.Grant;
                    }
                    key = opened.Grant;
                    current = new Active(opened.Resource);
                    active.Add(key, current);
                }
                catch
                {
                    opened.Resource.Dispose();
                    throw;
                }
            }
            current.References++;
            return new Lease(() => Release(key, current));
        }
    }

    public static bool Covers(FileAccessGrant grant, string path)
    {
        var root = Normalize(grant.Path);
        path = Normalize(path);
        if (string.Equals(root, path, StringComparison.Ordinal)) return true;
        return grant.IsDirectory && path.StartsWith(
            Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("File grants require absolute paths.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static FileAccessGrant NormalizeGrant(FileAccessGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (string.IsNullOrWhiteSpace(grant.Bookmark)) throw new ArgumentException("A file grant needs bookmark data.");
        return grant with { Path = Normalize(grant.Path) };
    }

    private void Release(FileAccessGrant key, Active scope)
    {
        lock (gate)
        {
            if (--scope.References != 0) return;
            active.Remove(key);
            scope.Resource.Dispose();
        }
    }

    private sealed class Active(IDisposable resource)
    {
        public IDisposable Resource { get; } = resource;
        public int References { get; set; }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? releaseOnce = release;
        public void Dispose() => Interlocked.Exchange(ref releaseOnce, null)?.Invoke();
    }
}
