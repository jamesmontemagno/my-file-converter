using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Native;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Engines;

/// <summary>Converts photos, PDF pages, and text documents in-process with the Mac's built-in frameworks.</summary>
public sealed class AppleNativeEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.AppleNative;

    public ConversionPlan Plan(ConversionJob job, ToolInventory tools, string workDirectory)
    {
        if (!tools.Has(ToolKind.AppleNative)) throw new InvalidOperationException("The built-in macOS converters are unavailable.");
        var converter = NativeConverter.Current ?? throw new InvalidOperationException("No native converter is registered.");
        if (!NativeConverter.CanConvert(converter, job.Source.Path, job.Format))
            throw new InvalidOperationException($"macOS cannot convert {job.Source.FileName} to {job.Format.DisplayName}.");

        return new ConversionPlan
        {
            Steps =
            [
                new EngineStep
                {
                    Label = "Converting with macOS",
                    IsIndeterminate = true,
                    Summary = Summarize(job),
                    Execute = (current, token) => converter.ConvertAsync(current.Source, current.Format, current.Options, current.WorkingOutputPath, token)
                }
            ]
        };
    }

    public static string Summarize(ConversionJob job)
    {
        var parts = new List<string> { "apple-native", job.Format.Id, CommandLine.Quote(job.Source.Path), "->", CommandLine.Quote(job.WorkingOutputPath) };
        if (job.Format.Supports(FormatFeatures.Quality)) parts.Add($"quality={job.Options.Quality}");
        if (job.Options.TargetHeight is { } height) parts.Add($"max-height={height}");
        if (job.Options.Rotation is 90 or 180 or 270) parts.Add($"rotate={job.Options.Rotation}");
        if (job.Options.StripMetadata) parts.Add("strip-metadata");
        return string.Join(' ', parts);
    }
}
