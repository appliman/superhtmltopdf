using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SuperHtmlToPdf.Tests;

[TestClass]
public sealed class HttpRenderTests
{
    [TestMethod]
    public async Task LoadsFinalSsrAndPassesPageNumbersToFooterAfterRedirect()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var requested = new List<string>();
        var cssRequests = 0;
        app.MapGet("/report", () => Results.Redirect("/docs/body"));
        app.MapGet("/docs/body", () => Results.Text("<html><head><link rel='stylesheet' href='report.css'></head>" +
            "<body><p>Première</p><p class='second'>Deuxième</p></body></html>", "text/html"));
        app.MapGet("/docs/report.css", () =>
        {
            Interlocked.Increment(ref cssRequests);
            return Results.Text(".second {break-before:page}", "text/css");
        });
        app.MapGet("/footer", (HttpRequest request) =>
        {
            var query = request.Query;
            requested.Add($"{query["code"]}:{query["page"]}/{query["topage"]}");
            return Results.Text($"<p>Page {query["page"]} sur {query["topage"]}</p>", "text/html");
        });
        app.MapGet("/outside", () => Results.Redirect("https://example.invalid/blocked"));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var origin = new Uri(address);
            var options = new PdfRenderOptions
            {
                Resources = new() { AllowedOrigins = [origin] },
                Footer = new(HtmlSource.FromUrl(new Uri(origin, "/footer?code=test&page=0")), 15)
            };
            using var stream = new MemoryStream();
            var renderer = new HtmlToPdfRenderer();
            var result = await renderer.RenderAsync(HtmlSource.FromUrl(new Uri(origin, "/report")), stream, options);
            Assert.AreEqual(2, result.PageCount);
            CollectionAssert.AreEqual(new[] { "test:1/2", "test:2/2" }, requested);
            Assert.AreEqual(1, cssRequests);
            using var denied = new MemoryStream();
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => renderer.RenderAsync(
                HtmlSource.FromUrl(new Uri(origin, "/outside")), denied, options));
            Assert.AreEqual(0, denied.Length);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
