using LocalMorph.Core.Engines;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Imaging;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;
using Xunit;

namespace LocalMorph.Core.Tests;

public sealed class FormatCatalogTests
{
    [Fact]
    public void Every_format_has_unique_id_and_known_engine()
    {
        var ids = FormatCatalog.All.Select(format => format.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(FormatCatalog.All, format => Assert.NotEmpty(format.Engines));
        Assert.All(FormatCatalog.All, format => Assert.False(string.IsNullOrWhiteSpace(format.Extension)));
    }

    [Fact]
    public void Every_preset_points_at_a_real_format_and_valid_options()
    {
        foreach (var preset in Presets.All)
        {
            var format = preset.Format;
            Assert.Null(preset.Options.Validate(format));
            Assert.All(preset.ForSources, category => Assert.Contains(category, format.AcceptsSources));
        }
    }

    [Fact]
    public void Video_sources_get_video_audio_and_image_targets_but_not_documents()
    {
        var formats = FormatCatalog.ForSource(MediaCategory.Video).ToList();
        Assert.Contains(formats, format => format.Id == "mp4-h264");
        Assert.Contains(formats, format => format.Id == "mp3");
        Assert.Contains(formats, format => format.Id == "jpg");
        Assert.DoesNotContain(formats, format => format.Id == "docx");
        Assert.DoesNotContain(formats, format => format.Id == "heic");
    }

    [Fact]
    public void Audio_sources_only_get_audio_targets()
    {
        var formats = FormatCatalog.ForSource(MediaCategory.Audio).ToList();
        Assert.All(formats, format => Assert.Equal(MediaCategory.Audio, format.Category));
        Assert.DoesNotContain(formats, format => format.Id == "audio-copy");
    }

    [Fact]
    public void Document_flavors_filter_targets()
    {
        var spreadsheet = FormatCatalog.ForSource(MediaCategory.Document, DocumentFlavor.Spreadsheet).Select(format => format.Id).ToList();
        Assert.Contains("xlsx", spreadsheet);
        Assert.Contains("pdf", spreadsheet);
        Assert.DoesNotContain("docx", spreadsheet);
        Assert.DoesNotContain("pptx", spreadsheet);

        var pdf = FormatCatalog.ForSource(MediaCategory.Document, DocumentFlavor.Pdf).Select(format => format.Id).ToList();
        Assert.Contains("pdf-compress", pdf);
        Assert.Contains("doc-png", pdf);
        Assert.DoesNotContain("pdf", pdf);
    }

    [Fact]
    public void Batch_intersection_keeps_only_shared_targets()
    {
        var shared = FormatCatalog.ForSources([(MediaCategory.Video, DocumentFlavor.None), (MediaCategory.Audio, DocumentFlavor.None)]).Select(format => format.Id).ToList();
        Assert.Contains("mp3", shared);
        Assert.Contains("flac", shared);
        Assert.DoesNotContain("mp4-h264", shared);
        Assert.DoesNotContain("audio-copy", shared);
    }

    [Fact]
    public void Resolve_engine_prefers_imagemagick_for_heic_when_installed()
    {
        var png = TestData.Format("png");
        Assert.Equal(EngineKind.ImageMagick, FormatCatalog.ResolveEngine(png, @"C:\photos\IMG_0001.heic", TestData.Tools(extra: ToolKind.ImageMagick)));
        Assert.Equal(EngineKind.Ffmpeg, FormatCatalog.ResolveEngine(png, @"C:\photos\IMG_0001.heic", TestData.Tools()));
        Assert.Equal(EngineKind.Ffmpeg, FormatCatalog.ResolveEngine(png, @"C:\photos\IMG_0001.jpg", TestData.Tools(extra: ToolKind.ImageMagick)));
    }

    [Fact]
    public void Resolve_engine_returns_null_and_lists_missing_tools_when_nothing_can_do_it()
    {
        var heic = TestData.Format("heic");
        var tools = TestData.Tools();
        Assert.Null(FormatCatalog.ResolveEngine(heic, @"C:\photos\a.png", tools));
        Assert.Equal([ToolKind.ImageMagick], FormatCatalog.MissingToolsFor(heic, tools).ToArray());

        var docx = TestData.Format("docx");
        Assert.Null(FormatCatalog.ResolveEngine(docx, @"C:\docs\a.odt", tools));
        Assert.Equal(EngineKind.LibreOffice, FormatCatalog.ResolveEngine(docx, @"C:\docs\a.odt", TestData.Tools(extra: ToolKind.LibreOffice)));
        Assert.Equal(EngineKind.Pandoc, FormatCatalog.ResolveEngine(docx, @"C:\docs\notes.md", TestData.Tools(extra: [ToolKind.LibreOffice, ToolKind.Pandoc])));
    }

    [Fact]
    public void Format_requiring_missing_encoder_is_unavailable()
    {
        var noWebp = new FfmpegCapabilities(new HashSet<string> { "libx264", "aac" }, new HashSet<string>(), [], null);
        var tools = TestData.Tools(noWebp);
        Assert.Null(FormatCatalog.ResolveEngine(TestData.Format("webp"), @"C:\a.png", tools));
        Assert.Equal(EngineKind.Ffmpeg, FormatCatalog.ResolveEngine(TestData.Format("mp4-h264"), @"C:\a.mp4", tools));
    }

    [Fact]
    public void Compiled_but_broken_hardware_encoder_does_not_make_format_available()
    {
        // h264_nvenc is compiled in but failed its test encode (not in WorkingHardwareEncoders) and there is no libx264.
        var brokenNvenc = new FfmpegCapabilities(new HashSet<string> { "h264_nvenc", "aac" }, new HashSet<string> { "cuda" }, [], null);
        Assert.Null(FormatCatalog.ResolveEngine(TestData.Format("mp4-h264"), @"C:\a.mp4", TestData.Tools(brokenNvenc)));

        var workingNvenc = new FfmpegCapabilities(new HashSet<string> { "h264_nvenc", "aac" }, new HashSet<string> { "cuda" },
            [new HardwareEncoder("h264", "h264_nvenc", HardwareVendor.Nvidia, "NVIDIA NVENC")], null);
        Assert.Equal(EngineKind.Ffmpeg, FormatCatalog.ResolveEngine(TestData.Format("mp4-h264"), @"C:\a.mp4", TestData.Tools(workingNvenc)));
    }

    [Fact]
    public void Pdf_is_libreoffice_only_because_pandoc_needs_an_external_pdf_engine()
    {
        var pdf = TestData.Format("pdf");
        Assert.Equal([EngineKind.LibreOffice], pdf.Engines);
        Assert.Null(FormatCatalog.ResolveEngine(pdf, @"C:\docs\notes.md", TestData.Tools(extra: ToolKind.Pandoc)));
        Assert.Equal(EngineKind.LibreOffice, FormatCatalog.ResolveEngine(pdf, @"C:\docs\notes.md", TestData.Tools(extra: ToolKind.LibreOffice)));
    }

    [Theory]
    [InlineData("movie.MKV", MediaCategory.Video)]
    [InlineData("song.m4a", MediaCategory.Audio)]
    [InlineData("photo.HEIC", MediaCategory.Image)]
    [InlineData("deck.pptx", MediaCategory.Document)]
    [InlineData("mystery.xyz", MediaCategory.Unknown)]
    public void Classifier_is_case_insensitive(string name, MediaCategory expected) =>
        Assert.Equal(expected, SourceClassifier.Classify(name));

    [Fact]
    public void Options_validation_catches_bad_values()
    {
        var mp4 = TestData.Format("mp4-h264");
        Assert.Null(new ConversionOptions().Validate(mp4));
        Assert.NotNull(new ConversionOptions { Quality = 0 }.Validate(mp4));
        Assert.NotNull(new ConversionOptions { TrimStartSeconds = 10, TrimEndSeconds = 5 }.Validate(mp4));
        Assert.NotNull(new ConversionOptions { Rotation = 45 }.Validate(mp4));
        Assert.NotNull(new ConversionOptions { TargetSizeMegabytes = 10 }.Validate(TestData.Format("webm-vp9")));
        Assert.Null(new ConversionOptions { TargetSizeMegabytes = 10 }.Validate(mp4));
    }
}

public sealed class OtherEngineTests
{
    [Fact]
    public void ImageMagick_heic_to_jpeg_flattens_resizes_and_sets_quality()
    {
        var source = TestData.Image(@"C:\photos\IMG.heic");
        var args = string.Join(' ', ImageMagickEngine.BuildArguments(source, TestData.Format("jpg"), new ConversionOptions { Quality = 85, TargetHeight = 1080 }, @"C:\out\IMG.jpg"));
        Assert.StartsWith(@"C:\photos\IMG.heic -auto-orient", args);
        Assert.Contains("-alpha remove", args);
        Assert.Contains("-resize x1080>", args);
        Assert.Contains("-quality 85", args);
        Assert.EndsWith(@"JPEG:C:\out\IMG.jpg", args);
    }

