#if LOCALMORPH_MAC_SANDBOX_PROOF
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Foundation;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;
using UIKit;
using UniformTypeIdentifiers;

namespace LocalMorph.App.Platforms.MacCatalyst;

public sealed class SandboxProofApp : Application
{
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new SandboxProofPage()) { Title = "LocalMorph Sandbox Proof", Width = 1000, Height = 760 };
}

internal sealed class SandboxProofPage : ContentPage
{
    private readonly Label status = new() { Text = "Starting isolated sandbox checks..." };
    private readonly Editor log = new() { IsReadOnly = true, HeightRequest = 480, FontSize = 12 };
    private readonly Button choose = new() { Text = "Test a selected source and output folder", IsEnabled = false };
    private readonly Button cancel = new() { Text = "Cancel", IsEnabled = false };
    private ToolInventory tools = ToolInventory.Empty;
    private CancellationTokenSource? operation;
    private bool started;

    public SandboxProofPage()
    {
        Title = "Mac sandbox feasibility";
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24, Spacing = 12,
                Children =
                {
                    new Label { Text = "Development proof only - not a Store build", FontSize = 24 },
                    new Label { Text = "Separate app identity; no real preferences or history. Bundled tools only. No feature availability changes." },
                    new Label { Text = "FFmpeg / FFprobe 9.0.2: LGPL 2.1-or-later. License texts are bundled; source and build instructions accompany the CI app artifact." },
                    status, choose, cancel, log
                }
            }
        };
        choose.Clicked += async (_, _) => await RunSelectedAsync();
        cancel.Clicked += (_, _) => operation?.Cancel();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (started) return;
        started = true;
        await RunOperationAsync(async token =>
        {
            var directory = Path.Combine(NSBundle.MainBundle.BundlePath, "Contents", "MacOS");
            var inventory = new Dictionary<ToolKind, ToolInfo>();
            foreach (var kind in new[] { ToolKind.Ffmpeg, ToolKind.Ffprobe })
            {
                var path = Path.Combine(directory, kind == ToolKind.Ffmpeg ? "ffmpeg" : "ffprobe");
                if (!ToolLocator.IsExecutable(path)) throw new FileNotFoundException("Required proof tool is not bundled.", path);
                await RunToolAsync(path, ["-version"], token);
                inventory.Add(kind, new ToolInfo(kind, path, null, ToolSource.Bundled));
            }
            var capabilities = await FfmpegCapabilities.DiscoverAsync(inventory[ToolKind.Ffmpeg].Path, verifyHardware: true, token: token);
            tools = new ToolInventory(inventory, capabilities);
            Write($"Verified encoders: {capabilities.HardwareSummary}");
            Write($"libx264={capabilities.HasEncoder("libx264")}; libx265={capabilities.HasEncoder("libx265")}; subtitles={capabilities.MayHaveFilter("subtitles")}; zscale={capabilities.MayHaveFilter("zscale")}");
            var root = Path.Combine(FileSystem.CacheDirectory, "sandbox-proof", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var source = Path.Combine(root, "source.mp4");
            await RunToolAsync(inventory[ToolKind.Ffmpeg].Path,
                ["-v", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=2",
                 "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-c:v", "mpeg4", "-c:a", "aac", "-shortest", source], token);
            await ConvertAsync(source, root, token);
            await ConvertAsync(source, root, token, options: new ConversionOptions
            { UseHardwareEncoder = false, VideoBitrateKbps = 1000 });
            await ConvertAsync(source, root, token, "mp4-h265", new ConversionOptions
            { UseHardwareEncoder = false, VideoBitrateKbps = 1000 });
            await ConvertAsync(source, root, token, options: new ConversionOptions
            { UseHardwareEncoder = false, TargetSizeMegabytes = 0.1, RemoveAudio = true });
            Write("APPLE_SOFTWARE_PROOF_PASSED: H.264, HEVC and hard-cap conversion through the shared queue.");
            await VerifyCancellationAsync(source, root, token);
            Write("CONTAINER_PROOF_PASSED. Selected external source/destination access is not yet verified.");
        });
    }

    private async Task RunSelectedAsync()
    {
        await RunOperationAsync(async token =>
        {
            using var source = await PickAsync(UTTypes.Item);
            if (source is null) { Write("Source picker canceled; no conversion attempted."); return; }
            using var destination = await PickAsync(UTTypes.Folder);
            if (destination is null) { Write("Destination picker canceled; no conversion attempted."); return; }
            var sourceAccess = source.StartAccessingSecurityScopedResource();
            var destinationAccess = destination.StartAccessingSecurityScopedResource();
            try
            {
                Write($"Native URL access: source={sourceAccess}; destination={destinationAccess}");
                await ConvertAsync(source.Path ?? throw new IOException("Selected source has no file path."),
                    destination.Path ?? throw new IOException("Selected folder has no file path."), token);
                Write("SELECTED_FILES_PROOF_PASSED. Persistent bookmarks and relaunch are not yet verified.");
            }
            finally
            {
                if (destinationAccess) destination.StopAccessingSecurityScopedResource();
                if (sourceAccess) source.StopAccessingSecurityScopedResource();
            }
        });
    }

    private async Task ConvertAsync(string sourcePath, string outputDirectory, CancellationToken token,
        string formatId = "mp4-h264", ConversionOptions? options = null)
    {
        var source = await SourceInspector.InspectAsync(sourcePath, tools, token);
        if (source.Media is null || source.DurationSeconds is null)
            throw new IOException("Bundled ffprobe did not inspect the selected source.");
        Write($"Inspected: {source.Media.VideoCodec}, {source.DurationSeconds:0.00}s");
        var format = FormatCatalog.Find(formatId) ?? throw new InvalidOperationException("MP4 format is missing.");
        await using var original = File.OpenRead(sourcePath);
        var originalHash = await SHA256.HashDataAsync(original, token);
        var output = Path.Combine(outputDirectory, $"localmorph-sandbox-proof-{Guid.NewGuid():N}.mp4");
        var job = new ConversionJob(source, format, options ?? new ConversionOptions
            { UseHardwareEncoder = true, VideoBitrateKbps = 1000 }, output, EngineKind.Ffmpeg);
        using var queue = new ConversionQueue(Path.Combine(FileSystem.CacheDirectory, "proof-work"), maxParallel: 1) { Tools = () => tools };
        using var registration = token.Register(job.Cancel);
        queue.Enqueue(job);
        queue.Start();
        await queue.WaitForIdleAsync();
        token.ThrowIfCancellationRequested();
        if (job.State != JobState.Completed)
            throw new IOException($"Conversion did not complete: {job.State}; {job.Error}");
        using var probe = JsonDocument.Parse(await RunToolAsync(tools.PathFor(ToolKind.Ffprobe)!,
            ["-v", "error", "-show_entries", "stream=codec_name,width,height", "-of", "json", output], token));
        if (!probe.RootElement.GetProperty("streams").EnumerateArray()
            .Any(stream => stream.GetProperty("codec_name").GetString() == format.VideoCodec))
            throw new IOException($"The committed output does not contain {format.VideoCodec} video.");
        if (options?.TargetSizeMegabytes is { } limit && new FileInfo(output).Length > limit * 1_000_000)
            throw new IOException("The committed output exceeds its decimal-MB hard cap.");
        original.Position = 0;
        var currentHash = await SHA256.HashDataAsync(original, token);
        if (!originalHash.SequenceEqual(currentHash))
            throw new IOException("The source changed during the proof.");
        Write($"Committed output: {output}");
    }

    private async Task VerifyCancellationAsync(string sourcePath, string directory, CancellationToken token)
    {
        var source = await SourceInspector.InspectAsync(sourcePath, tools, token);
        var format = FormatCatalog.Find("mp4-h264") ?? throw new InvalidOperationException("MP4 format is missing.");
        var output = Path.Combine(directory, $"canceled-{Guid.NewGuid():N}.mp4");
        var job = new ConversionJob(source, format,
            new ConversionOptions { TargetHeight = 2160, FrameRate = 120, PlaybackSpeed = 0.25, RemoveAudio = true, VideoBitrateKbps = 1000 }, output, EngineKind.Ffmpeg);
        using var queue = new ConversionQueue(Path.Combine(FileSystem.CacheDirectory, "proof-work"), maxParallel: 1) { Tools = () => tools };
        using var registration = token.Register(job.Cancel);
        var reports = 0;
        Task? cancelTask = null;
        job.Changed += current =>
        {
            if (current.CommandLine is not null && Interlocked.Increment(ref reports) == 2)
                cancelTask = CancelSoonAsync();
        };
        async Task CancelSoonAsync()
        {
            await Task.Delay(100, token);
            job.Cancel();
        }
        queue.Enqueue(job);
        queue.Start();
        await queue.WaitForIdleAsync();
        if (cancelTask is not null) await cancelTask;
        token.ThrowIfCancellationRequested();
        if (job.State != JobState.Canceled || job.CommandLine is null || job.LogTail.Length == 0)
            throw new IOException($"Expected cancellation after process launch; got {job.State}: {job.Error}");
        var staging = Path.Combine(directory, $".localmorph-{job.Id:N}.mp4");
        if (File.Exists(output) || File.Exists(staging))
            throw new IOException("Cancellation left a committed or staged output.");
        Write("CANCELLATION_PROOF_PASSED: queue canceled; no staged or committed output.");
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        operation = cancellation;
        choose.IsEnabled = false;
        cancel.IsEnabled = true;
        try
        {
            await action(cancellation.Token);
            status.Text = "Checks finished. See evidence below; untested gates remain unverified.";
        }
        catch (OperationCanceledException)
        {
            status.Text = "Canceled. No sandbox proof claimed.";
            Write("OPERATION_CANCELED");
        }
        catch (Exception exception)
        {
            status.Text = "Sandbox check failed.";
            Write($"PROOF_FAILED: {exception}");
        }
        finally
        {
            operation = null;
            choose.IsEnabled = tools.HasMediaTools;
            cancel.IsEnabled = false;
        }
    }

    private void Write(string message)
    {
        Console.WriteLine(message);
        log.Text += message + Environment.NewLine;
        File.AppendAllText(Path.Combine(FileSystem.AppDataDirectory, "sandbox-proof.log"), message + Environment.NewLine);
    }

    private static async Task<string> RunToolAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start bundled tool.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new IOException($"Bundled tool exited {process.ExitCode}: {await error}");
            await Task.WhenAll(output, error);
            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task<NSUrl?> PickAsync(UTType type)
    {
        var completion = new TaskCompletionSource<NSUrl?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pickerDelegate = new PickerDelegate(completion);
        using var picker = new UIDocumentPickerViewController([type], asCopy: false) { Delegate = pickerDelegate };
        var presenter = Platform.GetCurrentUIViewController() ?? throw new InvalidOperationException("No native presenter is available.");
        await presenter.PresentViewControllerAsync(picker, true);
        return await completion.Task;
    }

    private sealed class PickerDelegate(TaskCompletionSource<NSUrl?> completion) : UIDocumentPickerDelegate
    {
        public override void DidPickDocument(UIDocumentPickerViewController controller, NSUrl[] urls) => completion.TrySetResult(urls.FirstOrDefault());
        public override void WasCancelled(UIDocumentPickerViewController controller) => completion.TrySetResult(null);
    }
}
#endif
