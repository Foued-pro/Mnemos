using Snappier;

namespace Mnemos;

public static class SnappyDecompressor
{
    private static readonly byte[] SnappyHeader = [0xFF, 0x11, 0x02];
    private const int BlinkHeaderSize = 15;

    public static byte[]? Decompress(string path)
    {
        byte[]? raw = null;
    
        // Tentatives de lecture sécurisées (bypass du File Lock de Chromium)
        for (int i = 0; i < 5; i++) 
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                raw = ms.ToArray();
                break; // Succès, on sort de la boucle
            }
            catch (IOException)
            {
                Thread.Sleep(100); // Verrouillé, on attend 100ms et on retente
            }
        }

        if (raw == null || raw.Length < 3) return null;

        if (raw[0] != SnappyHeader[0] || raw[1] != SnappyHeader[1] || raw[2] != SnappyHeader[2])
            return null;

        byte[] decompressed = Snappy.DecompressToArray(raw[3..]);
        if (decompressed.Length < BlinkHeaderSize) return null;

        return decompressed[BlinkHeaderSize..];
    }
}