using LocalMorph.Core.Support;
using Xunit;

namespace LocalMorph.Core.Tests;

public sealed class FeedbackIssueTests
{
    private static readonly FeedbackEnvironment Windows = new("1.2.3", null, "Windows 10.0.26100");
    private static readonly FeedbackEnvironment Mac = new("1.2.3", "57", "macOS Version 26.0 (Build 25A354)");

    [Theory]
    [InlineData(FeedbackKind.Bug, "Bug")]
    [InlineData(FeedbackKind.FeatureRequest, "Feature request")]
    public void Issues_target_the_project_and_include_only_the_supplied_report_and_environment(FeedbackKind kind, string label)
    {
        var result = FeedbackIssue.Create(kind, "  My report  ", "  What happened  ", Windows);
        Assert.Null(result.Error);
        var url = Assert.IsType<Uri>(result.Url);
        Assert.Equal("https", url.Scheme);
        Assert.Equal("github.com", url.Host);
        Assert.Equal("/jamesmontemagno/my-file-converter/issues/new", url.AbsolutePath);
        Assert.Empty(url.Fragment);
        var query = ReadQuery(url);
        Assert.Equal(2, query.Count);
        Assert.Equal($"[{label}] My report", query["title"]);
        Assert.Equal($"## {label}\n\nWhat happened\n\n## Environment\n\n- App: LocalMorph\n- Version: 1.2.3\n- OS: Windows 10.0.26100\n", query["body"]);
    }

    [Fact]
    public void Mac_build_and_os_information_are_preserved()
    {
        var result = FeedbackIssue.Create(FeedbackKind.Bug, "A Mac report", "Details", Mac);
        Assert.Null(result.Error);
        var body = ReadQuery(Assert.IsType<Uri>(result.Url))["body"];
        Assert.Contains("- Build: 57\n", body);
        Assert.Contains("- OS: macOS Version 26.0 (Build 25A354)\n", body);
    }

    [Fact]
    public void Unicode_reserved_characters_and_newlines_round_trip_without_extra_query_fields()
    {
        const string title = "Caf\u00e9 \uD83D\uDC1B & title=?# + %2F";
        const string details = "First line\r\nSecond line\n\u65e5\u672c\u8a9e &labels=secret #section + 100% %2F";
        var result = FeedbackIssue.Create(FeedbackKind.Bug, title, details, Windows);
        Assert.Null(result.Error);
        var url = Assert.IsType<Uri>(result.Url);
        var query = ReadQuery(url);
        Assert.Equal(2, query.Count);
        Assert.Equal($"[Bug] {title}", query["title"]);
        Assert.Contains($"\n\n{details}\n\n", query["body"]);
        Assert.Contains("%252F", url.AbsoluteUri);
        Assert.Empty(url.Fragment);
    }

    [Theory]
    [InlineData(null, "Details", "title")]
    [InlineData("", "Details", "title")]
    [InlineData(" \r\n\t", "Details", "title")]
    [InlineData("Title", null, "details")]
    [InlineData("Title", "", "details")]
    [InlineData("Title", " \r\n\t", "details")]
    public void A_nonblank_title_and_details_are_required(string? title, string? details, string field)
    {
        var result = FeedbackIssue.Create(FeedbackKind.Bug, title, details, Windows);
        Assert.Null(result.Url);
        Assert.Contains(field, result.Error);
    }

    [Fact]
    public void Unknown_issue_types_are_reported()
    {
        var result = FeedbackIssue.Create((FeedbackKind)99, "Title", "Details", Windows);
        Assert.Null(result.Url);
        Assert.Equal("Choose Bug or Feature request.", result.Error);
    }

    [Theory]
    [InlineData("", "42", "Windows")]
    [InlineData("1.2.3", " ", "Windows")]
    [InlineData("1.2.3", "42", "")]
    public void Missing_environment_information_is_not_replaced_with_invented_values(string version, string build, string os)
    {
        var result = FeedbackIssue.Create(FeedbackKind.Bug, "Title", "Details", new(version, build, os));
        Assert.Null(result.Url);
        Assert.Contains("unavailable", result.Error);
    }

    [Fact]
    public void The_complete_escaped_url_can_reach_but_not_exceed_the_limit()
    {
        var baseline = Assert.IsType<Uri>(FeedbackIssue.Create(FeedbackKind.Bug, "x", "a", Windows).Url);
        var details = new string('a', FeedbackIssue.MaxUrlLength - baseline.AbsoluteUri.Length + 1);
        var exact = FeedbackIssue.Create(FeedbackKind.Bug, "x", details, Windows);
        Assert.Null(exact.Error);
        Assert.Equal(FeedbackIssue.MaxUrlLength, Assert.IsType<Uri>(exact.Url).AbsoluteUri.Length);

        var oversized = FeedbackIssue.Create(FeedbackKind.Bug, "x", details + "a", Windows);
        Assert.Null(oversized.Url);
        Assert.Contains("Shorten", oversized.Error);
    }

    [Fact]
    public void The_limit_applies_after_percent_encoding()
    {
        var details = new string('\u00e9', 400);
        Assert.True(details.Length < FeedbackIssue.MaxUrlLength);
        var result = FeedbackIssue.Create(FeedbackKind.FeatureRequest, "Title", details, Mac);
        Assert.Null(result.Url);
        Assert.Contains("too long", result.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Very_long_inputs_are_rejected_without_truncation(bool longTitle)
    {
        var text = new string('a', FeedbackIssue.MaxUrlLength + 1);
        var result = FeedbackIssue.Create(FeedbackKind.Bug, longTitle ? text : "Title", longTitle ? "Details" : text, Windows);
        Assert.Null(result.Url);
        Assert.Contains("Shorten", result.Error);
    }

    private static Dictionary<string, string> ReadQuery(Uri url) => url.Query[1..].Split('&')
        .Select(part => part.Split('=', 2))
        .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));
}
