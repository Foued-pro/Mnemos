using Snappier;

namespace Mnemos.Core;

public static class SnappyDecompressor
{
    private static readonly byte[] SnappyHeader = { 0xFF, 0x11, 0x02 };
    private const int BlinkHeaderSize = 15;

    public static byte[]? Decompress(string path, Action<string>? log = null)
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
            catch (IOException ex)
            {
                log?.Invoke($"[SNAPPY] File locked ({Path.GetFileName(path)}), retry {i + 1}/5: {ex.Message}");
                Thread.Sleep(100);
            }
        }

        if (raw == null)
        {
            log?.Invoke($"[SNAPPY] Failed to read {Path.GetFileName(path)} after 5 attempts.");
            return null;
        }

        if (raw.Length < 3) return null;

        if (raw[0] != SnappyHeader[0] || raw[1] != SnappyHeader[1] || raw[2] != SnappyHeader[2])
            return null;

        try
        {
            var decompressed = Snappy.DecompressToArray(raw[3..]);

            if (decompressed.Length < BlinkHeaderSize)
            {
                log?.Invoke($"[SNAPPY] Decompressed output too small ({decompressed.Length} bytes) for {Path.GetFileName(path)}.");
                return null;
            }

            return decompressed[BlinkHeaderSize..];
        }
        catch (Exception ex)
        {
            log?.Invoke($"[SNAPPY] Decompression failed for {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }
}