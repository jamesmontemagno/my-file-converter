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
        var context = new BuildContext(job.Source, job.Format, job.Options, tools.Ffmpeg, job.OutputPath);
        if (context.TargetSizeError is { } sizeError) throw new InvalidOperationException(sizeError);
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
            MediaCategory.Subtitle => context.Source.Category == MediaCategory.Subtitle ? "Converting subtitles" : "Extracting subtitles",
            MediaCategory.Image when context.Format.Id == ContactSheetId => "Building contact sheet",
            MediaCategory.Image when context.Format.Id == WaveformId => "Drawing waveform",
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

    public const string ContactSheetId = "contact-sheet";
    public const string WaveformId = "waveform";
    private const int ContactSheetColumns = 4;
    private const int ContactSheetRows = 4;

    private static List<string> BaseArguments() => ["-hide_banner", "-y", "-nostdin", "-loglevel", "error", "-progress", "pipe:1", "-nostats"];

    private static List<string> BuildArguments(BuildContext context, int pass, string? passLog)
    {
        if (context.Format.Category == MediaCategory.Subtitle) return BuildSubtitleArguments(context);
        if (context.Format.Id == ContactSheetId) return BuildContactSheetArguments(context);
        if (context.Format.Id == WaveformId) return BuildWaveformArguments(context);

        var (source, format, options, caps) = (context.Source, context.Format, context.Options, context.Capabilities);
        var args = BaseArguments();

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

        // Extra inputs go straight after the source so the output options below (-t, -frames) never bind to them.
        var nextInput = 1;
        int? replacementAudioInput = null;
        int? softSubtitleInput = null;
        if (isVideoTarget && pass != 1)
        {
            if (context.ReplacementAudioPath is { } replacementAudio && !dropAudio)
            {
                // A new soundtrack starts at the beginning of the output, so it is not seeked with the trim.
                args.AddRange(["-i", replacementAudio]);
                replacementAudioInput = nextInput++;
            }

            if (context.SoftSubtitlePath is { } subtitleFile)
            {
                // Subtitles follow the source timeline, so they seek with the trim.
                if (options.TrimStartSeconds is { } subtitleStart && subtitleStart > 0 && format.Supports(FormatFeatures.Trim)) args.AddRange(["-ss", Num(subtitleStart)]);
                args.AddRange(["-i", subtitleFile]);
                softSubtitleInput = nextInput++;
            }
        }

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
            // A still picture with a soundtrack lasts as long as the audio (-shortest below).
            if (replacementAudioInput is null) args.AddRange(["-t", Num(options.TrimEndSeconds ?? 5)]);
        }

        // ---- stream mapping ----
        if (isVideoTarget)
        {
            args.AddRange(["-map", context.BitmapSubtitleIndex is not null ? "[v]" : "0:v:0"]);
            if (replacementAudioInput is { } audioInput)
            {
                args.AddRange(["-map", $"{audioInput}:a:0", "-shortest"]);
            }
            else if (!dropAudio && (source.HasAudio || !sourceIsImage))
            {
                AddAudioMaps(args, context);
            }
            AddSubtitleMaps(args, context, softSubtitleInput);
            args.Add("-dn");
        }
        else if (isAudioTarget)
        {
            args.AddRange(["-map", $"0:a:{context.AudioTrack}", "-vn", "-sn", "-dn"]);
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
                if (context.BitmapSubtitleIndex is { } bitmapIndex)
                {
                    // Picture subtitles (PGS/VobSub) are overlaid from the source's own subtitle stream.
                    var chain = filters.Count > 0 ? "," + string.Join(",", filters) : string.Empty;
                    args.AddRange(["-filter_complex", $"[0:v:0][0:s:{bitmapIndex}]overlay=eof_action=pass{chain}[v]"]);
                }
                else if (filters.Count > 0)
                {
                    args.AddRange(["-vf", string.Join(",", filters)]);
                }
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
            else if (format.AudioCodec == "copy" && replacementAudioInput is not null && format.Extension != "mkv")
            {
                // A replacement soundtrack may be anything (WAV, FLAC…); MP4/MOV need a codec they can hold.
                args.AddRange(["-c:a", context.Capabilities.HasEncoder("aac_at") ? "aac_at" : "aac", "-b:a", $"{options.AudioBitrateKbps ?? 192}k"]);
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

    private static void AddAudioMaps(List<string> args, BuildContext context)
    {
        var (source, format, options) = (context.Source, context.Format, context.Options);
        var audioStreams = source.Media?.AudioStreams ?? [];
        var selected = context.AudioTrack;
        if (options.KeepAllAudioTracks && format.Supports(FormatFeatures.MultiAudio))
        {
            if (audioStreams.Count <= 1)
            {
                args.AddRange(["-map", "0:a?"]);
                return;
            }

            // The chosen track goes first and becomes the default; the rest keep their order.
            args.AddRange(["-map", $"0:a:{selected}"]);
            for (var track = 0; track < audioStreams.Count; track++)
            {
                if (track != selected) args.AddRange(["-map", $"0:a:{track}"]);
            }
            args.AddRange(["-disposition:a:0", "default"]);
            for (var output = 1; output < audioStreams.Count; output++) args.AddRange([$"-disposition:a:{output}", "0"]);
            return;
        }

        if (selected == 0)
        {
            args.AddRange(["-map", "0:a:0?"]);
        }
        else
        {
            args.AddRange(["-map", $"0:a:{selected}", "-disposition:a:0", "default"]);
        }
    }

    private static void AddSubtitleMaps(List<string> args, BuildContext context, int? softSubtitleInput)
    {
        var (source, format, options) = (context.Source, context.Format, context.Options);
        var mode = format.Supports(FormatFeatures.Subtitles) ? options.Subtitles : SubtitleMode.Auto;
        var codec = SoftSubtitleCodec(format);
        // Matroska cannot store MP4's mov_text, so convert those tracks to SubRip instead of copying.
        if (codec == "copy" && (source.Media?.SubtitleStreams ?? []).Any(stream => stream.Codec == "mov_text")) codec = "srt";
        if (mode == SubtitleMode.BurnIn || codec is null)
        {
            args.Add("-sn");
            return;
        }

        var mapped = 0;
        var keepSource = mode == SubtitleMode.Keep || mode == SubtitleMode.Auto && format.Extension == "mkv";
        if (keepSource && format.Extension == "mkv")
        {
            // Matroska holds every subtitle format as-is, pictures included.
            args.AddRange(["-map", "0:s?"]);
            mapped++;
        }
        else if (keepSource)
        {
            // MP4/MOV/WebM only hold text subtitles; picture tracks (PGS/VobSub) are left out.
            foreach (var stream in source.Media?.SubtitleStreams ?? [])
            {
                if (stream.IsPictureSubtitle) continue;
                args.AddRange(["-map", $"0:s:{stream.TypeIndex}"]);
                mapped++;
            }
        }

        if (softSubtitleInput is { } input)
        {
            args.AddRange(["-map", $"{input}:s:0"]);
            mapped++;
        }

        if (mapped == 0) args.Add("-sn");
        else args.AddRange(["-c:s", codec]);
    }

    /// <summary>The subtitle codec a container stores soft subtitles as, or null when it has none.</summary>
    private static string? SoftSubtitleCodec(OutputFormat format) => format.Extension switch
    {
        "mkv" => "copy",
        "mp4" or "mov" or "m4v" => "mov_text",
        "webm" => "webvtt",
        _ => null
    };

    private static List<string> BuildSubtitleArguments(BuildContext context)
    {
        var (source, format, options) = (context.Source, context.Format, context.Options);
        var args = BaseArguments();
        args.AddRange(["-i", source.Path]);
        var track = 0;
        if (source.Category != MediaCategory.Subtitle)
        {
            track = options.SubtitleTrack ?? 0;
            var streams = source.Media?.SubtitleStreams;
            if (streams is not null && source.Media?.Streams is not null)
            {
                if (streams.Count == 0) throw new InvalidOperationException($"{source.FileName} has no subtitle tracks to extract.");
                if (track >= streams.Count) throw new InvalidOperationException($"{source.FileName} has {streams.Count} subtitle track{(streams.Count == 1 ? string.Empty : "s")}; choose another.");
                if (streams[track].IsPictureSubtitle)
                {
                    throw new InvalidOperationException($"Subtitle track {track + 1} is picture-based ({streams[track].Codec}). Only text subtitles can be saved as {format.DisplayName}; burn it into a video instead.");
                }
            }
        }

        args.AddRange(["-map", $"0:s:{track}", "-vn", "-an", "-dn", "-c:s", format.Extension switch { "vtt" => "webvtt", "ass" => "ass", _ => "srt" }]);
        args.Add(context.OutputPath);
        return args;
    }

    private static void AddTrimInput(List<string> args, BuildContext context)
    {
        if (context.Options.TrimStartSeconds is { } start && start > 0 && context.Format.Supports(FormatFeatures.Trim)) args.AddRange(["-ss", Num(start)]);
        args.AddRange(["-i", context.Source.Path]);
        if (context.Options.TrimEndSeconds is { } end && context.Format.Supports(FormatFeatures.Trim))
        {
            var length = end - (context.Options.TrimStartSeconds ?? 0);
            if (length > 0) args.AddRange(["-t", Num(length)]);
        }
    }

    private static List<string> BuildContactSheetArguments(BuildContext context)
    {
        var (source, options) = (context.Source, context.Options);
        if (context.OutputDurationSeconds is not { } duration || duration <= 0)
        {
            throw new InvalidOperationException("A contact sheet needs a video with a known duration.");
        }

        var tiles = ContactSheetColumns * ContactSheetRows;
        var args = BaseArguments();
        AddTrimInput(args, context);
        var filters = new List<string>
        {
            // Sample evenly across the clip, then lay the frames out on a dark grid.
            $"fps={Num(tiles / duration)}:round=down",
            "scale=480:-2:flags=lanczos"
        };
        filters.AddRange(context.Options.Rotation switch { 90 => ["transpose=1"], 180 => ["hflip,vflip"], 270 => ["transpose=2"], _ => [] });
        filters.Add($"tile={ContactSheetColumns}x{ContactSheetRows}:padding=6:margin=6:color=0x0B0F16");
        args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", string.Join(",", filters), "-frames:v", "1", "-update", "1",
            "-c:v", "mjpeg", "-q:v", Num(Clamp(31 - options.Quality * 0.29, 2, 31)), "-pix_fmt", "yuvj420p"]);
        if (options.StripMetadata) args.AddRange(["-map_metadata", "-1"]);
        args.Add(context.OutputPath);
        return args;
    }

    private static List<string> BuildWaveformArguments(BuildContext context)
    {
        if (context.Source.Media is { } media && (media.Streams is null ? media.AudioCodec is null : media.AudioStreams.Count == 0))
        {
            throw new InvalidOperationException($"{context.Source.FileName} has no audio to draw.");
        }

        var args = BaseArguments();
        AddTrimInput(args, context);
        args.AddRange(["-filter_complex", $"[0:a:{context.AudioTrack}]aformat=channel_layouts=mono,showwavespic=s=1920x480:colors=0x3B82F6[wave]",
            "-map", "[wave]", "-frames:v", "1", "-update", "1", "-c:v", "png"]);
        if (context.Options.StripMetadata) args.AddRange(["-map_metadata", "-1"]);
        args.Add(context.OutputPath);
        return args;
    }

    /// <summary>
    /// Escapes a value (typically a file path) for a filter option inside a filtergraph: once for the option parser
    /// (<c>\ ' :</c>) and once for the graph parser (<c>\ ' [ ] , ;</c>).
    /// </summary>
    public static string EscapeFilterValue(string value)
    {
        var normalized = OperatingSystem.IsWindows() ? value.Replace('\\', '/') : value;
        var option = new System.Text.StringBuilder();
        foreach (var character in normalized)
        {
            if (character is '\\' or '\'' or ':') option.Append('\\');
            option.Append(character);
        }

        var graph = new System.Text.StringBuilder();
        foreach (var character in option.ToString())
        {
            if (character is '\\' or '\'' or '[' or ']' or ',' or ';') graph.Append('\\');
            graph.Append(character);
        }

        return graph.ToString();
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
                args.AddRange(["-c:a", context.Capabilities.HasEncoder("aac_at") ? "aac_at" : context.Capabilities.HasEncoder("aac") || !context.Capabilities.IsAvailable ? "aac" : "libfdk_aac", "-b:a", $"{bitrate ?? defaultBitrate}k"]);
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
        if (context.Format.Supports(FormatFeatures.AudioTuning) && options.AudioDelayMilliseconds != 0)
        {
            audioFilters.Add(options.AudioDelayMilliseconds > 0
                ? $"adelay=delays={Num(options.AudioDelayMilliseconds)}:all=1"
                : $"atrim=start={Num(-options.AudioDelayMilliseconds / 1000.0)},asetpts=PTS-STARTPTS");
        }
        if (Math.Abs(options.PlaybackSpeed - 1.0) > 0.001) audioFilters.AddRange(TempoFilters(options.PlaybackSpeed));
        var effects = context.Format.Supports(FormatFeatures.Effects);
        if (effects && options.Reverse) audioFilters.Add("areverse");
        if (options.VolumePercent != 100) audioFilters.Add($"volume={Num(options.VolumePercent / 100.0)}");
        if (options.NormalizeAudio) audioFilters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        if (effects && options.FadeInSeconds > 0) audioFilters.Add($"afade=t=in:st=0:d={Num(options.FadeInSeconds)}");
        if (effects && options.FadeOutSeconds > 0 && context.OutputDurationSeconds is { } audioDuration)
        {
            audioFilters.Add($"afade=t=out:st={Num(Math.Max(0, audioDuration - options.FadeOutSeconds))}:d={Num(options.FadeOutSeconds)}");
        }
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
        var isVideoTarget = format.Category == MediaCategory.Video;
        var videoFilters = isVideoTarget && format.Supports(FormatFeatures.VideoFilters);
        var effects = isVideoTarget && format.Supports(FormatFeatures.Effects);

        // Clean-up filters work best on the untouched source frames.
        if (videoFilters && options.Deinterlace) filters.Add("yadif");
        if (videoFilters && options.Denoise) filters.Add("hqdn3d");

        // Retiming early keeps GIF scaling cheap; burned-in subtitles need the source timeline, so they retime later.
        var retimeEarly = context.BurnSubtitleFilter is null;
        if (retimeEarly) AddRetiming(filters, context);

        switch (options.Rotation)
        {
            case 90: filters.Add("transpose=1"); break;
            case 180: filters.Add("hflip,vflip"); break;
            case 270: filters.Add("transpose=2"); break;
        }

        if (videoFilters && options.FlipHorizontal) filters.Add("hflip");
        if (videoFilters && options.FlipVertical) filters.Add("vflip");
        if (videoFilters && CropAspects.TryParse(options.CropAspect, out var aspectWidth, out var aspectHeight))
        {
            // Center crop to the largest region with the requested shape (after rotation, so 9:16 means the output shape).
            filters.Add($"crop=w='min(iw,ih*{aspectWidth}/{aspectHeight})':h='min(ih,iw*{aspectHeight}/{aspectWidth})'");
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

        if (context.BurnSubtitleFilter is { } burnIn)
        {
            // libass times subtitles from the frame timestamps, which restart at zero after an input seek.
            var shift = options.TrimStartSeconds is { } start && start > 0 && format.Supports(FormatFeatures.Trim) ? start : 0;
            if (shift > 0) filters.Add($"setpts=PTS+{Num(shift)}/TB");
            filters.Add(burnIn);
            if (shift > 0) filters.Add("setpts=PTS-STARTPTS");
        }

        if (!retimeEarly) AddRetiming(filters, context);

        if (effects && options.Reverse) filters.Add("reverse");
        if (effects && options.FadeInSeconds > 0) filters.Add($"fade=t=in:st=0:d={Num(options.FadeInSeconds)}");
        if (effects && options.FadeOutSeconds > 0 && context.OutputDurationSeconds is { } duration)
        {
            filters.Add($"fade=t=out:st={Num(Math.Max(0, duration - options.FadeOutSeconds))}:d={Num(options.FadeOutSeconds)}");
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

    private static void AddRetiming(List<string> filters, BuildContext context)
    {
        if (Math.Abs(context.Options.PlaybackSpeed - 1.0) > 0.001 && context.Format.Category == MediaCategory.Video)
        {
            filters.Add($"setpts=PTS/{Num(context.Options.PlaybackSpeed)}");
        }

        if (context.Format.VideoCodec == "gif")
        {
            filters.Add($"fps={Num(context.Options.FrameRate ?? 15)}");
        }
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

    private static long? EffectiveDurationMicroseconds(BuildContext context) =>
        context.Format.Category == MediaCategory.Image || context.OutputDurationSeconds is not { } duration
            ? null
            : (long)(duration * 1_000_000);

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

            var wantsTargetSize = options.TargetSizeMegabytes is not null && format.Supports(FormatFeatures.TargetSize);

            // Target-size encodes always use the software two-pass path: hardware rate control is too loose to hit a cap.
            HardwareEncoder = !wantsTargetSize && options.UseHardwareEncoder && format.Supports(FormatFeatures.HardwareAccel) && format.VideoCodec is { } codec
                ? capabilities.HardwareEncoderFor(codec)
                : null;

            if (wantsTargetSize)
            {
                var megabytes = options.TargetSizeMegabytes!.Value;
                var duration = EffectiveDurationSeconds(source, options);
                if (duration <= 0)
                {
                    TargetSizeError = "A target file size needs a source with a known duration.";
                }
                else
                {
                    var audioKbps = options.RemoveAudio || !source.HasAudio ? 0 : options.AudioBitrateKbps ?? 128;
                    var totalKbps = megabytes * 8192.0 / duration;
                    var videoKbps = totalKbps * 0.97 - audioKbps;
                    if (videoKbps < MinimumVideoKbps)
                    {
                        var minimumMb = (MinimumVideoKbps + audioKbps) / 0.97 * duration / 8192.0;
                        TargetSizeError = $"{megabytes:0.#} MB is too small for a {SourceFile.FormatDuration(duration)} clip. Use at least {Math.Ceiling(minimumMb * 10) / 10:0.#} MB, lower the audio bitrate, or trim the clip.";
                    }
                    else
                    {
                        TargetVideoKbps = (int)videoKbps;
                    }
                }
            }

            UsesTwoPass = TargetVideoKbps is not null && format.VideoCodec == "h264";

            var audioStreams = source.Media?.AudioStreams ?? [];
            AudioTrack = format.Supports(FormatFeatures.AudioTracks) ? options.AudioTrack ?? 0 : 0;
            if (AudioTrack > 0 && source.Media?.Streams is not null && AudioTrack >= audioStreams.Count)
            {
                throw new InvalidOperationException(audioStreams.Count == 0
                    ? $"{source.FileName} has no audio tracks."
                    : $"{source.FileName} has {audioStreams.Count} audio track{(audioStreams.Count == 1 ? string.Empty : "s")}; choose another.");
            }

            var isVideoTarget = format.Category == MediaCategory.Video;
            if (isVideoTarget && format.Supports(FormatFeatures.MultiAudio) && !string.IsNullOrWhiteSpace(options.ReplacementAudioPath))
            {
                if (!File.Exists(options.ReplacementAudioPath)) throw new InvalidOperationException($"The replacement audio file {Path.GetFileName(options.ReplacementAudioPath)} no longer exists.");
                ReplacementAudioPath = options.ReplacementAudioPath;
            }

            var externalSubtitles = string.IsNullOrWhiteSpace(options.ExternalSubtitlePath) ? null : options.ExternalSubtitlePath;
            if (isVideoTarget && externalSubtitles is not null && !File.Exists(externalSubtitles))
            {
                throw new InvalidOperationException($"The subtitle file {Path.GetFileName(externalSubtitles)} no longer exists.");
            }

            if (isVideoTarget && options.BurnsSubtitles && format.Supports(FormatFeatures.BurnSubtitles))
            {
                if (externalSubtitles is not null)
                {
                    BurnSubtitleFilter = $"subtitles=filename={EscapeFilterValue(externalSubtitles)}";
                }
                else
                {
                    var subtitleStreams = source.Media?.SubtitleStreams ?? [];
                    var track = options.SubtitleTrack ?? 0;
                    if (source.Media?.Streams is not null && subtitleStreams.Count == 0)
                    {
                        throw new InvalidOperationException($"{source.FileName} has no subtitle tracks to burn in. Add a subtitle file instead.");
                    }
                    if (source.Media?.Streams is not null && track >= subtitleStreams.Count)
                    {
                        throw new InvalidOperationException($"{source.FileName} has {subtitleStreams.Count} subtitle track{(subtitleStreams.Count == 1 ? string.Empty : "s")}; choose another.");
                    }

                    if (track < subtitleStreams.Count && subtitleStreams[track].IsPictureSubtitle) BitmapSubtitleIndex = track;
                    else BurnSubtitleFilter = $"subtitles=filename={EscapeFilterValue(source.Path)}:si={track}";
                }
            }
            else if (isVideoTarget && externalSubtitles is not null && format.Supports(FormatFeatures.Subtitles) && SoftSubtitleCodec(format) is not null)
            {
                SoftSubtitlePath = externalSubtitles;
            }

            OutputDurationSeconds = ComputeOutputDuration(source, format, options, ReplacementAudioPath is not null);
        }

        private static double? ComputeOutputDuration(SourceFile source, OutputFormat format, ConversionOptions options, bool replacementAudio)
        {
            double? duration;
            if (source.Category == MediaCategory.Image && !source.IsAnimatedImage && format.Category == MediaCategory.Video)
            {
                duration = replacementAudio ? null : options.TrimEndSeconds ?? 5;
            }
            else
            {
                var total = source.DurationSeconds;
                var trims = format.Supports(FormatFeatures.Trim);
                var start = trims ? options.TrimStartSeconds ?? 0 : 0;
                var end = trims ? options.TrimEndSeconds ?? total : total;
                duration = end is { } finish ? finish - start : null;
            }

            if (duration is not > 0) return null;
            return format.Supports(FormatFeatures.PlaybackSpeed) && Math.Abs(options.PlaybackSpeed - 1.0) > 0.001 ? duration / options.PlaybackSpeed : duration;
        }

        private const int MinimumVideoKbps = 100;

        /// <summary>Audio track to read, counted among the source's audio streams.</summary>
        public int AudioTrack { get; }
        public string? ReplacementAudioPath { get; }
        /// <summary>External subtitle file added as a soft track.</summary>
        public string? SoftSubtitlePath { get; }
        /// <summary>libass <c>subtitles</c> filter that renders text subtitles into the picture.</summary>
        public string? BurnSubtitleFilter { get; }
        /// <summary>Picture subtitle stream (PGS/VobSub) overlaid onto the video.</summary>
        public int? BitmapSubtitleIndex { get; }
        /// <summary>Length of the output after trim and speed, when known.</summary>
        public double? OutputDurationSeconds { get; }

        public SourceFile Source { get; }
        public OutputFormat Format { get; }
        public ConversionOptions Options { get; }
        public FfmpegCapabilities Capabilities { get; }
        public string OutputPath { get; }
        public HardwareEncoder? HardwareEncoder { get; }
        public int? TargetVideoKbps { get; }
        public bool UsesTwoPass { get; }
        public string? TargetSizeError { get; }

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
