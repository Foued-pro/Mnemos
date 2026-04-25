using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mnemos.Database;
using Mnemos.Models;
using Mnemos.Mcp;
using Mnemos.Core;
using Mnemos.Watchers;

namespace Mnemos;

/// <summary>
/// Main orchestrator for Mnemos: wires up the database, ONNX embedding engine,
/// file system watchers, and starts the MCP server on stdio.
/// </summary>
internal static class Program
{
    // ---------- Paths (derived from %APPDATA%\Claude) ----------

    private static readonly string AppData   = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string BlobRoot  = Path.Combine(AppData, @"Claude\IndexedDB\https_claude.ai_0.indexeddb.blob\1");
    private static readonly string CacheDir  = Path.Combine(AppData, @"Claude\Cache\Cache_Data");
    private static readonly string ModelDir  = Path.Combine(AppData, @"Claude\Models\minilm");
    private static readonly string LogFile   = Path.Combine(AppData, @"Claude\mnemos.log");
    private static readonly string DbPath    = Path.Combine(AppData, @"Claude\mnemos.db");

    // ---------- Shared components ----------

    private static readonly MnemosDb Db = new(DbPath, msg => Log(msg, "DB"));
    private static EmbeddingEngine? _embedder;

    // ---------- Logging ----------

    /// <summary>
    /// Writes a timestamped log entry to both the log file and stderr (conservative length).
    /// </summary>
    private static void Log(string message, string level = "INFO")
    {
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
        try
        {
            File.AppendAllText(LogFile, line + Environment.NewLine);
            string consoleLine = line.Length > 120 ? line[..117] + "..." : line;
            Console.Error.WriteLine(consoleLine);
        }
        catch
        {
            // Logging errors must not crash the process.
        }
    }

    // ---------- Entry point ----------

    /// <summary>
    /// Main entry point: synchronizes existing history, starts watchers
    /// and the embedding background thread, then runs the MCP host.
    /// </summary>
    
    private static Mutex? _appMutex;
    private static async Task Main(string[] args)
    {
        const string mutexName = "MnemosApp_McpServer";
        _appMutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            return;   
        }
        using var cts = new CancellationTokenSource();
        // Ensure stdout is left untouched for MCP JSON-RPC transport.
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding  = Encoding.UTF8;

