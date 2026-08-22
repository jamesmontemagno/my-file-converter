using System.Text.Json;
using System.Text.Json.Serialization;

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
    string Engine)
{
    [JsonIgnore] public string SourceName => Path.GetFileName(SourcePath);
    [JsonIgnore] public string OutputName => Path.GetFileName(OutputPath);
    [JsonIgnore] public bool OutputExists => File.Exists(OutputPath);
    [JsonIgnore] public double SizeRatio => SourceBytes > 0 ? (double)OutputBytes / SourceBytes : 1;
    [JsonIgnore] public string SizeChange => SourceBytes <= 0 ? SourceFile.FormatBytes(OutputBytes)
        : SizeRatio < 0.995 ? $"{(1 - SizeRatio) * 100:0}% smaller"
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
        }
        Save();
    }

    public void Clear()
    {
        lock (gate) entries.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(filePath), JsonOptions);
            if (loaded is not null) entries.AddRange(loaded);
        }
        catch
        {
            // A corrupt history file should never block the app.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            HistoryEntry[] snapshot;
            lock (gate) snapshot = entries.ToArray();
            File.WriteAllText(filePath, JsonSerializer.Serialize(snapshot, JsonOptions));
        }
        catch
        {
        }
    }
}
