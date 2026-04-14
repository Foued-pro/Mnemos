using Microsoft.Data.Sqlite;

namespace Mnemos.Database;

public class MnemosDb : IDisposable
{
    private readonly SqliteConnection _conn;

    public MnemosDb(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA foreign_keys=ON;");
        Initialize();
    }

    // ── INIT ──────────────────────────────────────────────────────────────
    private void Initialize()
    {
        Execute(@"
            CREATE TABLE IF NOT EXISTS conversations (
                uuid        TEXT    PRIMARY KEY,
                name        TEXT    NOT NULL,
                created_at  INTEGER NOT NULL,
                updated_at  INTEGER NOT NULL,
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

            CREATE TABLE IF NOT EXISTS tool_calls (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                message_uuid TEXT    NOT NULL,
                name         TEXT    NOT NULL,
                arguments    TEXT,
                result       TEXT,
                FOREIGN KEY (message_uuid) REFERENCES messages(uuid) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS conversation_summaries (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_uuid TEXT    NOT NULL,
                start_idx         INTEGER NOT NULL,
                end_idx           INTEGER NOT NULL,
                summary_text      TEXT    NOT NULL,
                embedding         BLOB,
                created_at        INTEGER NOT NULL,
                FOREIGN KEY (conversation_uuid) REFERENCES conversations(uuid) ON DELETE CASCADE
            );

            -- Tier 1 : session courante
            CREATE TABLE IF NOT EXISTS current_session (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                conversation_uuid TEXT    NOT NULL,
                message_uuid      TEXT    NOT NULL,
                added_at          INTEGER NOT NULL
            );

            -- FTS5 : recherche full-text BM25
            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                text,
                thinking,
                content='messages',
                content_rowid='rowid',
                tokenize='trigram'
            );

            -- FTS5 : recherche trigram pour le code
            CREATE VIRTUAL TABLE IF NOT EXISTS code_fts USING fts5(
                code,
                content='code_blocks',
                content_rowid='rowid',
                tokenize='trigram'
            );

            -- Triggers FTS5 messages
            CREATE TRIGGER IF NOT EXISTS messages_ai AFTER INSERT ON messages BEGIN
                INSERT INTO messages_fts(rowid, text, thinking)
                VALUES (new.rowid, new.text, COALESCE(new.thinking, ''));
            END;

            CREATE TRIGGER IF NOT EXISTS messages_ad AFTER DELETE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, text, thinking)
                VALUES ('delete', old.rowid, old.text, COALESCE(old.thinking, ''));
            END;

            -- Triggers FTS5 code
            CREATE TRIGGER IF NOT EXISTS code_ai AFTER INSERT ON code_blocks BEGIN
                INSERT INTO code_fts(rowid, code) VALUES (new.rowid, new.code);
            END;

            CREATE TRIGGER IF NOT EXISTS code_ad AFTER DELETE ON code_blocks BEGIN
                INSERT INTO code_fts(code_fts, rowid, code)
                VALUES ('delete', old.rowid, old.code);
            END;

            -- Index de performance
            CREATE INDEX IF NOT EXISTS idx_msgs_conv  ON messages(conversation_uuid, idx);
            CREATE INDEX IF NOT EXISTS idx_msgs_date  ON messages(created_at);
            CREATE INDEX IF NOT EXISTS idx_msgs_tier  ON messages(tier, created_at);
            CREATE INDEX IF NOT EXISTS idx_code_msg   ON code_blocks(message_uuid);
            CREATE INDEX IF NOT EXISTS idx_sum_conv   ON conversation_summaries(conversation_uuid);
        ");
    }

    // ── UPSERT ────────────────────────────────────────────────────────────
    public void SaveConversations(List<Models.Conversation> conversations)
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
                    ("@uuid",       conv.Uuid),
                    ("@name",       conv.Name),
                    ("@created_at", createdAt),
                    ("@updated_at", updatedAt),
                    ("@count",      conv.Messages.Count));

