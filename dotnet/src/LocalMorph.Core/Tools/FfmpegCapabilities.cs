using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LocalMorph.Core.Tools;

public enum HardwareVendor
{
    None,
    Nvidia,
    Intel,
    Amd,
    Apple
}

public sealed record HardwareEncoder(string Codec, string Encoder, HardwareVendor Vendor, string DisplayName);

/// <summary>
/// What the discovered FFmpeg build can actually do: which encoders are compiled in,
/// and which hardware encoders work on this machine (verified by a tiny test encode).
/// </summary>
public sealed partial class FfmpegCapabilities
{
    private static readonly Regex EncoderLine = EncoderLineRegex();

    public static readonly FfmpegCapabilities Empty = new(new HashSet<string>(), new HashSet<string>(), [], null);

    private static readonly (string Codec, string Encoder, HardwareVendor Vendor, string Name)[] HardwareCandidates =
    [
        ("h264", "h264_nvenc", HardwareVendor.Nvidia, "NVIDIA NVENC"),
        ("h264", "h264_qsv", HardwareVendor.Intel, "Intel Quick Sync"),
        ("h264", "h264_amf", HardwareVendor.Amd, "AMD AMF"),
        ("h264", "h264_videotoolbox", HardwareVendor.Apple, "Apple VideoToolbox"),
        ("hevc", "hevc_nvenc", HardwareVendor.Nvidia, "NVIDIA NVENC"),
        ("hevc", "hevc_qsv", HardwareVendor.Intel, "Intel Quick Sync"),
        ("hevc", "hevc_amf", HardwareVendor.Amd, "AMD AMF"),
        ("hevc", "hevc_videotoolbox", HardwareVendor.Apple, "Apple VideoToolbox"),
        ("av1", "av1_nvenc", HardwareVendor.Nvidia, "NVIDIA NVENC"),
        ("av1", "av1_qsv", HardwareVendor.Intel, "Intel Quick Sync"),
        ("av1", "av1_amf", HardwareVendor.Amd, "AMD AMF")
    ];

    public FfmpegCapabilities(
        IReadOnlySet<string> encoders,
        IReadOnlySet<string> hardwareAccelerations,
        IReadOnlyList<HardwareEncoder> workingHardwareEncoders,
        string? version)
    {
        Encoders = encoders;
        HardwareAccelerations = hardwareAccelerations;
        WorkingHardwareEncoders = workingHardwareEncoders;
        Version = version;
    }

    public IReadOnlySet<string> Encoders { get; }
    public IReadOnlySet<string> HardwareAccelerations { get; }
    public IReadOnlyList<HardwareEncoder> WorkingHardwareEncoders { get; }
    public string? Version { get; }
    public bool IsAvailable => Encoders.Count > 0;

    public bool HasEncoder(string name) => Encoders.Contains(name);

    public bool HasAnyEncoder(params string[] names) => names.Any(Encoders.Contains);

    public HardwareEncoder? HardwareEncoderFor(string codec) =>
        WorkingHardwareEncoders.FirstOrDefault(encoder => encoder.Codec == codec);

    public string HardwareSummary => WorkingHardwareEncoders.Count == 0
        ? "Software encoding"
        : string.Join(", ", WorkingHardwareEncoders.Select(encoder => encoder.DisplayName).Distinct());

    /// <summary>Discovers capabilities by running ffmpeg. Hardware encoders are verified with a 0.1s null encode.</summary>
    public static async Task<FfmpegCapabilities> DiscoverAsync(string ffmpegPath, bool verifyHardware = true, CancellationToken token = default)
    {
        var version = ToolLocator.ReadVersion(ToolKind.Ffmpeg, ffmpegPath);
        var encodersText = await RunAsync(ffmpegPath, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(10), token);
        var hwaccelText = await RunAsync(ffmpegPath, ["-hide_banner", "-hwaccels"], TimeSpan.FromSeconds(10), token);
        var encoders = ParseEncoders(encodersText);
        var hwaccels = ParseHardwareAccelerations(hwaccelText);

        var working = new List<HardwareEncoder>();
        if (verifyHardware)
        {
            foreach (var candidate in HardwareCandidates.Where(candidate => encoders.Contains(candidate.Encoder)))
            {
                if (!VendorPlausible(candidate.Vendor, hwaccels)) continue;
                if (await TestEncoderAsync(ffmpegPath, candidate.Encoder, token))
                {
                    working.Add(new HardwareEncoder(candidate.Codec, candidate.Encoder, candidate.Vendor, candidate.Name));
                }
            }
        }

        return new FfmpegCapabilities(encoders, hwaccels, working, version);
    }

    public static IReadOnlySet<string> ParseEncoders(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var match = EncoderLine.Match(line);
            if (match.Success) set.Add(match.Groups["name"].Value);
        }
        return set;
    }

    public static IReadOnlySet<string> ParseHardwareAccelerations(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var started = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Hardware acceleration methods", StringComparison.OrdinalIgnoreCase)) { started = true; continue; }
            if (started && line.Length > 0 && !line.Contains(' ')) set.Add(line);
        }
        return set;
    }

    private static bool VendorPlausible(HardwareVendor vendor, IReadOnlySet<string> hwaccels) => vendor switch
    {
        HardwareVendor.Apple => OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst(),
        HardwareVendor.Nvidia => hwaccels.Contains("cuda") || hwaccels.Contains("d3d11va") || hwaccels.Count == 0,
        HardwareVendor.Intel => hwaccels.Contains("qsv") || hwaccels.Contains("d3d11va") || hwaccels.Contains("vaapi") || hwaccels.Count == 0,
        HardwareVendor.Amd => hwaccels.Contains("amf") || hwaccels.Contains("d3d11va") || hwaccels.Count == 0,
        _ => false
    };

    private static async Task<bool> TestEncoderAsync(string ffmpegPath, string encoder, CancellationToken token)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpegPath)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in new[]
            {
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.2:r=10",
                "-c:v", encoder, "-f", "null", "-"
            })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                return false;
            }

            await Task.WhenAll(stdout, stderr);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> RunAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken token)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                token.ThrowIfCancellationRequested();
                return string.Empty;
            }

            await Task.WhenAll(stdout, stderr);
            return stdout.Result + "\n" + stderr.Result;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"^\s*[VAS][F.][S.][X.][B.][D.]\s+(?<name>[A-Za-z0-9_\-]+)\s+", RegexOptions.Compiled)]
    private static partial Regex EncoderLineRegex();
}
