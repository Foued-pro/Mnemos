namespace Mnemos.Database;

public partial class MnemosDb
{
    /// <summary>
    /// Persists a batch of embedding vectors for a single message.
    /// Replaces any existing embeddings for that message.
    /// </summary>
    /// <param name="messageUuid">UUID of the target message.</param>
    /// <param name="embeddings">One or more 384-dimension vectors.</param>
    public void SaveEmbeddings(string messageUuid, List<float[]> embeddings)
    {
        lock (_dbLock)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                // Remove old embeddings for this message
                ExecuteWithParams("DELETE FROM message_embeddings WHERE message_uuid = @uuid;",
                    ("@uuid", messageUuid));

                foreach (var emb in embeddings)
                {
                    byte[] bytes = EmbeddingSerializer.ToBytes(emb);
                    ExecuteWithParams(
                        "INSERT INTO message_embeddings (message_uuid, embedding) VALUES (@uuid, @emb);",
                        ("@uuid", messageUuid),
                        ("@emb", bytes));
                }

                // Mark the message as embedded (dummy blob)
                ExecuteWithParams("UPDATE messages SET embedding = X'' WHERE uuid = @uuid;",
                    ("@uuid", messageUuid));

                tx.Commit();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[DB] SaveEmbeddings failed: {ex.Message}");
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> messages that have not
    /// yet been embedded (embedding column is NULL).
    /// </summary>
    /// <param name="limit">Maximum number of messages to return.</param>
    /// <returns>List of (uuid, text) tuples.</returns>
    public List<(string Uuid, string Text)> GetMessagesWithoutEmbedding(int limit = 500)
    {
        var results = new List<(string, string)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT uuid, text FROM messages WHERE embedding IS NULL LIMIT @limit;";
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetString(0), reader.GetString(1)));
        return results;
    }

    /// <summary>
    /// Returns the total number of distinct messages that have at least one embedding.
    /// </summary>
    public int GetEmbeddedCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT message_uuid) FROM message_embeddings;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Retrieves all embeddings (one per message, using the first stored chunk).
    /// </summary>
    /// <returns>List of (message_uuid, embedding) tuples.</returns>
    public List<(string Uuid, float[] Embedding)> GetAllEmbeddings()
    {
        var results = new List<(string, float[])>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT message_uuid, embedding 
            FROM message_embeddings 
            WHERE id IN (
                SELECT MIN(id) FROM message_embeddings GROUP BY message_uuid
            );";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            float[] emb = EmbeddingSerializer.ToFloats((byte[])reader[1]);
            results.Add((reader.GetString(0), emb));
        }
        return results;
    }

    /// <summary>
    /// Computes the centroid (mean + L2-normalized) of all embeddings
    /// belonging to a given conversation. Used as the conversation's
    /// representative vector in the 3D graph.
    /// </summary>
    /// <param name="convUuid">UUID of the conversation.</param>
    /// <returns>384-dimension centroid vector, or <c>null</c> if no embeddings found.</returns>
    public float[]? GetConversationCentroid(string convUuid)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT e.embedding 
            FROM message_embeddings e
            JOIN messages m ON e.message_uuid = m.uuid
            WHERE m.conversation_uuid = @cid;";
        cmd.Parameters.AddWithValue("@cid", convUuid);

        var embeddings = new List<float[]>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            embeddings.Add(EmbeddingSerializer.ToFloats((byte[])reader[0]));
        }

        if (embeddings.Count == 0) return null;
        if (embeddings.Count == 1) return embeddings[0];

        int dimension = embeddings[0].Length;
        float[] centroid = new float[dimension];

        foreach (var emb in embeddings)
            for (int i = 0; i < dimension; i++)
                centroid[i] += emb[i];

        float norm = 0;
        for (int i = 0; i < dimension; i++)
        {
            centroid[i] /= embeddings.Count;
            norm += centroid[i] * centroid[i];
        }

        norm = MathF.Sqrt(norm);
        for (int i = 0; i < dimension; i++)
            centroid[i] /= Math.Max(norm, 1e-9f);

        return centroid;
    }
}