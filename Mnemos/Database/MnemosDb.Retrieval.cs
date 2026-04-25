
using Mnemos.Models;

namespace Mnemos.Database;

public partial class MnemosDb
{
    /// <summary>
    /// Retrieves the most recent N messages from a given conversation.
    /// </summary>
    /// <param name="convUuid">The conversation UUID.</param>
    /// <param name="n">Number of messages to return (default 10).</param>
    /// <returns>List of <see cref="ChatMessage"/> ordered by index descending.</returns>
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

    /// <summary>
    /// Returns the total number of conversations and messages in the database.
    /// </summary>
    public (int conversations, int messages) GetStats()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM conversations;";
        int convs = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COUNT(*) FROM messages;";
        int msgs = Convert.ToInt32(cmd.ExecuteScalar());
        return (convs, msgs);
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> distinct conversation UUIDs
    /// that have messages, ordered by most recent.
    /// </summary>
    /// <param name="limit">Maximum number of conversation UUIDs to return.</param>
    public List<string> GetConversationIds(int limit = 200)
    {
        var ids = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT conversation_uuid
            FROM messages
            WHERE conversation_uuid IS NOT NULL AND conversation_uuid != ''
            ORDER BY created_at DESC
            LIMIT @limit;";
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    /// Retrieves the version history of a given file path across all messages.
    /// </summary>
    /// <param name="filePath">The file path as stored in code_blocks.</param>
    /// <returns>List of <see cref="FileRevision"/> ordered chronologically.</returns>
    public List<FileRevision> GetFileHistory(string filePath)
    {
        var results = new List<FileRevision>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                m.uuid,
                m.created_at, 
                cb.language, 
                cb.code, 
                cb.content_hash
            FROM code_blocks cb
            JOIN messages m ON cb.message_uuid = m.uuid
            WHERE cb.file_path = @path
            ORDER BY m.created_at ASC;";

        cmd.Parameters.AddWithValue("@path", filePath);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new FileRevision(
                reader.GetString(0),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).ToString("o"),
                reader.IsDBNull(2) ? "txt" : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)
            ));
        }
        return results;
    }

    /// <summary>
    /// Returns the most recently stored messages across all conversations,
    /// with a short text preview and conversation name.
    /// </summary>
    /// <param name="limit">Number of recent items to return (default 20).</param>
    /// <returns>List of (ConvUuid, Sender, Preview, ConvName) tuples.</returns>
    public List<(string ConvUuid, string Sender, string Preview, string ConvName)> GetRecentStored(int limit = 20)
    {
        var results = new List<(string, string, string, string)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT m.conversation_uuid, m.sender, SUBSTR(m.text, 1, 100),
                   COALESCE(c.name, 'Unnamed conversation')
            FROM messages m
            LEFT JOIN conversations c ON c.uuid = m.conversation_uuid
            ORDER BY m.rowid DESC
            LIMIT @limit;";
        cmd.Parameters.AddWithValue("@limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return results;
    }

    /// <summary>
    /// Represents a single revision of a code snippet stored in a message.
    /// </summary>
    /// <param name="MessageUuid">UUID of the message containing this code block.</param>
    /// <param name="Date">Date of the message.</param>
    /// <param name="Language">Programming language of the code block.</param>
    /// <param name="Code">The code content.</param>
    /// <param name="Hash">SHA-256 hash of the code content.</param>
    public record FileRevision(string MessageUuid, string Date, string Language, string Code, string Hash);
}