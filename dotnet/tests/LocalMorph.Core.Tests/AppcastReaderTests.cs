using LocalMorph.Core.Updates;
using Xunit;

namespace LocalMorph.Core.Tests;

public sealed class AppcastReaderTests
{
    private const string WindowsFeed = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
          <channel>
            <title>LocalMorph for Windows</title>
            <item>
              <title>1.2.0</title>
              <pubDate>Sat, 22 Aug 2026 20:00:00 GMT</pubDate>
              <sparkle:version>1.2.0.0</sparkle:version>
              <sparkle:shortVersionString>1.2.0</sparkle:shortVersionString>
              <sparkle:releaseNotesLink>https://github.com/jamesmontemagno/my-file-converter/releases/tag/v1.2.0-windows</sparkle:releaseNotesLink>
              <enclosure url="https://example.com/LocalMorph-1.2.0-x64.msix" length="1" type="application/msix" sparkle:os="windows" sparkle:arch="x64" />
              <enclosure url="https://example.com/LocalMorph-1.2.0-arm64.msix" length="1" type="application/msix" sparkle:os="windows" sparkle:arch="arm64" />
            </item>
          </channel>
        </rss>
        """;

    private const string MacFeed = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
          <channel>
            <item>
              <title>1.1.0</title>
              <enclosure url="https://example.com/LocalMorph-v1.1.0-mac.zip" sparkle:version="41" sparkle:shortVersionString="1.1.0" sparkle:edSignature="abc" length="10" type="application/octet-stream" />
            </item>
            <item>
              <title>1.3.0</title>
              <enclosure url="https://example.com/LocalMorph-v1.3.0-mac.zip" sparkle:version="57" sparkle:shortVersionString="1.3.0" sparkle:edSignature="def" length="10" type="application/octet-stream" />
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void Picks_architecture_specific_enclosure()
    {
        var arm = AppcastReader.Parse(WindowsFeed, "arm64")!;
        var x64 = AppcastReader.Parse(WindowsFeed, "x64")!;
        Assert.EndsWith("arm64.msix", arm.DownloadUrl.ToString());
        Assert.EndsWith("x64.msix", x64.DownloadUrl.ToString());
        Assert.Equal("1.2.0", arm.DisplayVersion);
        Assert.NotNull(arm.ReleaseNotesUrl);
        Assert.Equal(new DateTimeOffset(2026, 8, 22, 20, 0, 0, TimeSpan.Zero), arm.Published);
    }

    [Fact]
    public void Picks_highest_version_from_generate_appcast_output()
    {
        var update = AppcastReader.Parse(MacFeed, "arm64")!;
        Assert.Equal("1.3.0", update.DisplayVersion);
        Assert.EndsWith("v1.3.0-mac.zip", update.DownloadUrl.ToString());
    }

    [Theory]
    [InlineData("1.1.0", true)]
    [InlineData("1.2", false)]
    [InlineData("1.2.0.0", false)]
    [InlineData("1.2.1", false)]
    [InlineData("0.9.9", true)]
    public void IsNewer_normalizes_version_lengths(string current, bool expected)
    {
        var update = AppcastReader.Parse(WindowsFeed)!;
        Assert.True(AppcastReader.TryParseVersion(current, out var version));
        Assert.Equal(expected, AppcastReader.IsNewer(update, version));
    }

    [Theory]
    [InlineData("v1.2.3-mac", "1.2.3")]
    [InlineData("1.0", "1.0")]
    [InlineData("7", "7.0")]
    public void TryParseVersion_handles_tags_and_short_forms(string input, string expected)
    {
        Assert.True(AppcastReader.TryParseVersion(input, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Fact]
    public void Invalid_xml_returns_null()
    {
        Assert.Null(AppcastReader.Parse("<not xml"));
        Assert.Null(AppcastReader.Parse("<rss><channel></channel></rss>"));
    }
}
