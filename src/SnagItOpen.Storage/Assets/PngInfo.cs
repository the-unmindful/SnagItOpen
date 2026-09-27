using System.Buffers.Binary;

namespace SnagItOpen.Storage.Assets;

/// <summary>Minimal PNG header reader (signature + IHDR) used to verify stored assets without decoding.</summary>
public static class PngInfo
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool TryReadSize(ReadOnlySpan<byte> header, out int width, out int height)
    {
        width = height = 0;
        if (header.Length < 24 || !header[..8].SequenceEqual(Signature)) return false;
        if (header[12] != (byte)'I' || header[13] != (byte)'H' || header[14] != (byte)'D' || header[15] != (byte)'R') return false;
        uint w = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        uint h = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
        if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue) return false;
        width = (int)w; height = (int)h;
        return true;
    }

    public static bool TryReadSize(Stream s, out int width, out int height)
    {
        Span<byte> buf = stackalloc byte[24];
        int read = 0;
        while (read < 24) { int n = s.Read(buf[read..]); if (n == 0) break; read += n; }
        return TryReadSize(buf[..read], out width, out height);
    }
}