    [Fact]
    public void ImageMagick_ico_generates_multiple_sizes_and_pdf_rasterizes_first_page()
    {
        var ico = string.Join(' ', ImageMagickEngine.BuildArguments(TestData.Image(), TestData.Format("ico"), new ConversionOptions(), @"C:\out\fav.ico"));
        Assert.Contains("icon:auto-resize=256,128,64,48,32,16", ico);

        var pdf = new SourceFile(@"C:\docs\file.pdf", 1, MediaCategory.Document, DocumentFlavor.Pdf, null);
        var png = string.Join(' ', ImageMagickEngine.BuildArguments(pdf, TestData.Format("doc-png"), new ConversionOptions(), @"C:\out\page.png"));
        Assert.Contains("-density 200", png);
        Assert.Contains(@"C:\docs\file.pdf[0]", png);
    }

    [Fact]
    public void LibreOffice_uses_headless_isolated_profile_and_flavor_specific_pdf_filter()
    {
        var args = LibreOfficeEngine.BuildArguments(TestData.Document(@"C:\docs\budget.xlsx"), TestData.Format("pdf"), new ConversionOptions(), @"C:\tmp\out", @"C:\tmp\profile");
        var text = string.Join(' ', args);
        Assert.Contains("--headless", text);
        Assert.Contains("-env:UserInstallation=file:///C:/tmp/profile/", text);
        Assert.Contains("--convert-to pdf:calc_pdf_Export", text);
        Assert.Contains(@"--outdir C:\tmp\out", text);
        Assert.Equal(@"C:\docs\budget.xlsx", args[^1]);
    }

