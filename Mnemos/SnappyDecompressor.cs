using Snappier;

namespace Mnemos;

public static class SnappyDecompressor
{
    private static readonly byte[] SnappyHeader = [0xFF, 0x11, 0x02];
    private const int BlinkHeaderSize = 15;

    public static byte[]? Decompress(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        if (raw.Length < 3) return null;

        if (raw[0] != SnappyHeader[0] ||
            raw[1] != SnappyHeader[1] ||
            raw[2] != SnappyHeader[2])
            return null;

        byte[] decompressed = Snappy.DecompressToArray(raw[3..]);
        if (decompressed.Length < BlinkHeaderSize) return null;

        return decompressed[BlinkHeaderSize..];
    }
}