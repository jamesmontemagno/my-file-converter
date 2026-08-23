using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Imaging;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Management.Deployment;
using Windows.Media.Core;
using Windows.Storage.Streams;

namespace LocalMorph.App.Platforms.Windows;

/// <summary>
/// HEIC/HEIF decoding through the Windows Imaging Component. The container parser ships as the free
/// Microsoft Store package "HEIF Image Extensions"; the HEVC tiles inside most iPhone photos additionally
/// need an HEVC decoder ("HEVC Video Extensions", usually preinstalled by the device manufacturer).
/// </summary>
public sealed class WindowsImageCodec : IPlatformImageCodec
{
    private const string HeifPackageFamily = "Microsoft.HEIFImageExtension_8wekyb3d8bbwe";
    public const string HevcStoreProductId = "9N4WGH0Z6VHQ";
    private const int WincodecComponentNotFound = unchecked((int)0x88982F50);
    private const int MfCodecNotFound = unchecked((int)0xC00D5212);

    private static readonly string[] CopiedMetadata =
    [
        "System.Photo.DateTaken", "System.Photo.CameraManufacturer", "System.Photo.CameraModel", "System.Photo.ExposureTime",
        "System.Photo.FNumber", "System.Photo.ISOSpeed", "System.Photo.FocalLength", "System.Photo.LensModel"
    ];

    public ToolInfo? Probe()
    {
        BitmapCodecInformation? heif;
        try
        {
            heif = BitmapDecoder.GetDecoderInformationEnumerator().FirstOrDefault(info => info.CodecId == BitmapDecoder.HeifDecoderId);
        }
        catch
        {
            return null;
        }

        if (heif is null) return null;

        var version = PackageVersion() ?? "installed";
        var notes = HasHevcDecoder() switch
        {
            false => "HEVC Video Extensions not found — most iPhone HEIC photos also need it (free \"from Device Manufacturer\" edition in the Microsoft Store).",
            _ => "Windows Imaging Component decodes HEIC/HEIF photos in-process; no extra download needed for PNG, JPEG, BMP, TIFF, or GIF output."
        };
        return new ToolInfo(ToolKind.WindowsHeif, heif.FriendlyName, version, ToolSource.System, notes);
    }

    public async Task ConvertAsync(SourceFile source, OutputFormat format, ConversionOptions options, string outputPath, CancellationToken token)
    {
        if (!PlatformImageCodec.EncodableFormats.Contains(format.Id)) throw new InvalidOperationException($"Windows Imaging cannot write {format.DisplayName}.");

        try
        {
            await using var inputFile = File.OpenRead(source.Path);
            using var input = inputFile.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(token);

            var width = decoder.OrientedPixelWidth;
            var height = decoder.OrientedPixelHeight;
            var transform = new BitmapTransform();
            if (options.TargetHeight is { } maxHeight && height > maxHeight && maxHeight > 0)
            {
                // Only ever shrink, matching the ImageMagick "x{height}>" behaviour.
                var scale = maxHeight / (double)height;
                width = (uint)Math.Max(1, Math.Round(width * scale));
                height = (uint)maxHeight;
                transform.ScaledWidth = width;
                transform.ScaledHeight = height;
                transform.InterpolationMode = BitmapInterpolationMode.Fant;
            }

            transform.Rotation = options.Rotation switch
            {
                90 => BitmapRotation.Clockwise90Degrees,
                180 => BitmapRotation.Clockwise180Degrees,
                270 => BitmapRotation.Clockwise270Degrees,
                _ => BitmapRotation.None
            };
            var swapped = options.Rotation is 90 or 270;
            var outputWidth = swapped ? height : width;
            var outputHeight = swapped ? width : height;

            // JPEG/BMP/GIF have no alpha channel; PNG/TIFF keep it.
            var alpha = format.Id is "jpg" or "bmp" or "gif-still" ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied;
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, alpha, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(token);
            var bytes = pixels.DetachPixelData();

            IDictionary<string, BitmapTypedValue>? metadata = null;
            if (!options.StripMetadata && format.Id is "jpg" or "tiff") metadata = await ReadMetadataAsync(decoder, token);

            token.ThrowIfCancellationRequested();
            await using var outputFile = File.Create(outputPath);
            using var output = outputFile.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(EncoderIdFor(format.Id), output, EncoderOptionsFor(format.Id, options)).AsTask(token);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, alpha, outputWidth, outputHeight, decoder.DpiX > 0 ? decoder.DpiX : 96, decoder.DpiY > 0 ? decoder.DpiY : 96, bytes);

            if (metadata is { Count: > 0 })
            {
                try { await encoder.BitmapProperties.SetPropertiesAsync(metadata).AsTask(token); }
                catch { /* metadata is best effort; the pixels matter */ }
            }

            await encoder.FlushAsync().AsTask(token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (COMException ex) when (ex.HResult is WincodecComponentNotFound or MfCodecNotFound)
        {
            throw new InvalidOperationException("Windows could not decode this HEIC photo: the HEVC decoder is missing. Install \"HEVC Video Extensions\" from the Microsoft Store, or install ImageMagick.", ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Windows Imaging could not convert this photo: {ex.Message}", ex);
        }
    }

    private static Guid EncoderIdFor(string formatId) => formatId switch
    {
        "png" => BitmapEncoder.PngEncoderId,
        "jpg" => BitmapEncoder.JpegEncoderId,
        "bmp" => BitmapEncoder.BmpEncoderId,
        "tiff" => BitmapEncoder.TiffEncoderId,
        "gif-still" => BitmapEncoder.GifEncoderId,
        _ => throw new InvalidOperationException($"No Windows Imaging encoder for {formatId}.")
    };

    private static BitmapPropertySet EncoderOptionsFor(string formatId, ConversionOptions options)
    {
        var set = new BitmapPropertySet();
        switch (formatId)
        {
            case "jpg":
                set.Add("ImageQuality", new BitmapTypedValue(Math.Clamp(options.Quality, 1, 100) / 100f, PropertyType.Single));
                break;
            case "tiff":
                set.Add("TiffCompressionMethod", new BitmapTypedValue((byte)TiffCompressionMode.Lzw, PropertyType.UInt8));
                break;
        }
        return set;
    }

    private static async Task<IDictionary<string, BitmapTypedValue>?> ReadMetadataAsync(BitmapDecoder decoder, CancellationToken token)
    {
        try
        {
            var values = await decoder.BitmapProperties.GetPropertiesAsync(CopiedMetadata).AsTask(token);
            return values.Count == 0 ? null : values.ToDictionary(pair => pair.Key, pair => pair.Value);
        }
        catch
        {
            return null;
        }
    }

    private static string? PackageVersion()
    {
        try
        {
            var package = new PackageManager().FindPackagesForUser(string.Empty, HeifPackageFamily).FirstOrDefault();
            if (package is null) return null;
            var version = package.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Null when the query itself fails; callers treat that as "unknown" rather than missing.</summary>
    private static bool? HasHevcDecoder()
    {
        try
        {
            var codecs = new CodecQuery().FindAllAsync(CodecKind.Video, CodecCategory.Decoder, CodecSubtypes.VideoFormatHevc).AsTask().GetAwaiter().GetResult();
            return codecs.Count > 0;
        }
        catch
        {
            return null;
        }
    }
}
