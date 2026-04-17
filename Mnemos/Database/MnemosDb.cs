using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;
using Mnemos.Models;

namespace Mnemos.Database;

public class MnemosDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private static readonly object _dbLock = new object();

    public MnemosDb(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA foreign_keys=ON;");
        RunMigrations();
    }

    // ── MIGRATIONS ────────────────────────────────────────────────────────

    private void RunMigrations()
    {
        int current = GetSchemaVersion();
        if (current < 1) { ApplyMigration1(); SetSchemaVersion(1); }
        // if (current < 2) { ApplyMigration2(); SetSchemaVersion(2); }
    }

    private int GetSchemaVersion()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private void SetSchemaVersion(int version)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private void ApplyMigration1()
    {
        Execute(@"
            CREATE TABLE IF NOT EXISTS conversations (
                uuid          TEXT PRIMARY KEY,
                name          TEXT NOT NULL,
                created_at    INTEGER NOT NULL,
                updated_at    INTEGER NOT NULL,
                message_count INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS messages (
                uuid              TEXT    PRIMARY KEY,
                conversation_uuid TEXT    NOT NULL,
                sender            TEXT    NOT NULL,
                text              TEXT    NOT NULL DEFAULT '',
                thinking          TEXT,
                metadata          TEXT,
                embedding         BLOB,
                created_at        INTEGER NOT NULL,
                idx               INTEGER NOT NULL,
                importance        INTEGER NOT NULL DEFAULT 0,
                tier              INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY (conversation_uuid) REFERENCES conversations(uuid) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS code_blocks (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                message_uuid TEXT    NOT NULL,
                language     TEXT,
                code         TEXT    NOT NULL,
                FOREIGN KEY (message_uuid) REFERENCES messages(uuid) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS message_embeddings (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                message_uuid TEXT    NOT NULL,
                embedding    BLOB    NOT NULL,
                FOREIGN KEY (message_uuid) REFERENCES messages(uuid) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_msg_emb   ON message_embeddings(message_uuid);
            CREATE INDEX IF NOT EXISTS idx_msgs_conv ON messages(conversation_uuid, idx);
            CREATE INDEX IF NOT EXISTS idx_msgs_date ON messages(created_at);
            CREATE INDEX IF NOT EXISTS idx_msgs_tier ON messages(tier, created_at);
            CREATE INDEX IF NOT EXISTS idx_code_msg  ON code_blocks(message_uuid);

            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                text, thinking,
                content='messages', content_rowid='rowid', tokenize='trigram'
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS code_fts USING fts5(
                code,
                content='code_blocks', content_rowid='rowid', tokenize='trigram'
            );

            CREATE TRIGGER IF NOT EXISTS messages_ai AFTER INSERT ON messages BEGIN
                INSERT INTO messages_fts(rowid, text, thinking)
                VALUES (new.rowid, new.text, COALESCE(new.thinking, ''));
            END;

            CREATE TRIGGER IF NOT EXISTS messages_ad AFTER DELETE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, text, thinking)
                VALUES ('delete', old.rowid, old.text, COALESCE(old.thinking, ''));
            END;

            CREATE TRIGGER IF NOT EXISTS code_ai AFTER INSERT ON code_blocks BEGIN
                INSERT INTO code_fts(rowid, code) VALUES (new.rowid, new.code);
            END;

            CREATE TRIGGER IF NOT EXISTS code_ad AFTER DELETE ON code_blocks BEGIN
                INSERT INTO code_fts(code_fts, rowid, code)
                VALUES ('delete', old.rowid, old.code);
            END;
        ");
    }

    // ── DATA PERSISTENCE ──────────────────────────────────────────────────

    public void SaveConversations(List<Conversation> conversations)
    {
        lock (_dbLock)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                foreach (var conv in conversations)
                {
                    long createdAt = ToUnix(conv.CreatedAt);
                    long updatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    ExecuteWithParams(@"
                        INSERT INTO conversations (uuid, name, created_at, updated_at, message_count)
                        VALUES (@uuid, @name, @created_at, @updated_at, @count)
                        ON CONFLICT(uuid) DO UPDATE SET
                            name          = excluded.name,
                            updated_at    = excluded.updated_at,
                            message_count = excluded.message_count;",
                        ("@uuid", conv.Uuid),
                        ("@name", conv.Name ?? "Conversation"),
                        ("@created_at", createdAt),
                        ("@updated_at", updatedAt),
                        ("@count", conv.Messages.Count));

                    foreach (var msg in conv.Messages)
                    {
                        ExecuteWithParams(@"
                            INSERT OR IGNORE INTO messages
                                (uuid, conversation_uuid, sender, text, thinking, created_at, idx)
                            VALUES (@uuid, @conv_uuid, @sender, @text, @thinking, @created_at, @idx);",
                            ("@uuid", msg.Uuid),
                            ("@conv_uuid", msg.ConversationUuid),
                            ("@sender", msg.Sender),
                            ("@text", msg.Text ?? ""),
                            ("@thinking", msg.Thinking ?? (object)DBNull.Value),
                            ("@created_at", ToUnix(msg.CreatedAt)),
                            ("@idx", msg.Index));

                        if (!string.IsNullOrWhiteSpace(msg.Text))
                        {
                            ExecuteWithParams("DELETE FROM code_blocks WHERE message_uuid = @uuid;", ("@uuid", msg.Uuid));
                            SaveCodeBlocks(msg.Uuid, msg.Text);
                        }
                    }
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    private void SaveCodeBlocks(string messageUuid, string text)
    {
        var matches = Regex.Matches(text, @"```(\w*)\n?([\s\S]*?)```");
        foreach (Match m in matches)
        {
            string lang = m.Groups[1].Value.Trim();
            string code = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(code)) continue;

            ExecuteWithParams(@"
                INSERT INTO code_blocks (message_uuid, language, code)
                VALUES (@msg_uuid, @lang, @code);",
                ("@msg_uuid", messageUuid),
                ("@lang", lang),
                ("@code", code));
        }
    }

    // ── EMBEDDINGS ────────────────────────────────────────────────────────

    public void SaveEmbeddings(string messageUuid, List<float[]> embeddings)
    {
        lock (_dbLock)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                ExecuteWithParams("DELETE FROM message_embeddings WHERE message_uuid = @uuid;", ("@uuid", messageUuid));

                foreach (var emb in embeddings)
                {
                    byte[] bytes = new byte[emb.Length * 4];
                    Buffer.BlockCopy(emb, 0, bytes, 0, bytes.Length);
                    ExecuteWithParams(
                        "INSERT INTO message_embeddings (message_uuid, embedding) VALUES (@uuid, @emb);",
                        ("@uuid", messageUuid),
                        ("@emb", bytes));
                }

                ExecuteWithParams("UPDATE messages SET embedding = X'' WHERE uuid = @uuid;", ("@uuid", messageUuid));
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

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

    public int GetEmbeddedCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE embedding = X'';";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ── SEARCH ────────────────────────────────────────────────────────────

    public List<SearchResult> Search(string query, int limit = 10, long tau = 2592000)
    {
        var results = new List<SearchResult>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            WITH fts AS (
                SELECT rowid, bm25(messages_fts) AS bm25_score
                FROM messages_fts
                WHERE messages_fts MATCH @query
            ),
            scored AS (
                SELECT m.uuid, m.conversation_uuid, m.sender, m.text, m.created_at, m.importance,
                       fts.bm25_score,
                       EXP(CAST(m.created_at - strftime('%s','now') AS REAL) / @tau) AS recency
                FROM messages m
                JOIN fts ON m.rowid = fts.rowid
                WHERE m.tier = 1
            )
            SELECT uuid, conversation_uuid, sender, text, created_at,
                   ((-bm25_score) * 0.7 + recency * 0.3 + importance * 0.5) AS relevance
            FROM scored
            ORDER BY relevance DESC
            LIMIT @limit;";

        cmd.Parameters.AddWithValue("@query", query);
        cmd.Parameters.AddWithValue("@tau", (double)tau);
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new SearchResult(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).ToString("o"),
                reader.GetDouble(5)));

        return results;
    }

    public List<SearchResult> SearchByEmbedding(float[] queryEmbedding, int limit = 20)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT m.uuid, m.conversation_uuid, m.sender, m.text, m.created_at, e.embedding
            FROM messages m
            JOIN message_embeddings e ON m.uuid = e.message_uuid
            WHERE m.tier = 1;";

        var chunks = new List<(string uuid, string conv, string sender, string text, long date, float[] emb)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                byte[] bytes = (byte[])reader["embedding"];
                float[] emb = new float[bytes.Length / 4];
                Buffer.BlockCopy(bytes, 0, emb, 0, bytes.Length);
                chunks.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt64(4), emb));
            }
        }

        return chunks
            .Select(c => new { c, score = CosineSimilarity(queryEmbedding, c.emb) })
            .Where(x => x.score > 0.4f)
            .GroupBy(x => x.c.uuid)
            .Select(g => g.OrderByDescending(x => x.score).First())
            .Select(x => new SearchResult(
                x.c.uuid, x.c.conv, x.c.sender, x.c.text,
                DateTimeOffset.FromUnixTimeSeconds(x.c.date).ToString("o"),
                x.score))
            .OrderByDescending(r => r.Relevance)
            .Take(limit)
            .ToList();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB) + 1e-9f);
    }

    // ── RETRIEVAL ─────────────────────────────────────────────────────────

    public List<ChatMessage> GetRecentMessages(string convUuid, int n = 10)
    {
        var results = new List<ChatMessage>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT uuid, sender, text, thinking, created_at, idx
            FROM messages
            WHERE conversation_uuid = @conv_uuid
            ORDER BY idx DESC
            LIMIT @n;";
        cmd.Parameters.AddWithValue("@conv_uuid", convUuid);
        cmd.Parameters.AddWithValue("@n", n);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new ChatMessage(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).ToString("o"),
                convUuid, reader.GetInt32(5)));

        return results;
    }

    public (int conversations, int messages) GetStats()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM conversations;";
        int convs = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(*) FROM messages;";
        int msgs = Convert.ToInt32(cmd.ExecuteScalar());
        return (convs, msgs);
    }

    // ── HELPERS ───────────────────────────────────────────────────────────

    private static long ToUnix(string iso) =>
        DateTimeOffset.TryParse(iso, out var dto)
            ? dto.ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void ExecuteWithParams(string sql, params (string name, object? value)[] parameters)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}