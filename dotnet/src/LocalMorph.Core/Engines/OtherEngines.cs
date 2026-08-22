using System.Globalization;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Engines;

/// <summary>ImageMagick handles photo formats FFmpeg can't decode (HEIC, RAW, SVG, PSD) and ICO/HEIC/PDF output.</summary>
public sealed class ImageMagickEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.ImageMagick;

    public ConversionPlan Plan(ConversionJob job, ToolInventory tools, string workDirectory)
    {
        var magick = tools.PathFor(ToolKind.ImageMagick) ?? throw new InvalidOperationException("ImageMagick is not installed.");
        var arguments = BuildArguments(job.Source, job.Format, job.Options, job.OutputPath);
        return new ConversionPlan
        {
            Steps = [new EngineStep { StartInfo = CommandLine.Create(magick, arguments), Label = "Converting with ImageMagick", IsIndeterminate = true }]
        };
    }

    public static IReadOnlyList<string> BuildArguments(SourceFile source, OutputFormat format, ConversionOptions options, string outputPath)
    {
        var args = new List<string>();
        var input = source.Path;
        var isMultiFrame = source.Category == MediaCategory.Document || SourceClassifier.AnimatedImageInputs.Contains(source.Extension) || source.Extension is ".psd" or ".tiff" or ".tif" or ".ico";

        // Rasterize vector/PDF inputs at a sensible density.
        if (source.Extension is ".pdf" or ".svg" or ".eps" or ".ai") args.AddRange(["-density", "200"]);

        // First frame/page only for multi-frame sources unless the target is animated.
        args.Add(isMultiFrame && format.Id != "ico" ? $"{input}[0]" : input);

        args.Add("-auto-orient");
        if (options.StripMetadata) args.Add("-strip");

        if (source.Extension is ".pdf" or ".svg" or ".eps" or ".ai" || format.Id is "jpg" or "doc-jpg" or "bmp")
        {
            args.AddRange(["-background", "white", "-alpha", "remove", "-alpha", "off"]);
        }

        switch (options.Rotation)
        {
            case 90 or 180 or 270:
                args.AddRange(["-rotate", options.Rotation.ToString(CultureInfo.InvariantCulture)]);
                break;
        }

        if (options.TargetHeight is { } height && format.Id != "ico")
        {
            // 'x{height}>' only shrinks images taller than the target.
            args.AddRange(["-resize", $"x{height}>"]);
        }

        switch (format.Id)
        {
            case "jpg":
            case "doc-jpg":
                args.AddRange(["-quality", options.Quality.ToString(CultureInfo.InvariantCulture), "-sampling-factor", "4:2:0", "-interlace", "JPEG"]);
                break;
            case "webp":
                args.AddRange(["-quality", options.Quality.ToString(CultureInfo.InvariantCulture)]);
                if (options.Lossless || options.Quality >= 100) args.AddRange(["-define", "webp:lossless=true"]);
                break;
            case "avif":
            case "heic":
                args.AddRange(["-quality", options.Quality.ToString(CultureInfo.InvariantCulture)]);
                break;
            case "jxl":
                args.AddRange(["-quality", options.Lossless ? "100" : options.Quality.ToString(CultureInfo.InvariantCulture)]);
                break;
            case "png":
            case "doc-png":
                args.AddRange(["-define", "png:compression-level=9"]);
                break;
            case "tiff":
                args.AddRange(["-compress", "LZW"]);
                break;
            case "gif-still":
                args.AddRange(["-colors", "256"]);
                break;
            case "ico":
                args.AddRange(["-background", "none", "-define", "icon:auto-resize=256,128,64,48,32,16"]);
                break;
            case "pdf-image":
                args.AddRange(["-quality", options.Quality.ToString(CultureInfo.InvariantCulture), "-compress", "JPEG"]);
                break;
        }

        // Explicit output format prefix protects against ambiguous extensions.
        var prefix = format.Id switch
        {
            "jpg" or "doc-jpg" => "JPEG:",
            "png" or "doc-png" => "PNG:",
            "webp" => "WEBP:",
            "avif" => "AVIF:",
            "heic" => "HEIC:",
            "jxl" => "JXL:",
            "tiff" => "TIFF:",
            "bmp" => "BMP3:",
            "gif-still" => "GIF:",
            "ico" => "ICO:",
            "pdf-image" => "PDF:",
            _ => string.Empty
        };
        args.Add(prefix + outputPath);
        return args;
    }
}

