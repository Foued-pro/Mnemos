// BlobWatcher.cs - Surveillance des réponses Claude
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
namespace Mnemos;

public class BlobWatcher : IDisposable
{
    private readonly string _blobRoot;
    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentDictionary<string, long> _seen = new(); // path -> lastSize
    
    public BlobWatcher(string blobRoot)
    {
        _blobRoot = blobRoot;
        
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
    
    public async IAsyncEnumerable<ClaudeMessage> WatchAsync(
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
                
                // Parse le blob
                var msg = await TryParseBlob(path);
                if (msg != null)
                    yield return msg;
            }
        }
    }
    
    private static async Task<ClaudeMessage?> TryParseBlob(string path)
{
    try
    {
        await Task.Delay(200);
        
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes);
        
        string raw = Encoding.UTF8.GetString(bytes);
        
        // DEBUG: Affiche tout nouveau blob avec contenu intéressant
        if (raw.Contains("thinking") || raw.Contains("text\":") || raw.Contains("content"))
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"\n[DEBUG BLOB] {Path.GetFileName(path)} ({bytes.Length}B)");
            Console.ResetColor();
            
            // Cherche les zones intéressantes
            int thinkIdx = raw.IndexOf("thinking");
            if (thinkIdx != -1)
            {
                Console.WriteLine($"  → 'thinking' trouvé à pos {thinkIdx}");
                Console.WriteLine($"    Context: ...{raw.Substring(Math.Max(0, thinkIdx - 20), Math.Min(200, raw.Length - thinkIdx + 20))}...");
            }
            
            int textIdx = raw.IndexOf("\"text\"");
            if (textIdx != -1)
            {
                Console.WriteLine($"  → '\"text\"' trouvé à pos {textIdx}");
                Console.WriteLine($"    Context: ...{raw.Substring(Math.Max(0, textIdx - 10), Math.Min(200, raw.Length - textIdx + 10))}...");
            }
        }
        
        var msg = BlobParser.ExtractMessage(bytes);
        
        if (msg != null)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"[PARSED] Text={msg.Text?.Length ?? 0} chars, Thinking={msg.Thinking?.Length ?? 0} chars");
            Console.ResetColor();
        }
        
        return msg;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[BLOB ERR] {ex.Message}");
        return null;
    }
}
    public void Dispose()
    {
        _watcher.Dispose();
        _signal.Dispose();
    }
}