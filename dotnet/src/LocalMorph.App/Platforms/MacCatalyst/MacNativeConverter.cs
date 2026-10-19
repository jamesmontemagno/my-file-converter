using CoreGraphics;
using Foundation;
using ImageIO;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Native;
using LocalMorph.Core.Tools;
using Markdig;
using UIKit;
using CGImageProperties = ImageIO.CGImageProperties;

namespace LocalMorph.App.Platforms.MacCatalyst;

/// <summary>
/// Photos, PDF pages, and simple documents converted with frameworks built into macOS: ImageIO decodes HEIC,
/// RAW, PSD, WebP, AVIF and more; Core Graphics renders PDF pages and writes PDFs; UIKit's text system reads and
/// writes RTF/HTML/plain text. Nothing external runs, so this works inside the App Store sandbox.
/// </summary>
public sealed class MacNativeConverter : INativeConverter
{
    private const double PdfRenderDpi = 200;

    private static readonly Dictionary<string, string> ImageOutputTypes = new(StringComparer.Ordinal)
    {
        ["png"] = "public.png", ["jpg"] = "public.jpeg", ["tiff"] = "public.tiff", ["bmp"] = "com.microsoft.bmp",
        ["gif-still"] = "com.compuserve.gif", ["heic"] = "public.heic", ["avif"] = "public.avif", ["ico"] = "com.microsoft.ico"
    };

