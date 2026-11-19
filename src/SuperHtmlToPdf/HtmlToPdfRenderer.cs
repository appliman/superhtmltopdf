using System.Globalization;

namespace SuperHtmlToPdf;

/// <summary>Renders HTML to PDF without sharing state between conversions.</summary>
public sealed class HtmlToPdfRenderer : IHtmlToPdfRenderer
{
    public async Task<PdfRenderResult> RenderAsync(HtmlSource source, Stream destination,
        PdfRenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Le flux de destination doit accepter les écritures.", nameof(destination));
        }
        options ??= new();
        var limits = options.Resources;
        if (limits.MaxResourceBytes <= 0 || limits.MaxTotalBytes <= 0 || limits.MaxImagePixels <= 0 ||
            limits.MaxResources <= 0 || limits.MaxNodes <= 0 || limits.MaxDepth is < 1 or > 256 ||
            limits.MaxPages <= 0 || limits.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Limites de rendu invalides.");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.Timeout);
        var job = new RenderJob(options, deadline.Token);
        return await job.Run(source, destination).ConfigureAwait(false);
    }

    private sealed class RenderJob(PdfRenderOptions options, CancellationToken token)
    {
        private readonly ResourceLoader _resources = new(options.Resources, token);
        private readonly RenderAssets _assets = new(options.Fonts, token);
        private readonly List<PdfDiagnostic> _diagnostics = [];
        private readonly HashSet<PdfDiagnostic> _reported = [];
        private readonly Dictionary<Uri, string> _sheets = [];
        private readonly Dictionary<string, string> _images = new(StringComparer.Ordinal);
        private long _imagePixels;

        internal async Task<PdfRenderResult> Run(HtmlSource source, Stream destination)
        {
            var body = await Prepare(source).ConfigureAwait(false);
            var page = PageGeometry.Create(options, body.Page, Warn);
            var header = BandSize(options.Header);
            var footer = BandSize(options.Footer);
            var width = page.Width - page.Left - page.Right;
            var height = page.Height - page.Top - page.Bottom - header - footer;
            if (height <= 0)
            {
                throw new ArgumentException("Les bandes et marges occupent toute la page.");
            }
            var bodyPages = Layout(body.Root, width, height);
            var pages = new List<List<Drawing>>(bodyPages.Count);
            for (int index = 0; index < bodyPages.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var sheet = new List<Drawing>();
                await PaintBand(options.Header, index + 1, bodyPages.Count, page.Left, page.Top, width, sheet).ConfigureAwait(false);
                sheet.AddRange(bodyPages[index].Select(d => d.Translate(page.Left, page.Top + header)));
                var footerTop = page.Height - page.Bottom - (options.Footer?.HeightMm ?? 0) * PageGeometry.PointsPerMm;
                await PaintBand(options.Footer, index + 1, bodyPages.Count, page.Left, footerTop, width, sheet).ConfigureAwait(false);
                pages.Add(sheet);
            }
            token.ThrowIfCancellationRequested();
            await PdfWriter.WriteAsync(pages, page.Width, page.Height, _assets, destination, token).ConfigureAwait(false);
            return new(pages.Count, _diagnostics.AsReadOnly());
        }

        private static double BandSize(PdfPageBand? band)
        {
            if (band == null)
            {
                return 0;
            }
            if (!double.IsFinite(band.HeightMm) || band.HeightMm <= 0 || !double.IsFinite(band.SpacingMm) || band.SpacingMm < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(band), "Dimensions de bande invalides.");
            }
            return (band.HeightMm + band.SpacingMm) * PageGeometry.PointsPerMm;
        }

        private List<List<Drawing>> Layout(Element root, double width, double height)
        {
            return new LayoutEngine(_assets, Warn, token).Paginate(root, width, height, options.Resources.MaxPages);
        }

        private async Task PaintBand(PdfPageBand? band, int page, int total, double left, double top, double width, List<Drawing> output)
        {
            if (band == null)
            {
                return;
            }
            var source = band.Source;
            if (source is HtmlSource.WebPage remote)
            {
                var builder = new UriBuilder(remote.Uri);
                var query = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Where(q => Uri.UnescapeDataString(q.Split('=', 2)[0]).ToLowerInvariant() is not ("page" or "topage"));
                builder.Query = string.Join("&", query.Append(FormattableString.Invariant($"page={page}&topage={total}")));
                source = HtmlSource.FromUrl(builder.Uri);
            }
            var doc = await Prepare(source, page, total).ConfigureAwait(false);
            var parts = Layout(doc.Root, width, band.HeightMm * PageGeometry.PointsPerMm);
            if (parts.Count > 1)
            {
                Warn("band-overflow", "Le contenu de l’en-tête ou du pied dépasse la hauteur réservée.");
            }
            output.AddRange(parts[0].Select(d => d.Translate(left, top)));
        }

