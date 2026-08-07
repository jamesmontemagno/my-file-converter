using LocalMorph.Bridge;

var options = BridgeOptions.FromEnvironment();
var application = BridgeApplication.Create(args, options);
await application.StartAsync();

var address = application.Urls.Single();
var ffmpeg = application.Services.GetRequiredService<FfmpegState>().Info;
Console.Out.WriteLine($"LOCALMORPH_BRIDGE={System.Text.Json.JsonSerializer.Serialize(new
{
    baseUrl = address,
    token = options.Token,
    version = BridgeApplication.Version
})}");
application.Logger.LogInformation(
    "LocalMorph Bridge {Version} is running at {Address}. FFmpeg: {FfmpegStatus}",
    BridgeApplication.Version,
    address,
    ffmpeg?.Version ?? "not found on PATH");
await application.WaitForShutdownAsync();

public partial class Program;
