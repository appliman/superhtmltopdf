namespace SuperHtmlToPdf;

/// <summary>All dimensions in the public API are in millimeters.</summary>
public sealed record PdfPageSize(double Width, double Height)
{
    public static PdfPageSize A3 { get; } = new(297, 420);
    public static PdfPageSize A4 { get; } = new(210, 297);
    public static PdfPageSize Letter { get; } = new(215.9, 279.4);
}

public sealed record PdfMargins(double Top = 10, double Right = 10, double Bottom = 10, double Left = 10);
public sealed record PdfFont(string Family, byte[] Data, bool Bold = false, bool Italic = false);
public sealed record PdfPageBand(HtmlSource Source, double HeightMm, double SpacingMm = 0)
{
    /// <summary>Replaces [page] and [topage] for each page.</summary>
    public static PdfPageBand Text(string text, double heightMm, double spacingMm = 0)
    {
        var html = System.Net.WebUtility.HtmlEncode(text);
        return new(HtmlSource.FromHtml("<div>" + html + "</div>"), heightMm, spacingMm);
    }
}

public sealed record PdfRenderOptions
{
    // Explicit options override @page. The fallback is A4 with 10 mm margins.
    public PdfPageSize? PageSize
    {
        get; init;
    }
    public bool? Landscape
    {
        get; init;
    }
    public PdfMargins? Margins
    {
        get; init;
    }
    public PdfPageBand? Header
    {
        get; init;
    }
    public PdfPageBand? Footer
    {
        get; init;
    }
    public IReadOnlyList<PdfFont> Fonts { get; init; } = [];
    public IReadOnlyList<string> StyleSheets { get; init; } = [];
    public PdfResourcePolicy Resources { get; init; } = new();
    public bool Strict
    {
        get; init;
    }
}

public sealed record PdfResourcePolicy
{
    public IReadOnlyList<Uri> AllowedOrigins { get; init; } = [];
    public IReadOnlyList<string> AllowedDirectories { get; init; } = [];
    public int MaxResourceBytes { get; init; } = 8 * 1024 * 1024;
    public long MaxTotalBytes { get; init; } = 48 * 1024 * 1024;
    public long MaxImagePixels { get; init; } = 16_000_000;
    public int MaxResources { get; init; } = 256;
    public int MaxNodes { get; init; } = 100_000;
    public int MaxDepth { get; init; } = 128;
    public int MaxPages { get; init; } = 500;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(45);
}
