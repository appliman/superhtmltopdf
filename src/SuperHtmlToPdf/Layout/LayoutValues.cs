using System.Globalization;

namespace SuperHtmlToPdf;

internal readonly record struct BoxEdges(double Left, double Top, double Right, double Bottom)
{
    public double Horizontal => Left + Right;
    public double Vertical => Top + Bottom;
}

internal static class LayoutValues
{
    // Larger values have no practical printed representation and can destabilize layout arithmetic.
    private const double MaxCssMagnitude = 1_000_000_000;

    public static string Css(this Element el, string property, string fallback = "")
        => el.Style.TryGetValue(property, out var value) && value.Length != 0 ? value : fallback;

    public static string Attr(this Element el, string name, string fallback = "")
        => el.Attributes.GetValueOrDefault(name, fallback);

    public static double Number(string value, double fallback = 0)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? Checked(number) : fallback;

    private static double Checked(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > MaxCssMagnitude)
        {
            throw new InvalidDataException("CSS numeric value is outside the supported layout range.");
        }

        return value;
    }

    public static double Length(string css, double reference, double font, double fallback = 0)
    {
        var value = css.Trim().ToLowerInvariant();
        if (value.StartsWith("calc(") && value.EndsWith(')'))
        {
            var parts = value[5..^1].Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && parts[1] is "+" or "-")
            {
                var a = PlainLength(parts[0], reference, font, double.NaN);
                var b = PlainLength(parts[2], reference, font, double.NaN);
                if (!double.IsFinite(a) || !double.IsFinite(b))
                {
                    return fallback;
                }

                return Checked(parts[1] == "+" ? a + b : a - b);
            }
            return fallback;
        }
        return PlainLength(value, reference, font, fallback);
    }

    private static double PlainLength(string value, double reference, double font, double fallback)
    {
        if (value is "thin" or "medium" or "thick")
        {
            return value switch
            {
                "thin" => 0.75,
                "thick" => 3.75,
                _ => 2.25
            };
        }

        if (value.EndsWith('%'))
        {
            return Scaled(value.AsSpan(0, value.Length - 1), reference / 100);
        }

        foreach (var (suffix, scale) in Units)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal))
            {
                return Scaled(value.AsSpan(0, value.Length - suffix.Length), suffix == "em" ? font : scale);
            }
        }

        return Scaled(value.AsSpan(), 0.75);

        double Scaled(ReadOnlySpan<char> token, double factor)
            => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                ? Checked(Checked(n) * factor) : fallback;
    }

    private static readonly (string, double)[] Units =
    [
        ("rem", 12), ("em", 1), ("pt", 1), ("px", 0.75), ("mm", 72 / 25.4),
        ("cm", 72 / 2.54), ("in", 72), ("pc", 12), ("q", 72 / 101.6)
    ];

    public static double FontSize(this Element el) => Math.Max(0.1, Length(el.Css("font-size", "12pt"), 12, 12, 12));
    public static double LineHeight(this Element el)
    {
        var font = el.FontSize();
        var value = el.Css("line-height");
        var multiple = Number(value, double.NaN);
        var height = double.IsNaN(multiple) ? Length(value, font, font, 1.2 * font) : Checked(font * multiple);
        return Math.Max(0.1, height);
    }

    public static bool Bold(this Element el)
    {
        var weight = el.Css("font-weight");
        return weight is "bold" or "bolder" || Number(weight) >= 600;
    }

    public static bool Italic(this Element el) => el.Css("font-style") is "italic" or "oblique";
    public static string Family(this Element el) => el.Css("font-family", "sans-serif");
    public static BoxEdges Edges(this Element el, string name, double reference)
    {
        double Side(string side)
        {
            var font = el.FontSize();
            if (name != "border")
            {
                var size = Length(el.Css(name + "-" + side), reference, font);
                return name == "margin" ? size : Math.Max(0, size);
            }
            var style = el.Css("border-" + side + "-style", "none");
            return style is "none" or "hidden" ? 0 :
                Math.Max(0, Length(el.Css("border-" + side + "-width", "medium"), reference, font));
        }
        return new BoxEdges(Side("left"), Side("top"), Side("right"), Side("bottom"));
    }

    public static int Integer(string value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    public static List<string> SplitTracks(string value, int maxTokens = 1024)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length)
            {
                if (value[i] == '(' && ++depth > 16)
                {
                    throw new InvalidDataException("CSS track functions exceed the nesting limit.");
                }

                if (value[i] == ')')
                {
                    depth--;
                }

                if (depth > 0 || !char.IsWhiteSpace(value[i]))
                {
                    continue;
                }
            }
            if (start < i)
            {
                if (result.Count >= maxTokens)
                {
                    throw new InvalidDataException("CSS track list exceeds the item limit.");
                }

                result.Add(value[start..i]);
            }
            start = i + 1;
        }
        return result;
    }
}
