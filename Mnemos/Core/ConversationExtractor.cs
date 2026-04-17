using System.Text;
using Mnemos.Models;

namespace Mnemos;

public static class ConversationExtractor
{
    public static List<Conversation> Extract(object? root)
    {
        var result = new List<Conversation>();

        if (root is not Dictionary<string, object?> rootObj) return result;
        
        if (!rootObj.TryGetValue("clientState", out var cs) || cs is not Dictionary<string, object?> clientState) 
            return result;
        
        if (!clientState.TryGetValue("queries", out var qs) || qs is not List<object?> queries) 
            return result;

        foreach (var q in queries)
        {
            if (q is not Dictionary<string, object?> query) continue;

            // Target the conversation tree query
            if (!query.TryGetValue("queryKey", out var qk) || qk is not List<object?> queryKey || 
                queryKey.Count == 0 || queryKey[0] as string != "chat_conversation_tree") continue;

            if (!query.TryGetValue("state", out var st) || st is not Dictionary<string, object?> state) continue;
            if (!state.TryGetValue("data", out var d) || d is not Dictionary<string, object?> data) continue;

            string convUuid = data.GetString("uuid");
            string convName = data.GetString("name");
            string convCreatedAt = data.GetString("created_at");

            if (!data.TryGetValue("chat_messages", out var msgs) || msgs is not List<object?> chatMessages) continue;

            var messages = new List<ChatMessage>();

            foreach (var m in chatMessages)
            {
                if (m is not Dictionary<string, object?> msg) continue;

                string uuid = msg.GetString("uuid");
                string sender = msg.GetString("sender");
                string createdAt = msg.GetString("created_at");

                var fullText = new StringBuilder();
                string? thinking = null;

                // Extract multi-part content (text and model thoughts)
                if (msg.TryGetValue("content", out var co) && co is List<object?> content)
                {
                    foreach (var block in content)
                    {
                        if (block is not Dictionary<string, object?> b) continue;
                        string type = b.GetString("type");

                        if (type == "text")
                            fullText.Append(b.GetString("text"));
                        else if (type == "thinking")
                            thinking = b.GetString("thinking");
                    }
                }

                string finalText = fullText.ToString().Trim();

                // skip empty or tiny messages unless they contain thinking blocks
                if (finalText.Length >= 10 || thinking != null)
                {
                    messages.Add(new ChatMessage(uuid, sender, finalText, thinking, createdAt, convUuid));
                }
            }

            if (messages.Count > 0)
                result.Add(new Conversation(convUuid, convName, convCreatedAt, messages));
        }

        return result;
    }
}