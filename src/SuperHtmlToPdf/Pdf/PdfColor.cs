using System.Drawing;
using System.Globalization;

namespace SuperHtmlToPdf;

internal readonly record struct PdfColor(byte R, byte G, byte B, byte A)
{
    public static PdfColor Parse(string value)
    {
        var input = value.Trim();
        if (input.StartsWith('#'))
        {
            var hex = input[1..];
            if (hex.Length is 3 or 4)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }
            if (hex.Length is 6 or 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var color))
            {
                if (hex.Length == 6)
                {
                    color = (color << 8) | 255;
                }
                return new PdfColor((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
            }
        }
        if ((input.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) || input.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase))
            && input.EndsWith(')'))
        {
            var channels = input[(input.IndexOf('(') + 1)..^1]
                .Split([',', ' ', '\t', '\r', '\n', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (channels.Length is 3 or 4)
            {
                return new PdfColor(Channel(channels[0], false), Channel(channels[1], false),
                    Channel(channels[2], false), channels.Length == 4 ? Channel(channels[3], true) : (byte)255);
            }
        }
        var named = Color.FromName(input);
        return named.IsKnownColor
            ? new PdfColor(named.R, named.G, named.B, named.A)
            : new PdfColor(0, 0, 0, 255);
    }

    private static byte Channel(string value, bool alpha)
    {
        var percentage = value.EndsWith('%');
        if (!double.TryParse(percentage ? value[..^1] : value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !double.IsFinite(number))
        {
            return 0;
        }
        var scale = percentage ? 255.0 / 100 : alpha ? 255.0 : 1.0;
        return (byte)Math.Round(Math.Clamp(number * scale, 0, 255));
    }
}