        try
        {
            // Reset log file for clean session
            try { File.WriteAllText(LogFile, string.Empty); } catch { }

            Log("Mnemos v1.1 starting");

            // 1. Import all existing conversations
            SyncAll();

            // 2. Initialize ONNX embedding engine if model present
            if (Directory.Exists(ModelDir))
            {
                try
                {
                    _embedder = new EmbeddingEngine(ModelDir, msg => Log(msg, "ONNX"));
                    Log("Semantic engine ready.");
                }
                catch (Exception ex)
                {
                    Log($"Failed to initialize ONNX engine: {ex.Message}", "ERROR");
                }
            }
            else
            {
                Log($"Models directory not found: {ModelDir}", "WARN");
            }

            // 3. Register tools for the MCP server
            McpTools.Init(Db, _embedder, (msg, level) => Log(msg, level));

            // 4. Start background embedding thread (if engine loaded)
            if (_embedder != null)
                StartEmbeddingThread(cts.Token);

            // 5. Start file system watchers (cache + blob)
            StartWatchers(cts.Token);
            // 6. Build and run the MCP host on stdio
            Log("Mnemos is now watching Claude. Ready.");

            var builder = Host.CreateApplicationBuilder(args);
            builder.Logging.ClearProviders();
            builder.Services.AddMcpServer()
                .WithStdioServerTransport()
                .WithToolsFromAssembly();

            var host = builder.Build();
            var hostLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            hostLifetime.ApplicationStopping.Register(() => cts.Cancel());

            await host.RunAsync();
        }
        catch (Exception fatalEx)
        {
            Log($"FATAL: {fatalEx}", "FATAL");
            throw;
        }
        finally
        {
            _appMutex?.ReleaseMutex();
            _appMutex?.Dispose();
        }
    }

    // ---------- Background embedding thread ----------

    /// <summary>
    /// Continuously polls for messages without embeddings and processes them in batches.
    /// </summary>
    private static void StartEmbeddingThread(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var msgs = Db.GetMessagesWithoutEmbedding(100);
                    if (msgs.Count == 0)
                    {
                        await Task.Delay(5000, ct);
                        continue;
                    }

                    foreach (var (uuid, text) in msgs)
                    {
                        try
                        {
                            if (ct.IsCancellationRequested) break;
                            Db.SaveEmbeddings(uuid, _embedder!.GenerateEmbeddings(text));
                        }
                        catch (Exception ex)
                        {
                            Log($"Embedding failed for {uuid}: {ex.Message}", "ERROR");
                        }
                    }

                    Log($"Indexed {msgs.Count} messages.");
                }
                catch (Exception ex)
                {
                    Log($"Embedding thread error: {ex.Message}", "ERROR");
                    await Task.Delay(10000, ct);
                    
                }
            }
        });
    }

    // ---------- Full history sync ----------

    /// <summary>
    /// Scans all existing blobs and imports any conversations not yet in the database.
    /// </summary>
    private static void SyncAll()
    {
        var allConversations = new List<Conversation>();
        var seenInThisSync   = new HashSet<string>();

        if (!Directory.Exists(BlobRoot))
        {
            Log($"Blob directory not found: {BlobRoot}", "WARN");
            return;
        }

        foreach (var dir in Directory.GetDirectories(BlobRoot).OrderBy(d => d))
        {
            foreach (var blobPath in Directory.GetFiles(dir))
            {
                var convs = ProcessBlob(blobPath);
                foreach (var conv in convs)
                {
                    if (!seenInThisSync.Add(conv.Uuid))
                        continue;
                    allConversations.Add(conv);
                }
            }
        }

        if (allConversations.Count > 0)
        {
            Db.SaveConversations(allConversations);
            Log($"[SYNC] {allConversations.Count} conversations recovered from history.");
        }
    }

    /// <summary>
    /// Decompresses, deserializes, and extracts conversations from a single blob file.
    /// </summary>
    private static List<Conversation> ProcessBlob(string path)
    {
        try
        {
            byte[]? v8Data = SnappyDecompressor.Decompress(path);
            if (v8Data == null) return [];

            var deserializer = new V8Deserializer(v8Data, msg => Log(msg, "DEBUG"));
            var root = deserializer.Deserialize();

            return ConversationExtractor.Extract(root, msg =>
            {
                if (msg.Contains("FOUND"))
                    Log(msg, "BLOB");
            });
        }
        catch (Exception ex)
        {
            Log($"Error processing blob {Path.GetFileName(path)}: {ex.Message}", "ERROR");
            return [];
        }
    }

    // ---------- Real-time watchers ----------

    /// <summary>
    /// Starts the cache watcher (live conversation updates) and the blob watcher
    /// (history changes) on separate background tasks.
    /// </summary>
    private static void StartWatchers(CancellationToken ct)
    {
        // Watch the HTTP cache for real-time conversation changes
        _ = Task.Run(async () =>
        {
            try
            {
                using var watcher = new CacheWatcher(CacheDir, msg => Log(msg, "CACHE"));
                await foreach (var convs in watcher.WatchAsync(ct))
                {
                    if (convs.Count > 0)
                    {
                        Db.SaveConversations(convs);
                        Log("Live message captured from cache.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"CacheWatcher fatal error: {ex.Message}", "ERROR");
            }
        });

        // Watch the IndexedDB blob storage for historical changes
        _ = Task.Run(async () =>
        {
            try
            {
                using var blobWatcher = new BlobWatcher(BlobRoot, ProcessBlob, msg => Log(msg, "BLOB"));
                await foreach (var convs in blobWatcher.WatchAsync(ct))
                {
                    if (convs.Count > 0)
                    {
                        Db.SaveConversations(convs);
                        Log("History update detected.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"BlobWatcher fatal error: {ex.Message}", "ERROR");
            }
        });
    }
}