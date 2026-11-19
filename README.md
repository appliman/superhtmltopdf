# SuperHtmlToPdf

SuperHtmlToPdf converts server-rendered HTML to PDF 1.7 on Windows and Linux with .NET 10 and .NET 11. The engine is written in C# and uses the .NET standard libraries. Embedded TrueType fonts keep text measurements consistent between containers and Windows workstations.

## Using the library

The `SuperHtmlToPdf` package contains `SuperHtmlToPdf.dll`. The optional `SuperHtmlToPdf.Blazor` package adds Razor component rendering and references the ASP.NET Core shared framework. The package version is defined in `Directory.Build.props`.

```csharp
using SuperHtmlToPdf;

var html = """
    <h1>Participation report</h1>
    <p>Spring session · 48 participants</p>
    """;

var renderer = new HtmlToPdfRenderer();
await using var output = File.Create("report.pdf");
var result = await renderer.RenderAsync(HtmlSource.FromHtml(html), output,
    new PdfRenderOptions
    {
        PageSize = PdfPageSize.A4,
        Margins = new PdfMargins(12, 10, 12, 10),
        Footer = PdfPageBand.Text("Page [page] / [topage]", heightMm: 10)
    });
Console.WriteLine($"{result.PageCount} page(s)");
```

For a server-rendered page, use `HtmlSource.FromUrl(uri)` and add its origin to `Resources.AllowedOrigins`. For a file, use `HtmlSource.FromFile(path)` and list the directories it may load images or stylesheets from in `Resources.AllowedDirectories`. For an HTML string, provide an absolute base URI to resolve relative paths. The caller owns the stream. Non-seekable streams, such as an HTTP response, are supported.

### Rendering a Razor component

In the host application, register `HtmlToPdfRenderer` as the singleton implementation of `IHtmlToPdfRenderer`, and register `BlazorPdfRenderer` as a scoped service. Example in a service that receives `BlazorPdfRenderer` through dependency injection:

```csharp
var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
{
    ["Report"] = report
});
await renderer.RenderComponentAsync<ParticipationReport>(
    parameters, output, baseUri: new Uri("https://reports.example/"),
    options: new PdfRenderOptions
    {
        Resources = new() { AllowedOrigins = [new Uri("https://reports.example")] },
        Footer = PdfPageBand.Text("Page [page] / [topage]", 10)
    },
    cancellationToken: cancellationToken);
```

`Report` and `ParticipationReport` represent the application's model and component. The adapter waits for asynchronous rendering to finish. Link the isolated stylesheet in the HTML document or add the compiled CSS to `StyleSheets`. The component receives services from its dependency injection scope. URL endpoints must return the final content in the server-rendered response. Components that use JavaScript, a canvas, an interactive circuit, or streaming SSR with DOM updates to build their output must provide a static print view.

## Compatibility profile

| Feature | Supported behavior |
|---|---|
| Inputs and output | UTF-8 HTML, files, allowed HTTP(S) URLs, Razor components, PDF streams, cancellation, and diagnostics. |
| Pages | A3, A4, Letter, custom dimensions, orientation, margins, `@page`, `@media print`, page breaks, and repeating `thead` elements. API dimensions are in millimeters. Explicit page size or margin options override the corresponding CSS rules. |
| Headers and footers | Text, HTML, or a URL. Height and spacing are reserved within the margins before pagination. The engine replaces `[page]` and `[topage]` after pagination. For a URL, the server receives `page` and `topage` parameters. A header or footer that is too tall produces a diagnostic. |
| HTML and CSS | Cascade, inline and external styles, inheritance, class, ID and attribute selectors, common pseudo-classes, block and inline layout, floats, borders, nested tables, and merged cells. |
| Flexbox and Grid | Axes, wrapping, ordering, alignment, space distribution, and gaps. Grid supports cell placement, explicit and implicit tracks, `fr` units, `minmax()`, `repeat()`, and spans within the engine's compatibility profile. Both sets of fixtures exercise page breaks. |
| Text and images | European Latin scripts, TrueType measurement, and selectable text with Unicode mapping. Liberation Sans regular, bold, italic, and bold italic are bundled with their license. Register additional fonts with `PdfFont`. PNG and JPEG images, PNG transparency, raster QR codes, and base64 data URIs are supported. |
| Access and limits | HTTP origins and directories must be explicitly allowed. Redirect targets are checked again. Application cookies are not forwarded implicitly. Configurable limits cover resources, bytes, total pixels, nodes, depth, pages, and rendering time. |
| Legacy options | The package prints server-rendered documents. Scripts, interactive PDF forms, automatic tables of contents, bookmarks, multi-input covers, and the wkhtmltopdf CLI are outside the current scope. |

In `Strict` mode, a rendering diagnostic raises `PdfRenderException` before the PDF is written. In normal mode, inspect `PdfRenderResult.Diagnostics`. The engine implements a print-oriented CSS subset. Verify advanced browser features against the templates that use them. Tax or archival documents require the application's downstream PDF/A conversion and enrichment.

