using Snappier;

namespace Mnemos;

public static class SnappyDecompressor
{
    private static readonly byte[] SnappyHeader = { 0xFF, 0x11, 0x02 };
    private const int BlinkHeaderSize = 15;

    public static byte[]? Decompress(string path)
    {
        byte[]? raw = null;
    
        // Retry loop to handle Chromium file locks
        for (int i = 0; i < 5; i++) 
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                raw = ms.ToArray();
                break; 
            }
            catch (IOException) { Thread.Sleep(100); }
        }

        if (raw == null || raw.Length < 3) return null;

        // Snappy magic check
        if (raw[0] != SnappyHeader[0] || raw[1] != SnappyHeader[1] || raw[2] != SnappyHeader[2])
            return null;

        try
        {
            // Skip magic header
            var decompressed = Snappy.DecompressToArray(raw[3..]);
            
            if (decompressed.Length < BlinkHeaderSize) return null;

            // Skip Chromium Blink header
            return decompressed[BlinkHeaderSize..];
        }
        catch { return null; }
    }
}