namespace Mnemos.Database;

/// <summary>
/// Serialization helpers for 384-dimension embedding vectors
/// stored as BLOBs in the database.
/// </summary>
internal static class EmbeddingSerializer
{
    /// <summary>Converts a raw BLOB to a float array.</summary>
    public static float[] ToFloats(byte[] bytes)
    {
        float[] result = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    /// <summary>Converts a float array to a BLOB for storage.</summary>
    public static byte[] ToBytes(float[] embedding)
    {
        byte[] bytes = new byte[embedding.Length * 4];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}