using System.Text;
using System.Text.Json;
using Mnemos.Models;

namespace Mnemos;
/// <summary>
/// Extracts conversations from Claude Desktop's IndexedDB state (V8 deserialization).
/// </summary>
public static class ConversationExtractor
{
    // Identifier of the targeted IndexedDB query in the clientState.
    private const string TargetQueryKey = "chat_conversation_tree";

    // Blink serialization artifact (V8 wrapper in IndexedDB),
    private const string BlinkIdbWrapperMarker = "blink-idb-value-wrapper";
    private const string DefaultConversationName = "Unnamed conversation";
    private const string DefaultSender = "system";

    /// <summary>
    /// Extracts all conversations and their messages from the root
    /// deserialized from the IndexedDB store of Claude Desktop.
    /// </summary>
    /// <param name="root">Root object resulting from V8 deserialization</param>
    /// <param name="logger">Log callback for tracking.</param>
    /// <returns>List of extracted conversations, ready to be indexed.</returns>
    public static List<Conversation> Extract(object? root, Action<string> logger)
    {
        var result = new List<Conversation>();
        var seenMessages = new HashSet<string>();
        
        if (root is not Dictionary<string, object?> rootObj)
            return result;

        if (!rootObj.TryGetValue("clientState", out var cs) ||
            cs is not Dictionary<string, object?> clientState)
            return result;

        if (!clientState.TryGetValue("queries", out var qs) ||
            qs is not List<object?> queries)
            return result;

        // --- IndexedDB query path ---
        foreach (var q in queries)
        {
            if (q is not Dictionary<string, object?> query)
                continue;

            if (!query.TryGetValue("queryKey", out var qk) ||
                qk is not List<object?> queryKey ||
                queryKey.Count == 0 ||
                queryKey[0] as string != TargetQueryKey)
                continue;

            if (!query.TryGetValue("state", out var st) ||
                st is not Dictionary<string, object?> state)
                continue;

            if (!state.TryGetValue("data", out var d) ||
                d is not Dictionary<string, object?> data)
                continue;

            string convUuid      = data.GetString("uuid") ?? Guid.NewGuid().ToString();
            string convName      = data.GetString("name") ?? DefaultConversationName;
            string convCreatedAt = data.GetString("created_at") ?? DateTimeOffset.UtcNow.ToString("o");

            if (!data.TryGetValue("chat_messages", out var msgs) ||
                msgs is not List<object?> chatMessages)
                continue;

            var messages = ExtractMessages(chatMessages, convUuid, seenMessages, logger);

            if (messages.Count > 0)
            {
                result.Add(new Conversation(convUuid, convName, convCreatedAt, messages));
                logger($"[SYNC] Conversation '{convName}' extracted with {messages.Count} messages.");
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts messages from a conversation, with management of content blocks
    /// (text, thinking, tool_use, tool_result) and attachments.
    /// </summary>
    private static List<ChatMessage> ExtractMessages(
        List<object?> chatMessages,
        string convUuid,
        HashSet<string> seenMessages,
        Action<string> logger)
    {
        var messages = new List<ChatMessage>();

        foreach (var m in chatMessages)
        {
            if (m is not Dictionary<string, object?> msg)
                continue;

            string uuid = msg.GetString("uuid") ?? Guid.NewGuid().ToString();
            
            if (!seenMessages.Add(uuid))
                continue;

            string sender       = msg.TryGetValue("sender", out var s) ? s?.ToString() ?? DefaultSender : DefaultSender;
            string msgCreatedAt = msg.GetString("created_at") ?? DateTimeOffset.UtcNow.ToString("o");
            int    msgIndex     = msg.TryGetValue("index", out var idx) ? Convert.ToInt32(idx) : 0;

            var fullText   = new StringBuilder();
            string? thinking    = null;
            var codeBlocks      = new List<CodeBlock>();

            // --- Attachments (files, pastes) ---
            if (msg.TryGetValue("attachments", out var att) && att is List<object?> attachments)
            {
                foreach (var a in attachments)
                {
                    if (a is not Dictionary<string, object?> attDict)
                        continue;

                    string attContent = (attDict.GetString("extracted_content") ?? "").Replace("\0", "");
                    string fileName   = attDict.GetString("file_name") ?? "";
                    string fileType   = attDict.GetString("file_type") ?? "text/plain";
                    
                    if (attContent.Contains(BlinkIdbWrapperMarker))
                        continue;

                    if (!string.IsNullOrEmpty(fileName))
                        logger($"[FILE] {fileName} ({fileType}) dans le message {uuid}");

                    codeBlocks.Add(new CodeBlock(
                        Language: fileType,
                        Code: attContent,
                        FileName: fileName
                    ));

                    if (!string.IsNullOrWhiteSpace(attContent))
                    {
                        string header = !string.IsNullOrEmpty(fileName)
                            ? $"[FILE: {fileName}]"
                            : "[PASTE]";
                        fullText.AppendLine($"\n{header}\n{attContent}\n");
                    }
                }
            }

            // --- Content blocks (structured message text) ---
            if (msg.TryGetValue("content", out var co) && co is List<object?> contentList)
            {
                foreach (var block in contentList)
                {
                    if (block is not Dictionary<string, object?> b)
                        continue;

                    string type = b.GetString("type");

                    switch (type)
                    {
                        case "text":
                            fullText.Append((b.GetString("text") ?? "").Replace("\0", ""));
                            break;

                        case "thinking":
                            thinking = (b.GetString("thinking") ?? "").Replace("\0", "");
                            break;

                        case "tool_use":
                            string toolName = b.GetString("name") ?? "outil";
                            fullText.Append($"\n[ACTION: {toolName}]");
                            break;

                        case "tool_result":
                            var resultContent = b.TryGetValue("content", out var rc)
                                ? JsonSerializer.Serialize(rc)
                                : "{}";
                            fullText.Append($"\n[RESULT: {resultContent}]");
                            break;
                    }
                }
            }

            string finalText = fullText.ToString().Trim();

            if (finalText.Length > 0 || thinking != null || codeBlocks.Count > 0)
            {
                messages.Add(new ChatMessage(uuid, sender, finalText, thinking, msgCreatedAt, convUuid, msgIndex)
                {
                    CodeBlocks = codeBlocks
                });
            }
        }

        return messages;
    }
}