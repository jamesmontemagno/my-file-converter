using LocalMorph.Core.Formats;
using LocalMorph.Core.Imaging;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Engines;

/// <summary>
/// Decodes HEIC/HEIF photos in-process with the Windows Imaging Component (via the Microsoft Store
/// "HEIF Image Extensions" codec) and writes PNG/JPEG/BMP/TIFF/GIF. No external tool is needed.
/// </summary>
public sealed class WindowsImagingEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.WindowsImaging;

    public ConversionPlan Plan(ConversionJob job, ToolInventory tools, string workDirectory)
    {
        if (!tools.Has(ToolKind.WindowsHeif)) throw new InvalidOperationException("The HEIF Image Extensions codec is not installed.");
        var codec = PlatformImageCodec.Current ?? throw new InvalidOperationException("No platform image codec is registered.");
        if (!PlatformImageCodec.EncodableFormats.Contains(job.Format.Id)) throw new InvalidOperationException($"Windows Imaging cannot write {job.Format.DisplayName}.");

        return new ConversionPlan
        {
            Steps =
            [
                new EngineStep
                {
                    Label = "Converting with Windows Imaging",
                    IsIndeterminate = true,
                    Summary = Summarize(job),
                    Execute = (current, token) => codec.ConvertAsync(current.Source, current.Format, current.Options, current.OutputPath, token)
                }
            ]
        };
    }

    public static string Summarize(ConversionJob job)
    {
        var parts = new List<string> { "windows-imaging", CommandLine.Quote(job.Source.Path), "->", CommandLine.Quote(job.OutputPath) };
        if (job.Format.Id is "jpg") parts.Add($"quality={job.Options.Quality}");
        if (job.Options.TargetHeight is { } height) parts.Add($"max-height={height}");
        if (job.Options.Rotation is 90 or 180 or 270) parts.Add($"rotate={job.Options.Rotation}");
        if (job.Options.StripMetadata) parts.Add("strip-metadata");
        return string.Join(' ', parts);
    }
}
