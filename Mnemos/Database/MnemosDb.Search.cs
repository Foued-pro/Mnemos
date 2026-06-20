using Mnemos.Models;

namespace Mnemos.Database;

public partial class MnemosDb
{
    // Hard cap on how many embeddings get pulled and scored per semantic
    // search call. Without this, SearchByEmbedding scans the entire
    // message_embeddings table on every call — fine at a few hundred rows,
    // but it scales linearly with the size of the database and becomes the
    // dominant cost once the index grows into the thousands (especially
    // on CPU fallback when the ONNX CUDA provider fails to load). Capping
    // to the most recent N candidates keeps each search bounded while still
    // covering the window of context that matters most in practice.
    private const int MaxEmbeddingCandidates = 3000;

    /// <summary>
    /// Performs a full-text search on messages using FTS5 with BM25 scoring,
    /// optionally filtered by programming language. Results are ranked
    /// combining BM25 relevance (70%) and recency boost (30%).
    /// </summary>
    /// <param name="query">The search query string.</param>
    /// <param name="filter">Optional language filter (e.g. "csharp").</param>
    /// <param name="limit">Maximum number of results (default 10).</param>
    /// <param name="tau">Recency decay constant in seconds (default 30 days).</param>
    /// <returns>List of <see cref="SearchResult"/> with relevance scores.</returns>
    public List<SearchResult> Search(string query, string filter = "all", int limit = 10, long tau = 2592000)
    {
        var results = new List<SearchResult>();
        using var cmd = _conn.CreateCommand();

        string filterSql = "";
        if (filter != "all")
        {
            filterSql = "AND m.uuid IN (SELECT cb.message_uuid FROM code_blocks cb WHERE LOWER(cb.language) = @filter)";
            cmd.Parameters.AddWithValue("@filter", filter.ToLower());
        }

        // Escape double quotes to avoid FTS5 syntax errors
        string safeQuery = $"\"{query.Replace("\"", "")}\"";

        cmd.CommandText = $@"
            WITH fts AS (
                SELECT rowid, bm25(messages_fts) AS bm25_score
                FROM messages_fts
                WHERE messages_fts MATCH @query
            )
            SELECT m.uuid, m.conversation_uuid, m.sender, m.text, m.thinking, m.created_at, m.importance,
                   fts.bm25_score,
                   EXP(CAST(m.created_at - strftime('%s','now') AS REAL) / @tau) AS recency
            FROM messages m
            JOIN fts ON m.rowid = fts.rowid
            WHERE m.tier = 1 {filterSql}
            ORDER BY ((-fts.bm25_score) * 0.7 +
                      (EXP(CAST(m.created_at - strftime('%s','now') AS REAL) / @tau)) * 0.3) DESC
            LIMIT @limit;";

        cmd.Parameters.AddWithValue("@query", safeQuery);
        cmd.Parameters.AddWithValue("@tau", (double)tau);
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SearchResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5)).ToString("o"),
                reader.GetDouble(8)
            ));
        }

        return results;
    }

    /// <summary>
    /// Performs semantic search by computing cosine similarity between
    /// a query embedding and the most recent stored message embeddings
    /// (capped at <see cref="MaxEmbeddingCandidates"/> to bound per-query cost).
    /// Optionally filtered by programming language.
    /// </summary>
    /// <param name="queryEmbedding">The query embedding vector (384 dimensions).</param>
    /// <param name="filter">Optional language filter (e.g. "csharp").</param>
    /// <param name="limit">Maximum number of results (default 20).</param>
    /// <returns>List of <see cref="SearchResult"/> ordered by relevance.</returns>
    public List<SearchResult> SearchByEmbedding(float[] queryEmbedding, string filter = "all", int limit = 20)
    {
        using var cmd = _conn.CreateCommand();

        string joinClause = "";
        string whereClause = "";

        if (filter != "all")
        {
            joinClause = "JOIN code_blocks cb ON m.uuid = cb.message_uuid";
            whereClause = "AND LOWER(cb.language) = @filter";
            cmd.Parameters.AddWithValue("@filter", filter);
        }

        // ORDER BY + LIMIT pushed down into SQL: avoids materializing the
        // entire embeddings table in managed memory on every search.
        cmd.CommandText = $@"
            SELECT m.uuid, m.conversation_uuid, m.sender, m.text, m.thinking, m.created_at, e.embedding
            FROM messages m
            JOIN message_embeddings e ON m.uuid = e.message_uuid
            {joinClause}
            WHERE m.tier = 1 {whereClause}
            ORDER BY m.created_at DESC
            LIMIT @maxCandidates;";
        cmd.Parameters.AddWithValue("@maxCandidates", MaxEmbeddingCandidates);

        var chunks = new List<(string uuid, string conv, string sender, string text, string? thinking, long date, float[] emb)>();

        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                float[] emb = EmbeddingSerializer.ToFloats((byte[])reader["embedding"]);
                chunks.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5),
                    emb
                ));
            }
        }

        // Cosine similarity scoring is the hot loop (up to MaxEmbeddingCandidates
        // vectors x 384 dims each). Parallelized across cores since each
        // comparison is independent and read-only over shared state.
        return chunks
            .AsParallel()
            .Select(c => new { c, score = CosineSimilarity(queryEmbedding, c.emb) })
            .Where(x => x.score > 0.4f)
            .GroupBy(x => x.c.uuid)
            .Select(g => g.OrderByDescending(x => x.score).First())
            .Select(x => new SearchResult(
                x.c.uuid, x.c.conv, x.c.sender, x.c.text, x.c.thinking,
                DateTimeOffset.FromUnixTimeSeconds(x.c.date).ToString("o"),
                x.score))
            .OrderByDescending(r => r.Relevance)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Computes the cosine similarity between two embedding vectors.
    /// Uses System.Numerics.Tensors for SIMD-accelerated dot product and
    /// norm computation instead of a scalar loop.
    /// </summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        var spanA = a.AsSpan();
        var spanB = b.AsSpan();

        float dot = System.Numerics.Tensors.TensorPrimitives.Dot(spanA, spanB);
        float normA = MathF.Sqrt(System.Numerics.Tensors.TensorPrimitives.Dot(spanA, spanA));
        float normB = MathF.Sqrt(System.Numerics.Tensors.TensorPrimitives.Dot(spanB, spanB));

        return dot / (normA * normB + 1e-9f);
    }

    /// <summary>
    /// Returns the sorted list of programming languages available
    /// as filters (extracted from code_blocks).
    /// </summary>
    public List<string> GetAvailableFilters()
    {
        var filters = new List<string>();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT language FROM code_blocks WHERE language IS NOT NULL AND language != ''";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string lang = reader.GetString(0).ToLower().Trim();
            if (!string.IsNullOrEmpty(lang))
                filters.Add(lang);
        }

        return filters.Distinct().OrderBy(x => x).ToList();
    }
}