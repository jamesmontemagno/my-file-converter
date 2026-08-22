using System.Diagnostics;
using System.Globalization;
using LocalMorph.Core.Formats;
using LocalMorph.Core.Jobs;
using LocalMorph.Core.Tools;

namespace LocalMorph.Core.Engines;

/// <summary>Builds FFmpeg invocations for every media format in the catalog, using hardware encoders when they work.</summary>
public sealed class FfmpegEngine : IConversionEngine
{
    public EngineKind Kind => EngineKind.Ffmpeg;

    public ConversionPlan Plan(ConversionJob job, ToolInventory tools, string workDirectory)
    {
        var ffmpeg = tools.PathFor(ToolKind.Ffmpeg) ?? throw new InvalidOperationException("FFmpeg is not installed.");
        if (job.Format.Id == "audio-copy")
        {
            // The copied stream dictates the container; .m4a only holds AAC/ALAC.
            job.OutputPath = Path.ChangeExtension(job.OutputPath, AudioCopyExtension(job.Source.Media?.AudioCodec));
        }
        var context = new BuildContext(job.Source, job.Format, job.Options, tools.Ffmpeg, job.OutputPath);
        var durationUs = EffectiveDurationMicroseconds(context);

        if (context.UsesTwoPass)
        {
            var passLog = Path.Combine(workDirectory, $"localmorph-{job.Id:N}");
            var pass1 = BuildArguments(context, pass: 1, passLog);
            var pass2 = BuildArguments(context, pass: 2, passLog);
            return new ConversionPlan
            {
                Steps =
                [
                    new EngineStep { StartInfo = CommandLine.Create(ffmpeg, pass1), Label = "Analyzing (pass 1 of 2)", ProgressStart = 0, ProgressEnd = 0.45, ParseStdout = line => ParseProgress(line, durationUs) },
                    new EngineStep { StartInfo = CommandLine.Create(ffmpeg, pass2), Label = "Encoding (pass 2 of 2)", ProgressStart = 0.45, ProgressEnd = 1, ParseStdout = line => ParseProgress(line, durationUs) }
                ],
                Cleanup = () =>
                {
                    if (!Directory.Exists(workDirectory)) return;
                    foreach (var file in Directory.EnumerateFiles(workDirectory, Path.GetFileName(passLog) + "*"))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            };
        }

        var arguments = BuildArguments(context, pass: 0, passLog: null);
        var label = context.Format.Category switch
        {
            MediaCategory.Image when context.Source.Category == MediaCategory.Video => "Extracting frame",
            MediaCategory.Image => "Converting image",
            MediaCategory.Audio => context.Format.AudioCodec == "copy" ? "Extracting audio" : "Encoding audio",
            _ => context.Format.VideoCodec == "copy" ? "Remuxing" : "Encoding video"
        };
        return new ConversionPlan
        {
            Steps =
            [
                new EngineStep
                {
                    StartInfo = CommandLine.Create(ffmpeg, arguments),
                    Label = label,
                    IsIndeterminate = durationUs is null,
                    ParseStdout = line => ParseProgress(line, durationUs)
                }
            ]
        };
    }

    public static IReadOnlyList<string> BuildArguments(SourceFile source, OutputFormat format, ConversionOptions options, FfmpegCapabilities capabilities, string outputPath) =>
        BuildArguments(new BuildContext(source, format, options, capabilities, outputPath), pass: 0, passLog: null);

    private static List<string> BuildArguments(BuildContext context, int pass, string? passLog)
    {
        var (source, format, options, caps) = (context.Source, context.Format, context.Options, context.Capabilities);
        var args = new List<string> { "-hide_banner", "-y", "-nostdin", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };

        var isVideoTarget = format.Category == MediaCategory.Video;
        var isAudioTarget = format.Category == MediaCategory.Audio;
        var isImageTarget = format.Category == MediaCategory.Image;
        var sourceIsImage = source.Category == MediaCategory.Image;
        var sourceIsVideo = source.Category == MediaCategory.Video;
        var frameExtract = isImageTarget && sourceIsVideo;
        var remux = format.VideoCodec == "copy" || format.AudioCodec == "copy" && isAudioTarget;
        // Animated image targets (GIF, WebP, APNG) have no audio track.
        var dropAudio = options.RemoveAudio || isVideoTarget && format.AudioCodec is null;

        // ---- input ----
        if (frameExtract)
        {
            var frameTime = options.FrameTimeSeconds ?? options.TrimStartSeconds ?? 0;
            if (frameTime > 0) args.AddRange(["-ss", Num(frameTime)]);
        }
        else if (options.TrimStartSeconds is { } trimStart && trimStart > 0 && format.Supports(FormatFeatures.Trim))
        {
            args.AddRange(["-ss", Num(trimStart)]);
        }

        if (sourceIsImage && isVideoTarget && !source.IsAnimatedImage)
        {
            // Still image → video: loop the frame for the requested duration.
            args.AddRange(["-loop", "1", "-framerate", Num(options.FrameRate ?? 30)]);
        }

        args.AddRange(["-i", source.Path]);

        if (frameExtract)
        {
            args.AddRange(["-frames:v", "1", "-update", "1"]);
        }
        else if (isImageTarget)
        {
            // Still-image targets always take exactly one frame (animated sources use FrameTimeSeconds via -ss below).
            if (source.IsAnimatedImage && options.FrameTimeSeconds is { } animatedFrame && animatedFrame > 0)
            {
                var inputIndex = args.IndexOf("-i");
                args.InsertRange(inputIndex, ["-ss", Num(animatedFrame)]);
            }
            args.AddRange(["-frames:v", "1", "-update", "1"]);
        }
        else if (options.TrimEndSeconds is { } trimEnd && format.Supports(FormatFeatures.Trim))
        {
            var length = trimEnd - (options.TrimStartSeconds ?? 0);
            if (length > 0) args.AddRange(["-t", Num(length)]);
        }
        else if (sourceIsImage && isVideoTarget && !source.IsAnimatedImage)
        {
            args.AddRange(["-t", Num(options.TrimEndSeconds ?? 5)]);
        }

        // ---- stream mapping ----
        if (isVideoTarget)
        {
            args.AddRange(["-map", "0:v:0"]);
            if (!dropAudio && (source.HasAudio || !sourceIsImage)) args.AddRange(["-map", "0:a:0?"]);
            if (format.Extension == "mkv") args.AddRange(["-map", "0:s?", "-c:s", "copy"]);
            else args.Add("-sn");
            args.Add("-dn");
        }
        else if (isAudioTarget)
        {
            args.AddRange(["-map", "0:a:0", "-vn", "-sn", "-dn"]);
        }
        else if (isImageTarget)
        {
            args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn"]);
        }

        if (options.StripMetadata) args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1"]);

        // ---- video ----
        if (isVideoTarget)
        {
            if (format.VideoCodec == "copy")
            {
                args.AddRange(["-c:v", "copy", "-avoid_negative_ts", "make_zero"]);
            }
            else
            {
                AddVideoEncoder(args, context, pass, passLog);
                var filters = BuildVideoFilters(context);
                if (filters.Count > 0) args.AddRange(["-vf", string.Join(",", filters)]);
                if (options.FrameRate is { } fps && format.VideoCodec != "gif") args.AddRange(["-r", Num(fps)]);
            }

            if (pass == 1)
            {
                args.AddRange(["-an", "-f", "null", CommandLine.NullDevice]);
                return args;
            }

            if (dropAudio || sourceIsImage && !source.HasAudio)
            {
                args.Add("-an");
            }
            else if (format.AudioCodec == "copy")
            {
                args.AddRange(["-c:a", "copy"]);
            }
            else if (format.AudioCodec is { } audioCodec)
            {
                AddAudioEncoder(args, context, audioCodec, defaultBitrate: 160);
            }

            if (format.Extension is "mp4" or "mov" or "m4a" && options.FastStart) args.AddRange(["-movflags", "+faststart"]);
            if (format.VideoCodec == "hevc" && format.Extension is "mp4" or "mov") args.AddRange(["-tag:v", "hvc1"]);
            if (format.VideoCodec == "gif") args.AddRange(["-loop", "0"]);
            if (format.VideoCodec == "libwebp_anim") args.AddRange(["-loop", "0"]);
            if (format.VideoCodec == "apng") args.AddRange(["-plays", "0", "-f", "apng"]);
        }
        else if (isAudioTarget)
        {
            if (format.AudioCodec == "copy")
            {
                args.AddRange(["-c:a", "copy"]);
            }
            else
            {
                AddAudioEncoder(args, context, format.AudioCodec ?? "aac", defaultBitrate: format.Id switch { "opus" => 128, "mp3" => 192, _ => 192 });
            }
            if (format.Extension == "m4a" && options.FastStart) args.AddRange(["-movflags", "+faststart"]);
        }
        else if (isImageTarget)
        {
            AddImageEncoder(args, context);
            var filters = BuildVideoFilters(context);
            if (filters.Count > 0) args.AddRange(["-vf", string.Join(",", filters)]);
        }

        if (remux && options.HasTrim && !args.Contains("-avoid_negative_ts")) args.AddRange(["-avoid_negative_ts", "make_zero"]);

        args.Add(context.OutputPath);
        return args;
    }

    private static void AddVideoEncoder(List<string> args, BuildContext context, int pass, string? passLog)
    {
        var (format, options, caps) = (context.Format, context.Options, context.Capabilities);
        var quality = options.Quality;
        var speed = options.Speed;
        var hardware = context.HardwareEncoder;
        var targetKbps = context.TargetVideoKbps;

        switch (format.VideoCodec)
        {
            case "h264":
            case "hevc":
            case "av1":
                if (hardware is not null)
                {
                    AddHardwareVideoEncoder(args, hardware, format.VideoCodec, quality, speed, targetKbps);
                }
                else
                {
                    AddSoftwareVideoEncoder(args, format.VideoCodec, caps, quality, speed, targetKbps, pass, passLog);
                }
                if (format.VideoCodec is "h264" or "hevc") args.AddRange(["-pix_fmt", "yuv420p"]);
                break;
            case "vp9":
                args.AddRange(["-c:v", "libvpx-vp9", "-deadline", speed switch { EncodingSpeed.Fast => "realtime", EncodingSpeed.Quality => "best", _ => "good" },
                    "-cpu-used", speed switch { EncodingSpeed.Fast => "8", EncodingSpeed.Quality => "1", _ => "4" },
                    "-row-mt", "1", "-crf", Num(Clamp(50 - quality * 0.35, 4, 63)), "-b:v", "0", "-pix_fmt", "yuv420p"]);
                break;
            case "mpeg4":
                args.AddRange(["-c:v", "mpeg4", "-vtag", "xvid", "-q:v", Num(Clamp(31 - quality * 0.29, 2, 31))]);
                break;
            case "prores":
                args.AddRange(["-c:v", caps.HasEncoder("prores_ks") ? "prores_ks" : "prores", "-profile:v", "3", "-pix_fmt", "yuv422p10le"]);
                break;
            case "gif":
                args.AddRange(["-c:v", "gif"]);
                break;
            case "libwebp_anim":
                args.AddRange(["-c:v", caps.HasEncoder("libwebp_anim") ? "libwebp_anim" : "libwebp", "-quality", Num(quality), "-compression_level", "4"]);
                if (options.Lossless) args.AddRange(["-lossless", "1"]);
                break;
            case "apng":
                args.AddRange(["-c:v", "apng"]);
                break;
        }
    }

    private static void AddSoftwareVideoEncoder(List<string> args, string codec, FfmpegCapabilities caps, int quality, EncodingSpeed speed, int? targetKbps, int pass, string? passLog)
    {
        switch (codec)
        {
            case "h264":
                args.AddRange(["-c:v", "libx264", "-preset", speed switch { EncodingSpeed.Fast => "veryfast", EncodingSpeed.Quality => "slow", _ => "medium" }]);
                if (targetKbps is { } kbps)
                {
                    args.AddRange(["-b:v", $"{kbps}k", "-maxrate", $"{(int)(kbps * 1.3)}k", "-bufsize", $"{kbps * 2}k"]);
                    if (pass > 0 && passLog is not null) args.AddRange(["-pass", Num(pass), "-passlogfile", passLog]);
                }
                else
                {
                    args.AddRange(["-crf", Num(Clamp(36 - quality * 0.18, 10, 40))]);
                }
                args.AddRange(["-profile:v", "high", "-level", "4.1"]);
                break;
            case "hevc":
                args.AddRange(["-c:v", "libx265", "-preset", speed switch { EncodingSpeed.Fast => "veryfast", EncodingSpeed.Quality => "slow", _ => "medium" }]);
                if (targetKbps is { } hevcKbps)
                {
                    args.AddRange(["-b:v", $"{hevcKbps}k", "-maxrate", $"{(int)(hevcKbps * 1.3)}k", "-bufsize", $"{hevcKbps * 2}k"]);
                }
                else
                {
                    args.AddRange(["-crf", Num(Clamp(40 - quality * 0.2, 12, 45))]);
                }
                args.AddRange(["-x265-params", "log-level=error"]);
                break;
            case "av1":
                if (caps.HasEncoder("libsvtav1"))
                {
                    args.AddRange(["-c:v", "libsvtav1", "-preset", speed switch { EncodingSpeed.Fast => "10", EncodingSpeed.Quality => "4", _ => "7" },
                        "-crf", Num(Clamp(55 - quality * 0.35, 10, 63))]);
                }
                else
                {
                    args.AddRange(["-c:v", "libaom-av1", "-cpu-used", speed switch { EncodingSpeed.Fast => "8", EncodingSpeed.Quality => "3", _ => "6" },
                        "-crf", Num(Clamp(55 - quality * 0.35, 10, 63)), "-b:v", "0", "-row-mt", "1"]);
                }
                break;
        }
    }

    private static void AddHardwareVideoEncoder(List<string> args, HardwareEncoder hardware, string codec, int quality, EncodingSpeed speed, int? targetKbps)
    {
        args.AddRange(["-c:v", hardware.Encoder]);
        var cq = Clamp(36 - quality * 0.18, 10, 45);
        switch (hardware.Vendor)
        {
            case HardwareVendor.Nvidia:
                args.AddRange(["-preset", speed switch { EncodingSpeed.Fast => "p2", EncodingSpeed.Quality => "p6", _ => "p4" }, "-tune", "hq"]);
                if (targetKbps is { } kbps) args.AddRange(["-rc", "vbr", "-b:v", $"{kbps}k", "-maxrate", $"{(int)(kbps * 1.3)}k", "-bufsize", $"{kbps * 2}k"]);
                else args.AddRange(["-rc", "vbr", "-cq", Num(cq), "-b:v", "0"]);
                if (codec == "h264") args.AddRange(["-profile:v", "high"]);
                break;
            case HardwareVendor.Intel:
                args.AddRange(["-preset", speed switch { EncodingSpeed.Fast => "veryfast", EncodingSpeed.Quality => "veryslow", _ => "medium" }]);
                if (targetKbps is { } qsvKbps) args.AddRange(["-b:v", $"{qsvKbps}k", "-maxrate", $"{(int)(qsvKbps * 1.3)}k", "-bufsize", $"{qsvKbps * 2}k"]);
                else args.AddRange(["-global_quality", Num(cq), "-look_ahead", "0"]);
                break;
            case HardwareVendor.Amd:
                args.AddRange(["-quality", speed switch { EncodingSpeed.Fast => "speed", EncodingSpeed.Quality => "quality", _ => "balanced" }]);
                if (targetKbps is { } amfKbps) args.AddRange(["-rc", "vbr_peak", "-b:v", $"{amfKbps}k", "-maxrate", $"{(int)(amfKbps * 1.3)}k"]);
                else args.AddRange(["-rc", "cqp", "-qp_i", Num(cq), "-qp_p", Num(cq), "-qp_b", Num(cq)]);
                break;
            case HardwareVendor.Apple:
                if (targetKbps is { } vtKbps) args.AddRange(["-b:v", $"{vtKbps}k", "-maxrate", $"{(int)(vtKbps * 1.3)}k", "-bufsize", $"{vtKbps * 2}k"]);
                else args.AddRange(["-q:v", Num(Clamp(quality, 1, 100))]);
                args.AddRange(["-allow_sw", "1"]);
                if (codec == "h264") args.AddRange(["-profile:v", "high"]);
                break;
        }
    }

    private static void AddAudioEncoder(List<string> args, BuildContext context, string codec, int defaultBitrate)
    {
        var options = context.Options;
        var bitrate = options.AudioBitrateKbps;
        switch (codec)
        {
            case "aac":
                args.AddRange(["-c:a", context.Capabilities.HasEncoder("aac_at") ? "aac_at" : "aac", "-b:a", $"{bitrate ?? defaultBitrate}k"]);
                break;
            case "libmp3lame":
                args.AddRange(["-c:a", "libmp3lame"]);
                if (bitrate is { } mp3Kbps) args.AddRange(["-b:a", $"{mp3Kbps}k"]);
                else args.AddRange(["-q:a", "2"]);
                break;
            case "libopus":
                args.AddRange(["-c:a", "libopus", "-b:a", $"{bitrate ?? defaultBitrate}k", "-vbr", "on", "-application", options.Channels == ChannelMode.Mono && (bitrate ?? defaultBitrate) <= 64 ? "voip" : "audio"]);
                break;
            case "libvorbis":
                args.AddRange(["-c:a", "libvorbis"]);
                if (bitrate is { } oggKbps) args.AddRange(["-b:a", $"{oggKbps}k"]);
                else args.AddRange(["-q:a", "6"]);
                break;
            case "flac":
                args.AddRange(["-c:a", "flac", "-compression_level", "8"]);
                break;
            case "alac":
                args.AddRange(["-c:a", "alac"]);
                break;
            case "pcm":
                args.AddRange(["-c:a", options.WavBitDepth switch { 24 => "pcm_s24le", 32 => "pcm_s32le", _ => "pcm_s16le" }]);
                break;
            case "pcm_be":
                args.AddRange(["-c:a", options.WavBitDepth switch { 24 => "pcm_s24be", 32 => "pcm_s32be", _ => "pcm_s16be" }]);
                break;
            case "pcm_s16le":
                args.AddRange(["-c:a", "pcm_s16le"]);
                break;
            default:
                args.AddRange(["-c:a", codec]);
                if (bitrate is { } otherKbps) args.AddRange(["-b:a", $"{otherKbps}k"]);
                break;
        }

        if (options.SampleRate is { } sampleRate)
        {
            // Opus only supports a fixed set of rates; anything else is resampled to 48 kHz.
            var effectiveRate = codec == "libopus" && sampleRate is not (8000 or 12000 or 16000 or 24000 or 48000) ? 48000 : sampleRate;
            args.AddRange(["-ar", Num(effectiveRate)]);
        }
        if (options.Channels == ChannelMode.Mono) args.AddRange(["-ac", "1"]);
        if (options.Channels == ChannelMode.Stereo) args.AddRange(["-ac", "2"]);

        var audioFilters = new List<string>();
        if (Math.Abs(options.PlaybackSpeed - 1.0) > 0.001) audioFilters.AddRange(TempoFilters(options.PlaybackSpeed));
        if (options.VolumePercent != 100) audioFilters.Add($"volume={Num(options.VolumePercent / 100.0)}");
        if (options.NormalizeAudio) audioFilters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        if (audioFilters.Count > 0) args.AddRange(["-af", string.Join(",", audioFilters)]);
    }

    private static void AddImageEncoder(List<string> args, BuildContext context)
    {
        var (format, options) = (context.Format, context.Options);
        switch (format.Id)
        {
            case "png":
            case "doc-png":
                args.AddRange(["-c:v", "png", "-pred", "mixed"]);
                break;
            case "jpg":
            case "doc-jpg":
                args.AddRange(["-c:v", "mjpeg", "-q:v", Num(Clamp(31 - options.Quality * 0.29, 2, 31)), "-pix_fmt", "yuvj420p"]);
                break;
            case "webp":
                args.AddRange(["-c:v", "libwebp", "-quality", Num(options.Quality), "-compression_level", "6"]);
                if (options.Lossless || options.Quality >= 100) args.AddRange(["-lossless", "1"]);
                break;
            case "avif":
                args.AddRange(["-c:v", "libaom-av1", "-still-picture", "1", "-cpu-used", "6", "-b:v", "0",
                    "-crf", options.Lossless ? "0" : Num(Clamp(63 - options.Quality * 0.55, 0, 63)), "-pix_fmt", options.Lossless ? "yuv444p" : "yuv420p", "-f", "avif"]);
                break;
            case "jxl":
                args.AddRange(["-c:v", "libjxl"]);
                if (options.Lossless || options.Quality >= 100) args.AddRange(["-distance", "0"]);
                else args.AddRange(["-q:v", Num(options.Quality)]);
                break;
            case "gif-still":
                args.AddRange(["-c:v", "gif"]);
                break;
            case "bmp":
                args.AddRange(["-c:v", "bmp"]);
                break;
            case "tiff":
                args.AddRange(["-c:v", "tiff", "-compression_algo", "lzw"]);
                break;
            case "ico":
                args.AddRange(["-c:v", "png", "-f", "ico"]);
                break;
        }
    }

    private static List<string> BuildVideoFilters(BuildContext context)
    {
        var (source, format, options) = (context.Source, context.Format, context.Options);
        var filters = new List<string>();
        var needsEven = format.VideoCodec is "h264" or "hevc" or "av1" or "vp9" or "mpeg4" or "prores";

        if (Math.Abs(options.PlaybackSpeed - 1.0) > 0.001 && format.Category == MediaCategory.Video)
        {
            filters.Add($"setpts=PTS/{Num(options.PlaybackSpeed)}");
        }

        if (format.VideoCodec == "gif")
        {
            filters.Add($"fps={Num(options.FrameRate ?? 15)}");
        }

        switch (options.Rotation)
        {
            case 90: filters.Add("transpose=1"); break;
            case 180: filters.Add("hflip,vflip"); break;
            case 270: filters.Add("transpose=2"); break;
        }

        if (format.Id == "ico")
        {
            filters.Add("scale=256:256:force_original_aspect_ratio=decrease,pad=256:256:(ow-iw)/2:(oh-ih)/2:color=0x00000000");
        }
        else if (options.TargetHeight is { } height)
        {
            // Only downscale; never upscale a smaller source.
            var sourceHeight = options.Rotation is 90 or 270 ? source.Media?.Width : source.Media?.Height;
            if (sourceHeight is null || sourceHeight > height)
            {
                filters.Add(needsEven ? $"scale=-2:{height}:flags=lanczos" : $"scale=-1:{height}:flags=lanczos");
            }
            else if (needsEven)
            {
                filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
            }
        }
        else if (needsEven)
        {
            filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        }

        if (format.VideoCodec == "gif")
        {
            filters.Add("split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle");
        }

        if (format.Id is "jpg" or "doc-jpg" && source.Category == MediaCategory.Image)
        {
            // Flatten any transparency onto white before JPEG (which has no alpha channel).
            filters.Insert(0, "split[a][b];[a]drawbox=c=white:t=fill[bg];[bg][b]overlay=format=auto");
        }

        return filters;
    }

    private static IEnumerable<string> TempoFilters(double speed)
    {
        // atempo accepts 0.5–100 per instance (older builds 0.5–2); chain for slow-downs below 0.5.
        var remaining = speed;
        while (remaining < 0.5)
        {
            yield return "atempo=0.5";
            remaining /= 0.5;
        }
        while (remaining > 2.0)
        {
            yield return "atempo=2.0";
            remaining /= 2.0;
        }
        yield return $"atempo={Num(remaining)}";
    }

    private static long? EffectiveDurationMicroseconds(BuildContext context)
    {
        var source = context.Source;
        var options = context.Options;
        if (context.Format.Category == MediaCategory.Image) return null;
        double? duration = source.DurationSeconds;
        if (source.Category == MediaCategory.Image && !source.IsAnimatedImage && context.Format.Category == MediaCategory.Video)
        {
            duration = options.TrimEndSeconds ?? 5;
        }
        else if (options.TrimEndSeconds is { } end)
        {
            duration = end - (options.TrimStartSeconds ?? 0);
        }
        else if (duration is { } total && options.TrimStartSeconds is { } start)
        {
            duration = total - start;
        }

        if (duration is not > 0) return null;
        if (context.Format.Supports(FormatFeatures.PlaybackSpeed) && Math.Abs(options.PlaybackSpeed - 1.0) > 0.001) duration /= options.PlaybackSpeed;
        return (long)(duration.Value * 1_000_000);
    }

    public static ProgressSample? ParseProgress(string line, long? durationMicroseconds)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0) return null;
        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();
        switch (key)
        {
            case "progress":
                return value == "end" ? new ProgressSample(1, 0, null, true) : null;
            case "out_time_us":
            case "out_time_ms":
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var elapsed) || elapsed < 0) return null;
                if (durationMicroseconds is not > 0) return new ProgressSample(null, null, null, false);
                var ratio = Math.Min(0.995, (double)elapsed / durationMicroseconds.Value);
                return new ProgressSample(ratio, null, null, false);
            case "speed":
                var speedText = value.TrimEnd('x');
                if (double.TryParse(speedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0)
                {
                    return new ProgressSample(null, null, $"{speed:0.0}×", false);
                }
                return null;
            default:
                return null;
        }
    }

    public static string AudioCopyExtension(string? codec) => codec?.ToLowerInvariant() switch
    {
        "aac" or "alac" => "m4a",
        "mp3" => "mp3",
        "opus" => "opus",
        "vorbis" => "ogg",
        "flac" => "flac",
        "ac3" or "eac3" => "ac3",
        var pcm when pcm is not null && pcm.StartsWith("pcm_", StringComparison.Ordinal) => "wav",
        _ => "mka"
    };

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static int Clamp(double value, int min, int max) => (int)Math.Round(Math.Clamp(value, min, max));

    private sealed class BuildContext
    {
        public BuildContext(SourceFile source, OutputFormat format, ConversionOptions options, FfmpegCapabilities capabilities, string outputPath)
        {
            Source = source;
            Format = format;
            Options = options;
            Capabilities = capabilities;
            OutputPath = outputPath;

            HardwareEncoder = options.UseHardwareEncoder && format.Supports(FormatFeatures.HardwareAccel) && format.VideoCodec is { } codec
                ? capabilities.HardwareEncoderFor(codec)
                : null;

            if (options.TargetSizeMegabytes is { } megabytes && format.Supports(FormatFeatures.TargetSize))
            {
                var duration = EffectiveDurationSeconds(source, options);
                if (duration > 0)
                {
                    var audioKbps = options.RemoveAudio || !source.HasAudio ? 0 : options.AudioBitrateKbps ?? 128;
                    var totalKbps = megabytes * 8192.0 / duration;
                    TargetVideoKbps = (int)Math.Max(100, totalKbps * 0.97 - audioKbps);
                }
            }

            UsesTwoPass = TargetVideoKbps is not null && HardwareEncoder is null && format.VideoCodec == "h264";
        }

        public SourceFile Source { get; }
        public OutputFormat Format { get; }
        public ConversionOptions Options { get; }
        public FfmpegCapabilities Capabilities { get; }
        public string OutputPath { get; }
        public HardwareEncoder? HardwareEncoder { get; }
        public int? TargetVideoKbps { get; }
        public bool UsesTwoPass { get; }

        private static double EffectiveDurationSeconds(SourceFile source, ConversionOptions options)
        {
            var total = source.DurationSeconds ?? 0;
            var start = options.TrimStartSeconds ?? 0;
            var end = options.TrimEndSeconds ?? total;
            var duration = Math.Max(0, end - start);
            return Math.Abs(options.PlaybackSpeed - 1.0) > 0.001 ? duration / options.PlaybackSpeed : duration;
        }
    }
}
