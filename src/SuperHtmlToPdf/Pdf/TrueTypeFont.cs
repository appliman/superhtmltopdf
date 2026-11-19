using System.Buffers.Binary;
using System.Text;

namespace SuperHtmlToPdf;

/// <summary>Metrics and Unicode mapping from the same TrueType program embedded in the PDF.</summary>
internal sealed class TrueTypeFont
{
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _tables = new(StringComparer.Ordinal);
    private readonly ReadOnlyMemory<byte> _characterMap;
    private readonly ReadOnlyMemory<byte> _metrics;
    private readonly int _metricCount;
    private readonly int _glyphCount;
    private readonly int _format;

    public TrueTypeFont(byte[] data)
    {
        Data = data;
        var file = data.AsSpan();
        if (file.Length < 12 || U32(file, 0) != 0x00010000)
        {
            throw new InvalidDataException("A static TrueType font with glyf outlines is required.");
        }

        var tableCount = U16(file, 4);
        Require(file, 12, tableCount * 16);
        for (var i = 0; i < tableCount; i++)
        {
            var entry = 12 + i * 16;
            var name = Encoding.ASCII.GetString(file.Slice(entry, 4));
            var start = BoundedInt(U32(file, entry + 8));
            var length = BoundedInt(U32(file, entry + 12));
            Require(file, start, length);
            _tables[name] = data.AsMemory(start, length);
        }

        var head = Table("head", 54).Span;
        var hhea = Table("hhea", 36).Span;
        _ = Table("glyf", 1);
        _ = Table("loca", 2);
        UnitsPerEm = U16(head, 18);
        _glyphCount = U16(Table("maxp", 6).Span, 4);
        _metricCount = U16(hhea, 34);
        if (UnitsPerEm is < 16 or > 16384 || _metricCount < 1 || _metricCount > _glyphCount)
        {
            throw new InvalidDataException("Invalid TrueType metrics.");
        }
        _metrics = Table("hmtx", _metricCount * 4);
        if (_tables.TryGetValue("OS/2", out var os2) && os2.Length >= 10 && (U16(os2.Span, 8) & 0x202) != 0)
        {
            throw new InvalidDataException("This font forbids embedding its outlines.");
        }
        BoundingBox = [Scale(I16(head, 36)), Scale(I16(head, 38)), Scale(I16(head, 40)), Scale(I16(head, 42))];
        Ascent = Scale(I16(hhea, 4));
        Descent = Scale(I16(hhea, 6));
        PostScriptName = ReadPostScriptName();
        _characterMap = SelectCharacterMap(Table("cmap", 4));
        _format = U16(_characterMap.Span, 0);
    }

    public byte[] Data
    {
        get;
    }
    public int UnitsPerEm
    {
        get;
    }
    public int[] BoundingBox
    {
        get;
    }
    public int Ascent
    {
        get;
    }
    public int Descent
    {
        get;
    }
    public string PostScriptName
    {
        get;
    }

    public int Glyph(int scalar)
    {
        var map = _characterMap.Span;
        var glyph = _format == 12 ? LookupGroup(map, scalar) : LookupSegment(map, scalar);
        return glyph < _glyphCount ? glyph : 0;
    }

    public int Width(int glyph)
    {
        return Scale(U16(_metrics.Span, 4 * Math.Min(glyph, _metricCount - 1)));
    }

    private int Scale(int number)
    {
        return (int)Math.Round(1000.0 * number / UnitsPerEm, MidpointRounding.AwayFromZero);
    }

