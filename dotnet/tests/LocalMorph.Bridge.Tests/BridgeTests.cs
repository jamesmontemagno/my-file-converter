using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Reflection;
using LocalMorph.Bridge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LocalMorph.Bridge.Tests;

public sealed class BridgeTests : IAsyncLifetime
{
    private readonly string jobRoot = Path.Combine(Directory.GetCurrentDirectory(), ".test-job-root", Guid.NewGuid().ToString("N"));
    private WebApplication? app;
    private HttpClient? client;
    private const string Token = "test-token";

    public async Task InitializeAsync()
    {
        app = BridgeApplication.Create([], new BridgeOptions { Port = 0, Token = Token, JobRoot = jobRoot },
            configure: builder => builder.WebHost.UseTestServer(), discoverFfmpeg: false);
        await app.StartAsync();
        client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (app is not null) await app.DisposeAsync();
        if (Directory.Exists(jobRoot)) Directory.Delete(jobRoot, true);
    }

    [Fact]
    public async Task Health_requires_exact_origin_and_bearer_token()
    {
        using var missingOrigin = await client!.GetAsync("/v1/health");
        Assert.Equal(HttpStatusCode.Forbidden, missingOrigin.StatusCode);

        using var rejected = await Send(HttpMethod.Get, "/v1/health", "https://localmorph.com.evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);

        using var accepted = await Send(HttpMethod.Get, "/v1/health");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal("https://localmorph.com", accepted.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("\"supportedTargets\"", await accepted.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Preflight_accepts_private_network_without_bearer_token()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/jobs");
        request.Headers.Add("Origin", "https://localmorph.com");
        request.Headers.Add("Access-Control-Request-Private-Network", "true");
        using var response = await client!.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Private-Network").Single());
    }

    [Fact]
    public async Task Post_job_rejects_when_ffmpeg_is_not_on_path()
    {
        using var content = MultipartRequest("video/mp4", "video", "result.mp4");
        using var response = await Send(HttpMethod.Post, "/v1/jobs", content: content);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Job_status_uses_the_existing_snake_case_contract()
    {
        var store = app!.Services.GetRequiredService<JobStore>();
        var job = store.Create(jobRoot, Path.Combine(jobRoot, "output.mp4"), "result.mp4");

        using var response = await Send(HttpMethod.Get, $"/v1/jobs/{job.Id}");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\":\"queued\"", json);
        Assert.Contains("\"progressPercent\":null", json);
    }

    [Theory]
    [InlineData("../unsafe.mp4", false)]
    [InlineData("result.mp4", true)]
    public void Request_validation_rejects_paths_and_accepts_plain_names(string outputName, bool expected)
    {
        var request = new ConversionRequest { TargetMime = "video/mp4", MediaType = "video", OutputName = outputName, Quality = 75 };
        Assert.Equal(expected, request.IsValid());
    }

    [Fact]
    public void Command_uses_only_structured_whitelisted_arguments()
    {
        var request = new ConversionRequest { TargetMime = "video/mp4", MediaType = "video", OutputName = "output.mp4", Quality = 75 };
        var command = Ffmpeg.BuildCommand("ffmpeg", "input.bin", "output.mp4", request);
        Assert.Equal(["-hide_banner", "-y", "-i", "input.bin"], command.ArgumentList.Take(4));
        Assert.Contains("-c:v", command.ArgumentList);
        Assert.DoesNotContain(command.ArgumentList, argument => argument.Contains(';'));
        Assert.Equal(
            "ffmpeg -hide_banner -y -i \"input file.bin\" -progress pipe:1 -nostats -c:v libx264 -preset medium -crf 27 -c:a aac -fs 2147483648 output.mp4",
            Ffmpeg.DescribeCommand(Ffmpeg.BuildCommand("ffmpeg", "input file.bin", "output.mp4", request)));
    }

    [Fact]
    public void Command_applies_validated_format_specific_tuning()
    {
        var request = new ConversionRequest
        {
            TargetMime = "video/webm",
            MediaType = "video",
            OutputName = "output.webm",
            Quality = 90,
            Media = new MediaOptions
            {
                VideoEncodingSpeed = "quality",
                VideoFrameRate = 60,
                VideoHeight = 720,
                AudioBitrate = 192,
                AudioSampleRate = 48000
            }
        };

        Assert.True(request.IsValid());
        var command = Ffmpeg.BuildCommand("ffmpeg", "input.bin", "output.webm", request);

        Assert.Contains("-deadline", command.ArgumentList);
        Assert.Contains("best", command.ArgumentList);
        Assert.Contains("-cpu-used", command.ArgumentList);
        Assert.Contains("1", command.ArgumentList);
        Assert.Contains("-b:a", command.ArgumentList);
        Assert.Contains("192k", command.ArgumentList);
        Assert.Contains("-ar", command.ArgumentList);
        Assert.Contains("48000", command.ArgumentList);
        Assert.Contains("-r", command.ArgumentList);
        Assert.Contains("60", command.ArgumentList);
        Assert.Contains("-vf", command.ArgumentList);
        Assert.Contains("scale=-2:720", command.ArgumentList);
    }

    [Fact]
    public void Request_validation_rejects_unrecognized_tuning_values()
    {
        var request = new ConversionRequest
        {
            TargetMime = "audio/wav",
            MediaType = "audio",
            OutputName = "output.wav",
            Media = new MediaOptions { AudioBitrate = 111, WavBitDepth = 12 }
        };

        Assert.False(request.IsValid());
    }

    [Fact]
    public void Progress_parser_reports_bounded_progress_and_completion()
    {
        Assert.Equal(50, Ffmpeg.ParseProgress("out_time_us=5000000", 10_000_000)!.Percent);
        Assert.Equal(100, Ffmpeg.ParseProgress("progress=end", 10_000_000)!.Percent);
    }

    [Fact]
    public void Media_probe_parses_video_metadata_and_image_extensions()
    {
        const string json = """
            {"streams":[{"codec_type":"video","width":1920,"height":1080,"avg_frame_rate":"30000/1001"},{"codec_type":"audio","sample_rate":"48000","channels":2}],"format":{"duration":"12.5"}}
            """;

        var video = MediaProbe.Parse(json, "clip.mp4")!;
        Assert.Equal(SourceMediaKind.Video, video.Kind);
        Assert.Equal(1920, video.Width);
        Assert.Equal(1080, video.Height);
        Assert.Equal(12.5, video.DurationSeconds);
        Assert.Equal(29.97, video.FrameRate!.Value, 2);
        Assert.Equal(48000, video.SampleRate);
        Assert.Equal(2, video.Channels);

        Assert.Equal(SourceMediaKind.Image, MediaProbe.Parse(json, "photo.png")!.Kind);
    }

    [Theory]
    [InlineData("image/png", "output.png")]
    [InlineData("image/jpeg", "output.jpg")]
    [InlineData("image/webp", "output.webp")]
    public void Command_supports_still_image_targets(string targetMime, string outputName)
    {
        var request = new ConversionRequest
        {
            TargetMime = targetMime,
            MediaType = "image",
            OutputName = outputName,
            Quality = 82,
            Image = new ImageOptions { Height = 720 }
        };

        Assert.True(request.IsValid());
        var command = Ffmpeg.BuildCommand("ffmpeg", "input.png", outputName, request);
        Assert.Contains("-frames:v", command.ArgumentList);
        Assert.Contains("scale=-2:720", command.ArgumentList);
    }

    [Fact]
    public void Protocol_version_comes_from_assembly_metadata()
    {
        var expected = typeof(BridgeApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        Assert.Equal(expected, BridgeApplication.Version);
        if (Environment.GetEnvironmentVariable("EXPECTED_BRIDGE_VERSION") is { } releaseVersion)
        {
            Assert.Equal(releaseVersion, BridgeApplication.Version);
        }
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string origin = "https://localmorph.com", HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("Origin", origin);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return await client!.SendAsync(request);
    }

    private static MultipartFormDataContent MultipartRequest(string targetMime, string mediaType, string outputName)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent($$"""{"targetMime":"{{targetMime}}","outputName":"{{outputName}}","mediaType":"{{mediaType}}"}"""), "request");
        content.Add(new ByteArrayContent([1, 2, 3]), "file", "input.bin");
        return content;
    }
}
