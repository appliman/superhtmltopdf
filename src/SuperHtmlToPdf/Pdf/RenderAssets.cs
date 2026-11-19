using System.Collections.Concurrent;
using System.Text;

namespace SuperHtmlToPdf;

internal sealed class RenderAssets
{
    private static readonly ConcurrentDictionary<(bool Bold, bool Italic), TrueTypeFont> BundledFaces = new();
    private readonly Dictionary<string, Dictionary<(bool Bold, bool Italic), TrueTypeFont>> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Family, bool Bold, bool Italic), TrueTypeFont> _resolved = new();
    private readonly Dictionary<string, PdfRaster> _images = new(StringComparer.Ordinal);
    private readonly CancellationToken _token;

    public RenderAssets(IReadOnlyList<PdfFont> fonts, CancellationToken cancellationToken = default)
    {
        _token = cancellationToken;
        foreach (var font in fonts)
        {
            _token.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(font.Family);
            ArgumentNullException.ThrowIfNull(font.Data);
            if (!_families.TryGetValue(font.Family, out var faces))
            {
                faces = new();
                _families.Add(font.Family, faces);
            }
            faces[(font.Bold, font.Italic)] = new TrueTypeFont((byte[])font.Data.Clone());
        }
    }

    public double Measure(string text, double size, bool bold, bool italic, string family)
    {
        var face = Resolve(family, bold, italic);
        long units = 0;
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if ((count++ & 1023) == 0)
            {
                _token.ThrowIfCancellationRequested();
            }
            units += face.Width(face.Glyph(rune.Value));
        }
        return units * size / 1000;
    }

    public void AddImage(string key, byte[] bytes, long maxPixels)
    {
        if (!_images.ContainsKey(key))
        {
            _images.Add(key, PdfRaster.Read(bytes, maxPixels, _token));
        }
    }

    public bool TryImage(string key, out double width, out double height)
    {
        if (_images.TryGetValue(key, out var image))
        {
            width = image.Width * 0.75;
            height = image.Height * 0.75;
            return true;
        }
        width = height = 0;
        return false;
    }

    internal PdfRaster Image(string key)
    {
        return _images[key];
    }

    internal TrueTypeFont Resolve(string family, bool bold, bool italic)
    {
        var key = (family, bold, italic);
        if (_resolved.TryGetValue(key, out var face))
        {
            return face;
        }
        foreach (var part in family.Split(','))
        {
            var name = part.Trim().Trim('\'', '"');
            if (_families.TryGetValue(name, out var faces))
            {
                if (!faces.TryGetValue((bold, italic), out face) && !faces.TryGetValue((false, false), out face))
                {
                    face = faces.Values.First();
                }
                break;
            }
            if (name.Equals("Arial", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Liberation Sans", StringComparison.OrdinalIgnoreCase)
                || name.Equals("sans-serif", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
        face ??= BundledFaces.GetOrAdd((bold, italic), static variant =>
        {
            var name = variant switch
            {
                (true, true) => "BoldItalic",
                (true, false) => "Bold",
                (false, true) => "Italic",
                _ => "Regular"
            };
            var resource = $"SuperHtmlToPdf.Fonts.LiberationSans-{name}.ttf";
            using var source = typeof(RenderAssets).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded font '{resource}' is missing.");
            using var data = new MemoryStream();
            source.CopyTo(data);
            return new TrueTypeFont(data.ToArray());
        });
        _resolved.Add(key, face);
        return face;
    }
}