                foreach (var msg in conv.Messages)
                {
                    long msgCreatedAt = ToUnix(msg.CreatedAt);

                    ExecuteWithParams(@"
                        INSERT OR IGNORE INTO messages
                            (uuid, conversation_uuid, sender, text, thinking, created_at, idx)
                        VALUES
                            (@uuid, @conv_uuid, @sender, @text, @thinking, @created_at, @idx);",
                        ("@uuid",       msg.Uuid),
                        ("@conv_uuid",  msg.ConversationUuid),
                        ("@sender",     msg.Sender),
                        ("@text",       msg.Text ?? ""),
                        ("@thinking",   (object?)msg.Thinking ?? DBNull.Value),
                        ("@created_at", msgCreatedAt),
                        ("@idx",        msg.Index));

                    // Code blocks
                    SaveCodeBlocks(msg.Uuid, msg.Text ?? "");
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

    private void SaveCodeBlocks(string messageUuid, string text)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text, @"```(\w*)\n?([\s\S]*?)```");

        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            string lang = m.Groups[1].Value.Trim();
            string code = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(code)) continue;

            ExecuteWithParams(@"
                INSERT INTO code_blocks (message_uuid, language, code)
                VALUES (@msg_uuid, @lang, @code);",
                ("@msg_uuid", messageUuid),
                ("@lang",     lang),
                ("@code",     code));
        }
    }

    // ── SEARCH (MCP) ──────────────────────────────────────────────────────
    /// <summary>
    /// Recherche hybride BM25 + time decay.
    /// tau = fenêtre de "mémoire" en secondes (ex: 604800 = 1 semaine)
    /// </summary>
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
                SELECT
                    m.uuid,
                    m.conversation_uuid,
                    m.sender,
                    m.text,
                    m.created_at,
                    m.importance,
                    fts.bm25_score,
                    EXP(CAST(m.created_at - strftime('%s','now') AS REAL) / @tau) AS recency
                FROM messages m
                JOIN fts ON m.rowid = fts.rowid
                WHERE m.tier = 1
            )
            SELECT
                uuid,
                conversation_uuid,
                sender,
                text,
                created_at,
                ((-bm25_score) * 0.7 + recency * 0.3 + importance * 0.5) AS relevance
            FROM scored
            ORDER BY relevance DESC
            LIMIT @limit;";

        cmd.Parameters.AddWithValue("@query",  query);
        cmd.Parameters.AddWithValue("@tau",    (double)tau);
        cmd.Parameters.AddWithValue("@limit",  limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SearchResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).ToString("o"),
                reader.GetDouble(5)
            ));
        }
        return results;
    }

    /// <summary>Derniers N messages d'une conv (Tier 1 sliding window)</summary>
    public List<Models.ChatMessage> GetRecentMessages(string convUuid, int n = 10)
    {
        var results = new List<Models.ChatMessage>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT uuid, sender, text, thinking, created_at, idx
            FROM messages
            WHERE conversation_uuid = @conv_uuid
            ORDER BY idx DESC
            LIMIT @n;";
        cmd.Parameters.AddWithValue("@conv_uuid", convUuid);
        cmd.Parameters.AddWithValue("@n",         n);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Models.ChatMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).ToString("o"),
                convUuid,
                reader.GetInt32(5)
            ));
        }
        return results;
    }

    /// <summary>Archive les messages de plus de N jours (→ Tier 3)</summary>
    public int ArchiveOldMessages(int daysOld = 30)
    {
        long cutoff = DateTimeOffset.UtcNow.AddDays(-daysOld).ToUnixTimeSeconds();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE messages SET tier = 2
            WHERE tier = 1
              AND importance = 0
              AND created_at < @cutoff;";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }

    // ── STATS ─────────────────────────────────────────────────────────────
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
    private static long ToUnix(string iso)
    {
        if (DateTimeOffset.TryParse(iso, out var dto))
            return dto.ToUnixTimeSeconds();
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

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

public record SearchResult(
    string Uuid,
    string ConversationUuid,
    string Sender,
    string Text,
    string CreatedAt,
    double Relevance
);