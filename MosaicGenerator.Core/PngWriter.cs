using System.IO.Compression;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace MosaicGenerator.Core;

// Writes a single PNG file by streaming strips row by row. The full image is never
// held in memory at once, so output size is limited only by available disk space.
// Takes ownership of each strip Image and disposes it once its rows have been written.
public static class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    // Each IDAT chunk is 128 KB. Small enough to stay responsive, large enough to
    // keep per-chunk overhead negligible for even the largest mosaics.
    private const int IdatChunkSize = 128 * 1024;

    public static void Write(string path, int width, int height, IEnumerable<Image<Rgba32>> strips)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 1 << 16);

        file.Write(Signature);
        WriteIhdr(file, width, height);
        WritePixelData(file, strips);
        WriteIend(file);
    }

    // Chunks
    private static void WriteIhdr(Stream file, int width, int height)
    {
        Span<byte> data = stackalloc byte[13];
        WriteBE32(data, 0, (uint)width);
        WriteBE32(data, 4, (uint)height);
        data[8]  = 8; // bit depth: 8 bits per channel
        data[9]  = 6; // colour type: RGBA
        data[10] = 0; // compression: deflate (only valid value)
        data[11] = 0; // filter method: adaptive (only valid value)
        data[12] = 0; // interlace: none
        WriteChunk(file, "IHDR"u8, data);
    }

    private static void WritePixelData(Stream file, IEnumerable<Image<Rgba32>> strips)
    {
        // using declarations dispose in reverse (LIFO) order:
        //   zlib is disposed first. Finalises the deflate stream and writes Adler-32
        //   idatWriter is disposed next. Flushes any remaining bytes as the last IDAT chunk
        using var idatWriter = new IdatChunkWriter(file, IdatChunkSize);
        using var zlib       = new ZLibStream(idatWriter, CompressionLevel.Optimal, leaveOpen: true);

        foreach (var strip in strips)
        {
            using (strip)
            {
                strip.ProcessPixelRows(accessor =>
                {
                    Span<byte> filterByte = [0]; // PNG filter type 0: None
                    for (int y = 0; y < strip.Height; y++)
                    {
                        zlib.Write(filterByte);
                        zlib.Write(MemoryMarshal.AsBytes(accessor.GetRowSpan(y)));
                    }
                });
            }
        }
    }

    private static void WriteIend(Stream file) =>
        WriteChunk(file, "IEND"u8, ReadOnlySpan<byte>.Empty);

    private static void WriteChunk(Stream file, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> tmp = stackalloc byte[4];

        WriteBE32(tmp, 0, (uint)data.Length);
        file.Write(tmp);
        file.Write(type);
        file.Write(data);

        // CRC covers type + data
        uint crc = CrcUpdate(0xFFFFFFFF, type);
               crc = CrcUpdate(crc, data);
        WriteBE32(tmp, 0, crc ^ 0xFFFFFFFF);
        file.Write(tmp);
    }

    // Helpers
    private static void WriteBE32(Span<byte> buf, int offset, uint v)
    {
        buf[offset]     = (byte)(v >> 24);
        buf[offset + 1] = (byte)(v >> 16);
        buf[offset + 2] = (byte)(v >>  8);
        buf[offset + 3] = (byte) v;
    }

    // CRC-32 (ISO 3309 / PNG spec)
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    // Accumulates CRC without finalising. Call ^ 0xFFFFFFFF on the last chunk only.
    private static uint CrcUpdate(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    // IDAT chunk writer
    // Buffers the compressed deflate stream and emits it as sized IDAT chunks.
    // This lets the zlib stream write continuously while the file gets proper
    // PNG chunk framing (length + type + data + CRC) around each piece.
    private sealed class IdatChunkWriter : Stream
    {
        private readonly Stream _output;
        private readonly byte[] _buf;
        private int _pos;

        internal IdatChunkWriter(Stream output, int chunkSize)
        {
            _output = output;
            _buf    = new byte[chunkSize];
        }

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                int take = Math.Min(data.Length, _buf.Length - _pos);
                data[..take].CopyTo(_buf.AsSpan(_pos));
                _pos += take;
                data  = data[take..];
                if (_pos == _buf.Length)
                    FlushChunk();
            }
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Flush()
        {
            if (_pos > 0) FlushChunk();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Flush();
            base.Dispose(disposing);
        }

        private void FlushChunk()
        {
            WriteChunk(_output, "IDAT"u8, _buf.AsSpan(0, _pos));
            _pos = 0;
        }

        public override bool CanRead  => false;
        public override bool CanSeek  => false;
        public override bool CanWrite => true;
        public override long Length   => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int  Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)       => throw new NotSupportedException();
        public override void SetLength(long value)                      => throw new NotSupportedException();
    }
}
