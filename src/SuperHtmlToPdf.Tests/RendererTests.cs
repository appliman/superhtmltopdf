using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SuperHtmlToPdf.Blazor;

namespace SuperHtmlToPdf.Tests;

[TestClass]
public sealed class RendererTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [TestMethod]
    [DataRow("invoice", 2)]
    [DataRow("order", 1)]
    [DataRow("audit", 2)]
    [DataRow("flex", 1)]
    [DataRow("grid", 2)]
    public async Task DocumentProducesPortablePdf(string fixture, int minimumPages)
    {
        using var destination = new MemoryStream();
        var result = await new HtmlToPdfRenderer().RenderAsync(HtmlSource.FromFile(Path.Combine(Fixtures, fixture + ".html")),
            destination, Options());
        Assert.IsGreaterThanOrEqualTo(minimumPages, result.PageCount);
        CheckStructure(destination.ToArray(), result);
        await SaveForInspection(fixture, destination, result);
    }

    [TestMethod]
    public async Task RendersAsyncRazorComponentWithLogoTableAndPageNumbers()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var renderer = new BlazorPdfRenderer(services, services.GetRequiredService<ILoggerFactory>(), new HtmlToPdfRenderer());
        var logo = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "logo.png"));
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Logo"] = "data:image/png;base64," + Convert.ToBase64String(logo),
            ["Participants"] = 92
        });
        using var destination = new MemoryStream();
        var result = await renderer.RenderComponentAsync<ParticipationReport>(parameters, destination, options: Options());
        Assert.IsGreaterThan(1, result.PageCount);
        CheckStructure(destination.ToArray(), result);
        await SaveForInspection("participation", destination, result);
    }

    [TestMethod]
    public async Task StrictModeRejectsUnsupportedCssBeforeWritingPdf()
    {
        using var destination = new MemoryStream();
        var exception = await Assert.ThrowsExactlyAsync<PdfRenderException>(() => new HtmlToPdfRenderer().RenderAsync(
            HtmlSource.FromHtml("<p style='invented-option:1'>Texte</p>"), destination, new()
            {
                Strict = true
            }));
        Assert.IsNotEmpty(exception.Diagnostics);
        Assert.AreEqual(0, destination.Length);
    }

    [TestMethod]
    public async Task FileAssetsRequireExplicitlyAllowedDirectory()
    {
        using var destination = new MemoryStream();
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => new HtmlToPdfRenderer().RenderAsync(
            HtmlSource.FromFile(Path.Combine(Fixtures, "invoice.html")), destination));
        Assert.AreEqual(0, destination.Length);
    }

    [TestMethod]
    public async Task EnforcesInputAndPixelBudgets()
    {
        using var destination = new MemoryStream();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new HtmlToPdfRenderer().RenderAsync(
            HtmlSource.FromHtml("<p>Budget</p>"), destination, new()
            {
                Resources = new()
                {
                    MaxResourceBytes = 5
                }
            }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new HtmlToPdfRenderer().RenderAsync(
            HtmlSource.FromFile(Path.Combine(Fixtures, "invoice.html")), destination,
            new()
            {
                Resources = new()
                {
                    AllowedDirectories = [Fixtures],
                    MaxImagePixels = 100
                }
            }));
    }

    [TestMethod]
    public async Task HtmlSourceRejectsUnauthorizedNetworkAndHonorsCancellation()
    {
        using var destination = new MemoryStream();
        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => new HtmlToPdfRenderer().RenderAsync(
            HtmlSource.FromUrl(new Uri("https://example.invalid/report")), destination));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new HtmlToPdfRenderer().RenderAsync(
            HtmlSource.FromHtml("<p>Document</p>"), destination, cancellationToken: stop.Token));
    }

    [TestMethod]
    public async Task SupportsNonSeekableOutputAndConcurrentDocuments()
    {
        var renderer = new HtmlToPdfRenderer();
        var results = await Task.WhenAll(Enumerable.Range(1, 4).Select(async i =>
        {
            using var stream = new ForwardOnlyStream();
            var result = await renderer.RenderAsync(HtmlSource.FromHtml($"<p>Rapport {i}</p>"), stream);
            return (stream.Bytes, result);
        }));
        foreach (var (bytes, result) in results)
        {
            Assert.AreEqual(1, result.PageCount);
            CheckStructure(bytes, result);
        }
    }

    private static PdfRenderOptions Options() => new()
    {
        Resources = new()
        {
            AllowedDirectories = [Fixtures]
        },
        Footer = PdfPageBand.Text("Page [page] / [topage]", 12, 2)
    };

    private static void CheckStructure(byte[] bytes, PdfRenderResult result)
    {
        var pdf = Encoding.Latin1.GetString(bytes);
        StringAssert.StartsWith(pdf, "%PDF-1.7");
        StringAssert.Contains(pdf, "/ToUnicode");
        StringAssert.Contains(pdf, "/FontFile2");
        StringAssert.Contains(pdf, "%%EOF");
        Assert.IsGreaterThan(0, result.PageCount);
    }

    private static async Task SaveForInspection(string name, MemoryStream stream, PdfRenderResult result)
    {
        var folder = Environment.GetEnvironmentVariable("SUPERHTMLTOPDF_INSPECTION_DIR");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(Path.Combine(folder, name + ".pdf"), stream.ToArray());
            await File.WriteAllLinesAsync(Path.Combine(folder, name + ".diagnostics"),
                result.Diagnostics.Select(d => $"{d.Code}: {d.Message}"));
        }
    }

    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _output = new();
        public byte[] Bytes => _output.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override void Flush() => _output.Flush();
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => _output.WriteAsync(buffer, token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _output.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
