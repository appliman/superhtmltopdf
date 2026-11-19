using System.Buffers.Binary;
using System.IO.Compression;

namespace SuperHtmlToPdf;

internal sealed record PdfRaster(int Width, int Height, int Components, byte[] Data, byte[]? Alpha, bool IsJpeg, bool InvertedCmyk)
{
    public static PdfRaster Read(byte[] data, long maxPixels, CancellationToken cancellationToken)
    {
        if (data.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return PngDecoder.Read(data, maxPixels, cancellationToken);
        }
        if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xd8 }))
        {
            return ReadJpeg(data, maxPixels, cancellationToken);
        }
        throw new InvalidDataException("Only PNG and JPEG images are supported.");
    }

    public static void CheckDimensions(int width, int height, long maxPixels)
    {
        if (width < 1 || height < 1 || (long)width * height > Math.Min(maxPixels, int.MaxValue / 8))
        {
            throw new InvalidDataException("Image dimensions exceed the permitted pixel count.");
        }
    }

    private static PdfRaster ReadJpeg(byte[] data, long maxPixels, CancellationToken cancellationToken)
    {
        var span = data.AsSpan();
        var index = 2;
        var inverted = false;
        var width = 0;
        var height = 0;
        var components = 0;
        while (index < span.Length && span[index++] == 0xff)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (index < span.Length && span[index] == 0xff)
            {
                index++;
            }
            if (index >= span.Length)
            {
                break;
            }
            var marker = span[index++];
            if (index + 2 > span.Length)
            {
                break;
            }
            var size = BinaryPrimitives.ReadUInt16BigEndian(span[index..]);
            if (size < 2 || size > span.Length - index)
            {
                break;
            }
            var segment = span.Slice(index + 2, size - 2);
            if (marker == 0xee && segment.StartsWith("Adobe"u8))
            {
                inverted = true;
            }
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (segment.Length < 6 || segment[0] != 8 || segment[5] is not (1 or 3 or 4))
                {
                    break;
                }
                height = BinaryPrimitives.ReadUInt16BigEndian(segment[1..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(segment[3..]);
                components = segment[5];
                CheckDimensions(width, height, maxPixels);
            }
            if (marker == 0xda && width > 0 && span[(index + size)..].IndexOf(new byte[] { 0xff, 0xd9 }) >= 0)
            {
                return new PdfRaster(width, height, components, data, null, true, inverted && components == 4);
            }
            index += size;
        }
        throw new InvalidDataException("Invalid or unsupported JPEG image.");
    }
}

internal sealed class PngDecoder(CancellationToken cancellationToken)
{
    private static readonly uint[] CrcTable = BuildCrcTable();
    private readonly MemoryStream _compressed = new();
    private byte[] _palette = [];
    private byte[] _transparency = [];
    private int _width;
    private int _height;
    private int _depth;
    private int _type;
    private int _channels;
    private bool _interlaced;
    private byte[] _rgb = [];
    private byte[]? _alpha;

    public static PdfRaster Read(byte[] file, long maxPixels, CancellationToken cancellationToken)
    {
        var decoder = new PngDecoder(cancellationToken);
        using (decoder._compressed)
        {
            decoder.ReadChunks(file, maxPixels);
            return decoder.Decode();
        }
    }

