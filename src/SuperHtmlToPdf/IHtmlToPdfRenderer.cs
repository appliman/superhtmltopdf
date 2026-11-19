namespace SuperHtmlToPdf;

public interface IHtmlToPdfRenderer
{
    Task<PdfRenderResult> RenderAsync(HtmlSource source, Stream destination,
        PdfRenderOptions? options = null, CancellationToken cancellationToken = default);
}

public sealed record PdfDiagnostic(string Code, string Message);
public sealed record PdfRenderResult(int PageCount, IReadOnlyList<PdfDiagnostic> Diagnostics);

public sealed class PdfRenderException(string message, IReadOnlyList<PdfDiagnostic> diagnostics)
    : Exception(message)
{
    public IReadOnlyList<PdfDiagnostic> Diagnostics { get; } = diagnostics;
}