    [Fact]
    public void Pandoc_maps_markdown_and_html_flavors()
    {
        var md = string.Join(' ', PandocEngine.BuildArguments(TestData.Document(@"C:\docs\a.docx"), TestData.Format("md"), @"C:\out\a.md"));
        Assert.Contains("--to gfm", md);
        Assert.Contains(@"--output C:\out\a.md", md);
        var html = string.Join(' ', PandocEngine.BuildArguments(TestData.Document(@"C:\docs\a.md"), TestData.Format("html"), @"C:\out\a.html"));
        Assert.Contains("--to html5", html);
        Assert.Contains("--standalone", html);
    }

    [Fact]
    public void Ghostscript_quality_maps_to_pdf_presets()
    {
        var pdf = new SourceFile(@"C:\docs\big.pdf", 1, MediaCategory.Document, DocumentFlavor.Pdf, null);
        Assert.Contains("-dPDFSETTINGS=/screen", string.Join(' ', GhostscriptEngine.BuildArguments(pdf, TestData.Format("pdf-compress"), new ConversionOptions { Quality = 30 }, @"C:\out\small.pdf")));
        Assert.Contains("-dPDFSETTINGS=/ebook", string.Join(' ', GhostscriptEngine.BuildArguments(pdf, TestData.Format("pdf-compress"), new ConversionOptions { Quality = 60 }, @"C:\out\small.pdf")));
        Assert.Contains("-sDEVICE=png16m", string.Join(' ', GhostscriptEngine.BuildArguments(pdf, TestData.Format("doc-png"), new ConversionOptions(), @"C:\out\p.png")));
    }
}

public sealed class ToolTests
{
    [Fact]
    public void Encoder_list_parsing_picks_names_only()
    {
        const string sample = """
            Encoders:
             V..... = Video
             ------
             V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
             V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
             A....D aac                  AAC (Advanced Audio Coding)
             S..... ass                  ASS (Advanced SubStation Alpha) subtitle
            """;
        var encoders = FfmpegCapabilities.ParseEncoders(sample);
        Assert.Equal(["aac", "ass", "h264_nvenc", "libx264"], encoders.Order().ToArray());
    }

    [Fact]
    public void Hwaccel_parsing_reads_methods()
    {
        var set = FfmpegCapabilities.ParseHardwareAccelerations("Hardware acceleration methods:\ncuda\nqsv\nd3d11va\n");
        Assert.Equal(["cuda", "d3d11va", "qsv"], set.Order().ToArray());
    }

