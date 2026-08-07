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
    public void Progress_parser_reports_bounded_progress_and_completion()
    {
        Assert.Equal(50, Ffmpeg.ParseProgress("out_time_us=5000000", 10_000_000)!.Percent);
        Assert.Equal(100, Ffmpeg.ParseProgress("progress=end", 10_000_000)!.Percent);
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
