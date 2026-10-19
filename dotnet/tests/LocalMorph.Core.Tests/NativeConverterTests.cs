using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Native;
using LocalMorph.Core.Tools;
using Xunit;

namespace LocalMorph.Core.Tests;

[CollectionDefinition(nameof(NativeConverterCollection), DisableParallelization = true)]
public sealed class NativeConverterCollection;

[Collection(nameof(NativeConverterCollection))]
public sealed class NativeConverterTests : IDisposable
{
    private sealed class FakeNativeConverter : INativeConverter
    {
        public IReadOnlySet<string> WritableFormats { get; init; } = new HashSet<string>
        {
            "png", "jpg", "tiff", "heic", "ico", "pdf-image", "doc-png", "doc-jpg", "pdf", "rtf", "html", "txt"
        };

        public IReadOnlySet<string> ReadableImageExtensions { get; init; } = new HashSet<string> { ".heic", ".jpg", ".png", ".webp", ".cr2" };

        public ToolInfo? Probe() => new(ToolKind.AppleNative, "Built into macOS", "macOS 15", ToolSource.System);

        public (int Width, int Height)? ReadImageSize(string path) => (4032, 3024);

        public Task ConvertAsync(SourceFile source, OutputFormat format, ConversionOptions options, string outputPath, CancellationToken token) =>
            Task.CompletedTask;
    }

    private readonly INativeConverter? previous = NativeConverter.Current;

    public NativeConverterTests() => NativeConverter.Current = new FakeNativeConverter();

    public void Dispose() => NativeConverter.Current = previous;

    [Theory]
    [InlineData("/p/IMG_1.heic", "png", true)]
    [InlineData("/p/IMG_1.webp", "jpg", true)]
    [InlineData("/p/raw.cr2", "tiff", true)]
    [InlineData("/p/a.png", "avif", false)] // encoder not present on this "device"
    [InlineData("/p/a.bmp", "png", false)] // decoder not reported
    [InlineData("/p/a.png", "pdf", false)] // image sources only get image outputs
    [InlineData("/d/a.pdf", "doc-png", true)]
    [InlineData("/d/a.pdf", "png", false)]
    [InlineData("/d/a.pdf", "txt", false)]
    [InlineData("/d/notes.md", "pdf", true)]
    [InlineData("/d/page.html", "rtf", true)]
    [InlineData("/d/a.rtf", "txt", true)]
    [InlineData("/d/a.docx", "pdf", false)]
    [InlineData("/v/clip.mp4", "png", false)]
    public void CanConvert_routes_by_source_category(string path, string formatId, bool expected) =>
        Assert.Equal(expected, NativeConverter.CanConvert(NativeConverter.Current, path, TestData.Format(formatId)));

    [Fact]
    public void CanConvert_is_false_without_a_converter() =>
        Assert.False(NativeConverter.CanConvert(null, "/p/a.heic", TestData.Format("png")));

    [Fact]
    public void Still_images_prefer_native_when_available()
    {
        var tools = TestData.Tools(extra: [ToolKind.AppleNative, ToolKind.ImageMagick]);
        Assert.Equal(EngineKind.AppleNative, FormatCatalog.ResolveEngine(TestData.Format("png"), "/p/IMG_1.heic", tools));
        Assert.Equal(EngineKind.AppleNative, FormatCatalog.ResolveEngine(TestData.Format("heic"), "/p/a.png", tools));
    }

    [Fact]
    public void Native_is_skipped_when_the_tool_is_not_in_the_inventory()
    {
        var tools = TestData.Tools();
        Assert.NotEqual(EngineKind.AppleNative, FormatCatalog.ResolveEngine(TestData.Format("png"), "/p/a.jpg", tools));
        Assert.Null(FormatCatalog.ResolveEngine(TestData.Format("heic"), "/p/a.png", tools));
    }

    [Fact]
    public void Unsupported_native_outputs_fall_back_to_other_engines()
    {
        var tools = TestData.Tools(extra: ToolKind.AppleNative);
        Assert.Equal(EngineKind.Ffmpeg, FormatCatalog.ResolveEngine(TestData.Format("png"), "/p/a.bmp", tools));
    }

    [Fact]
    public void Documents_keep_preferring_installed_document_engines()
    {
        var withPandoc = TestData.Tools(extra: [ToolKind.AppleNative, ToolKind.Pandoc]);
        Assert.Equal(EngineKind.Pandoc, FormatCatalog.ResolveEngine(TestData.Format("html"), "/d/notes.md", withPandoc));

        var nativeOnly = TestData.Tools(extra: ToolKind.AppleNative);
        Assert.Equal(EngineKind.AppleNative, FormatCatalog.ResolveEngine(TestData.Format("pdf"), "/d/notes.md", nativeOnly));
        Assert.Equal(EngineKind.AppleNative, FormatCatalog.ResolveEngine(TestData.Format("doc-png"), "/d/a.pdf", nativeOnly));
        Assert.Null(FormatCatalog.ResolveEngine(TestData.Format("pdf"), "/d/a.docx", nativeOnly));
    }

    [Fact]
    public void Native_heif_support_suppresses_decoder_suggestions()
    {
        Assert.Empty(FormatCatalog.MissingDecodersFor("/p/IMG_1.heic", TestData.Tools(extra: ToolKind.AppleNative)));
    }

    [Fact]
    public void System_frameworks_are_never_suggested_as_installable_tools()
    {
        var missing = FormatCatalog.MissingToolsFor(TestData.Format("heic"), TestData.Tools()).ToArray();
        Assert.DoesNotContain(ToolKind.AppleNative, missing);
        Assert.Contains(ToolKind.ImageMagick, missing);
    }

    [Fact]
    public void Catalog_lists_native_frameworks_only_on_mac()
    {
        var listed = ToolCatalog.All.Any(descriptor => descriptor.Kind == ToolKind.AppleNative && descriptor.IsSystemFramework);
        Assert.Equal(OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst(), listed);
        if (listed) Assert.Equal(string.Empty, ToolCatalog.InstallCommand(ToolKind.AppleNative));
    }

    [Fact]
    public async Task Inspection_uses_the_native_decoder_for_image_size()
    {
        var native = await SourceInspector.InspectAsync("/p/IMG_1.heic", TestData.Tools(extra: ToolKind.AppleNative));
        Assert.Equal(MediaCategory.Image, native.Category);
        Assert.Equal((4032, 3024), (native.Media?.Width, native.Media?.Height));

        var withoutNative = await SourceInspector.InspectAsync("/p/IMG_1.heic", TestData.Tools());
        Assert.Null(withoutNative.Media);
    }
}
