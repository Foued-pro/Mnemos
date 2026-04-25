using Mnemos.Models;

namespace Mnemos.Database;

public partial class MnemosDb
{
    /// <summary>
    /// Persists UMAP coordinates and cluster assignments for a batch of messages.
    /// </summary>
    /// <param name="points">Collection of (Uuid, X, Y, Z, ClusterId) tuples.</param>
    public void SaveUmapCoordinates(IEnumerable<(string Uuid, float X, float Y, float Z, int ClusterId)> points)
    {
        lock (_dbLock)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                string now = DateTimeOffset.UtcNow.ToString("o");
                foreach (var (uuid, x, y, z, clusterId) in points)
                {
                    ExecuteWithParams(
                        "UPDATE messages SET umap_x=@x, umap_y=@y, umap_z=@z, cluster_id=@c, umap_computed_at=@at WHERE uuid=@uuid;",
                        ("@x", x), ("@y", y), ("@z", z), ("@c", clusterId), ("@at", now), ("@uuid", uuid));
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[DB] SaveUmapCoordinates failed: {ex.Message}");
                tx.Rollback();
                throw;
            }
        }
    }

    /// <summary>
    /// Retrieves all messages that have valid UMAP coordinates, ordered by creation date.
    /// Returns full-text and cluster label for display in the 3D constellation.
    /// </summary>
    /// <returns>List of <see cref="UniversePoint"/>.</returns>
    public List<UniversePoint> GetUmapPoints()
    {
        var results = new List<UniversePoint>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT m.uuid, m.conversation_uuid, m.sender, SUBSTR(m.text, 1, 80),
                   m.umap_x, m.umap_y, m.umap_z, m.created_at,
                   COALESCE(m.cluster_id, 0),
                   COALESCE(c.name, 'Cluster ' || (COALESCE(m.cluster_id, 0) + 1)),
                   m.text  
            FROM messages m
            LEFT JOIN clusters c ON c.id = m.cluster_id
            WHERE m.umap_x IS NOT NULL AND m.umap_y IS NOT NULL AND m.umap_z IS NOT NULL
            ORDER BY m.created_at ASC;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new UniversePoint(
                reader.GetString(0),      // uuid
                reader.GetString(1),      // conversation_uuid
                reader.GetString(2),      // sender
                reader.GetString(3),      // text preview (first 80 chars)
                (float)reader.GetDouble(4), // umap_x
                (float)reader.GetDouble(5), // umap_y
                (float)reader.GetDouble(6), // umap_z
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)).ToString("o"),
                reader.GetInt32(8),       // cluster_id
                reader.IsDBNull(9) ? $"Cluster {reader.GetInt32(8) + 1}" : reader.GetString(9),
                reader.GetString(10)      // full text
            ));
        }
        return results;
    }

    /// <summary>
    /// Returns messages that have embeddings but no UMAP coordinates yet,
    /// so they can be projected via UMAP in the next batch.
    /// </summary>
    /// <param name="limit">Maximum number of messages to return.</param>
    /// <returns>List of (Uuid, Embedding) tuples.</returns>
    public List<(string Uuid, float[] Embedding)> GetMessagesWithoutUmap(int limit = 500)
    {
        var results = new List<(string, float[])>();
        using var cmd = _conn.CreateCommand();

        cmd.CommandText = @"
            SELECT m.uuid, e.embedding
            FROM messages m
            JOIN message_embeddings e ON e.id = (
                SELECT MIN(id) FROM message_embeddings WHERE message_uuid = m.uuid
            )
            WHERE m.umap_x IS NULL OR m.umap_z IS NULL
            LIMIT @limit;";

        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            float[] emb = EmbeddingSerializer.ToFloats((byte[])reader[1]);
            results.Add((reader.GetString(0), emb));
        }
        return results;
    }
}