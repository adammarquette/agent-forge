using System.Buffers.Binary;
using System.Text;

namespace AgentForge.GenerateFixtureDocuments;

/// <summary>
/// Encodes a 1-bit greyscale PNG with <b>stored</b> (uncompressed) deflate blocks. Deliberately not
/// <c>ZLibStream</c>: its output is whatever the runtime's zlib build produces, so a runtime upgrade would
/// change the bytes, and with them the sha256 the secret scan pins. Stored blocks are the same bytes on
/// every machine; the cost is size, which 1 bit per pixel keeps small.
/// </summary>
internal static class SyntheticPng
{
    private const int MaxStoredBlock = 65535;

    /// <summary>Encodes <paramref name="ink"/> (true = black) as a PNG.</summary>
    public static byte[] Encode(bool[,] ink)
    {
        var width = ink.GetLength(0);
        var height = ink.GetLength(1);
        var rowBytes = (width + 7) / 8;
        var raw = new byte[height * (rowBytes + 1)];
        for (var y = 0; y < height; y++)
        {
            var offset = y * (rowBytes + 1);
            raw[offset] = 0; // filter: none
            for (var x = 0; x < rowBytes * 8; x++)
            {
                // Greyscale 1 is white; padding bits past the width stay white.
                var white = x >= width || !ink[x, y];
                if (white)
                {
                    raw[offset + 1 + (x / 8)] |= (byte)(0x80 >> (x % 8));
                }
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 1; // bit depth
        header[9] = 0; // colour type: greyscale
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", Zlib(raw));
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x01);
        var position = 0;
        Span<byte> lengths = stackalloc byte[4];
        do
        {
            var length = Math.Min(MaxStoredBlock, data.Length - position);
            var final = position + length == data.Length;
            output.WriteByte(final ? (byte)1 : (byte)0);
            BinaryPrimitives.WriteUInt16LittleEndian(lengths, (ushort)length);
            BinaryPrimitives.WriteUInt16LittleEndian(lengths[2..], (ushort)~length);
            output.Write(lengths);
            output.Write(data, position, length);
            position += length;
        }
        while (position < data.Length);

        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(data));
        output.Write(adler);
        return output.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        png.Write(buffer);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32([.. typeBytes, .. data]));
        png.Write(buffer);
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var d in data)
        {
            crc ^= d;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
