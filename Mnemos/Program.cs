using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mnemos.Database;
using Mnemos.Models;
using Mnemos.Mcp;
using Mnemos.Core;
using Mnemos.Watchers;

namespace Mnemos;

class Program
{
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string BlobRoot = Path.Combine(AppData, @"Claude\IndexedDB\https_claude.ai_0.indexeddb.blob\1");
    private static readonly string CacheDir = Path.Combine(AppData, @"Claude\Cache\Cache_Data");
    private static readonly string ModelDir = Path.Combine(AppData, @"Claude\Models\minilm");
    private static readonly string LogFile = Path.Combine(AppData, @"Claude\mnemos.log");

    private static readonly MnemosDb Db = new(Path.Combine(AppData, @"Claude\mnemos.db"));
    private static EmbeddingEngine? _embedder;

    // ── LOGGING ──────────────────────────────────────────────────────────
    static void Log(string message, string level = "INFO")
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        try 
        { 
            File.AppendAllText(LogFile, line + "\n"); 
        } 
        catch { }

    }

    // ── MAIN (MCP DAEMON) ─────────────────────────────────────────────────
    static async Task Main(string[] args)
    {
        Log("Mnemos v4.0 (Daemon MCP) démarrage...");

        //  Database Initialization & Initial Sync
        var (_, mcpMsgs) = Db.GetStats();
        if (mcpMsgs == 0)
        {
            Log("Empty database detected. Starting initial sync...");
            SyncAll();
        }

        // Semantic Engine Initialization (ONNX)
        if (Directory.Exists(ModelDir))
        {
            try
            {
                _embedder = new EmbeddingEngine(ModelDir, msg => Log(msg));
                Log("[ONNX] Semantic engine initialized.");
            }
            catch (Exception ex)
            {
                Log($"[ONNX] Initialization error: {ex.Message}", "ERROR");
            }
        }

        // Initialize MCP Tools
        McpTools.Init(Db, _embedder);

        // 1. Thread ONNX (Background Indexing)
        if (_embedder != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var msgs = Db.GetMessagesWithoutEmbedding(500);
                        if (msgs.Count == 0) { await Task.Delay(TimeSpan.FromSeconds(5)); continue; }

                        int successCount = 0;
                        foreach (var (uuid, text) in msgs)
                        {
                            try { Db.SaveEmbeddings(uuid, _embedder.GenerateEmbeddings(text)); successCount++; }
                            catch (Exception ex)
                            {
                                Log($"[ONNX] Embedding failed for {uuid}: {ex.Message}", "WARN");
                            }
                        }
                        if (successCount > 0) Log($"[ONNX] Batch processed: {successCount} messages vectorized.");
                        await Task.Delay(1000);
                    }
                }
                catch (Exception ex) { Log($"[ONNX] Thread crashed: {ex.Message}", "ERROR"); }
            });
        }

        // Real-time Cache Watcher
        _ = Task.Run(async () =>
        {
            try
            {
                using var watcher = new CacheWatcher(CacheDir, msg => Log(msg));
                await foreach (var convs in watcher.WatchAsync(CancellationToken.None))
                {
                    if (convs.Count > 0)
                    {
                        Db.SaveConversations(convs);
                        Log($"[CACHE] {convs.Count} conversation(s) interceptée(s)");

                        if (_embedder != null)
                        {
                            var newMsgs = Db.GetMessagesWithoutEmbedding(50);
                            foreach (var (uuid, text) in newMsgs)
                                Db.SaveEmbeddings(uuid, _embedder.GenerateEmbeddings(text));
                        }
                    }
                }
            }
            catch (Exception ex) { Log($"[CACHE] Thread crashed: {ex.Message}", "ERROR"); }
        });

        // Real-time Blob Watcher (IndexedDB)
        _ = Task.Run(async () =>
        {
            try
            {
                using var blobWatcher = new BlobWatcher(BlobRoot, ProcessBlob, msg => Log(msg));
                await foreach (var convs in blobWatcher.WatchAsync(CancellationToken.None))
                {
                    if (convs.Count > 0)
                    {
                        Db.SaveConversations(convs);
                        Log($"[BLOB] {convs.Count} conversations synchronized from IndexedDB.");
                    }
                }
            }
            catch (Exception ex) { Log($"[BLOB] Watcher crashed: {ex.Message}", "ERROR"); }
        });

        // Start MCP Stdio Server 
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
                        .WithStdioServerTransport()
                        .WithToolsFromAssembly();

        Log("MCP Server started successfully (Stdio mode).");
        await builder.Build().RunAsync();
    }

    // ── SYNC & HELPERS ────────────────────────────────────────────────────
    static void SyncAll()
    {
        Log("[SYNC] Extracting history...");
        var allConversations = new List<Conversation>();

        if (Directory.Exists(BlobRoot))
        {
            foreach (var dir in Directory.GetDirectories(BlobRoot).OrderBy(d => d))
                foreach (var blobPath in Directory.GetFiles(dir))
                    allConversations.AddRange(ProcessBlob(blobPath));
        }

        if (Directory.Exists(CacheDir))
        {
            var seen = new HashSet<string>();
            foreach (var path in Directory.GetFiles(CacheDir, "f_*").OrderByDescending(f => new FileInfo(f).LastWriteTime))
            {
                foreach (var conv in CacheWatcher.TryParseCache(path))
                {
                    if (seen.Contains(conv.Uuid)) continue;
                    seen.Add(conv.Uuid);
                    allConversations.Add(conv);
                }
            }
        }

        if (allConversations.Count > 0)
        {
            Db.SaveConversations(allConversations);
            Log($"[SYNC] {allConversations.Count} conversations loaded into DB.");
        }
    }

    static List<Conversation> ProcessBlob(string path)
    {
        try
        {
            byte[]? v8Data = SnappyDecompressor.Decompress(path, msg => Log(msg, "WARN"));
            if (v8Data == null) return [];
            var deserializer = new V8Deserializer(v8Data, msg => Log(msg, "WARN"));
            return ConversationExtractor.Extract(deserializer.Deserialize());
        }
        catch (Exception ex)
        {
            Log($"[BLOB] Failed to process {Path.GetFileName(path)}: {ex.Message}", "WARN");
            return [];
        }
    }
}