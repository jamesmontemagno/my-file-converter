using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;

namespace LocalMorph.Bridge;

public static class BridgeApplication
{
    public static readonly string Version =
        typeof(BridgeApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "unknown";
    private static readonly string[] SupportedTargets = ["video/mp4", "video/quicktime", "video/webm", "image/gif", "audio/mpeg", "audio/wav"];

    public static WebApplication Create(string[] args, BridgeOptions? suppliedOptions = null, FfmpegInfo? suppliedFfmpeg = null, Action<WebApplicationBuilder>? configure = null, bool discoverFfmpeg = true)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        builder.Logging.ClearProviders();
        var options = suppliedOptions ?? BridgeOptions.FromEnvironment();
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = BridgeOptions.MaxFileBytes);
        builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = BridgeOptions.MaxFileBytes);
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        });
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new FfmpegState(suppliedFfmpeg ?? (discoverFfmpeg ? Ffmpeg.Discover() : null)));
        builder.Services.AddSingleton<JobStore>();
        builder.Services.AddSingleton<JobRunner>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<JobRunner>());
        builder.Services.AddHostedService<JobCleanupService>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (!BridgeOptions.AllowedOrigins.Contains(origin))
            {
                await Error(context, StatusCodes.Status403Forbidden, "origin is not allowed");
                return;
            }

            AddCors(context.Response, origin, context.Request.Headers["Access-Control-Request-Private-Network"] == "true");
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            if (!IsAuthorized(context.Request.Headers.Authorization.ToString(), options.Token))
            {
                await Error(context, StatusCodes.Status401Unauthorized, "missing or invalid bearer token");
                return;
            }
            await next();
        });

        app.MapGet("/v1/health", (FfmpegState ffmpegState) => Results.Json(new
        {
            version = Version,
            ffmpeg = new { available = ffmpegState.Info is not null, version = ffmpegState.Info?.Version },
            supportedTargets = SupportedTargets
        }));

        app.MapPost("/v1/jobs", async (HttpRequest request, JobStore store, JobRunner runner, FfmpegState ffmpegState, BridgeOptions bridgeOptions, CancellationToken token) =>
        {
            if (ffmpegState.Info is null) return ErrorResult(StatusCodes.Status503ServiceUnavailable, "ffmpeg was not found on PATH");
            if (!request.HasFormContentType) return ErrorResult(StatusCodes.Status400BadRequest, "invalid multipart body");
            IFormCollection form;
            try { form = await request.ReadFormAsync(token); }
            catch { return ErrorResult(StatusCodes.Status400BadRequest, "invalid multipart body"); }
            if (form.Files.Count != 1 || form.Files[0].Name != "file" || form["request"].Count != 1 || form.Keys.Count != 1)
                return ErrorResult(StatusCodes.Status400BadRequest, "unexpected or duplicate multipart field");

            ConversionRequest? conversion;
            try
            {
                var json = form["request"][0];
                if (json is null || Encoding.UTF8.GetByteCount(json) > 64 * 1024) throw new JsonException();
                conversion = JsonSerializer.Deserialize<ConversionRequest>(json, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
                });
            }
            catch { return ErrorResult(StatusCodes.Status400BadRequest, "invalid request JSON"); }
            if (conversion is null || !conversion.IsValid()) return ErrorResult(StatusCodes.Status400BadRequest, "unsupported conversion options");
            var file = form.Files[0];
            if (file.Length is <= 0 or > BridgeOptions.MaxFileBytes) return ErrorResult(StatusCodes.Status400BadRequest, "file is empty or exceeds upload limit");

            var directory = Path.Combine(bridgeOptions.JobRoot, Guid.NewGuid().ToString());
            var input = Path.Combine(directory, "input");
            try
            {
                Directory.CreateDirectory(directory);
                await using var output = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
                await using var upload = file.OpenReadStream();
                await upload.CopyToAsync(output, token);
            }
            catch
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                return ErrorResult(StatusCodes.Status400BadRequest, "file is empty or exceeds upload limit");
            }

            var job = store.Create(directory, Path.Combine(directory, $"output.{conversion.Extension}"), conversion.OutputName);
            runner.Enqueue(job, conversion, input);
            return Results.Accepted(value: new { id = job.Id });
        });

        app.MapGet("/v1/jobs/{id:guid}", (Guid id, JobStore store) =>
            store.TryGet(id, out var job) ? Results.Json(job!.Snapshot()) : ErrorResult(StatusCodes.Status404NotFound, "job not found"));

        app.MapDelete("/v1/jobs/{id:guid}", (Guid id, JobStore store) =>
            store.Cancel(id) ? Results.StatusCode(StatusCodes.Status202Accepted) : ErrorResult(StatusCodes.Status404NotFound, "job not found"));

        app.MapGet("/v1/jobs/{id:guid}/events", async (HttpContext context, Guid id, JobStore store) =>
        {
            if (!store.TryGet(id, out var job))
            {
                await Error(context, StatusCodes.Status404NotFound, "job not found");
                return;
            }
            var (current, events) = job!.SubscribeWithCurrent();
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            await WriteSse(context, current, context.RequestAborted);
            await foreach (var update in events.ReadAllAsync(context.RequestAborted))
                await WriteSse(context, update, context.RequestAborted);
        });

        app.MapGet("/v1/jobs/{id:guid}/output", (Guid id, JobStore store) =>
        {
            if (!store.TryGet(id, out var job) || job!.Snapshot().Status != JobStatus.Completed || !File.Exists(job.OutputPath))
                return ErrorResult(StatusCodes.Status404NotFound, "completed output not found");
            if (new FileInfo(job.OutputPath).Length > BridgeOptions.MaxFileBytes)
                return ErrorResult(StatusCodes.Status413PayloadTooLarge, "completed output exceeds size limit");
            return Results.File(job.OutputPath, "application/octet-stream", job.OutputName, enableRangeProcessing: false);
        });

        return app;
    }

    private static bool IsAuthorized(string? authorization, string token)
    {
        if (authorization is null) return false;
        var expected = Encoding.UTF8.GetBytes($"Bearer {token}");
        var actual = Encoding.UTF8.GetBytes(authorization);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static void AddCors(HttpResponse response, string origin, bool privateNetwork)
    {
        response.Headers.AccessControlAllowOrigin = origin;
        response.Headers.AccessControlAllowHeaders = "authorization, content-type";
        response.Headers.AccessControlAllowMethods = "GET, POST, DELETE, OPTIONS";
        response.Headers.Vary = "Origin, Access-Control-Request-Private-Network";
        if (privateNetwork) response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    private static IResult ErrorResult(int status, string message) => Results.Json(new { error = message }, statusCode: status);
    private static Task Error(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }

    private static async Task WriteSse(HttpContext context, JobView job, CancellationToken token)
    {
        var (message, detail) = job.Status switch
        {
            JobStatus.Queued => ("Job queued", (string?)null),
            JobStatus.Running => ("Conversion in progress", null),
            JobStatus.Completed => ("Conversion completed", null),
            JobStatus.Failed => ("Conversion failed", job.Error),
            JobStatus.Canceled => ("Conversion canceled", null),
            _ => throw new InvalidOperationException()
        };
        var json = JsonSerializer.Serialize(new { status = job.Status.ToString().ToLowerInvariant(), progress = job.ProgressPercent ?? 0, message, detail, rawOutput = (string?)null },
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        await context.Response.WriteAsync($"event: status\ndata: {json}\n\n", token);
        await context.Response.Body.FlushAsync(token);
    }
}
