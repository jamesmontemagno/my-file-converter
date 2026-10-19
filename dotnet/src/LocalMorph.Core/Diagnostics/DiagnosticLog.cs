using System.Text;

namespace LocalMorph.Core.Diagnostics;

/// <summary>
/// Local, size-capped diagnostic log. Nothing is sent anywhere; users copy or reveal it when reporting a problem.
/// Writes never throw so logging can't turn a recoverable failure into a crash.
/// </summary>
public static class DiagnosticLog
{
    public const string FileName = "localmorph.log";
    public const string PreviousFileName = "localmorph.previous.log";
    private static readonly object Gate = new();
    private static long maxBytes = 1_000_000;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string? Directory { get; private set; }
    public static string? FilePath => Directory is null ? null : Path.Combine(Directory, FileName);

    public static void Configure(string? directory, long maxBytesPerFile = 1_000_000)
    {
        lock (Gate)
        {
            if (directory is not null) System.IO.Directory.CreateDirectory(directory);
            Directory = directory;
            maxBytes = Math.Max(4_096, maxBytesPerFile);
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message}\n{exception}");

    public static void Write(string level, string message)
    {
        var entry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message.TrimEnd()}\n";
        lock (Gate)
        {
            var path = FilePath;
            if (path is null) return;
            try
            {
                var existing = new FileInfo(path);
                if (existing.Exists && existing.Length + entry.Length > maxBytes)
                    File.Move(path, Path.Combine(Directory!, PreviousFileName), overwrite: true);
                File.AppendAllText(path, entry, Utf8NoBom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Previous then current log, oldest first.</summary>
    public static string ReadAll()
    {
        lock (Gate)
        {
            if (Directory is null) return string.Empty;
            var builder = new StringBuilder();
            foreach (var name in new[] { PreviousFileName, FileName })
            {
                var path = Path.Combine(Directory, name);
                try { if (File.Exists(path)) builder.Append(File.ReadAllText(path)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return builder.ToString();
        }
    }
}
