using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace SuperHtmlToPdf;

/// <summary>Counts bytes as they leave the writer, including with non-seekable HTTP streams.</summary>
internal sealed class PdfObjectWriter(Stream destination, CancellationToken cancellationToken)
{
    private readonly List<long> _offsets = [0];
    private long _position;

    public int Reserve()
    {
        _offsets.Add(-1);
        return _offsets.Count - 1;
    }

    public async Task Start()
    {
        await Bytes("%PDF-1.7\n%"u8.ToArray());
        await Bytes(new byte[] { 0xe2, 0xe3, 0xcf, 0xd3, 10 });
    }

    public async Task Object(int id, string body)
    {
        _offsets[id] = _position;
        await Ascii($"{id} 0 obj\n{body}\nendobj\n");
    }

    public async Task<int> StreamObject(string fields, byte[] data, bool compress)
    {
        if (compress)
        {
            return await CompressedStream(fields, async stream =>
            {
                for (var offset = 0; offset < data.Length;)
                {
                    var length = Math.Min(64 * 1024, data.Length - offset);
                    await stream.WriteAsync(data.AsMemory(offset, length), cancellationToken);
                    offset += length;
                }
            });
        }
        var id = Reserve();
        _offsets[id] = _position;
        await Ascii($"{id} 0 obj\n<< {fields} /Length {data.Length} >>\nstream\n");
        await Bytes(data);
        await Ascii("\nendstream\nendobj\n");
        return id;
    }

    // An indirect /Length lets all compressed data flow straight to an HTTP response.
    public async Task<int> CompressedStream(string fields, Func<Stream, Task> write)
    {
        var id = Reserve();
        var lengthId = Reserve();
        _offsets[id] = _position;
        await Ascii($"{id} 0 obj\n<< {fields} /Filter /FlateDecode /Length {lengthId} 0 R >>\nstream\n");
        using var counter = new LengthCounter(destination);
        await using (var encoder = new ZLibStream(counter, CompressionLevel.Fastest, true))
        {
            await write(encoder);
        }
        _position += counter.Written;
        await Ascii("\nendstream\nendobj\n");
        await Object(lengthId, counter.Written.ToString(CultureInfo.InvariantCulture));
        return id;
    }

    private sealed class LengthCounter(Stream output) : Stream
    {
        public long Written
        {
            get; private set;
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush()
        {
        }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count)
        {
            output.Write(buffer, offset, count);
            Written += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await output.WriteAsync(buffer, token);
            Written += buffer.Length;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            return WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        }
    }

    public async Task Finish(int catalog)
    {
        var xref = _position;
        var table = new StringBuilder();
        table.Append("xref\n0 ").Append(_offsets.Count).Append("\n0000000000 65535 f \n");
        for (var i = 1; i < _offsets.Count; i++)
        {
            if (_offsets[i] is < 0 or > 9999999999)
            {
                throw new InvalidDataException("PDF object offset exceeds the xref table format.");
            }
            table.Append(_offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        table.Append("trailer\n<< /Size ").Append(_offsets.Count).Append(" /Root ").Append(catalog)
            .Append(" 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        await Ascii(table.ToString());
    }

    private ValueTask Ascii(string value)
    {
        return Bytes(Encoding.ASCII.GetBytes(value));
    }

    private async ValueTask Bytes(ReadOnlyMemory<byte> data)
    {
        await destination.WriteAsync(data, cancellationToken);
        _position += data.Length;
    }
}
