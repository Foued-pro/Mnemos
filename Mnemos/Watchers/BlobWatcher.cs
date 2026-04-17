using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Mnemos.Models;

namespace Mnemos.Watchers;

public class BlobWatcher : IDisposable
{
    private readonly string _blobRoot;
    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly ConcurrentDictionary<string, long> _seen = new();
    
    // Injected delegate for V8 deserialization to avoid tight coupling
    private readonly Func<string, List<Conversation>> _processBlobFunc;
    private readonly Action<string> _logger;

    public BlobWatcher(string blobRoot, Func<string, List<Conversation>> processBlobFunc, Action<string> logger)
    {
        _blobRoot = blobRoot;
        _processBlobFunc = processBlobFunc;
        _logger = logger;

        _logger($"[BLOB] Initializing watcher on: {_blobRoot}");

        if (!Directory.Exists(_blobRoot))
        {
            _logger("[BLOB] BlobRoot directory not found!");
        }
        else
        {
            int dirs = Directory.GetDirectories(_blobRoot).Length;
            int files = Directory.GetFiles(_blobRoot, "*", SearchOption.AllDirectories).Length;
            _logger($"[BLOB] Directory found → {dirs} subdirectories, {files} files total");
        }
        
        _watcher = new FileSystemWatcher(blobRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 65536
        };

        _watcher.Created += (_, _) => Signal();
        _watcher.Changed += (_, _) => Signal();
        _watcher.Renamed += (_, _) => Signal();

        _watcher.EnableRaisingEvents = true;
        _logger("[BLOB] FileSystemWatcher started");
    }

    private void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
        catch { }
    }

    public async IAsyncEnumerable<List<Conversation>> WatchAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        bool isFirstScan = true;

        while (!ct.IsCancellationRequested)
        {
            // Wait for FS events with a 2-second polling fallback
            await _signal.WaitAsync(TimeSpan.FromSeconds(2), ct);

            var allFiles = Directory.GetFiles(_blobRoot, "*", SearchOption.AllDirectories)
                                    .OrderBy(f => f)
                                    .ToList();

            if (isFirstScan)
                _logger($"[BLOB] First full scan: {allFiles.Count} files found");

            foreach (var path in allFiles)
            {
                FileInfo fi;
                try { fi = new FileInfo(path); }
                catch { continue; }

                // Ignore temporary/empty Chromium blobs
                if (fi.Length < 500) continue;

                bool shouldProcess = isFirstScan ||
                                     !_seen.TryGetValue(path, out long lastSize) ||
                                     lastSize != fi.Length;

                if (!shouldProcess) continue;

                _seen[path] = fi.Length;

                _logger($"[BLOB] Processing → {Path.GetFileName(path)} ({fi.Length / 1024} KB)");

                // Give Chromium time to flush the file stream to disk
                await Task.Delay(300, ct);

                List<Conversation>? convs = null;
                try
                {
                    // Process the binary blob
                    convs = _processBlobFunc(path);
                }
                catch (Exception ex)
                {
                    _logger($"[BLOB] Error processing {Path.GetFileName(path)}: {ex.Message}");
                }

                if (convs != null && convs.Count > 0)
                {
                    _logger($"[BLOB] {convs.Count} conversation(s) extracted successfully");
                    foreach (var conv in convs)
                        _logger($"[BLOB]    → \"{conv.Name}\" ({conv.Messages.Count} messages)");
                    
                    yield return convs;
                }
                else if (convs != null)
                {
                    _logger($"[BLOB] No conversations extracted from {Path.GetFileName(path)}");
                }
            }

            isFirstScan = false;
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _signal.Dispose();
    }
}