Rounded corners, CSS clipping, fixed and sticky positioning, and named grid areas are among the limits of this preview. For reproducible typography, register the TrueType variants you use and check that they contain the required glyphs. Acceptance testing covers European Latin scripts.

## Implementation and acceptance test plan

The solution `SuperHtmlToPdf.slnx` is at the repository root. All C# code lives under `src` in the `SuperHtmlToPdf`, `SuperHtmlToPdf.Blazor`, and `SuperHtmlToPdf.Tests` projects.

1. **Define the compatibility profile.** Build an anonymized corpus from the ActuAsso participation report, the Appliman invoice, the StockAsso2 order, and the AuditStock supplier report. Identify the wkhtmltopdf options and CSS rules those documents need. Add dedicated Flexbox and Grid fixtures.
2. **Build the PDF generator.** Produce PDF 1.7 in C# with selectable text, embedded TrueType fonts, Unicode mappings, vector drawing, and images. Check accented characters, the euro sign, non-breaking spaces, transparent logos, and QR codes.
3. **Render server-rendered documents.** Implement HTML, the CSS cascade, Blazor CSS isolation, block and inline layout, floats, nested tables, and merged cells. Print the ActuAsso report from Razor and the AuditStock report in A3 landscape.
4. **Implement pagination.** Support print and break rules, repeating table headers, and reserved space for headers and footers. Render their HTML once the page count is known. Verify footers loaded from a URL and page numbers on multi-page invoices.
5. **Validate Flexbox and Grid.** Check axes, alignment, wrapping, space distribution, tracks, automatic placement, and spanning cells. Exercise page breaks and elements taller than the printable area.
6. **Package and validate.** Install both packages in fresh applications on Windows and Linux with both runtimes. Check `lib/net10.0/SuperHtmlToPdf.dll` and `lib/net11.0/SuperHtmlToPdf.dll`, and run a test in a Linux container.

Given identical fonts and resources, both operating systems must produce the same page breaks, page count, and text. Use an independent PDF reader to check file structure, Unicode extraction, and rasterized pages. Acceptance testing also covers allowlists, redirects, resource limits, and diagnostics in strict mode.

The first migration target is the ActuAsso participation report, including its logo, tables, and "Page X / Y" footer. AuditStock is next, followed by StockAsso2. For Appliman invoices, validate the Factur-X-enriched PDF/A-3 output before removing wkhtmltopdf from the application.

### Initial acceptance test results

Fresh NuGet consumer projects produced byte-for-byte identical files on Windows and in .NET 10 and .NET 11 Linux containers for five HTML fixtures and one Razor component. A separate test of the original ActuAsso Razor component verified three pages, 88 synthetic participation records, repeated table headers, and page numbering.

The invoice fixture went through Ghostscript conversion and the application's Factur-X enrichment. The embedded EN16931 XML passed XSD and Schematron validation. However, the PDF/A-3b validator rejected the final PDF because `Creator` and `Producer` differed between the Info dictionary and XMP metadata. The issue also occurs with an independent input PDF. Synchronizing those two fields in a test copy passed PDF/A-3b validation. Migrating invoices therefore depends on a fix in the downstream exporter.

## Building locally

Use the .NET 11 SDK and the .NET and ASP.NET Core runtimes for both .NET 10 and .NET 11:

```shell
dotnet test SuperHtmlToPdf.slnx -c Release
dotnet pack SuperHtmlToPdf.slnx -c Release -o artifacts/packages
```

Optionally, set `SUPERHTMLTOPDF_INSPECTION_DIR` to a directory where the tests can write the six fixture PDFs and their diagnostics for independent inspection. Use a different directory for each run when comparing runtimes.

## Publishing to NuGet.org

The [Publish to NuGet.org workflow](.github/workflows/publish-nuget.yml) runs manually on `main`. It builds and tests both target frameworks, then publishes the resulting `SuperHtmlToPdf` and `SuperHtmlToPdf.Blazor` packages.

1. Create the `nuget-org` GitHub environment and restrict deployments to the `main` branch. Set its `NUGET_USER` secret to the NuGet.org profile name (not the email address) for the account authorized by the publishing policy.
2. In that NuGet.org account, create a Trusted Publishing policy for the GitHub owner `appliman`, repository `superhtmltopdf`, workflow file `publish-nuget.yml`, and environment `nuget-org`. Allow publishing new packages for the first release, then restrict the policy to new versions of the two package IDs. The workflow uses OpenID Connect to obtain a short-lived API key; no long-lived API key is required.
3. Set the intended release version in `Directory.Build.props` on `main`. In GitHub Actions, select **Publish to NuGet.org** and run it on `main`. Existing package versions are skipped. Bump the version to publish changed content.
