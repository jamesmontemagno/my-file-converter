using System.Security.Cryptography;

namespace LocalMorph.Bridge;

public sealed class BridgeOptions
{
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxStderrBytes = 16 * 1024;
    public static readonly IReadOnlySet<string> AllowedOrigins = new HashSet<string>(StringComparer.Ordinal)
    {
        "https://localmorph.com",
        "http://localhost:5173",
        "http://127.0.0.1:5173",
        "http://localhost:4173",
        "http://127.0.0.1:4173"
    };

    public int Port { get; init; }
    public required string Token { get; init; }
    public string JobRoot { get; init; } = CreateJobRoot();
    public TimeSpan JobTtl { get; init; } = TimeSpan.FromHours(1);

    public static BridgeOptions FromEnvironment()
    {
        var portValue = Environment.GetEnvironmentVariable("LOCALMORPH_BRIDGE_PORT");
        if (portValue is not null && (!ushort.TryParse(portValue, out var port)))
        {
            throw new InvalidOperationException("LOCALMORPH_BRIDGE_PORT must be an unsigned 16-bit port");
        }

        return new BridgeOptions
        {
            Port = portValue is null ? 0 : ushort.Parse(portValue),
            Token = Base64UrlEncode(RandomNumberGenerator.GetBytes(32))
        };
    }

    public static string CreateJobRoot()
    {
        var dataRoot = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(dataRoot, "LocalMorphBridge", "jobs", Guid.NewGuid().ToString("N"));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
