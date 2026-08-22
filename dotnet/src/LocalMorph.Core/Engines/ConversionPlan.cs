using System.Diagnostics;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Engines;

/// <summary>Progress sample parsed from a tool's output.</summary>
public sealed record ProgressSample(double? Ratio, double? SecondsRemaining, string? Speed, bool Finished);

/// <summary>One external process invocation. A plan may have several (e.g. two-pass encodes).</summary>
public sealed class EngineStep
{
    public required ProcessStartInfo StartInfo { get; init; }
    public required string Label { get; init; }
    public double ProgressStart { get; init; }
    public double ProgressEnd { get; init; } = 1;
    public bool IsIndeterminate { get; init; }
    /// <summary>Parses a stdout line into progress, or null when the line is not a progress line.</summary>
    public Func<string, ProgressSample?>? ParseStdout { get; init; }
    /// <summary>Parses a stderr line into progress (for tools that report progress on stderr).</summary>
    public Func<string, ProgressSample?>? ParseStderr { get; init; }
}

public sealed class ConversionPlan
{
    public required IReadOnlyList<EngineStep> Steps { get; init; }
    /// <summary>Runs after all steps succeed, e.g. to move a tool's fixed-name output to the requested path.</summary>
    public Func<CancellationToken, Task>? Finalize { get; init; }
    /// <summary>Always runs, success or failure.</summary>
    public Action? Cleanup { get; init; }
    public string Describe() => string.Join("\n", Steps.Select(step => CommandLine.Describe(step.StartInfo)));
}

public interface IConversionEngine
{
    EngineKind Kind { get; }
    ConversionPlan Plan(ConversionJob job, ToolInventory tools, string workDirectory);
}

public static class CommandLine
{
    public static ProcessStartInfo Create(string executable, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    public static string Describe(ProcessStartInfo startInfo) =>
        string.Join(' ', new[] { startInfo.FileName }.Concat(startInfo.ArgumentList).Select(Quote));

    public static string Quote(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;

    public static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
}

public static class EngineRegistry
{
    public static readonly IReadOnlyDictionary<EngineKind, IConversionEngine> Engines = new Dictionary<EngineKind, IConversionEngine>
    {
        [EngineKind.Ffmpeg] = new FfmpegEngine(),
        [EngineKind.ImageMagick] = new ImageMagickEngine(),
        [EngineKind.LibreOffice] = new LibreOfficeEngine(),
        [EngineKind.Pandoc] = new PandocEngine(),
        [EngineKind.Ghostscript] = new GhostscriptEngine()
    };

    public static IConversionEngine Get(EngineKind kind) => Engines[kind];
}