        private async Task<(Element Root, Dictionary<string, string> Page)> Prepare(HtmlSource source, int page = 0, int total = 0)
        {
            var (text, baseUri) = await _resources.Document(source).ConfigureAwait(false);
            if (page > 0)
            {
                text = text.Replace("[page]", page.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                    .Replace("[topage]", total.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
            }
            var root = HtmlParser.Parse(text, options.Resources.MaxNodes, options.Resources.MaxDepth, token);
            var elements = root.DescendantsAndSelf().ToList();
            var documentBase = elements.FirstOrDefault(e => e.Tag == "base" && e.Attributes.ContainsKey("href"));
            if (documentBase != null && Uri.TryCreate(baseUri, documentBase.Attributes["href"], out var resolvedBase))
            {
                baseUri = resolvedBase;
            }
            var sheets = new List<string>();
            foreach (var el in elements)
            {
                token.ThrowIfCancellationRequested();
                string? css = null;
                if (el.Tag == "style")
                {
                    css = string.Concat(el.Children.Select(n => n.Text));
                }
                else if (el.Tag == "link" && el.Attributes.GetValueOrDefault("rel", "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("stylesheet", StringComparer.OrdinalIgnoreCase) && el.Attributes.TryGetValue("href", out var href))
                {
                    if (!Uri.TryCreate(baseUri, href, out var uri))
                    {
                        Warn("css-uri", "URI de feuille de style non résolue.");
                        continue;
                    }
                    if (!_sheets.TryGetValue(uri, out css))
                    {
                        var resource = await _resources.Load(uri).ConfigureAwait(false);
                        css = ResourceLoader.Decode(resource.Bytes);
                        _sheets.Add(uri, css);
                    }
                }
                if (css != null)
                {
                    sheets.Add(el.Attributes.TryGetValue("media", out var media) && media.Length > 0
                        ? "@media " + media + "{" + css + "}" : css);
                }
            }
            sheets.AddRange(options.StyleSheets);
            var pageCss = CssCascade.Apply(root, sheets, Warn, token);
            foreach (var image in elements.Where(e => e.Tag == "img" && e.Attributes.ContainsKey("src")))
            {
                if (!IsVisible(image))
                {
                    continue;
                }
                await RegisterImage(image, baseUri).ConfigureAwait(false);
            }
            return (root, pageCss);
        }

        private static bool IsVisible(Element element)
        {
            for (Element? node = element; node != null; node = node.Parent)
            {
                if (node.Style.GetValueOrDefault("display") == "none" || node.Attributes.ContainsKey("hidden"))
                {
                    return false;
                }
            }
            return true;
        }

        private async Task RegisterImage(Element element, Uri? baseUri)
        {
            var raw = element.Attributes["src"].Trim();
            var inline = raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
            Uri? uri = null;
            if (!inline && (raw.Length == 0 || !Uri.TryCreate(baseUri, raw, out uri)))
            {
                Warn("image-uri", "URI d’image non résolue.");
                return;
            }
            var location = inline ? raw : uri!.AbsoluteUri;
            if (!_images.TryGetValue(location, out var key))
            {
                var bytes = inline ? _resources.InlineData(raw) : (await _resources.Load(uri!).ConfigureAwait(false)).Bytes;
                key = "I" + _images.Count.ToString(CultureInfo.InvariantCulture);
                var available = options.Resources.MaxImagePixels - _imagePixels;
                if (available <= 0)
                {
                    throw new InvalidDataException("Budget total de pixels dépassé.");
                }
                _assets.AddImage(key, bytes, available);
                _assets.TryImage(key, out var w, out var h);
                _imagePixels += checked((long)Math.Round(w / 0.75) * (long)Math.Round(h / 0.75));
                _images.Add(location, key);
            }
            element.Attributes["src"] = key;
        }

        private void Warn(string code, string message)
        {
            var warning = new PdfDiagnostic(code, message);
            if (options.Strict)
            {
                throw new PdfRenderException(message, [warning]);
            }
            if (_diagnostics.Count < 256 && _reported.Add(warning))
            {
                _diagnostics.Add(warning);
            }
        }
    }
}
