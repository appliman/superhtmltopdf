namespace SuperHtmlToPdf;

internal abstract record Drawing
{
    internal Drawing Translate(double x, double y)
    {
        return this switch
        {
            DrawText text => text with { X = text.X + x, Baseline = text.Baseline + y },
            DrawRect rect => rect with { X = rect.X + x, Y = rect.Y + y },
            DrawImage image => image with { X = image.X + x, Y = image.Y + y },
            _ => throw new InvalidOperationException()
        };
    }
}

internal sealed record DrawText(double X, double Baseline, string Value, double Size, bool Bold,
    bool Italic, string Color, string Family) : Drawing;
internal sealed record DrawRect(double X, double Y, double Width, double Height, string? Fill,
    string? Stroke, double StrokeWidth) : Drawing;
internal sealed record DrawImage(double X, double Y, double Width, double Height, string Key) : Drawing;
