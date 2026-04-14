using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Mnemos;

public class BlobWatcher : IDisposable
{
    private readonly string _blobRoot;
    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentDictionary<string, long> _seen = new();

    public BlobWatcher(string blobRoot)
    {
        _blobRoot = blobRoot;

        foreach (var f in Directory.GetFiles(blobRoot, "*", SearchOption.AllDirectories))
            try { _seen[f] = new FileInfo(f).Length; } catch { }

        _watcher = new FileSystemWatcher(blobRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Created += (_, _) => Signal();
        _watcher.Changed += (_, _) => Signal();
        _watcher.EnableRaisingEvents = true;
    }

    private void Signal()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    public async IAsyncEnumerable<List<Conversation>> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            await _signal.WaitAsync(TimeSpan.FromSeconds(2), ct);

            foreach (var path in Directory.GetFiles(_blobRoot, "*", SearchOption.AllDirectories))
            {
                FileInfo fi;
                try { fi = new FileInfo(path); } catch { continue; }

                if (_seen.TryGetValue(path, out long lastSize) && lastSize == fi.Length) continue;
                if (fi.Length < 500) continue;

                _seen[path] = fi.Length;

                await Task.Delay(300, ct); // Laisser Chromium finir d'écrire

                var convs = TryParseBlob(path);
                if (convs.Count > 0)
                    yield return convs;
            }
        }
    }

    private static List<Conversation> TryParseBlob(string path)
    {
        try
        {
            byte[]? v8Data = SnappyDecompressor.Decompress(path);
            if (v8Data == null) return [];

            var deserializer = new V8Deserializer(v8Data);
            object? root = deserializer.Deserialize();

            return ConversationExtractor.Extract(root);
        }
        catch { return []; }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _signal.Dispose();
    }
}