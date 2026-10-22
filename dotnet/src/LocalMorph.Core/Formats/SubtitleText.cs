using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LocalMorph.Core.Formats;

public sealed record SubtitleCue(long StartMilliseconds, long EndMilliseconds, string Text);

/// <summary>Plain-text caption interchange, using SRT as the bridge to FFmpeg.</summary>
public static class SubtitleText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly Regex LrcTime = new(@"\[(\d+):([0-5]\d)(?:[.:](\d{1,3}))?\]");

    public static bool NeedsConversion(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".sbv" or ".lrc" or ".ttml";

    public static IReadOnlyList<SubtitleCue> Parse(string content, string extension)
    {
        content = content.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var cues = extension.ToLowerInvariant().TrimStart('.') switch
        {
            "srt" => ParseBlocks(content, sbv: false),
            "sbv" => ParseBlocks(content, sbv: true),
            "lrc" => ParseLrc(content),
            "ttml" => ParseTtml(content),
            _ => throw new InvalidOperationException($"Unsupported subtitle format: {extension}.")
        };
        if (cues.Count == 0) throw new FormatException("The subtitle file contains no timed captions.");
        foreach (var cue in cues)
        {
            if (cue.StartMilliseconds < 0 || cue.EndMilliseconds <= cue.StartMilliseconds)
                throw new FormatException("Each caption must have a non-negative start and an end after its start.");
        }
        return cues;
    }

    public static string Serialize(IReadOnlyList<SubtitleCue> cues, string extension)
    {
        if (cues.Count == 0) throw new FormatException("The subtitle file contains no timed captions.");
        return extension.ToLowerInvariant().TrimStart('.') switch
        {
            "srt" => string.Join("\n\n", cues.Select((cue, index) =>
                $"{index + 1}\n{Clock(cue.StartMilliseconds, ',')} --> {Clock(cue.EndMilliseconds, ',')}\n{WebUtility.HtmlEncode(cue.Text)}")) + "\n",
            "sbv" => string.Join("\n\n", cues.Select(cue =>
                $"{Clock(cue.StartMilliseconds)},{Clock(cue.EndMilliseconds)}\n{cue.Text}")) + "\n",
            "lrc" => string.Join("\n", cues.OrderBy(cue => cue.StartMilliseconds).Select(cue =>
                $"[{cue.StartMilliseconds / 60000:00}:{cue.StartMilliseconds / 1000 % 60:00}.{cue.StartMilliseconds % 1000:000}]{cue.Text.Replace('\n', ' ')}")) + "\n",
            "ttml" => SerializeTtml(cues),
            _ => throw new InvalidOperationException($"Unsupported subtitle format: {extension}.")
        };
    }

    public static async Task ConvertAsync(string source, string destination, CancellationToken token)
    {
        var content = await File.ReadAllTextAsync(source, token);
        token.ThrowIfCancellationRequested();
        var cues = Parse(content, Path.GetExtension(source));
        var output = Serialize(cues, Path.GetExtension(destination));
        await File.WriteAllTextAsync(destination, output, token);
    }

    private static string Clock(long milliseconds, char separator = '.') =>
        string.Create(Invariant, $"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}:{milliseconds / 1000 % 60:00}{separator}{milliseconds % 1000:000}");

    private static long Timestamp(string value)
    {
        var match = Regex.Match(value.Trim(), @"^(?:(\d+):)?(\d+):([0-5]\d)(?:[.,](\d{1,3}))?$");
        if (!match.Success) throw new FormatException($"Invalid subtitle timestamp: {value}.");
        var hours = match.Groups[1].Success ? long.Parse(match.Groups[1].Value, Invariant) : 0;
        var minutes = long.Parse(match.Groups[2].Value, Invariant);
        if (match.Groups[1].Success && minutes >= 60) throw new FormatException($"Invalid subtitle timestamp: {value}.");
        return checked(hours * 3600000 + minutes * 60000 + long.Parse(match.Groups[3].Value, Invariant) * 1000
            + (match.Groups[4].Success ? long.Parse(match.Groups[4].Value.PadRight(3, '0'), Invariant) : 0));
    }

    private static List<SubtitleCue> ParseBlocks(string content, bool sbv)
    {
        var cues = new List<SubtitleCue>();
        foreach (var block in Regex.Split(content, @"\n[ \t]*\n"))
        {
            var lines = block.Split('\n');
            var timingIndex = !sbv && lines.Length > 1 && Regex.IsMatch(lines[0].Trim(), @"^\d+$") ? 1 : 0;
            var times = lines[timingIndex].Split(sbv ? "," : "-->", StringSplitOptions.TrimEntries);
            if (times.Length != 2 || lines.Length <= timingIndex + 1)
                throw new FormatException($"Invalid {(sbv ? "SBV" : "SRT")} caption block: {lines[0]}.");
            var text = string.Join('\n', lines.Skip(timingIndex + 1));
            if (!sbv) text = WebUtility.HtmlDecode(Regex.Replace(text, @"<[^>]*>|\{\\[^}]*\}", ""));
            cues.Add(new(Timestamp(times[0]), Timestamp(times[1]), text));
        }
        return cues;
    }

    private static List<SubtitleCue> ParseLrc(string content)
    {
        var entries = new List<(long Start, string Text)>();
        long offset = 0;
        foreach (var line in content.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var offsetMatch = Regex.Match(line.Trim(), @"^\[offset:([+-]?\d+)\]$", RegexOptions.IgnoreCase);
            if (offsetMatch.Success) { offset = long.Parse(offsetMatch.Groups[1].Value, Invariant); continue; }
            var matches = LrcTime.Matches(line);
            if (matches.Count == 0)
            {
                if (Regex.IsMatch(line.Trim(), @"^\[[a-z]+:.*\]$", RegexOptions.IgnoreCase)) continue;
                throw new FormatException($"Invalid LRC caption: {line}.");
            }
            var text = LrcTime.Replace(line, "").Trim();
            foreach (Match match in matches)
                entries.Add((Timestamp($"{match.Groups[1].Value}:{match.Groups[2].Value}.{(match.Groups[3].Success ? match.Groups[3].Value : "0")}"), text));
        }
        entries = entries.OrderBy(entry => entry.Start).ToList();
        var cues = new List<SubtitleCue>();
        long end = entries.Count > 0 ? checked(entries[^1].Start + 3000) : 0;
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            if (index + 1 < entries.Count && entries[index + 1].Start > entry.Start)
                end = entries[index + 1].Start;
            cues.Add(new(checked(entry.Start + offset), checked(end + offset), entry.Text));
        }
        cues.Reverse();
        return cues;
    }

    private static List<SubtitleCue> ParseTtml(string content)
    {
        using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new FormatException("TTML needs a tt root element.");
        if (root.Name.LocalName != "tt") throw new FormatException("TTML needs a tt root element.");
        XNamespace parameters = "http://www.w3.org/ns/ttml#parameter";
        if ((string?)root.Attribute(parameters + "timeBase") is { } timeBase && timeBase != "media")
            throw new FormatException("Only media-relative TTML timing is supported.");
        if (root.DescendantsAndSelf().Any(element => (string?)element.Attribute("timeContainer") == "seq"))
            throw new FormatException("Sequential TTML timing is not supported. Use parallel, explicitly timed captions.");
        if (root.Descendants().Any(element => element.Name.LocalName == "span" &&
            (element.Attribute("begin") is not null || element.Attribute("end") is not null || element.Attribute("dur") is not null)))
            throw new FormatException("Timed TTML spans are not supported. Use paragraph-level caption timing.");
        var cues = new List<SubtitleCue>();
        foreach (var paragraph in root.Descendants().Where(element => element.Name.LocalName == "p"))
        {
            long start = 0;
            long? end = null;
            foreach (var element in paragraph.AncestorsAndSelf().Reverse())
            {
                var parentStart = start;
                start = checked(start + TtmlTime((string?)element.Attribute("begin") ?? "0s"));
                if ((string?)element.Attribute("end") is { } endValue)
                    end = Min(end, checked(parentStart + TtmlTime(endValue)));
                if ((string?)element.Attribute("dur") is { } duration)
                    end = Min(end, checked(start + TtmlTime(duration)));
            }
            if (end is null) throw new FormatException("Every TTML paragraph needs an end or duration, directly or on its container.");
            var preserve = paragraph.AncestorsAndSelf().Select(element => (string?)element.Attribute(XNamespace.Xml + "space")).FirstOrDefault(value => value is not null) == "preserve";
            var text = XmlText(paragraph, preserve);
            if (!preserve)
                text = string.Join('\n', text.Split('\n').Select(line => Regex.Replace(line, @"\s+", " ").Trim()));
            cues.Add(new(start, end.Value, text));
        }
        return cues;
    }

    private static long Min(long? first, long second) => first is { } value ? Math.Min(value, second) : second;

    private static long TtmlTime(string value)
    {
        var match = Regex.Match(value, @"^(\d+(?:\.\d+)?)(ms|s|m|h)$");
        if (!match.Success) return Timestamp(value);
        var factor = match.Groups[2].Value switch { "ms" => 1, "s" => 1000, "m" => 60000, _ => 3600000 };
        return checked((long)Math.Round(decimal.Parse(match.Groups[1].Value, Invariant) * factor));
    }

    private static string XmlText(XElement element, bool preserve) => string.Concat(element.Nodes().Select(node => node switch
    {
        XText text => preserve ? text.Value : Regex.Replace(text.Value, @"\s+", " "),
        XElement child when child.Name.LocalName == "br" => "\n",
        XElement child => XmlText(child, preserve),
        _ => ""
    }));

    private static string SerializeTtml(IReadOnlyList<SubtitleCue> cues)
    {
        XNamespace ns = "http://www.w3.org/ns/ttml";
        return new XDocument(new XElement(ns + "tt", new XAttribute(XNamespace.Xml + "space", "preserve"),
            new XElement(ns + "body", new XElement(ns + "div", cues.Select(cue =>
                new XElement(ns + "p", new XAttribute("begin", Clock(cue.StartMilliseconds)),
                    new XAttribute("end", Clock(cue.EndMilliseconds)),
                    cue.Text.Split('\n').SelectMany((line, index) => index == 0
                        ? new object[] { line } : [new XElement(ns + "br"), line]))))))).ToString() + "\n";
    }
}
