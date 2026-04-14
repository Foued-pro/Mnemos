using Mnemos.Models;

namespace Mnemos;

public static class ConversationExtractor
{
    public static List<Conversation> Extract(object? root)
    {
        var result = new List<Conversation>();

        if (root is not Dictionary<string, object?> rootObj) return result;
        if (!rootObj.TryGetValue("clientState", out var cs)) return result;
        if (cs is not Dictionary<string, object?> clientState) return result;
        if (!clientState.TryGetValue("queries", out var qs)) return result;
        if (qs is not List<object?> queries) return result;

        foreach (var q in queries)
        {
            if (q is not Dictionary<string, object?> query) continue;

            // Filtre chat_conversation_tree
            if (!query.TryGetValue("queryKey", out var qk)) continue;
            if (qk is not List<object?> queryKey) continue;
            if (queryKey.Count == 0 || queryKey[0] as string != "chat_conversation_tree") continue;

            // Extraire state.data
            if (!query.TryGetValue("state", out var st)) continue;
            if (st is not Dictionary<string, object?> state) continue;
            if (!state.TryGetValue("data", out var d)) continue;
            if (d is not Dictionary<string, object?> data) continue;

            string convUuid = data.GetString("uuid");
            string convName = data.GetString("name");
            string convCreatedAt = data.GetString("created_at");

            if (!data.TryGetValue("chat_messages", out var msgs)) continue;
            if (msgs is not List<object?> chatMessages) continue;

            var messages = new List<ChatMessage>();

            foreach (var m in chatMessages)
            {
                if (m is not Dictionary<string, object?> msg) continue;

                string uuid = msg.GetString("uuid");
                string sender = msg.GetString("sender");
                string createdAt = msg.GetString("created_at");

                string text = "";
                string? thinking = null;

                if (msg.TryGetValue("content", out var co) && co is List<object?> content)
                {
                    foreach (var block in content)
                    {
                        if (block is not Dictionary<string, object?> b) continue;
                        string type = b.GetString("type");

                        if (type == "text")
                            text = b.GetString("text");
                        else if (type == "thinking")
                            thinking = b.GetString("thinking");
                    }
                }

                if (string.IsNullOrEmpty(text) && thinking == null) continue;

                messages.Add(new ChatMessage(uuid, sender, text, thinking, createdAt, convUuid));
            }

            if (messages.Count > 0)
                result.Add(new Conversation(convUuid, convName, convCreatedAt, messages));
        }

        return result;
    }
}

// Extension helper
public static class DictExtensions
{
    public static string GetString(this Dictionary<string, object?> dict, string key)
        => dict.TryGetValue(key, out var v) && v is string s ? s : "";
}