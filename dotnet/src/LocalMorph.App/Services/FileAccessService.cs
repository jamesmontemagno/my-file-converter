using LocalMorph.Core.Jobs;

namespace LocalMorph.App.Services;

public static class FileAccessService
{
    public static IDisposable Acquire(string path, bool writeDirectory = false)
    {
#if MACCATALYST
        if (Distribution.IsMacStoreBuild)
            return Platforms.MacCatalyst.MacFileAccess.Acquire(path, writeDirectory);
#endif
        return new AccessSet([]);
    }

    public static IDisposable AcquireJob(ConversionJob job)
    {
        var leases = new List<IDisposable>();
        try
        {
            foreach (var path in job.Inputs.Select(input => input.Path)
                .Concat(new[] { job.Options.ReplacementAudioPath, job.Options.ExternalSubtitlePath }.OfType<string>())
                .Distinct(StringComparer.Ordinal))
                leases.Add(Acquire(path));
            leases.Add(Acquire(Path.GetDirectoryName(job.OutputPath)!, writeDirectory: true));
            return new AccessSet(leases);
        }
        catch
        {
            foreach (var lease in leases) lease.Dispose();
            throw;
        }
    }

    public static async Task<IDisposable?> RequestAsync(IEnumerable<string> paths)
    {
        var leases = new List<IDisposable>();
        try
        {
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                try { leases.Add(Acquire(path)); }
                catch (UnauthorizedAccessException) when (Distribution.IsMacStoreBuild)
                {
                    if (!await Shell.Current.DisplayAlertAsync("Authorize file access",
                        $"Choose this item, or a containing folder, to use it again:\n{path}", "Choose", "Cancel"))
                    {
                        foreach (var lease in leases) lease.Dispose();
                        return null;
                    }
#if MACCATALYST
                    if ((await Platforms.MacCatalyst.MacFileAccess.PickAsync(folder: false, multiple: false, allowFolders: true)).Count == 0)
                    {
                        foreach (var lease in leases) lease.Dispose();
                        return null;
                    }
#endif
                    leases.Add(Acquire(path));
                }
            }
            return new AccessSet(leases);
        }
        catch
        {
            foreach (var lease in leases) lease.Dispose();
            throw;
        }
    }

    private sealed class AccessSet(IReadOnlyList<IDisposable> leases) : IDisposable
    {
        private IReadOnlyList<IDisposable>? owned = leases;
        public void Dispose()
        {
            var release = Interlocked.Exchange(ref owned, null);
            if (release is null) return;
            foreach (var lease in release.Reverse()) lease.Dispose();
        }
    }
}