    [Theory]
    [InlineData(ToolKind.Ffmpeg, "ffmpeg version 7.1-full_build-www.gyan.dev Copyright (c) 2000-2024", "7.1")]
    [InlineData(ToolKind.Ffmpeg, "ffmpeg version n7.0.2 Copyright", "n7.0.2")]
    [InlineData(ToolKind.ImageMagick, "Version: ImageMagick 7.1.1-43 Q16-HDRI x64", "7.1.1-43")]
    [InlineData(ToolKind.LibreOffice, "LibreOffice 24.8.4.2 bb3cfa12c7b1bf994ecc5649a80400d06cd71002", "24.8.4.2")]
    [InlineData(ToolKind.Pandoc, "pandoc 3.6.2", "3.6.2")]
    [InlineData(ToolKind.Pandoc, null, "version unavailable")]
    public void Versions_are_shortened(ToolKind kind, string? raw, string expected) =>
        Assert.Equal(expected, ToolCatalog.ShortenVersion(kind, raw));

    [Fact]
    public void Install_command_targets_platform_package_manager()
    {
        var command = ToolCatalog.InstallCommand(ToolKind.ImageMagick);
        if (OperatingSystem.IsWindows()) Assert.Equal("winget install --id ImageMagick.ImageMagick -e", command);
        else Assert.Contains("imagemagick", command);
    }

    [Fact]
    public void Windows_heif_codec_is_a_store_tool_only_on_windows()
    {
        var descriptor = ToolCatalog.All.FirstOrDefault(tool => tool.Kind == ToolKind.WindowsHeif);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Null(descriptor);
            return;
        }

        Assert.NotNull(descriptor);
        Assert.True(descriptor.IsStoreCodec);
        Assert.Empty(descriptor.ExecutableNames);
        Assert.Equal("ms-windows-store://pdp/?productid=9PMMSR1CGPWG", ToolCatalog.InstallCommand(ToolKind.WindowsHeif));
        Assert.Equal(ToolCatalog.InstallCommand(ToolKind.WindowsHeif), ToolCatalog.StoreUri(descriptor));
        Assert.Contains("9pmmsr1cgpwg", descriptor.WebsiteUrl);
    }
}

/// <summary>Stand-in for the Windows Imaging Component: "decodes" by writing a small file.</summary>
internal sealed class FakeImageCodec(bool installed) : IPlatformImageCodec
{
    public List<(string Source, string Output, string Format)> Calls { get; } = [];

    public ToolInfo? Probe() => installed
        ? new ToolInfo(ToolKind.WindowsHeif, "Microsoft HEIF Decoder", "1.2.3.0", ToolSource.System, "HEVC ok")
        : null;

    public Task ConvertAsync(SourceFile source, OutputFormat format, ConversionOptions options, string outputPath, CancellationToken token)
    {
        Calls.Add((source.Path, outputPath, format.Id));
        File.WriteAllBytes(outputPath, [0x89, 0x50, 0x4E, 0x47]);
        return Task.CompletedTask;
    }
}

