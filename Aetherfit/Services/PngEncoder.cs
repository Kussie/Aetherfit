using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Aetherfit.Services;

// Minimal dependency-free PNG writer (RGB, 8-bit, no interlacing) built on the BCL's own
// zlib support, so image capture doesn't need System.Drawing/GDI+ at all.
internal static class PngEncoder
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    // raw must already be laid out as height rows of (1 filter-type byte + width*3 RGB bytes).
    public static byte[] Encode(int width, int height, byte[] raw)
    {
        using var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);

        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR", BuildIhdr(width, height));
        WriteChunk(output, "IDAT", idat.ToArray());
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static byte[] BuildIhdr(int width, int height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // color type: truecolor RGB
        ihdr[10] = 0; // compression method
        ihdr[11] = 0; // filter method
        ihdr[12] = 0; // interlace method
        return ihdr;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBuf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuf, data.Length);
        output.Write(lengthBuf);

        Span<byte> typeBytes = stackalloc byte[4];
        typeBytes[0] = (byte)type[0];
        typeBytes[1] = (byte)type[1];
        typeBytes[2] = (byte)type[2];
        typeBytes[3] = (byte)type[3];
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
        output.Write(crcBuf);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