    private static int LookupGroup(ReadOnlySpan<byte> map, int scalar)
    {
        var low = 0;
        var high = (int)U32(map, 12) - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var i = 16 + mid * 12;
            var start = U32(map, i);
            var end = U32(map, i + 4);
            if (scalar < start)
            {
                high = mid - 1;
            }
            else if (scalar > end)
            {
                low = mid + 1;
            }
            else
            {
                var glyph = (long)U32(map, i + 8) + scalar - start;
                return glyph <= ushort.MaxValue ? (int)glyph : 0;
            }
        }
        return 0;
    }

    private static int LookupSegment(ReadOnlySpan<byte> map, int scalar)
    {
        if (scalar > ushort.MaxValue)
        {
            return 0;
        }
        var count = U16(map, 6) / 2;
        var low = 0;
        var high = count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (U16(map, 14 + mid * 2) < scalar)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }
        if (low == count)
        {
            return 0;
        }
        var start = U16(map, 16 + 2 * count + low * 2);
        if (scalar < start)
        {
            return 0;
        }
        var delta = I16(map, 16 + 4 * count + low * 2);
        var rangeIndex = 16 + 6 * count + low * 2;
        var range = U16(map, rangeIndex);
        if (range == 0)
        {
            return (scalar + delta) & 0xffff;
        }
        var word = U16(map, rangeIndex + range + (scalar - start) * 2);
        return word == 0 ? 0 : (word + delta) & 0xffff;
    }

    private static ReadOnlyMemory<byte> SelectCharacterMap(ReadOnlyMemory<byte> table)
    {
        var span = table.Span;
        var count = U16(span, 2);
        Require(span, 4, count * 8);
        ReadOnlyMemory<byte> best = default;
        var priority = -1;
        for (var i = 0; i < count; i++)
        {
            var entry = 4 + i * 8;
            var platform = U16(span, entry);
            var encoding = U16(span, entry + 2);
            if (platform != 0 && (platform != 3 || encoding is not (1 or 10)))
            {
                continue;
            }
            var at = BoundedInt(U32(span, entry + 4));
            var format = U16(span, at);
            if (format is not (4 or 12) || format <= priority)
            {
                continue;
            }
            var length = format == 12 ? BoundedInt(U32(span, at + 4)) : U16(span, at + 2);
            Require(span, at, length);
            var map = table.Slice(at, length);
            if (format == 12)
            {
                var groups = U32(map.Span, 12);
                if (groups > (length - 16L) / 12)
                {
                    throw new InvalidDataException("Invalid TrueType character groups.");
                }
            }
            else
            {
                Require(map.Span, 16, 8 * (U16(map.Span, 6) / 2));
            }
            best = map;
            priority = format;
        }
        return !best.IsEmpty ? best : throw new InvalidDataException("No supported Unicode TrueType map.");
    }

    private string ReadPostScriptName()
    {
        var names = Table("name", 6).Span;
        var count = U16(names, 2);
        var storage = U16(names, 4);
        Require(names, 6, 12 * count);
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 12 * i;
            if (U16(names, entry + 6) != 6)
            {
                continue;
            }
            var start = storage + U16(names, entry + 10);
            var length = U16(names, entry + 8);
            Require(names, start, length);
            var encoding = U16(names, entry) is 0 or 3 ? Encoding.BigEndianUnicode : Encoding.ASCII;
            var name = encoding.GetString(names.Slice(start, length));
            if (name.Length is > 0 and <= 127 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            {
                return name;
            }
        }
        return "EmbeddedTrueType";
    }

    private ReadOnlyMemory<byte> Table(string name, int minimum)
    {
        if (!_tables.TryGetValue(name, out var table) || table.Length < minimum)
        {
            throw new InvalidDataException($"Invalid TrueType table '{name}'.");
        }
        return table;
    }

    private static int BoundedInt(uint value)
    {
        return value <= int.MaxValue ? (int)value : throw new InvalidDataException("Invalid TrueType offset.");
    }

    private static int U16(ReadOnlySpan<byte> data, int index)
    {
        Require(data, index, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(data[index..]);
    }

    private static int I16(ReadOnlySpan<byte> data, int index)
    {
        return unchecked((short)U16(data, index));
    }

    private static uint U32(ReadOnlySpan<byte> data, int index)
    {
        Require(data, index, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(data[index..]);
    }

    private static void Require(ReadOnlySpan<byte> data, int start, int size)
    {
        if (start < 0 || size < 0 || start > data.Length - size)
        {
            throw new InvalidDataException("Truncated TrueType data.");
        }
    }
}
