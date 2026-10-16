using System.Text.Json;
using System.Text.Json.Serialization;
using LocalMorph.Core.Formats;

namespace LocalMorph.Core.Jobs;

public sealed record HistoryEntry(
    DateTimeOffset CompletedAt,
    string SourcePath,
    string OutputPath,
    string FormatId,
    string FormatName,
    long SourceBytes,
    long OutputBytes,
    double ElapsedSeconds,
    string Engine,
    IReadOnlyList<string>? InputPaths = null,
    IReadOnlyList<OutputResult>? Outputs = null,
    OperationRequest? Operation = null,
    ConversionOptions? Options = null,
    JobState State = JobState.Completed)
{
    [JsonIgnore] public IReadOnlyList<string> AllInputs => InputPaths ?? [SourcePath];
    [JsonIgnore] public IReadOnlyList<OutputResult> AllOutputs => Outputs ?? [new(OutputPath, OutputBytes)];
    [JsonIgnore] public string OperationLabel => $"{(Operation?.Kind ?? MediaOperation.Convert)} · {AllInputs.Count} input(s) · {AllOutputs.Count} result(s)" + (State == JobState.Completed ? "" : $" · {State}, partial results");
    [JsonIgnore] public string SourceName => Path.GetFileName(SourcePath);
    [JsonIgnore] public string OutputName => Path.GetFileName(OutputPath);
    [JsonIgnore] public bool OutputExists => AllOutputs.Any(output => File.Exists(output.Path));
    [JsonIgnore] public double SizeRatio => SourceBytes > 0 ? (double)OutputBytes / SourceBytes : 1;
    [JsonIgnore] public string SizeChange => SourceBytes <= 0 ? SourceFile.FormatBytes(OutputBytes)
        : SizeRatio < 0.995 ? $"{Math.Min(99, (1 - SizeRatio) * 100):0}% smaller"
        : SizeRatio > 1.005 ? $"{(SizeRatio - 1) * 100:0}% larger"
        : "same size";
}

/// <summary>Remembers recent conversions so people can find what they made.</summary>
public sealed class ConversionHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string filePath;
    private readonly List<HistoryEntry> entries = [];
    private readonly object gate = new();

    public ConversionHistory(string filePath, int capacity = 200)
    {
        this.filePath = filePath;
        Capacity = capacity;
        Load();
    }

    public int Capacity { get; }
    public string? PersistenceError { get; private set; }
    public event Action<string>? PersistenceFailed;

    public IReadOnlyList<HistoryEntry> Entries
    {
        get { lock (gate) return entries.ToArray(); }
    }

    public void Add(HistoryEntry entry)
    {
        lock (gate)
        {
            entries.Insert(0, entry);
            if (entries.Count > Capacity) entries.RemoveRange(Capacity, entries.Count - Capacity);
            Save();
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            Save();
        }
    }

    public void Remove(HistoryEntry entry)
    {
        lock (gate)
        {
            entries.Remove(entry);
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(filePath), JsonOptions);
            if (loaded is not null) entries.AddRange(loaded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            ReportPersistenceFailure("Could not load conversion history", ex);
        }
    }

    /// <summary>Caller must hold <see cref="gate"/>: snapshot + write happen atomically so parallel jobs cannot interleave writes.</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var temp = filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temp, filePath, overwrite: true);
            PersistenceError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            ReportPersistenceFailure("Conversion history could not be saved; your output files are unchanged", ex);
        }
    }

    private void ReportPersistenceFailure(string message, Exception exception)
    {
        PersistenceError = $"{message}: {exception.Message}";
        PersistenceFailed?.Invoke(PersistenceError);
    }
}