public sealed class WindowsImagingEngineTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localmorph-tests", Guid.NewGuid().ToString("N"));
    private readonly IPlatformImageCodec? previous = PlatformImageCodec.Current;

    public WindowsImagingEngineTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        PlatformImageCodec.Current = previous;
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static ToolInventory WithCodec(params ToolKind[] extra) => TestData.Tools(extra: [ToolKind.WindowsHeif, .. extra]);

    [Fact]
    public void Store_codec_lookup_uses_platform_codec_probe_not_disk()
    {
        if (!OperatingSystem.IsWindows()) return;
        PlatformImageCodec.Current = null;
        Assert.Null(ToolLocator.Find(ToolKind.WindowsHeif));

        PlatformImageCodec.Current = new FakeImageCodec(installed: false);
        Assert.Null(ToolLocator.Find(ToolKind.WindowsHeif));

        PlatformImageCodec.Current = new FakeImageCodec(installed: true);
        var info = ToolLocator.Find(ToolKind.WindowsHeif);
        Assert.NotNull(info);
        Assert.Equal(ToolSource.System, info.Source);
        Assert.Equal("HEVC ok", info.Notes);
    }

    [Fact]
    public void Heic_sources_prefer_the_windows_codec_when_installed()
    {
        var png = TestData.Format("png");
        Assert.Equal(EngineKind.WindowsImaging, FormatCatalog.ResolveEngine(png, @"C:\photos\IMG_0001.heic", WithCodec()));
        Assert.Equal(EngineKind.WindowsImaging, FormatCatalog.ResolveEngine(TestData.Format("jpg"), @"C:\photos\IMG_0001.HEIF", WithCodec(ToolKind.ImageMagick)));
        // Outputs WIC cannot write still go to ImageMagick/FFmpeg.
        Assert.Equal(EngineKind.ImageMagick, FormatCatalog.ResolveEngine(TestData.Format("webp"), @"C:\photos\IMG_0001.heic", WithCodec(ToolKind.ImageMagick)));
        Assert.Equal(EngineKind.Ffmpeg, FormatCatalog.ResolveEngine(TestData.Format("webp"), @"C:\photos\IMG_0001.heic", WithCodec()));
        // The codec is only for HEIF inputs; ordinary images keep their usual engines.
        Assert.Equal(EngineKind.Ffmpeg, FormatCatalog.ResolveEngine(png, @"C:\photos\IMG_0001.jpg", WithCodec()));
        Assert.Equal(EngineKind.Ffmpeg, FormatCatalog.ResolveEngine(png, @"C:\photos\IMG_0001.heic", TestData.Tools()));
    }

    [Fact]
    public void Missing_decoders_are_reported_for_heif_only()
    {
        var expected = OperatingSystem.IsWindows() ? ToolKind.WindowsHeif : ToolKind.ImageMagick;
        Assert.Equal([expected], FormatCatalog.MissingDecodersFor(@"C:\photos\a.heic", TestData.Tools()).ToArray());
        Assert.Empty(FormatCatalog.MissingDecodersFor(@"C:\photos\a.heic", TestData.Tools(extra: ToolKind.ImageMagick)));
        Assert.Empty(FormatCatalog.MissingDecodersFor(@"C:\photos\a.heic", WithCodec()));
        Assert.Empty(FormatCatalog.MissingDecodersFor(@"C:\photos\a.png", TestData.Tools()));
    }

    [Fact]
    public async Task In_process_step_runs_the_codec_and_completes_the_job()
    {
        var codec = new FakeImageCodec(installed: true);
        PlatformImageCodec.Current = codec;
        var source = Path.Combine(root, "IMG_0001.heic");
        File.WriteAllBytes(source, [1, 2, 3]);
        var output = Path.Combine(root, "IMG_0001.png");
        var job = new ConversionJob(TestData.Image(source), TestData.Format("png"), new ConversionOptions { TargetHeight = 1080, Rotation = 90 }, output, EngineKind.WindowsImaging);

        await ConversionRunner.RunAsync(job, WithCodec(), Path.Combine(root, "work"));

        Assert.Equal(JobState.Completed, job.State);
        Assert.Single(codec.Calls);
        Assert.Equal((source, output, "png"), codec.Calls[0]);
        Assert.Contains("windows-imaging", job.CommandLine);
        Assert.Contains("max-height=1080", job.CommandLine);
        Assert.Contains("rotate=90", job.CommandLine);
        Assert.Equal(4, job.Result!.OutputBytes);
    }

    [Fact]
    public async Task Codec_failure_marks_the_job_failed_and_removes_partial_output()
    {
        PlatformImageCodec.Current = new ThrowingCodec();
        var source = Path.Combine(root, "broken.heic");
        File.WriteAllBytes(source, [1]);
        var output = Path.Combine(root, "broken.jpg");
        var job = new ConversionJob(TestData.Image(source), TestData.Format("jpg"), new ConversionOptions(), output, EngineKind.WindowsImaging);

        await ConversionRunner.RunAsync(job, WithCodec(), Path.Combine(root, "work"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("HEVC decoder is missing", job.Error);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Planning_without_the_codec_fails_clearly()
    {
        PlatformImageCodec.Current = new FakeImageCodec(installed: true);
        var job = new ConversionJob(TestData.Image(@"C:\photos\a.heic"), TestData.Format("png"), new ConversionOptions(), @"C:\photos\a.png", EngineKind.WindowsImaging);
        var ex = Assert.Throws<InvalidOperationException>(() => new WindowsImagingEngine().Plan(job, TestData.Tools(), root));
        Assert.Contains("HEIF Image Extensions", ex.Message);

        var webp = new ConversionJob(TestData.Image(@"C:\photos\a.heic"), TestData.Format("webp"), new ConversionOptions(), @"C:\photos\a.webp", EngineKind.WindowsImaging);
        Assert.Throws<InvalidOperationException>(() => new WindowsImagingEngine().Plan(webp, WithCodec(), root));
    }

    private sealed class ThrowingCodec : IPlatformImageCodec
    {
        public ToolInfo? Probe() => null;

        public Task ConvertAsync(SourceFile source, OutputFormat format, ConversionOptions options, string outputPath, CancellationToken token)
        {
            File.WriteAllBytes(outputPath, [0xFF]);
            throw new InvalidOperationException("Windows could not decode this HEIC photo: the HEVC decoder is missing.");
        }
    }
}

public sealed class OutputNamingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "localmorph-tests", Guid.NewGuid().ToString("N"));

    public OutputNamingTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Rename_policy_appends_counter_when_file_exists()
    {
        var source = Path.Combine(root, "clip.mov");
        File.WriteAllText(source, "x");
        File.WriteAllText(Path.Combine(root, "clip.mp4"), "x");
        var output = OutputNaming.BuildOutputPath(source, TestData.Format("mp4-h264"), null, string.Empty, OverwritePolicy.Rename);
        Assert.Equal(Path.Combine(root, "clip (2).mp4"), output);
    }

    [Fact]
    public void Output_never_overwrites_the_source_file()
    {
        var source = Path.Combine(root, "clip.mp4");
        File.WriteAllText(source, "x");
        var output = OutputNaming.BuildOutputPath(source, TestData.Format("mp4-h264"), null, string.Empty, OverwritePolicy.Overwrite);
        Assert.NotEqual(source, output);
        Assert.Equal(Path.Combine(root, "clip (2).mp4"), output);
    }

    [Fact]
    public void Batch_reservations_avoid_duplicate_outputs()
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var first = OutputNaming.BuildOutputPath(Path.Combine(root, "a", "photo.heic"), TestData.Format("jpg"), root, string.Empty, OverwritePolicy.Rename, reserved);
        var second = OutputNaming.BuildOutputPath(Path.Combine(root, "b", "photo.png"), TestData.Format("jpg"), root, string.Empty, OverwritePolicy.Rename, reserved);
        Assert.Equal(Path.Combine(root, "photo.jpg"), first);
        Assert.Equal(Path.Combine(root, "photo (2).jpg"), second);
    }

    [Fact]
    public void Reserved_sources_are_never_used_as_outputs_even_when_overwriting()
    {
        var mp4 = Path.Combine(root, "clip.mp4");
        var mkv = Path.Combine(root, "clip.mkv");
        File.WriteAllText(mp4, "x");
        File.WriteAllText(mkv, "x");
        var reserved = new HashSet<string>([Path.GetFullPath(mp4), Path.GetFullPath(mkv)], StringComparer.OrdinalIgnoreCase);
        var output = OutputNaming.BuildOutputPath(mp4, TestData.Format("mkv-copy"), null, string.Empty, OverwritePolicy.Overwrite, reserved);
        Assert.Equal(Path.Combine(root, "clip (2).mkv"), output);
    }

    [Fact]
    public void Preferred_path_uses_codec_specific_extension_for_audio_copy()
    {
        var webm = new SourceFile(Path.Combine(root, "clip.webm"), 1, MediaCategory.Video, DocumentFlavor.None,
            new LocalMorph.Bridge.SourceMediaInfo(LocalMorph.Bridge.SourceMediaKind.Video, 10, 640, 360, 30, 48000, 2, "vp9", "vorbis", null, "webm"));
        File.WriteAllText(Path.Combine(root, "clip.ogg"), "x");
        var output = OutputNaming.BuildOutputPath(webm.Path, TestData.Format("audio-copy"), null, string.Empty, OverwritePolicy.Rename, null, webm);
        Assert.Equal(Path.Combine(root, "clip (2).ogg"), output);
    }

    [Fact]
    public void Preferred_path_is_the_unsuffixed_name_skip_policy_checks()
    {
        var source = Path.Combine(root, "clip.mp4");
        File.WriteAllText(source, "x");
        var preferred = OutputNaming.PreferredOutputPath(source, TestData.Format("mp3"), null, string.Empty);
        Assert.Equal(Path.Combine(root, "clip.mp3"), preferred);
        Assert.EndsWith(".mp3", preferred); // regression: the old check built "clipmp3" (missing dot)
    }

    [Fact]
    public void Suffix_and_sanitization_apply()
    {
        var output = OutputNaming.BuildOutputPath(Path.Combine(root, "we:ird<name>.mov"), TestData.Format("mp3"), root, "-audio", OverwritePolicy.Rename);
        Assert.Equal(Path.Combine(root, "we-ird-name--audio.mp3"), output);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }
}
