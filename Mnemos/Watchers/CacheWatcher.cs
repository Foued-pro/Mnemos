using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Mnemos.Models;
using ZstdSharp;

namespace Mnemos.Watchers;

public class CacheWatcher : IDisposable
{
    private readonly string _cacheDir;
    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly ConcurrentDictionary<string, byte> _seen = new();
    private readonly Action<string> _logger;

    public CacheWatcher(string cacheDir, Action<string> logger)
    {
        _cacheDir = cacheDir;
        _logger = logger;

        foreach (var f in Directory.GetFiles(cacheDir, "f_*"))
            _seen[f] = 0;

        _watcher = new FileSystemWatcher(cacheDir)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            Filter = "f_*",
            IncludeSubdirectories = false
        };

        _watcher.Created += (_, _) => SafeRelease();
        _watcher.Renamed += (_, _) => SafeRelease();
        _watcher.EnableRaisingEvents = true;

        _logger($"[WATCH] Monitoring started on: {_cacheDir} ({_seen.Count} existing files ignored)");
    }

    private void SafeRelease()
    {
        try { if (_signal.CurrentCount == 0) _signal.Release(); } catch { }
    }

    public async IAsyncEnumerable<List<Conversation>> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await _signal.WaitAsync(TimeSpan.FromSeconds(2), ct);

            foreach (var path in Directory.GetFiles(_cacheDir, "f_*"))
            {
                if (_seen.ContainsKey(path)) continue;

                _seen[path] = 0;
                _logger($"[WATCH] New cache file detected -> {Path.GetFileName(path)}");

                await Task.Delay(100, ct);

                var convs = TryParseCache(path);
                if (convs.Count > 0)
                {
                    foreach (var conv in convs)
                    {
                        var humanMsg = conv.Messages.LastOrDefault(m => m.Sender == "human");
                        var assistantMsg = conv.Messages.LastOrDefault(m => m.Sender == "assistant");

                        if (humanMsg != null)
                            _logger($"[WATCH] [HUMAN] :\n{humanMsg.Text}");

                        if (assistantMsg != null)
                            _logger($"[WATCH] [ASSISTANT] :\n{assistantMsg.Text}");
                    }
                    yield return convs;
                }
            }
        }
    }

    public static List<Conversation> TryParseCache(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            byte[] raw = ms.ToArray();

            // Zstandard magic number check (0x28 0xB5 0x2F 0xFD)
            if (raw.Length < 4 || raw[0] != 0x28 || raw[1] != 0xB5 || raw[2] != 0x2F || raw[3] != 0xFD)
                return [];

            using var decompressor = new Decompressor();
            byte[] decompressed = decompressor.Unwrap(raw).ToArray();
            string json = Encoding.UTF8.GetString(decompressed);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var conversations = new List<Conversation>();

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    var conv = ParseSingleConversation(item);
                    if (conv != null) conversations.Add(conv);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("chat_messages", out _) || root.TryGetProperty("messages", out _))
                {
                    var conv = ParseSingleConversation(root);
                    if (conv != null) conversations.Add(conv);
                }
                else
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            var conv = ParseSingleConversation(item);
                            if (conv != null) conversations.Add(conv);
                        }
                    }
                }
            }

            return conversations;
        }
        catch { return []; }
    }

    private static Conversation? ParseSingleConversation(JsonElement el)
    {
        try
        {
            string uuid = el.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "";

            string name = "Conversation";
            if (el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                name = n.GetString() ?? name;
            else if (el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                name = t.GetString() ?? name;

            JsonElement? messagesEl = null;
            if (el.TryGetProperty("chat_messages", out var cm) && cm.ValueKind == JsonValueKind.Array)
                messagesEl = cm;
            else if (el.TryGetProperty("messages", out var m) && m.ValueKind == JsonValueKind.Array)
                messagesEl = m;

            if (!messagesEl.HasValue) return null;

            var messages = new List<ChatMessage>();

            foreach (var msgEl in messagesEl.Value.EnumerateArray())
            {
                string sender = msgEl.TryGetProperty("sender", out var s) ? s.GetString() ?? "unknown" : "unknown";
                string msgUuid = msgEl.TryGetProperty("uuid", out var mu) ? mu.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                string msgCreatedAt = msgEl.TryGetProperty("created_at", out var mc) ? mc.GetString() ?? "" : "";

                var fullText = new StringBuilder();
                string? thinking = null;

                if (msgEl.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        string type = block.TryGetProperty("type", out var typ) ? typ.GetString() ?? "" : "";
                        if (type == "text" && block.TryGetProperty("text", out var tx))
                            fullText.Append(tx.GetString());
                        else if (type == "thinking" && block.TryGetProperty("thinking", out var th))
                            thinking = th.GetString();
                    }
                }

                if (msgEl.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
                {
                    foreach (var att in attachments.EnumerateArray())
                    {
                        if (att.TryGetProperty("extracted_content", out var ext) && !string.IsNullOrEmpty(ext.GetString()))
                            fullText.Append("\n[Attachment] : " + ext.GetString());
                    }
                }

                string finalText = fullText.ToString().Trim();
                if (finalText.Length >= 10 || thinking != null)
                    messages.Add(new ChatMessage(msgUuid, sender, finalText, thinking, msgCreatedAt, uuid));
            }

            if (messages.Count == 0) return null;
            return new Conversation(uuid, name, "", messages);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _signal.Dispose();
    }
}