using System.Globalization;
using System.Text;

namespace SuperHtmlToPdf;

internal static class PdfWriter
{
    public static async Task WriteAsync(IReadOnlyList<List<Drawing>> pages, double width, double height,
        RenderAssets assets, Stream destination, CancellationToken cancellationToken)
    {
        var fonts = new Dictionary<TrueTypeFont, PdfFontResource>();
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        var colors = new Dictionary<string, PdfColor>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            foreach (var drawing in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (drawing)
                {
                    case DrawText text:
                        var face = assets.Resolve(text.Family, text.Bold, text.Italic);
                        if (!fonts.TryGetValue(face, out var font))
                        {
                            font = new PdfFontResource(face, fonts.Count + 1, text.Italic);
                            fonts.Add(face, font);
                        }
                        font.Collect(text.Value, cancellationToken);
                        CollectColor(colors, text.Color);
                        break;
                    case DrawRect rect:
                        CollectColor(colors, rect.Fill);
                        CollectColor(colors, rect.Stroke);
                        break;
                    case DrawImage image:
                        images.TryAdd(image.Key, "I" + (images.Count + 1).ToString(CultureInfo.InvariantCulture));
                        break;
                }
            }
        }

        var writer = new PdfObjectWriter(destination, cancellationToken);
        var catalog = writer.Reserve();
        var tree = writer.Reserve();
        await writer.Start();
        var resources = new StringBuilder("<< /Font << ");
        foreach (var font in fonts.Values)
        {
            var id = await font.Write(writer);
            resources.Append('/').Append(font.Name).Append(' ').Append(id).Append(" 0 R ");
        }
        resources.Append(">> /XObject << ");
        foreach (var (key, name) in images)
        {
            var id = await WriteImage(writer, assets.Image(key));
            resources.Append('/').Append(name).Append(' ').Append(id).Append(" 0 R ");
        }
        resources.Append(">> /ExtGState << ");
        foreach (var alpha in colors.Values.Select(color => color.A).Where(alpha => alpha < 255).Distinct())
        {
            var id = writer.Reserve();
            await writer.Object(id, $"<< /Type /ExtGState /ca {Number(alpha / 255d)} /CA {Number(alpha / 255d)} >>");
            resources.Append("/A").Append(alpha).Append(' ').Append(id).Append(" 0 R ");
        }
        resources.Append(">> >>");
        var shared = writer.Reserve();
        await writer.Object(shared, resources.ToString());
        var children = new StringBuilder();
        foreach (var page in pages)
        {
            var contents = await writer.CompressedStream("", stream => WritePage(stream, page));
            var pageId = writer.Reserve();
            await writer.Object(pageId, $"<< /Type /Page /Parent {tree} 0 R /Contents {contents} 0 R >>");
            children.Append(pageId).Append(" 0 R ");
        }
        await writer.Object(tree, $"<< /Type /Pages /Count {pages.Count} /Kids [{children}] "
            + $"/Resources {shared} 0 R /MediaBox [0 0 {Number(width)} {Number(height)}] >>");
        await writer.Object(catalog, $"<< /Type /Catalog /Pages {tree} 0 R >>");
        await writer.Finish(catalog);

        async Task WritePage(Stream stream, List<Drawing> page)
        {
            var content = new StringBuilder();
            async Task Flush()
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(content.ToString()), cancellationToken);
                content.Clear();
            }
            foreach (var drawing in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (drawing)
                {
                    case DrawText text:
                        var font = fonts[assets.Resolve(text.Family, text.Bold, text.Italic)];
                        Color(content, colors[text.Color], false);
                        content.Append("BT /").Append(font.Name).Append(' ').Append(Number(text.Size)).Append(" Tf 1 0 0 1 ")
                            .Append(Number(text.X)).Append(' ').Append(Number(height - text.Baseline)).Append(" Tm ");
                        for (var start = 0; start < text.Value.Length;)
                        {
                            var count = Math.Min(4096, text.Value.Length - start);
                            if (start + count < text.Value.Length && char.IsHighSurrogate(text.Value[start + count - 1]))
                            {
                                count--;
                            }
                            font.Encode(content, text.Value.AsSpan(start, count));
                            content.Append(" Tj ");
                            start += count;
                            if (content.Length >= 32768)
                            {
                                await Flush();
                            }
                        }
                        content.Append("ET Q\n");
                        break;
                    case DrawRect rect:
                        var path = $"{Number(rect.X)} {Number(height - rect.Y - rect.Height)} {Number(rect.Width)} {Number(rect.Height)} re ";
                        if (rect.Fill is not null)
                        {
                            Color(content, colors[rect.Fill], false);
                            content.Append(path).Append("f Q\n");
                        }
                        if (rect.Stroke is not null && rect.StrokeWidth > 0)
                        {
                            Color(content, colors[rect.Stroke], true);
                            content.Append(Number(rect.StrokeWidth)).Append(" w ").Append(path).Append("S Q\n");
                        }
                        break;
                    case DrawImage image:
                        content.Append("q ").Append(Number(image.Width)).Append(" 0 0 ").Append(Number(image.Height))
                            .Append(' ').Append(Number(image.X)).Append(' ').Append(Number(height - image.Y - image.Height))
                            .Append(" cm /").Append(images[image.Key]).Append(" Do Q\n");
                        break;
                }
                if (content.Length >= 32768)
                {
                    await Flush();
                }
            }
            await Flush();
        }
    }

    private static async Task<int> WriteImage(PdfObjectWriter writer, PdfRaster image)
    {
        var basis = $"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /BitsPerComponent 8";
        var colorSpace = image.Components switch
        {
            1 => "DeviceGray",
            3 => "DeviceRGB",
            _ => "DeviceCMYK"
        };
        var attributes = basis + " /ColorSpace /" + colorSpace;
        if (image.Alpha is not null)
        {
            var mask = await writer.StreamObject(basis + " /ColorSpace /DeviceGray", image.Alpha, true);
            attributes += $" /SMask {mask} 0 R";
        }
        if (image.IsJpeg)
        {
            attributes += " /Filter /DCTDecode";
        }
        if (image.InvertedCmyk)
        {
            attributes += " /Decode [1 0 1 0 1 0 1 0]";
        }
        return await writer.StreamObject(attributes, image.Data, !image.IsJpeg);
    }

    private static void CollectColor(Dictionary<string, PdfColor> colors, string? value)
    {
        if (value is not null && !colors.ContainsKey(value))
        {
            colors.Add(value, PdfColor.Parse(value));
        }
    }

    private static void Color(StringBuilder content, PdfColor color, bool stroke)
    {
        content.Append("q ");
        if (color.A != 255)
        {
            content.Append("/A").Append(color.A).Append(" gs ");
        }
        content.Append(Number(color.R / 255d)).Append(' ').Append(Number(color.G / 255d)).Append(' ')
            .Append(Number(color.B / 255d)).Append(stroke ? " RG " : " rg ");
    }

    private static string Number(double number)
    {
        if (!double.IsFinite(number))
        {
            throw new InvalidDataException("A PDF drawing contains a non-finite coordinate.");
        }
        return number.ToString("0.#####", CultureInfo.InvariantCulture);
    }
}
