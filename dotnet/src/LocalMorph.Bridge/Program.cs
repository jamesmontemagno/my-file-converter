using LocalMorph.Bridge;

var options = BridgeOptions.FromEnvironment();
var application = BridgeApplication.Create(args, options);
await application.StartAsync();

var address = application.Urls.Single();
Console.Out.WriteLine($"LOCALMORPH_BRIDGE={System.Text.Json.JsonSerializer.Serialize(new
{
    baseUrl = address,
    token = options.Token,
    version = BridgeApplication.Version
})}");
await application.WaitForShutdownAsync();

public partial class Program;
