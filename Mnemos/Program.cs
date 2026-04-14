using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mnemos;

class Program
{
    private static readonly string BlobRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Claude\IndexedDB\https_claude.ai_0.indexeddb.blob\1"
    );

    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Claude\Cache\Cache_Data"
    );

    private static readonly string OutputFile = "conversations.txt";

    static async Task Main(string[] args)
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║         MNEMOS v4.0 - MOTEUR         ║");
        Console.WriteLine("╚══════════════════════════════════════╝\n");
        Console.ResetColor();

        if (args.Contains("--sync"))
        {
            SyncAll();
        }
        else if (args.Contains("--watch"))
        {
            await WatchAsync();
        }
        else
        {
            Console.WriteLine("Choisir un mode :");
            Console.WriteLine("  [1] Extraire tout l'historique");
            Console.WriteLine("  [2] Voir les conversations en temps réel");
            Console.Write("\nChoix : ");

            string? choice = Console.ReadLine();
            if (choice == "1") SyncAll();
            else if (choice == "2") await WatchAsync();
            else Console.WriteLine("Choix invalide.");
        }
    }

    // ── SYNC ──────────────────────────────────────────────────────────────
    static void SyncAll()
    {
        Console.WriteLine("[SYNC] Extraction de tout l'historique...\n");
        var allConversations = new List<Conversation>();

        // 1. Blobs
        Console.WriteLine("--- BLOBS ---");
        if (Directory.Exists(BlobRoot))
        {
            foreach (var dir in Directory.GetDirectories(BlobRoot).OrderBy(d => d))
            {
                foreach (var blobPath in Directory.GetFiles(dir))
                {
                    var convs = ProcessBlob(blobPath);
                    if (convs.Count > 0)
                    {
                        allConversations.AddRange(convs);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"[+] {Path.GetFileName(dir)} → {convs.Count} conv(s)");
                        Console.ResetColor();
                    }
                }
            }
        }

        // 2. Cache Chromium
        Console.WriteLine("\n--- CACHE CHROMIUM ---");
        if (Directory.Exists(CacheDir))
        {
            var seen = new HashSet<string>();
            foreach (var path in Directory.GetFiles(CacheDir, "f_*")
                         .OrderByDescending(f => new FileInfo(f).LastWriteTime))
            {
                var convs = CacheWatcher.TryParseCache(path);
                foreach (var conv in convs)
                {
                    if (seen.Contains(conv.Uuid)) continue;
                    seen.Add(conv.Uuid);
                    allConversations.Add(conv);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[+] Cache → {conv.Name} ({conv.Messages.Count} messages)");
                    Console.ResetColor();
                }
            }
        }

        Console.WriteLine($"\n[i] Total : {allConversations.Count} conversations extraites");

        if (allConversations.Count > 0)
        {
            WriteToFile(allConversations);
            Console.WriteLine($"[i] Sauvegardé dans {OutputFile}");
        }
    }

    // ── WATCH ─────────────────────────────────────────────────────────────
    static async Task WatchAsync()
    {
        Console.WriteLine("[WATCH] En écoute... (Ctrl+C pour arrêter)\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

        var seenMessages = new HashSet<string>();

        var cacheTask = Task.Run(async () =>
        {
            try
            {
                using var watcher = new CacheWatcher(CacheDir);
                await foreach (var convs in watcher.WatchAsync(cts.Token))
                {
                    foreach (var conv in convs)
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"\n╔══ {conv.Name}");
                        Console.ResetColor();

                        foreach (var msg in conv.Messages)
                        {
                            if (seenMessages.Contains(msg.Uuid)) continue;
                            seenMessages.Add(msg.Uuid);
                            PrintMessage(msg);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERR] {ex.Message}");
                Console.ResetColor();
            }
        });

        try { await cacheTask; }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\n[i] Arrêté.");
        }
    }

    // ── HELPERS ───────────────────────────────────────────────────────────
    static List<Conversation> ProcessBlob(string path)
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

    static void PrintMessage(ChatMessage msg)
    {
        if (string.IsNullOrEmpty(msg.Text) && msg.Thinking == null) return;

        Console.ForegroundColor = msg.Sender == "human"
            ? ConsoleColor.White
            : ConsoleColor.Green;

        string time = msg.CreatedAt.Length >= 19 ? msg.CreatedAt[..19] : msg.CreatedAt;
        Console.WriteLine($"\n[{msg.Sender.ToUpper()}] {time}");
        Console.ResetColor();

        if (!string.IsNullOrEmpty(msg.Text))
            Console.WriteLine(msg.Text.Length > 500
                ? msg.Text[..500] + "..."
                : msg.Text);

        if (msg.Thinking != null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[thinking] {msg.Thinking[..Math.Min(80, msg.Thinking.Length)]}...");
            Console.ResetColor();
        }
    }

    static void WriteToFile(List<Conversation> conversations)
    {
        using var writer = new StreamWriter(OutputFile, false);
        foreach (var conv in conversations.OrderBy(c => c.CreatedAt))
        {
            writer.WriteLine(new string('=', 60));
            writer.WriteLine($"CONVERSATION : {conv.Name}");
            writer.WriteLine($"UUID         : {conv.Uuid}");
            writer.WriteLine($"DATE         : {conv.CreatedAt}");
            writer.WriteLine(new string('=', 60));

            foreach (var msg in conv.Messages)
            {
                writer.WriteLine($"\n[{msg.Sender.ToUpper()}] {msg.CreatedAt}");
                writer.WriteLine(msg.Text);
                if (msg.Thinking != null)
                    writer.WriteLine($"[THINKING] {msg.Thinking}");
            }
            writer.WriteLine();
        }
    }
}