using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Mnemos.Models;

namespace Mnemos;

public class BlobWatcher : IDisposable
{
    private readonly string _blobRoot;
    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentDictionary<string, long> _seen = new();
    
    // Injected delegate for V8 deserialization to avoid tight coupling
    private readonly Func<string, List<Conversation>> _processBlobFunc; 

    public BlobWatcher(string blobRoot, Func<string, List<Conversation>> processBlobFunc)
    {
        _blobRoot = blobRoot;
        _processBlobFunc = processBlobFunc;
        
        // Index existing files to prevent reprocessing on startup
        foreach (var f in Directory.GetFiles(blobRoot, "*", SearchOption.AllDirectories))
        {
            try { _seen[f] = new FileInfo(f).Length; } catch { }
        }
        
        _watcher = new FileSystemWatcher(blobRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        
        _watcher.Created += (s, e) => Signal();
        _watcher.Changed += (s, e) => Signal();
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
            // Wait for FS events with a 2-second polling fallback
            await _signal.WaitAsync(TimeSpan.FromSeconds(2), ct);
            
            foreach (var path in Directory.GetFiles(_blobRoot, "*", SearchOption.AllDirectories))
            {
                FileInfo fi;
                try { fi = new FileInfo(path); }
                catch { continue; }
                
                // Skip if file hasn't grown/changed
                if (_seen.TryGetValue(path, out long lastSize) && lastSize == fi.Length)
                    continue;
                
                // Ignore temporary/empty Chromium blobs
                if (fi.Length < 500) continue;
                
                _seen[path] = fi.Length;
                
                // Give Chromium time to flush the file stream to disk
                await Task.Delay(200, ct);

                // Process the binary blob
                List<Conversation> convs = _processBlobFunc(path);
                
                if (convs != null && convs.Count > 0)
                {
                    yield return convs;
                }
            }
        }
    }
    
    public void Dispose()
    {
        _watcher.Dispose();
        _signal.Dispose();
    }
}