    private void ReadChunks(ReadOnlySpan<byte> data, long maxPixels)
    {
        var offset = 8;
        var foundEnd = false;
        while (offset <= data.Length - 12)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unsignedSize = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            if (unsignedSize > data.Length - offset - 12)
            {
                throw new InvalidDataException("Truncated PNG chunk.");
            }
            var size = (int)unsignedSize;
            var kind = data.Slice(offset + 4, 4);
            var chunk = data.Slice(offset + 8, size);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 8 + size)..]);
            var crc = uint.MaxValue;
            var payload = data.Slice(offset + 4, size + 4);
            for (var i = 0; i < payload.Length; i++)
            {
                if ((i & 32767) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                crc = CrcTable[(byte)(crc ^ payload[i])] ^ (crc >> 8);
            }
            if (~crc != expectedCrc)
            {
                throw new InvalidDataException("Invalid PNG chunk checksum.");
            }
            if (kind.SequenceEqual("IHDR"u8))
            {
                if (offset != 8 || size != 13)
                {
                    throw new InvalidDataException("Invalid PNG header.");
                }
                _width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                _height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
                PdfRaster.CheckDimensions(_width, _height, maxPixels);
                _type = chunk[9];
                _depth = chunk[8];
                _channels = _type switch
                {
                    0 or 3 => 1,
                    2 => 3,
                    4 => 2,
                    6 => 4,
                    _ => 0
                };
                var validDepth = _type switch
                {
                    0 => _depth is 1 or 2 or 4 or 8 or 16,
                    3 => _depth is 1 or 2 or 4 or 8,
                    2 or 4 or 6 => _depth is 8 or 16,
                    _ => false
                };
                if (!validDepth || chunk[10] != 0 || chunk[11] != 0 || chunk[12] > 1)
                {
                    throw new InvalidDataException("Unsupported PNG layout.");
                }
                _interlaced = chunk[12] == 1;
            }
            else if (_width == 0)
            {
                throw new InvalidDataException("Missing PNG header.");
            }
            else if (kind.SequenceEqual("PLTE"u8))
            {
                if (size < 3 || size > 768 || size % 3 != 0)
                {
                    throw new InvalidDataException("Invalid PNG palette.");
                }
                _palette = chunk.ToArray();
            }
            else if (kind.SequenceEqual("tRNS"u8))
            {
                if (_type switch
                {
                    0 => size != 2,
                    2 => size != 6,
                    3 => size > 256,
                    _ => true
                })
                {
                    throw new InvalidDataException("Invalid PNG transparency.");
                }
                _transparency = chunk.ToArray();
            }
            else if (kind.SequenceEqual("IDAT"u8))
            {
                _compressed.Write(chunk);
            }
            else if (kind.SequenceEqual("IEND"u8))
            {
                foundEnd = size == 0;
                break;
            }
            else if ((kind[0] & 32) == 0)
            {
                throw new InvalidDataException("Unsupported critical PNG chunk.");
            }
            offset += size + 12;
        }
        if (!foundEnd || _compressed.Length == 0 || (_type == 3 && _palette.Length == 0))
        {
            throw new InvalidDataException("Incomplete PNG image.");
        }
    }

    private PdfRaster Decode()
    {
        _compressed.Position = 0;
        using var inflated = new ZLibStream(_compressed, CompressionMode.Decompress);
        _rgb = new byte[_width * _height * 3];
        _alpha = _type is 4 or 6 || _transparency.Length > 0 ? new byte[_width * _height] : null;
        (int X, int Y, int Dx, int Dy)[] passes = _interlaced
            ? [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
            : [(0, 0, 1, 1)];
        foreach (var pass in passes)
        {
            if (pass.X >= _width || pass.Y >= _height)
            {
                continue;
            }
            var columns = 1 + (_width - 1 - pass.X) / pass.Dx;
            var count = checked((int)(((long)columns * _channels * _depth + 7) / 8));
            var row = new byte[count];
            var previous = new byte[count];
            var step = (_depth * _channels + 7) / 8;
            for (var y = pass.Y; y < _height; y += pass.Dy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var filter = inflated.ReadByte();
                if (filter is < 0 or > 4)
                {
                    throw new InvalidDataException("Invalid PNG scanline.");
                }
                try
                {
                    for (var offset = 0; offset < row.Length; offset += 32768)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        inflated.ReadExactly(row.AsSpan(offset, Math.Min(32768, row.Length - offset)));
                    }
                }
                catch (EndOfStreamException exception)
                {
                    throw new InvalidDataException("Truncated PNG scanline.", exception);
                }
                Unfilter(row, previous, step, filter);
                for (var col = 0; col < columns; col++)
                {
                    if ((col & 8191) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    StorePixel(row, col, y * _width + pass.X + col * pass.Dx);
                }
                (row, previous) = (previous, row);
            }
        }
        if (inflated.ReadByte() != -1)
        {
            throw new InvalidDataException("PNG scanlines exceed declared dimensions.");
        }
        return new PdfRaster(_width, _height, 3, _rgb, _alpha, false, false);
    }

    private void StorePixel(byte[] row, int col, int dest)
    {
        var channel = col * _channels;
        var first = Sample(row, channel);
        var opacity = 255;
        var rgb = dest * 3;
        switch (_type)
        {
            case 0:
            case 4:
                _rgb[rgb] = _rgb[rgb + 1] = _rgb[rgb + 2] = ToByte(first);
                if (_type == 4)
                {
                    opacity = ToByte(Sample(row, channel + 1));
                }
                else if (_transparency.Length != 0 && first == BinaryPrimitives.ReadUInt16BigEndian(_transparency))
                {
                    opacity = 0;
                }
                break;
            case 3:
                if (first * 3 + 2 >= _palette.Length)
                {
                    throw new InvalidDataException("Unknown PNG palette entry.");
                }
                _palette.AsSpan(first * 3, 3).CopyTo(_rgb.AsSpan(rgb, 3));
                opacity = first < _transparency.Length ? _transparency[first] : 255;
                break;
            default:
                var green = Sample(row, channel + 1);
                var blue = Sample(row, channel + 2);
                _rgb[rgb] = ToByte(first);
                _rgb[rgb + 1] = ToByte(green);
                _rgb[rgb + 2] = ToByte(blue);
                if (_type == 6)
                {
                    opacity = ToByte(Sample(row, channel + 3));
                }
                else if (_transparency.Length == 6 && first == BinaryPrimitives.ReadUInt16BigEndian(_transparency)
                    && green == BinaryPrimitives.ReadUInt16BigEndian(_transparency.AsSpan(2))
                    && blue == BinaryPrimitives.ReadUInt16BigEndian(_transparency.AsSpan(4)))
                {
                    opacity = 0;
                }
                break;
        }
        if (_alpha is not null)
        {
            _alpha[dest] = (byte)opacity;
        }
    }

    private int Sample(byte[] row, int sample)
    {
        if (_depth == 16)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(sample * 2));
        }
        if (_depth == 8)
        {
            return row[sample];
        }
        var bit = (long)sample * _depth;
        return (row[(int)(bit >> 3)] >> (8 - _depth - (int)(bit & 7))) & ((1 << _depth) - 1);
    }

    private byte ToByte(int sample)
    {
        var max = (1 << _depth) - 1;
        return (byte)((sample * 255 + max / 2) / max);
    }

    private void Unfilter(byte[] row, byte[] previous, int step, int filter)
    {
        for (var i = 0; filter != 0 && i < row.Length; i++)
        {
            if ((i & 32767) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var left = i >= step ? row[i - step] : 0;
            var up = previous[i];
            var corner = i >= step ? previous[i - step] : 0;
            var predictor = filter switch
            {
                1 => left,
                2 => up,
                3 => (left + up) / 2,
                _ => Paeth(left, up, corner)
            };
            row[i] = unchecked((byte)(row[i] + predictor));
        }
    }

    private static int Paeth(int left, int up, int corner)
    {
        var p = left + up - corner;
        var a = Math.Abs(p - left);
        var b = Math.Abs(p - up);
        var c = Math.Abs(p - corner);
        return a <= b && a <= c ? left : b <= c ? up : corner;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value >> 1) ^ ((value & 1) == 0 ? 0u : 0xedb88320u);
            }
            table[i] = value;
        }
        return table;
    }
}
