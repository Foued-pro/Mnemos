using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ZstdSharp;

namespace Mnemos;

public class CacheWatcher : IDisposable
{
    private readonly string _cacheDir;
    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentDictionary<string, byte> _seen = new();

    public CacheWatcher(string cacheDir)
    {
        _cacheDir = cacheDir;

        foreach (var f in Directory.GetFiles(cacheDir))
            _seen[f] = 0;

        _watcher = new FileSystemWatcher(cacheDir)
        {
            NotifyFilter = NotifyFilters.FileName,
            Filter = "f_*"
        };

        _watcher.Created += (_, _) => { if (_signal.CurrentCount == 0) _signal.Release(); };
        _watcher.EnableRaisingEvents = true;
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

                await Task.Delay(100, ct); // Laisser Chromium finir d'écrire

                var convs = TryParseCache(path);
                if (convs.Count > 0)
                    yield return convs;
            }
        }
    }

    public static List<Conversation> TryParseCache(string path)
    {
        try
        {
            byte[] raw = File.ReadAllBytes(path);

            // Vérifier magic zstd : 28 B5 2F FD
            if (raw.Length < 4 ||
                raw[0] != 0x28 || raw[1] != 0xB5 ||
                raw[2] != 0x2F || raw[3] != 0xFD)
                return [];

            using var decompressor = new Decompressor();
            byte[] decompressed = decompressor.Unwrap(raw).ToArray();
            string json = System.Text.Encoding.UTF8.GetString(decompressed);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("chat_messages", out var messagesEl)) return [];

            string uuid = root.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "";
            string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string createdAt = root.TryGetProperty("created_at", out var c) ? c.GetString() ?? "" : "";

            var messages = new List<ChatMessage>();

            foreach (var msgEl in messagesEl.EnumerateArray())
            {
                string sender = msgEl.TryGetProperty("sender", out var s) ? s.GetString() ?? "" : "";
                string msgCreatedAt = msgEl.TryGetProperty("created_at", out var mc) ? mc.GetString() ?? "" : "";
                string msgUuid = msgEl.TryGetProperty("uuid", out var mu) ? mu.GetString() ?? "" : "";

                string text = "";
                string? thinking = null;

                if (msgEl.TryGetProperty("content", out var content))
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        string type = block.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                        if (type == "text")
                            text = block.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                        else if (type == "thinking")
                            thinking = block.TryGetProperty("thinking", out var th) ? th.GetString() : null;
                    }
                }

                if (!string.IsNullOrEmpty(text) || thinking != null)
                    messages.Add(new ChatMessage(msgUuid, sender, text, thinking, msgCreatedAt, uuid));
            }

            if (messages.Count == 0) return [];
            return [new Conversation(uuid, name, createdAt, messages)];
        }
        catch { return []; }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _signal.Dispose();
    }
}