/// <summary>LibreOffice converts office documents headlessly. It names the output itself, so we convert into a scratch folder then move.</summary>
public sealed class LibreOfficeEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.LibreOffice;

    public ConversionPlan Plan(ConversionJob job, ToolInventory tools, string workDirectory)
    {
        var soffice = tools.PathFor(ToolKind.LibreOffice) ?? throw new InvalidOperationException("LibreOffice is not installed.");
        var scratch = Path.Combine(workDirectory, $"lo-{job.Id:N}");
        Directory.CreateDirectory(scratch);
        var profile = Path.Combine(scratch, "profile");
        var arguments = BuildArguments(job.Source, job.Format, job.Options, scratch, profile);
        var produced = Path.Combine(scratch, Path.GetFileNameWithoutExtension(job.Source.Path) + "." + job.Format.Extension);

        return new ConversionPlan
        {
            Steps = [new EngineStep { StartInfo = CommandLine.Create(soffice, arguments), Label = "Converting with LibreOffice", IsIndeterminate = true }],
            Finalize = _ =>
            {
                if (!File.Exists(produced))
                {
                    var any = Directory.EnumerateFiles(scratch).FirstOrDefault(file => Path.GetExtension(file).Equals("." + job.Format.Extension, StringComparison.OrdinalIgnoreCase))
                              ?? throw new InvalidOperationException("LibreOffice finished but produced no output file. The document may be password protected or unsupported.");
                    produced = any;
                }
                File.Move(produced, job.OutputPath, overwrite: true);
                return Task.CompletedTask;
            },
            Cleanup = () =>
            {
                try { Directory.Delete(scratch, recursive: true); } catch { }
            }
        };
    }

    public static IReadOnlyList<string> BuildArguments(SourceFile source, OutputFormat format, ConversionOptions options, string outputDirectory, string profileDirectory)
    {
        var filter = format.Id switch
        {
            "pdf" => source.Flavor switch
            {
                DocumentFlavor.Spreadsheet => "pdf:calc_pdf_Export",
                DocumentFlavor.Presentation => "pdf:impress_pdf_Export",
                _ => "pdf:writer_pdf_Export"
            },
            "docx" => "docx:MS Word 2007 XML",
            "odt" => "odt",
            "rtf" => "rtf",
            "txt" => "txt:Text",
            "html" => source.Flavor == DocumentFlavor.Spreadsheet ? "html:HTML (StarCalc)" : "html:XHTML Writer File",
            "epub" => "epub",
            "xlsx" => "xlsx:Calc MS Excel 2007 XML",
            "ods" => "ods",
            "csv" => "csv:Text - txt - csv (StarCalc):44,34,76,1",
            "pptx" => "pptx:Impress MS PowerPoint 2007 XML",
            "odp" => "odp",
            "doc-png" => "png",
            "doc-jpg" => "jpg",
            _ => format.Extension
        };

        var profileUri = new Uri(profileDirectory.EndsWith(Path.DirectorySeparatorChar) ? profileDirectory : profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri;
        return
        [
            "--headless", "--norestore", "--nologo", "--nolockcheck",
            $"-env:UserInstallation={profileUri}",
            "--convert-to", filter,
            "--outdir", outputDirectory,
            source.Path
        ];
    }
}

/// <summary>Pandoc for Markdown, HTML, EPUB, and text-first document conversion.</summary>
public sealed class PandocEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.Pandoc;

    public ConversionPlan Plan(ConversionJob job, ToolInventory tools, string workDirectory)
    {
        var pandoc = tools.PathFor(ToolKind.Pandoc) ?? throw new InvalidOperationException("Pandoc is not installed.");
        return new ConversionPlan
        {
            Steps = [new EngineStep { StartInfo = CommandLine.Create(pandoc, BuildArguments(job.Source, job.Format, job.OutputPath)), Label = "Converting with Pandoc", IsIndeterminate = true }]
        };
    }

    public static IReadOnlyList<string> BuildArguments(SourceFile source, OutputFormat format, string outputPath)
    {
        var args = new List<string> { source.Path, "--standalone", "--resource-path", Path.GetDirectoryName(source.Path) ?? "." };
        var to = format.Id switch
        {
            "md" => "gfm",
            "html" => "html5",
            "txt" => "plain",
            "pdf" => "pdf",
            _ => format.Extension
        };
        args.AddRange(["--to", to]);
        if (format.Id == "pdf") args.AddRange(["--pdf-engine", "wkhtmltopdf"]);
        args.AddRange(["--output", outputPath]);
        return args;
    }
}

/// <summary>Ghostscript for PDF compression and rasterizing PDF pages.</summary>
public sealed class GhostscriptEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.Ghostscript;

    public ConversionPlan Plan(ConversionJob job, ToolInventory tools, string workDirectory)
    {
        var gs = tools.PathFor(ToolKind.Ghostscript) ?? throw new InvalidOperationException("Ghostscript is not installed.");
        return new ConversionPlan
        {
            Steps = [new EngineStep { StartInfo = CommandLine.Create(gs, BuildArguments(job.Source, job.Format, job.Options, job.OutputPath)), Label = "Processing PDF with Ghostscript", IsIndeterminate = true }]
        };
    }

    public static IReadOnlyList<string> BuildArguments(SourceFile source, OutputFormat format, ConversionOptions options, string outputPath)
    {
        var args = new List<string> { "-dSAFER", "-dBATCH", "-dNOPAUSE", "-dQUIET" };
        switch (format.Id)
        {
            case "pdf-compress":
                var preset = options.Quality switch { <= 40 => "/screen", <= 70 => "/ebook", <= 90 => "/printer", _ => "/prepress" };
                args.AddRange(["-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.5", $"-dPDFSETTINGS={preset}", "-dDetectDuplicateImages=true", "-dCompressFonts=true", $"-sOutputFile={outputPath}", source.Path]);
                break;
            case "doc-png":
                args.AddRange(["-sDEVICE=png16m", "-r150", "-dFirstPage=1", "-dLastPage=1", "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4", $"-sOutputFile={outputPath}", source.Path]);
                break;
            case "doc-jpg":
                args.AddRange(["-sDEVICE=jpeg", "-r150", "-dFirstPage=1", "-dLastPage=1", $"-dJPEGQ={options.Quality}", "-dTextAlphaBits=4", "-dGraphicsAlphaBits=4", $"-sOutputFile={outputPath}", source.Path]);
                break;
            default:
                args.AddRange(["-sDEVICE=pdfwrite", $"-sOutputFile={outputPath}", source.Path]);
                break;
        }
        return args;
    }
}
