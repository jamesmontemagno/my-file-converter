namespace LocalMorph.Core.Support;

public enum FeedbackKind
{
    Bug,
    FeatureRequest
}

public sealed record FeedbackEnvironment(string Version, string? Build, string OperatingSystem);

public sealed record FeedbackIssueResult(Uri? Url, string? Error = null);

public static class FeedbackIssue
{
    public const int MaxUrlLength = 2000;
    private const string IssueUrl = "https://github.com/jamesmontemagno/my-file-converter/issues/new";
    private const string TooLongMessage = "This report is too long to open in a browser. Shorten the title or details and try again.";

    public static FeedbackIssueResult Create(FeedbackKind kind, string? title, string? details, FeedbackEnvironment environment)
    {
        if (kind is not (FeedbackKind.Bug or FeedbackKind.FeatureRequest))
            return new(null, "Choose Bug or Feature request.");
        if (string.IsNullOrWhiteSpace(title))
            return new(null, "Enter a title for your report.");
        if (string.IsNullOrWhiteSpace(details))
            return new(null, "Enter details for your report.");
        if (string.IsNullOrWhiteSpace(environment.Version) ||
            (environment.Build is not null && string.IsNullOrWhiteSpace(environment.Build)) ||
            string.IsNullOrWhiteSpace(environment.OperatingSystem))
            return new(null, "App version, build, or OS information is unavailable. Restart LocalMorph and try again.");
        if (title.Length > MaxUrlLength || details.Length > MaxUrlLength)
            return new(null, TooLongMessage);

        var label = kind == FeedbackKind.Bug ? "Bug" : "Feature request";
        var buildLine = environment.Build is null ? "" : $"- Build: {environment.Build}\n";
        var body = $"## {label}\n\n{details.Trim()}\n\n## Environment\n\n- App: LocalMorph\n- Version: {environment.Version}\n{buildLine}- OS: {environment.OperatingSystem}\n";
        var url = $"{IssueUrl}?title={Uri.EscapeDataString($"[{label}] {title.Trim()}")}&body={Uri.EscapeDataString(body)}";
        if (url.Length > MaxUrlLength) return new(null, TooLongMessage);

        return new(new Uri(url));
    }
}
