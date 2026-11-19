using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SuperHtmlToPdf;

/// <summary>Separate character IDs retain the original Unicode even when two scalars share a glyph.</summary>
internal sealed class PdfFontResource(TrueTypeFont face, int index, bool italic)
{
    private readonly Dictionary<int, int> _ids = new();
    private readonly List<Rune> _characters = [];

    public string Name { get; } = "F" + index.ToString(CultureInfo.InvariantCulture);

    public void Collect(string text, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if ((count++ & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (_ids.TryAdd(rune.Value, _characters.Count + 1))
            {
                _characters.Add(rune);
                if (_characters.Count > ushort.MaxValue)
                {
                    throw new InvalidDataException("Too many distinct characters in one PDF font.");
                }
            }
        }
    }

    public void Encode(StringBuilder content, ReadOnlySpan<char> text)
    {
        content.Append('<');
        foreach (var rune in text.EnumerateRunes())
        {
            content.Append(_ids[rune.Value].ToString("X4", CultureInfo.InvariantCulture));
        }
        content.Append('>');
    }

    public async Task<int> Write(PdfObjectWriter writer)
    {
        var program = await writer.StreamObject($"/Length1 {face.Data.Length}", face.Data, true);
        var descriptor = writer.Reserve();
        await writer.Object(descriptor, $"<< /Type /FontDescriptor /FontName /{face.PostScriptName} /Flags {(italic ? 96 : 32)} "
            + $"/FontBBox [{string.Join(" ", face.BoundingBox)}] /ItalicAngle {(italic ? -12 : 0)} /Ascent {face.Ascent} "
            + $"/Descent {face.Descent} /CapHeight {face.Ascent} /StemV 80 /FontFile2 {program} 0 R >>");

        var widths = new StringBuilder();
        var glyphMap = new byte[(_characters.Count + 1) * 2];
        for (var i = 0; i < _characters.Count; i++)
        {
            var glyph = face.Glyph(_characters[i].Value);
            BinaryPrimitives.WriteUInt16BigEndian(glyphMap.AsSpan((i + 1) * 2), (ushort)glyph);
            widths.Append(face.Width(glyph)).Append(' ');
        }
        var glyphObject = await writer.StreamObject("", glyphMap, true);
        var cid = writer.Reserve();
        await writer.Object(cid, $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{face.PostScriptName} "
            + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
            + $"/FontDescriptor {descriptor} 0 R /CIDToGIDMap {glyphObject} 0 R /W [1 [{widths}]] >>");
        var unicode = await writer.StreamObject("", Encoding.ASCII.GetBytes(CharacterMap()), true);
        var composite = writer.Reserve();
        await writer.Object(composite, $"<< /Type /Font /Subtype /Type0 /BaseFont /{face.PostScriptName} /Encoding /Identity-H "
            + $"/DescendantFonts [{cid} 0 R] /ToUnicode {unicode} 0 R >>");
        return composite;
    }

    private string CharacterMap()
    {
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n")
            .Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
            .Append("/CMapName /SuperHtmlUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        for (var start = 0; start < _characters.Count; start += 100)
        {
            var count = Math.Min(100, _characters.Count - start);
            cmap.Append(count).Append(" beginbfchar\n");
            for (var i = start; i < start + count; i++)
            {
                cmap.Append('<').Append((i + 1).ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                    .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(_characters[i].ToString())))
                    .Append(">\n");
            }
            cmap.Append("endbfchar\n");
        }
        return cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n").ToString();
    }
}
