using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;

namespace SuperHtmlToPdf.Blazor;

/// <summary>Create in the dependency injection scope of the user or export task.</summary>
public sealed class BlazorPdfRenderer(IServiceProvider services, ILoggerFactory loggerFactory, IHtmlToPdfRenderer pdf)
{
    public async Task<PdfRenderResult> RenderComponentAsync<TComponent>(ParameterView parameters,
        Stream destination, Uri? baseUri = null, PdfRenderOptions? options = null,
        CancellationToken cancellationToken = default) where TComponent : IComponent
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var renderer = new HtmlRenderer(services, loggerFactory);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<TComponent>(parameters);
            return root.ToHtmlString();
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        return await pdf.RenderAsync(HtmlSource.FromHtml(html, baseUri), destination, options, cancellationToken).ConfigureAwait(false);
    }
}
