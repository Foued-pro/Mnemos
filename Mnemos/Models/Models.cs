namespace Mnemos.Models;

/// <summary>
/// Represents a file attachment extracted from a message.
/// </summary>
/// <param name="Id">Unique attachment identifier.</param>
/// <param name="FileName">Original file name (may be empty for pastes).</param>
/// <param name="FileType">MIME type or language identifier.</param>
/// <param name="FileSize">Size in bytes.</param>
/// <param name="Content">Extracted text content.</param>
public record Attachment(
    string Id,
    string FileName,
    string FileType,
    long FileSize,
    string Content
);

/// <summary>
/// A complete conversation with all its messages.
/// </summary>
/// <param name="Uuid">Unique conversation identifier.</param>
/// <param name="Name">Display name (set by user or auto-generated).</param>
/// <param name="CreatedAt">ISO 8601 creation date.</param>
/// <param name="Messages">All messages in this conversation.</param>
public record Conversation(
    string Uuid,
    string Name,
    string CreatedAt,
    List<ChatMessage> Messages
);

/// <summary>
/// A single search hit returned by full-text or semantic queries.
/// </summary>
/// <param name="Uuid">Message UUID.</param>
/// <param name="ConversationUuid">Parent conversation UUID.</param>
/// <param name="Sender">"human" or "assistant".</param>
/// <param name="Text">Full message text.</param>
/// <param name="Thinking">Claude's internal reasoning block, if any.</param>
/// <param name="CreatedAt">ISO 8601 message date.</param>
/// <param name="Relevance">Relevance score (0–1, higher is better).</param>
public record SearchResult(
    string Uuid,
    string ConversationUuid,
    string Sender,
    string Text,
    string? Thinking,
    string CreatedAt,
    double Relevance
);

/// <summary>
/// A code snippet extracted from a message (either from a fenced code block
/// or from an attachment).
/// </summary>
/// <param name="Language">Programming language (e.g. "csharp", "python").</param>
/// <param name="Code">The actual code content.</param>
/// <param name="FileName">Optional file name for tracking.</param>
public record CodeBlock(
    string Language,
    string Code,
    string? FileName = null
);

/// <summary>
/// A single message within a conversation.
/// </summary>
public class ChatMessage
{
    /// <summary>Unique message identifier.</summary>
    public string Uuid { get; set; }

    /// <summary>"human" or "assistant".</summary>
    public string Sender { get; set; }

    /// <summary>Plain text content (attachments and tool results are inlined here).</summary>
    public string Text { get; set; }

    /// <summary>Claude's internal reasoning block (null for user messages).</summary>
    public string? Thinking { get; set; }

    /// <summary>ISO 8601 message creation date.</summary>
    public string CreatedAt { get; set; }

    /// <summary>UUID of the parent conversation.</summary>
    public string ConversationUuid { get; set; }

    /// <summary>Ordinal position within the conversation.</summary>
    public int Index { get; set; }

    /// <summary>Code blocks and attachments extracted from this message.</summary>
    public List<CodeBlock> CodeBlocks { get; set; } = new();

    /// <summary>
    /// Creates a new ChatMessage.
    /// </summary>
    /// <param name="uuid">Message UUID.</param>
    /// <param name="sender">"human" or "assistant".</param>
    /// <param name="text">Plain text content.</param>
    /// <param name="thinking">Internal reasoning block (optional).</param>
    /// <param name="createdAt">ISO 8601 creation date.</param>
    /// <param name="convUuid">Parent conversation UUID.</param>
    /// <param name="index">Ordinal position.</param>
    public ChatMessage(string uuid, string sender, string text, string? thinking, string createdAt, string convUuid, int index)
    {
        Uuid = uuid;
        Sender = sender;
        Text = text;
        Thinking = thinking;
        CreatedAt = createdAt;
        ConversationUuid = convUuid;
        Index = index;
    }
}

/// <summary>
/// A single point in the 3D constellation visualization,
/// after UMAP projection and K-Means clustering.
/// </summary>
/// <param name="Uuid">Message UUID.</param>
/// <param name="ConversationUuid">Parent conversation UUID.</param>
/// <param name="Sender">"human" or "assistant".</param>
/// <param name="Preview">First 80 characters of the message text.</param>
/// <param name="X">UMAP X coordinate.</param>
/// <param name="Y">UMAP Y coordinate.</param>
/// <param name="Z">UMAP Z coordinate.</param>
/// <param name="Date">ISO 8601 message date.</param>
/// <param name="ClusterId">K-Means cluster index.</param>
/// <param name="ClusterName">Human-readable cluster label.</param>
/// <param name="FullText">Complete message text (for tooltip/details).</param>
public record UniversePoint(
    string Uuid,
    string ConversationUuid,
    string Sender,
    string Preview,
    float  X,
    float  Y,
    float Z,
    string Date,
    int ClusterId,
    string ClusterName,
    string FullText = ""
);