    private static readonly Dictionary<string, string> ImageInputTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "public.png", [".jpg"] = "public.jpeg", [".jpeg"] = "public.jpeg", [".gif"] = "com.compuserve.gif",
        [".tif"] = "public.tiff", [".tiff"] = "public.tiff", [".bmp"] = "com.microsoft.bmp", [".heic"] = "public.heic",
        [".heif"] = "public.heif", [".avif"] = "public.avif", [".jxl"] = "public.jpeg-xl", [".webp"] = "org.webmproject.webp",
        [".psd"] = "com.adobe.photoshop-image", [".ico"] = "com.microsoft.ico", [".tga"] = "com.truevision.tga-image",
        [".dds"] = "com.microsoft.dds", [".exr"] = "com.ilm.openexr-image", [".hdr"] = "public.radiance", [".pbm"] = "public.pbm",
        [".jp2"] = "public.jpeg-2000", [".cr2"] = "com.canon.cr2-raw-image", [".cr3"] = "com.canon.cr3-raw-image",
        [".nef"] = "com.nikon.raw-image", [".arw"] = "com.sony.arw-raw-image", [".dng"] = "com.adobe.raw-image",
        [".orf"] = "com.olympus.raw-image", [".rw2"] = "com.panasonic.rw2-raw-image", [".raf"] = "com.fuji.raw-image"
    };

    private static readonly int[] IconSizes = [256, 128, 64, 48, 32, 16];

    private readonly Lazy<(HashSet<string> Writable, HashSet<string> Readable)> capabilities = new(DiscoverCapabilities);

    public IReadOnlySet<string> WritableFormats => capabilities.Value.Writable;
    public IReadOnlySet<string> ReadableImageExtensions => capabilities.Value.Readable;

    public (int Width, int Height)? ReadImageSize(string path)
    {
        using var url = NSUrl.FromFilename(path);
        using var imageSource = CGImageSource.FromUrl(url);
        if (imageSource is null || imageSource.ImageCount < 1) return null;
        using var properties = imageSource.CopyProperties((NSDictionary?)null, 0);
        if (properties?[CGImageProperties.PixelWidth] is not NSNumber width || properties[CGImageProperties.PixelHeight] is not NSNumber height) return null;
        // EXIF orientations 5-8 are quarter turns, so the upright picture swaps width and height.
        var turned = properties[CGImageProperties.Orientation] is NSNumber orientation && orientation.Int32Value is >= 5 and <= 8;
        return turned ? (height.Int32Value, width.Int32Value) : (width.Int32Value, height.Int32Value);
    }

    public ToolInfo? Probe()
    {
        if (WritableFormats.Count == 0) return null;
        var outputs = FormatCatalog.All.Where(format => WritableFormats.Contains(format.Id) && format.Category == MediaCategory.Image && !format.Id.StartsWith("doc-", StringComparison.Ordinal))
            .Select(format => format.Extension.ToUpperInvariant()).Distinct();
        var notes = $"ImageIO, Core Graphics, and the text system convert photos (HEIC, RAW, PSD, WebP, AVIF), PDF pages, and RTF/HTML/Markdown in-process. Writes {string.Join(", ", outputs)}, PDF, RTF, HTML, and TXT.";
        return new ToolInfo(ToolKind.AppleNative, "Built into macOS", $"macOS {NSProcessInfo.ProcessInfo.OperatingSystemVersion}", ToolSource.System, notes);
    }

    private static (HashSet<string>, HashSet<string>) DiscoverCapabilities()
    {
        var encoders = new HashSet<string>((CGImageDestination.TypeIdentifiers ?? []).OfType<string>(), StringComparer.Ordinal);
        var decoders = new HashSet<string>((CGImageSource.TypeIdentifiers ?? []).OfType<string>(), StringComparer.Ordinal);

        var writable = new HashSet<string>(StringComparer.Ordinal) { "pdf-image", "pdf", "rtf", "html", "txt" };
        foreach (var (id, type) in ImageOutputTypes)
        {
            if (encoders.Contains(type)) writable.Add(id);
        }
        if (writable.Contains("png")) writable.Add("doc-png");
        if (writable.Contains("jpg")) writable.Add("doc-jpg");

        var readable = new HashSet<string>(ImageInputTypes.Where(pair => decoders.Contains(pair.Value)).Select(pair => pair.Key.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        return (writable, readable);
    }

    public async Task ConvertAsync(SourceFile source, OutputFormat format, ConversionOptions options, string outputPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            switch (source.Category)
            {
                case MediaCategory.Image:
                    await Task.Run(() => ConvertImage(source, format, options, outputPath, token), token);
                    break;
                case MediaCategory.Document when source.Extension == ".pdf":
                    await Task.Run(() => RenderPdfPage(source, format, options, outputPath, token), token);
                    break;
                case MediaCategory.Document:
                    await ConvertTextAsync(source, format, outputPath, token);
                    break;
                default:
                    throw new InvalidOperationException($"macOS cannot convert {source.FileName} to {format.DisplayName}.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            throw new InvalidOperationException($"macOS could not convert {source.FileName}: {ex.Message}", ex);
        }
    }

    // ---------------- Images ----------------

    private static void ConvertImage(SourceFile source, OutputFormat format, ConversionOptions options, string outputPath, CancellationToken token)
    {
        using var url = NSUrl.FromFilename(source.Path);
        using var imageSource = CGImageSource.FromUrl(url) ?? throw new InvalidOperationException($"macOS could not open {source.FileName} as an image.");
        if (imageSource.ImageCount < 1) throw new InvalidOperationException($"{source.FileName} contains no image.");

        // A "thumbnail" with no size limit is the full-resolution image with its EXIF orientation applied.
        var decode = new CGImageThumbnailOptions { CreateThumbnailFromImageAlways = true, CreateThumbnailWithTransform = true, ShouldCache = false };
        using var decoded = imageSource.CreateThumbnail(0, decode) ?? imageSource.CreateImage(0, new CGImageOptions())
            ?? throw new InvalidOperationException($"macOS could not decode {source.FileName}.");
        token.ThrowIfCancellationRequested();

        var flatten = format.Id is "jpg" && HasAlpha(decoded);
        using var image = Transform(decoded, options.TargetHeight, options.Rotation, flatten);
        token.ThrowIfCancellationRequested();

        if (format.Id == "pdf-image")
        {
            WriteImagePdf(image, options, outputPath);
            return;
        }

        var metadata = options.StripMetadata || format.Id is "ico" or "gif-still" or "bmp" ? null : CopyMetadata(imageSource);
        if (format.Id == "ico")
        {
            WriteIcon(image, outputPath);
            return;
        }

        WriteImage(image, ImageOutputTypes[format.Id], format.Supports(FormatFeatures.Quality) ? options.Quality : null, metadata, outputPath);
    }

    private static bool HasAlpha(CGImage image) =>
        image.AlphaInfo is not (CGImageAlphaInfo.None or CGImageAlphaInfo.NoneSkipFirst or CGImageAlphaInfo.NoneSkipLast);

    /// <summary>Shrinks to <paramref name="maxHeight"/> (never enlarges), rotates clockwise, and optionally flattens onto white.</summary>
    private static CGImage Transform(CGImage image, int? maxHeight, int rotation, bool flatten)
    {
        var scale = maxHeight is > 0 && image.Height > maxHeight.Value ? maxHeight.Value / (double)image.Height : 1.0;
        rotation = rotation is 90 or 180 or 270 ? rotation : 0;
        if (scale == 1.0 && rotation == 0 && !flatten) return image.Clone()!;

        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var swapped = rotation is 90 or 270;
        var canvasWidth = swapped ? height : width;
        var canvasHeight = swapped ? width : height;

        using var colorSpace = image.ColorSpace?.Model == CGColorSpaceModel.RGB ? image.ColorSpace : CGColorSpace.CreateSrgb();
        using var context = new CGBitmapContext(null, canvasWidth, canvasHeight, 8, 0, colorSpace, CGImageAlphaInfo.PremultipliedLast);
        context.InterpolationQuality = CGInterpolationQuality.High;
        if (flatten)
        {
            context.SetFillColor(new CGColor(1, 1, 1, 1));
            context.FillRect(new CGRect(0, 0, canvasWidth, canvasHeight));
        }

        // Core Graphics' origin is bottom-left, so a clockwise turn is a negative angle.
        context.TranslateCTM(canvasWidth / 2f, canvasHeight / 2f);
        context.RotateCTM((nfloat)(-rotation * Math.PI / 180));
        context.DrawImage(new CGRect(-width / 2f, -height / 2f, width, height), image);
        return context.ToImage() ?? throw new InvalidOperationException("macOS could not resize this image.");
    }

    private static NSMutableDictionary? CopyMetadata(CGImageSource imageSource)
    {
        using var properties = imageSource.CopyProperties((NSDictionary?)null, 0);
        if (properties is null) return null;
        var copy = new NSMutableDictionary(properties);
        // Pixels are already upright; a stale orientation tag would rotate them a second time.
        copy[CGImageProperties.Orientation] = NSNumber.FromInt32(1);
        if (copy[CGImageProperties.TIFFDictionary] is NSDictionary tiff)
        {
            var tiffCopy = new NSMutableDictionary(tiff) { [CGImageProperties.TIFFOrientation] = NSNumber.FromInt32(1) };
            copy[CGImageProperties.TIFFDictionary] = tiffCopy;
        }
        foreach (var key in new[] { CGImageProperties.PixelWidth, CGImageProperties.PixelHeight }) copy.Remove(key);
        return copy;
    }

    private static void WriteImage(CGImage image, string typeIdentifier, int? quality, NSDictionary? metadata, string outputPath)
    {
        using var url = NSUrl.FromFilename(outputPath);
        using var destination = CGImageDestination.Create(url, typeIdentifier, 1)
            ?? throw new InvalidOperationException($"macOS cannot write {typeIdentifier} on this Mac.");
        var properties = metadata is null ? new NSMutableDictionary() : new NSMutableDictionary(metadata);
        if (quality is { } value)
        {
            properties[new NSString("kCGImageDestinationLossyCompressionQuality")] = NSNumber.FromDouble(Math.Clamp(value, 1, 100) / 100.0);
        }
        destination.AddImage(image, properties);
        if (!destination.Close()) throw new InvalidOperationException("macOS could not finish writing the image.");
    }

    private static void WriteIcon(CGImage image, string outputPath)
    {
        var longest = Math.Max(image.Width, image.Height);
        var sizes = IconSizes.Where(size => size <= Math.Max(longest, 16)).ToArray();
        using var url = NSUrl.FromFilename(outputPath);
        using var destination = CGImageDestination.Create(url, "com.microsoft.ico", sizes.Length)
            ?? throw new InvalidOperationException("macOS cannot write ICO files on this Mac.");
        foreach (var size in sizes)
        {
            using var frame = FitSquare(image, size);
            destination.AddImage(frame, (NSDictionary?)null);
        }
        if (!destination.Close()) throw new InvalidOperationException("macOS could not finish writing the icon.");
    }

    private static CGImage FitSquare(CGImage image, int size)
    {
        var scale = Math.Min(size / (double)image.Width, size / (double)image.Height);
        var width = image.Width * scale;
        var height = image.Height * scale;
        using var colorSpace = CGColorSpace.CreateSrgb();
        using var context = new CGBitmapContext(null, size, size, 8, 0, colorSpace, CGImageAlphaInfo.PremultipliedLast);
        context.InterpolationQuality = CGInterpolationQuality.High;
        context.DrawImage(new CGRect((size - width) / 2, (size - height) / 2, width, height), image);
        return context.ToImage() ?? throw new InvalidOperationException("macOS could not scale this icon.");
    }

    /// <summary>One page sized to the image at 150 dpi; opaque images are embedded as JPEG at the chosen quality.</summary>
    private static void WriteImagePdf(CGImage image, ConversionOptions options, string outputPath)
    {
        var page = new CGRect(0, 0, image.Width * 72.0 / 150, image.Height * 72.0 / 150);
        using var embedded = HasAlpha(image) ? image.Clone()! : AsJpeg(image, options.Quality) ?? image.Clone()!;
        using var url = NSUrl.FromFilename(outputPath);
        using var context = new CGContextPDF(url, page);
        context.BeginPage(new CGPDFPageInfo());
        context.DrawImage(page, embedded);
        context.EndPage();
        context.Close();
    }

    private static CGImage? AsJpeg(CGImage image, int quality)
    {
        using var data = new NSMutableData();
        using (var destination = CGImageDestination.Create(data, "public.jpeg", 1))
        {
            if (destination is null) return null;
            destination.AddImage(image, new NSDictionary(new NSString("kCGImageDestinationLossyCompressionQuality"), NSNumber.FromDouble(Math.Clamp(quality, 1, 100) / 100.0)));
            if (!destination.Close()) return null;
        }
        using var provider = new CGDataProvider(data);
        return CGImage.FromJPEG(provider, null, true, CGColorRenderingIntent.Default);
    }

    // ---------------- PDF pages ----------------

    private static void RenderPdfPage(SourceFile source, OutputFormat format, ConversionOptions options, string outputPath, CancellationToken token)
    {
        using var url = NSUrl.FromFilename(source.Path);
        using var document = CGPDFDocument.FromUrl(url.AbsoluteString!) ?? throw new InvalidOperationException($"macOS could not open {source.FileName} as a PDF.");
        if (!document.IsUnlocked) throw new InvalidOperationException($"{source.FileName} is password-protected.");
        if (document.Pages < 1) throw new InvalidOperationException($"{source.FileName} has no pages.");
        using var page = document.GetPage(1) ?? throw new InvalidOperationException($"macOS could not read the first page of {source.FileName}.");

        var box = page.GetBoxRect(CGPDFBox.Crop);
        var rotated = page.RotationAngle % 180 != 0;
        var pageWidth = rotated ? box.Height : box.Width;
        var pageHeight = rotated ? box.Width : box.Height;
        var scale = PdfRenderDpi / 72.0;
        if (options.TargetHeight is > 0 && pageHeight * scale > options.TargetHeight.Value) scale = options.TargetHeight.Value / (double)pageHeight;
        var width = Math.Max(1, (int)Math.Round(pageWidth * scale));
        var height = Math.Max(1, (int)Math.Round(pageHeight * scale));

        using var colorSpace = CGColorSpace.CreateSrgb();
        using var context = new CGBitmapContext(null, width, height, 8, 0, colorSpace, CGImageAlphaInfo.PremultipliedLast);
        context.SetFillColor(new CGColor(1, 1, 1, 1));
        context.FillRect(new CGRect(0, 0, width, height));
        context.InterpolationQuality = CGInterpolationQuality.High;
        context.ScaleCTM((nfloat)scale, (nfloat)scale);
        context.ConcatCTM(page.GetDrawingTransform(CGPDFBox.Crop, new CGRect(0, 0, pageWidth, pageHeight), 0, true));
        context.DrawPDFPage(page);
        token.ThrowIfCancellationRequested();

        using var image = context.ToImage() ?? throw new InvalidOperationException("macOS could not render this PDF page.");
        var jpeg = format.Id == "doc-jpg";
        WriteImage(image, jpeg ? "public.jpeg" : "public.png", jpeg ? options.Quality : null, null, outputPath);
    }

    // ---------------- Text documents ----------------

    private static async Task ConvertTextAsync(SourceFile source, OutputFormat format, string outputPath, CancellationToken token)
    {
        var extension = source.Extension;
        var raw = await File.ReadAllBytesAsync(source.Path, token);
        string? html = extension switch
        {
            ".md" or ".markdown" => WrapHtml(Path.GetFileNameWithoutExtension(source.Path),
                Markdig.Markdown.ToHtml(DecodeText(raw), new MarkdownPipelineBuilder().UseAdvancedExtensions().Build())),
            ".html" or ".htm" => DecodeText(raw),
            _ => null
        };

        // Markdown → HTML keeps Markdig's markup; HTML → HTML would be a copy.
        if (format.Id == "html" && html is not null)
        {
            await File.WriteAllTextAsync(outputPath, html, new System.Text.UTF8Encoding(false), token);
            return;
        }
        if (format.Id == "html" && extension == ".txt")
        {
            await File.WriteAllTextAsync(outputPath, WrapHtml(Path.GetFileNameWithoutExtension(source.Path),
                "<pre style=\"white-space: pre-wrap; font: inherit\">" + System.Net.WebUtility.HtmlEncode(DecodeText(raw)) + "</pre>"),
                new System.Text.UTF8Encoding(false), token);
            return;
        }
        if (format.Id == "txt" && extension == ".txt")
        {
            await File.WriteAllBytesAsync(outputPath, raw, token);
            return;
        }

        // The HTML importer and UIKit printing must run on the main thread.
        var bytes = await MainThread.InvokeOnMainThreadAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            using var attributed = Load(extension, raw, html);
            return format.Id switch
            {
                "txt" => System.Text.Encoding.UTF8.GetBytes(attributed.Value ?? string.Empty),
                "rtf" => Export(attributed, NSDocumentType.RTF),
                "html" => Export(attributed, NSDocumentType.HTML),
                "pdf" => RenderPdf(attributed),
                _ => throw new InvalidOperationException($"macOS cannot write {format.DisplayName} from {source.FileName}.")
            };
        });
        await File.WriteAllBytesAsync(outputPath, bytes, token);
    }

    private static string DecodeText(byte[] raw)
    {
        using var reader = new StreamReader(new MemoryStream(raw), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string WrapHtml(string title, string body) =>
        $"""
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><title>{System.Net.WebUtility.HtmlEncode(title)}</title>
        <style>body {"{"} font-family: -apple-system, "Helvetica Neue", sans-serif; font-size: 12pt; line-height: 1.45; {"}"} pre, code {"{"} font-family: Menlo, monospace; font-size: 10.5pt; {"}"} table {"{"} border-collapse: collapse; {"}"} td, th {"{"} border: 1px solid #ccc; padding: 4px 8px; {"}"}</style>
        </head><body>
        {body}
        </body></html>
        """;

    private static NSAttributedString Load(string extension, byte[] raw, string? html)
    {
        if (extension == ".txt")
        {
            var attributes = new UIStringAttributes { Font = UIFont.SystemFontOfSize(12) };
            return new NSAttributedString(DecodeText(raw), attributes);
        }

        var documentType = html is null ? NSDocumentType.RTF : NSDocumentType.HTML;
        using var data = html is null ? NSData.FromArray(raw) : NSData.FromString(html, NSStringEncoding.UTF8);
        var options = new NSAttributedStringDocumentAttributes { DocumentType = documentType, CharacterEncoding = NSStringEncoding.UTF8 };
        var attributed = NSAttributedString.Create(data, options, out var error);
        if (attributed is null || error is not null) throw new InvalidOperationException($"macOS could not read this document: {error?.LocalizedDescription ?? "unreadable"}");
        return attributed;
    }

    private static byte[] Export(NSAttributedString attributed, NSDocumentType documentType)
    {
        var attributes = new NSAttributedStringDocumentAttributes { DocumentType = documentType, CharacterEncoding = NSStringEncoding.UTF8 };
        using var data = attributed.GetData(new NSRange(0, attributed.Length), attributes, out var error);
        if (data is null || error is not null) throw new InvalidOperationException($"macOS could not write this document: {error?.LocalizedDescription ?? "no data"}");
        return data.ToArray();
    }

    /// <summary>US Letter pages with 0.75" margins, paginated by UIKit's print formatter.</summary>
    private static byte[] RenderPdf(NSAttributedString attributed)
    {
        var paper = new CGRect(0, 0, 612, 792);
        var printable = paper.Inset(54, 54);
        using var formatter = new UISimpleTextPrintFormatter(attributed);
        using var renderer = new UIPrintPageRenderer();
        renderer.AddPrintFormatter(formatter, 0);
        renderer.SetValueForKey(NSValue.FromCGRect(paper), new NSString("paperRect"));
        renderer.SetValueForKey(NSValue.FromCGRect(printable), new NSString("printableRect"));

        var data = new NSMutableData();
        UIGraphics.BeginPDFContext(data, paper, null);
        var pages = Math.Max(1, (int)renderer.NumberOfPages);
        renderer.PrepareForDrawingPages(new NSRange(0, pages));
        for (var index = 0; index < pages; index++)
        {
            UIGraphics.BeginPDFPage();
            renderer.DrawPage(index, UIGraphics.PDFContextBounds);
        }
        UIGraphics.EndPDFContext();
        return data.ToArray();
    }
}
