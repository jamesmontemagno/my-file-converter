using System.Globalization;
using System.Xml.Linq;

namespace LocalMorph.Core.Updates;

public sealed record UpdateInfo(Version Version, string DisplayVersion, Uri DownloadUrl, Uri? ReleaseNotesUrl, DateTimeOffset? Published);

/// <summary>
/// Reads a Sparkle-style appcast and decides whether a newer build is available. Shared by Windows
/// (appcast-windows.xml) and macOS (appcast.xml); Sparkle itself does not support Mac Catalyst.
/// </summary>
public static class AppcastReader
{
    private static readonly XNamespace Sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle";

    public static UpdateInfo? Parse(string xml, string? architecture = null)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        UpdateInfo? best = null;
        foreach (var item in document.Descendants("item"))
        {
            var enclosures = item.Elements("enclosure").ToList();
            if (enclosures.Count == 0) continue;

            // Prefer an enclosure matching the running architecture; fall back to the first.
            var enclosure = enclosures.FirstOrDefault(element => architecture is not null &&
                                string.Equals((string?)element.Attribute(Sparkle + "arch"), architecture, StringComparison.OrdinalIgnoreCase))
                            ?? enclosures.FirstOrDefault(element => element.Attribute(Sparkle + "arch") is null)
                            ?? enclosures[0];

            var shortVersion = (string?)item.Element(Sparkle + "shortVersionString") ?? (string?)enclosure.Attribute(Sparkle + "shortVersionString");
            var rawVersion = (string?)item.Element(Sparkle + "version") ?? (string?)enclosure.Attribute(Sparkle + "version") ?? shortVersion;
            if (!TryParseVersion(shortVersion ?? rawVersion, out var version)) continue;
            if (!Uri.TryCreate((string?)enclosure.Attribute("url"), UriKind.Absolute, out var url)) continue;

            Uri.TryCreate((string?)item.Element(Sparkle + "releaseNotesLink"), UriKind.Absolute, out var notes);
            DateTimeOffset? published = DateTimeOffset.TryParse((string?)item.Element("pubDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

            var candidate = new UpdateInfo(version, shortVersion ?? version.ToString(), url, notes, published);
            if (best is null || candidate.Version > best.Version) best = candidate;
        }

        return best;
    }

    public static bool IsNewer(UpdateInfo update, Version current) => Normalize(update.Version) > Normalize(current);

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = text.Trim().TrimStart('v', 'V');
        var dash = cleaned.IndexOf('-');
        if (dash > 0) cleaned = cleaned[..dash];
        if (!cleaned.Contains('.')) cleaned += ".0";
        return Version.TryParse(cleaned, out version!);
    }

    /// <summary>Treats 1.2 == 1.2.0 == 1.2.0.0 so appcast and assembly versions compare cleanly.</summary>
    private static Version Normalize(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
