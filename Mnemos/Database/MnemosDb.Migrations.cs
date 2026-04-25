
namespace Mnemos.Database;

public partial class MnemosDb
{
    /// <summary>
    /// Applies pending migrations based on the current schema version.
    /// </summary>
    private void RunMigrations()
    {
        int current = GetSchemaVersion();
        if (current < 1)
        {
            ApplyMigration1();
            SetSchemaVersion(1);
        }
    }

    /// <summary>
    /// Reads the current schema version from SQLite's PRAGMA user_version.
    /// </summary>
    private int GetSchemaVersion()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Writes the schema version to SQLite's PRAGMA user_version.
    /// </summary>
    private void SetSchemaVersion(int version)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates the initial database schema: tables, indexes, FTS5 virtual tables,
    /// and triggers for keeping FTS indexes in sync.
    /// </summary>
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
                uuid              TEXT PRIMARY KEY,
                conversation_uuid TEXT NOT NULL,
                sender            TEXT NOT NULL,
                text              TEXT NOT NULL DEFAULT '',
                thinking          TEXT,
                metadata          TEXT,
                embedding         BLOB,
                created_at        INTEGER NOT NULL,
                idx               INTEGER NOT NULL,
                importance        INTEGER NOT NULL DEFAULT 0,
                tier              INTEGER NOT NULL DEFAULT 1,
                umap_x            REAL,
                umap_y            REAL,
                umap_z            REAL,
                umap_computed_at  TEXT,
                cluster_id        INTEGER DEFAULT 0,
                FOREIGN KEY (conversation_uuid) REFERENCES conversations(uuid) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS code_blocks (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                message_uuid TEXT NOT NULL,
                language     TEXT,
                code         TEXT NOT NULL,
                file_path    TEXT,
                content_hash TEXT,
                FOREIGN KEY (message_uuid) REFERENCES messages(uuid) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS message_embeddings (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                message_uuid TEXT NOT NULL,
                embedding    BLOB NOT NULL,
                FOREIGN KEY (message_uuid) REFERENCES messages(uuid) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS clusters (
                id         INTEGER PRIMARY KEY,
                name       TEXT NOT NULL DEFAULT 'Cluster',
                keywords   TEXT,
                updated_at TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_msgs_conv  ON messages(conversation_uuid, idx);
            CREATE INDEX IF NOT EXISTS idx_msgs_date  ON messages(created_at);
            CREATE INDEX IF NOT EXISTS idx_code_msg   ON code_blocks(message_uuid);
            CREATE INDEX IF NOT EXISTS idx_code_file  ON code_blocks(file_path);
            CREATE INDEX IF NOT EXISTS idx_msg_emb    ON message_embeddings(message_uuid);

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

            CREATE TRIGGER IF NOT EXISTS messages_au AFTER UPDATE ON messages BEGIN
                INSERT INTO messages_fts(messages_fts, rowid, text, thinking) 
                VALUES('delete', old.rowid, old.text, COALESCE(old.thinking, ''));
                INSERT INTO messages_fts(rowid, text, thinking) 
                VALUES(new.rowid, new.text, COALESCE(new.thinking, ''));
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
}