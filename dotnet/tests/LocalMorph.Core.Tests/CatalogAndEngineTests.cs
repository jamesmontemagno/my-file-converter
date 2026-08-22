using LocalMorph.Core.Engines;
using LocalMorph.Core.Formats;
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
