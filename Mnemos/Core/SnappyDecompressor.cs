// ---------------------------------------------------------------
// SnappyDecompressor — Decompresses Snappy-framed Blink values
// from Claude Desktop's IndexedDB localStorage.
// ---------------------------------------------------------------

using Snappier;

namespace Mnemos.Core;

/// <summary>
/// Handles decompression of Snappy-framed streams produced by Chromium's
/// IndexedDB value serialization. Strips the Snappy header (0xFF 0x11 0x02)
/// and the 15-byte Blink wrapper prefix.
/// </summary>
public static class SnappyDecompressor
{
    // Magic bytes for Snappy framed format (see idb_value_wrapping.cc in Chromium)
    private static readonly byte[] SnappyHeader = { 0xFF, 0x11, 0x02 };

    // Blink wraps decompressed values with a 15-byte internal header
    private const int BlinkHeaderSize = 15;

    /// <summary>
    /// Reads and decompresses a Snappy-framed file, retrying on file locks.
    /// Returns the payload with the 15-byte Blink header removed,
    /// or <c>null</c> if decompression fails.
    /// </summary>
    /// <param name="path">Path to the Snappy-compressed file.</param>
    /// <param name="log">Optional logging callback for diagnostics.</param>
    /// <returns>Decompressed bytes without the Blink prefix, or <c>null</c>.</returns>
    public static byte[]? Decompress(string path, Action<string>? log = null)
    {
        byte[]? raw = null;

        // Chromium may hold write locks on these files.
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

        // Must start with the Snappy framed header
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

            // Skip the 15-byte Blink wrapper to reach the actual V8 serialized payload
            return decompressed[BlinkHeaderSize..];
        }
        catch (Exception ex)
        {
            log?.Invoke($"[SNAPPY] Decompression failed for {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }
}