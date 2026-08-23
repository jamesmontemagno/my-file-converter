namespace LocalMorph.Core.Tools;

/// <summary>
/// Snapshot of every conversion tool found on this device plus FFmpeg capabilities.
/// </summary>
public sealed class ToolInventory
{
    public static readonly ToolInventory Empty = new(new Dictionary<ToolKind, ToolInfo>(), FfmpegCapabilities.Empty);

    public ToolInventory(IReadOnlyDictionary<ToolKind, ToolInfo> tools, FfmpegCapabilities ffmpeg)
    {
        Tools = tools;
        Ffmpeg = ffmpeg;
    }

    public IReadOnlyDictionary<ToolKind, ToolInfo> Tools { get; }
    public FfmpegCapabilities Ffmpeg { get; }

    public bool Has(ToolKind kind) => Tools.ContainsKey(kind);
    public ToolInfo? Get(ToolKind kind) => Tools.GetValueOrDefault(kind);
    public string? PathFor(ToolKind kind) => Get(kind)?.Path;
    public bool HasFfmpeg => Has(ToolKind.Ffmpeg);

    public static async Task<ToolInventory> DiscoverAsync(bool verifyHardware = true, string? appBaseDirectory = null, CancellationToken token = default)
    {
        var tools = await Task.Run(() => ToolLocator.FindAll(appBaseDirectory), token);
        var capabilities = tools.TryGetValue(ToolKind.Ffmpeg, out var ffmpeg)
            ? await FfmpegCapabilities.DiscoverAsync(ffmpeg.Path, verifyHardware, token)
            : FfmpegCapabilities.Empty;
        return new ToolInventory(tools, capabilities);
    }
}
