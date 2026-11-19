using System.Globalization;

namespace SuperHtmlToPdf;

internal sealed record PageGeometry(double Width, double Height, double Left, double Top, double Right, double Bottom)
{
    internal const double PointsPerMm = 72 / 25.4;

    internal static PageGeometry Create(PdfRenderOptions options, Dictionary<string, string> css, Action<string, string> warn)
    {
        var size = PdfPageSize.A4;
        var landscape = false;
        if (css.TryGetValue("size", out var sizeValue))
        {
            var parts = sizeValue.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            landscape = parts.Contains("landscape");
            var values = parts.Where(p => p is not ("landscape" or "portrait")).ToArray();
            if (values.Length == 1 && values[0] is "a3" or "a4" or "letter" or "auto")
            {
                size = values[0] switch
                {
                    "a3" => PdfPageSize.A3,
                    "letter" => PdfPageSize.Letter,
                    _ => PdfPageSize.A4
                };
            }
            else if (values.Length is 1 or 2 && Length(values[0]) is double w && Length(values[^1]) is double h)
            {
                size = new(w / PointsPerMm, h / PointsPerMm);
            }
            else if (values.Length > 0)
            {
                warn("page-size", "Format @page hors profil.");
            }
        }
        size = options.PageSize ?? size;
        var width = size.Width * PointsPerMm;
        var height = size.Height * PointsPerMm;
        if (options.Landscape ?? landscape)
        {
            (width, height) = (Math.Max(width, height), Math.Min(width, height));
        }
        else if (options.Landscape == false)
        {
            (width, height) = (Math.Min(width, height), Math.Max(width, height));
        }
        var edges = Enumerable.Repeat(10 * PointsPerMm, 4).ToArray();
        if (css.TryGetValue("margin", out var margin))
        {
            var values = margin.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Length).ToArray();
            if (values.Length is >= 1 and <= 4 && values.All(v => v.HasValue))
            {
                edges = [values[0]!.Value, values.ElementAtOrDefault(1) ?? values[0]!.Value,
                    values.ElementAtOrDefault(2) ?? values[0]!.Value, values.ElementAtOrDefault(3) ?? values.ElementAtOrDefault(1) ?? values[0]!.Value];
            }
            else
            {
                warn("page-margin", "Marge @page non reconnue.");
            }
        }
        string[] names = ["top", "right", "bottom", "left"];
        for (var i = 0; i < names.Length; i++)
        {
            if (css.TryGetValue("margin-" + names[i], out var raw))
            {
                if (Length(raw) is double length)
                {
                    edges[i] = length;
                }
                else
                {
                    warn("page-margin", "Marge @page non reconnue.");
                }
            }
        }
        if (options.Margins is { } m)
        {
            edges = [m.Top * PointsPerMm, m.Right * PointsPerMm, m.Bottom * PointsPerMm, m.Left * PointsPerMm];
        }
        if (!double.IsFinite(width) || !double.IsFinite(height) || width is <= 0 or > 14400 || height is <= 0 or > 14400 ||
            edges.Any(v => !double.IsFinite(v) || v < 0) || edges[1] + edges[3] >= width || edges[0] + edges[2] >= height)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Dimensions de page ou de marges invalides.");
        }
        return new(width, height, edges[3], edges[0], edges[1], edges[2]);
    }

    private static double? Length(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value == "0")
        {
            return 0;
        }
        if (value.Length <= 2 || !double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            return null;
        }
        return value[^2..] switch
        {
            "mm" => number * PointsPerMm,
            "cm" => number * PointsPerMm * 10,
            "in" => number * 72,
            "pt" => number,
            "pc" => number * 12,
            "px" => number * 0.75,
            _ => null
        };
    }
}
