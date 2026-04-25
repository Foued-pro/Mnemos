
using System.Text;
using System.Text.RegularExpressions;
using Mnemos.Models;

namespace Mnemos.Database;

public partial class MnemosDb
{
    /// <summary>
    /// Persists a batch of extracted conversations and their messages.
    /// Existing entries are updated, code blocks are rebuilt from scratch
    /// for each message to ensure consistency.
    /// </summary>
    /// <param name="conversations">List of conversations to save.</param>
    public void SaveConversations(List<Conversation> conversations)
    {
        lock (_dbLock)
        {
            using var tx = _conn.BeginTransaction();
            try
            {
                foreach (var conv in conversations)
                {
                    ExecuteWithParams(@"
                        INSERT INTO conversations (uuid, name, created_at, updated_at, message_count)
                        VALUES (@uuid, @name, @created_at, @updated_at, @count)
                        ON CONFLICT(uuid) DO UPDATE SET
                            name = excluded.name,
                            updated_at = excluded.updated_at,
                            message_count = excluded.message_count;",
                        ("@uuid", conv.Uuid),
                        ("@name", conv.Name ?? "Unnamed conversation"),
                        ("@created_at", ToUnix(conv.CreatedAt)),
                        ("@updated_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                        ("@count", conv.Messages.Count));

                    foreach (var msg in conv.Messages)
                    {
                        ExecuteWithParams(@"
                            INSERT INTO messages (uuid, conversation_uuid, sender, text, thinking, created_at, idx)
                            VALUES (@uuid, @conv_uuid, @sender, @text, @thinking, @created_at, @idx)
                            ON CONFLICT(uuid) DO UPDATE SET 
                                text = excluded.text,
                                thinking = excluded.thinking
                            WHERE text != excluded.text OR thinking != excluded.thinking;",
                            ("@uuid", msg.Uuid),
                            ("@conv_uuid", msg.ConversationUuid),
                            ("@sender", msg.Sender),
                            ("@text", msg.Text ?? ""),
                            ("@thinking", msg.Thinking ?? (object)DBNull.Value),
                            ("@created_at", ToUnix(msg.CreatedAt)),
                            ("@idx", msg.Index));

                        // Rebuild code blocks from scratch to avoid stale entries
                        ExecuteWithParams("DELETE FROM code_blocks WHERE message_uuid = @uuid;",
                            ("@uuid", msg.Uuid));

                        foreach (var block in msg.CodeBlocks)
                            SaveSingleBlock(msg.Uuid, block.Language, block.Code, block.FileName);

                        if (!string.IsNullOrWhiteSpace(msg.Text))
                            SaveCodeBlocksFromText(msg.Uuid, msg.Text);
                    }
                }
                tx.Commit();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[DB] SaveConversations failed, rolling back: {ex.Message}");
                tx.Rollback();
                throw;
            }
        }
    }

    // ── Private helpers ─────────────────────────────────────

    /// <summary>
    /// Inserts a single code block unless an identical one
    /// (same file path + content hash) already exists.
    /// </summary>
    private void SaveSingleBlock(string messageUuid, string? lang, string code, string? fileName = null)
    {
        string hash = ComputeHash(code);
        string path = fileName
            ?? GuessFilePath(code, lang)
            ?? $"snippet_{hash[..8]}.{(string.IsNullOrEmpty(lang) ? "txt" : lang)}";

        ExecuteWithParams(@"
            INSERT INTO code_blocks (message_uuid, language, code, file_path, content_hash)
            SELECT @msg_uuid, @lang, @code, @path, @hash
            WHERE NOT EXISTS (
                SELECT 1 FROM code_blocks 
                WHERE file_path = @path AND content_hash = @hash
            );",
            ("@msg_uuid", messageUuid),
            ("@lang", lang),
            ("@code", code),
            ("@path", path),
            ("@hash", hash));
    }

    /// <summary>
    /// Scans message text for fenced code blocks (``` ```) and persists each one.
    /// </summary>
    private void SaveCodeBlocksFromText(string messageUuid, string text)
    {
        var matches = Regex.Matches(text, @"```(\w*)\n?([\s\S]*?)```");
        foreach (Match m in matches)
        {
            string lang = m.Groups[1].Value.Trim();
            string code = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(code)) continue;

            string hash = ComputeHash(code);
            string path = GuessFilePath(code, lang)
                ?? $"snippet_{hash[..8]}.{(lang != "" ? lang : "txt")}";

            ExecuteWithParams(@"
                INSERT INTO code_blocks (message_uuid, language, code, file_path, content_hash)
                SELECT @msg_uuid, @lang, @code, @path, @hash
                WHERE NOT EXISTS (
                    SELECT 1 FROM code_blocks 
                    WHERE file_path = @path AND content_hash = @hash
                );",
                ("@msg_uuid", messageUuid),
                ("@lang", lang),
                ("@code", code),
                ("@path", path),
                ("@hash", hash));
        }
    }

    /// <summary>SHA-256 hash for code block deduplication.</summary>
    private static string ComputeHash(string code)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(code));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Guesses a file path from a code block:
    /// 1. Explicit "// File:" comment
    /// 2. C# class/interface/record name
    /// Returns <c>null</c> if nothing matches.
    /// </summary>
    private static string? GuessFilePath(string code, string? language)
    {
        var commentMatch = Regex.Match(code, @"(?://|#|--)\s*File:\s*([^\n\r]+)", RegexOptions.IgnoreCase);
        if (commentMatch.Success)
            return commentMatch.Groups[1].Value.Trim();

        if (language == "csharp")
        {
            var classMatch = Regex.Match(code, @"(?:class|interface|record)\s+(\w+)");
            if (classMatch.Success)
                return classMatch.Groups[1].Value + ".cs";
        }

        return null;
    }
}