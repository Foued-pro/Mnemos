using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Mnemos;

public class BlobWatcher : IDisposable
{
    private readonly string _blobRoot;
    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentDictionary<string, long> _seen = new();
    
    // On ajoute un "pointeur" vers ta super fonction de décodage V8 !
    private readonly Func<string, List<Conversation>> _processBlobFunc; 

    public BlobWatcher(string blobRoot, Func<string, List<Conversation>> processBlobFunc)
    {
        _blobRoot = blobRoot;
        _processBlobFunc = processBlobFunc;
        
        // Index les fichiers existants au démarrage
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
            // Attend un signal ou timeout (polling de secours)
            await _signal.WaitAsync(TimeSpan.FromSeconds(2), ct);
            
            // Scan tous les fichiers pour trouver les nouveaux/modifiés
            foreach (var path in Directory.GetFiles(_blobRoot, "*", SearchOption.AllDirectories))
            {
                FileInfo fi;
                try { fi = new FileInfo(path); }
                catch { continue; }
                
                // Skip si déjà vu avec même taille
                if (_seen.TryGetValue(path, out long lastSize) && lastSize == fi.Length)
                    continue;
                
                // Skip les petits fichiers
                if (fi.Length < 500) continue;
                
                _seen[path] = fi.Length;
                
                // Laisse le temps à Chrome d'écrire le fichier sur le disque
                await Task.Delay(200, ct);

                // MAGIE : On utilise ton V8Deserializer ici !
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