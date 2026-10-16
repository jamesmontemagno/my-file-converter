using LocalMorph.Core.Engines;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Jobs;

public sealed record ConversionInput(SourceFile Source, ConversionOptions Options);
public sealed record BatchEntry(IReadOnlyList<SourceFile> Inputs, ConversionOptions Options, EngineKind Engine, string OutputPath, string? Issue)
{
    public bool CanRun => Issue is null;
}
public sealed record BatchPreflight(IReadOnlyList<BatchEntry> Entries)
{
    public int RunnableCount => Entries.Count(entry => entry.CanRun);
}

public static class BatchPlanner
{
    public static BatchPreflight Prepare(IReadOnlyList<ConversionInput> inputs, OutputFormat format, OperationRequest operation,
        ToolInventory tools, string? outputDirectory, string suffix, OverwritePolicy policy, IEnumerable<string> protectedPaths)
    {
        var entries = new List<BatchEntry>();
        var reserved = protectedPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (operation.Kind == MediaOperation.Join && inputs.Count > 0)
        {
            var sources = inputs.Select(input => input.Source).ToArray();
            var options = new ConversionOptions { StripMetadata = inputs[0].Options.StripMetadata };
            var issue = MediaOperations.JoinIssue(sources, format);
            if (!tools.HasFfmpeg) issue ??= "FFmpeg is not installed.";
            var preferred = OutputNaming.PreferredOutputPath(sources[0].Path, format, outputDirectory, suffix + "-joined");
            if (policy == OverwritePolicy.Skip && File.Exists(preferred)) issue ??= "The joined destination already exists.";
            var output = OutputNaming.BuildOutputPath(sources[0].Path, format, outputDirectory, suffix + "-joined", policy, reserved);
            entries.Add(new(sources, options, EngineKind.Ffmpeg, output, issue));
            return new(entries);
        }
        foreach (var input in inputs)
        {
            var source = input.Source;
            var options = input.Options;
            string? issue = null;
            if (!format.AcceptsSources.Contains(source.Category) ||
                (source.Category == MediaCategory.Document && format.AcceptsDocumentFlavors is { } flavors && !flavors.Contains(source.Flavor)))
                issue = $"{format.DisplayName} does not apply to this source.";
            issue ??= options.Validate(format);
            var engine = FormatCatalog.ResolveEngine(format, source.Path, tools);
            if (engine is null) issue ??= "A required conversion tool or decoder is unavailable. Open Tools.";
            if (engine == EngineKind.Ffmpeg) issue ??= FfmpegEngine.ValidateConfiguration(source, format, options, tools.Ffmpeg);
            if ((options.MeasureLoudness || options.TrimSilence) && ((!source.HasAudio && string.IsNullOrWhiteSpace(options.ReplacementAudioPath)) || options.RemoveAudio)) issue ??= "There is no retained audio to analyze.";
            if (operation.Kind == MediaOperation.Split) issue ??= MediaOperations.SplitIssue(source, format, options, operation);
            var preferred = OutputNaming.PreferredOutputPath(source.Path, format, outputDirectory, suffix, source, options.AudioTrack);
            if (operation.Kind != MediaOperation.Split && policy == OverwritePolicy.Skip && File.Exists(preferred)) issue ??= "The destination already exists.";
            var output = OutputNaming.BuildOutputPath(source.Path, format, outputDirectory, suffix, policy, reserved, source, options.AudioTrack);
            entries.Add(new([source], options, engine ?? EngineKind.Ffmpeg, output, issue));
        }
        return new(entries);
    }
}
