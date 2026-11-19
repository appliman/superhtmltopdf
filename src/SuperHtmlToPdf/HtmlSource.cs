namespace SuperHtmlToPdf;

/// <summary>A UTF-8 HTML string, an explicitly specified file, or a complete server-rendered HTTP response.</summary>
public abstract record HtmlSource
{
    private HtmlSource()
    {
    }

    public sealed record Markup(string Html, Uri? BaseUri = null) : HtmlSource;
    public sealed record LocalFile(string Path) : HtmlSource;
    public sealed record WebPage(Uri Uri) : HtmlSource;

    public static HtmlSource FromHtml(string html, Uri? baseUri = null) => new Markup(html, baseUri);
    public static HtmlSource FromFile(string path) => new LocalFile(path);
    public static HtmlSource FromUrl(Uri uri) => new WebPage(uri);
}
