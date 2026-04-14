namespace Mnemos;

class Program
{
    private static readonly string BlobRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Claude\IndexedDB\https_claude.ai_0.indexeddb.blob\1"
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
            Console.WriteLine("[SYNC] Extraction de tout l'historique...\n");
            SyncAll();
        }
        else if (args.Contains("--watch"))
        {
            Console.WriteLine("[WATCH] Écoute en temps réel...\n");
            await WatchAsync();
        }
        else
        {
            Console.WriteLine("Choisir un mode :");
            Console.WriteLine("  [1] --sync    Extraire tout l'historique");
            Console.WriteLine("  [2] --watch   Écouter en temps réel");
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
        var allConversations = new List<Conversation>();

        foreach (var dir in Directory.GetDirectories(BlobRoot).OrderBy(d => d))
        {
            foreach (var blobPath in Directory.GetFiles(dir))
            {
                var convs = ProcessBlob(blobPath);
                if (convs.Count > 0)
                {
                    allConversations.AddRange(convs);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[+] {Path.GetFileName(dir)} → {convs.Count} conversation(s)");
                    Console.ResetColor();
                }
            }
        }

        Console.WriteLine($"\n[i] Total : {allConversations.Count} conversations extraites");
        WriteToFile(allConversations);
        Console.WriteLine($"[i] Sauvegardé dans {OutputFile}");
    }

    // ── WATCH ─────────────────────────────────────────────────────────────
    static async Task WatchAsync()
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
    Console.WriteLine("[i] En écoute... (Ctrl+C pour arrêter)\n");

    string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    string levelDbDir = Path.Combine(appData, @"Claude\IndexedDB\https_claude.ai_0.indexeddb.leveldb");

    // Thread 1 : LevelDB .log → drafts live
    var logTask = Task.Run(async () =>
    {
        try
        {
            string? logFile = Directory.GetFiles(levelDbDir, "*.log")
                .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                .FirstOrDefault();

            if (logFile == null) return;

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[LOG] Surveillance : {Path.GetFileName(logFile)}");
            Console.ResetColor();

            using var reader = new LevelDbLogReader(logFile);
            await foreach (var record in reader.ReadRecordsAsync(cts.Token))
            {
                if (!record.IsLive) continue;
                Console.WriteLine($"[KEY] {record.Key[..Math.Min(50, record.Key.Length)]}");

                if (!record.Key.Contains("chat-draft")) continue;

                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[DRAFT] {DateTime.Now:HH:mm:ss} {record.ValueString[..Math.Min(100, record.ValueString.Length)]}");
                Console.ResetColor();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"[LOG ERR] {ex.Message}"); }
    });

    // Thread 2 : Blobs → messages complets après flush
    var blobTask = Task.Run(async () =>
    {
        try
        {
            using var watcher = new BlobWatcher(BlobRoot);
            await foreach (var convs in watcher.WatchAsync(cts.Token))
            {
                foreach (var conv in convs)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n[CONV] {conv.Name}");
                    Console.ResetColor();
                    foreach (var msg in conv.Messages.TakeLast(2))
                        PrintMessage(msg);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"[BLOB ERR] {ex.Message}"); }
    });

    await Task.WhenAll(logTask, blobTask);
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

    static IEnumerable<string> GetAllBlobs()
        => Directory.GetDirectories(BlobRoot)
                    .OrderBy(d => d)
                    .SelectMany(Directory.GetFiles);

    static void PrintMessage(ChatMessage msg)
    {
        Console.ForegroundColor = msg.Sender == "human"
            ? ConsoleColor.White
            : ConsoleColor.Green;

        Console.WriteLine($"[{msg.Sender.ToUpper()}] {msg.CreatedAt[..19]}");
        Console.ResetColor();

        if (!string.IsNullOrEmpty(msg.Text))
            Console.WriteLine(msg.Text.Length > 300
                ? msg.Text[..300] + "..."
                : msg.Text);

        if (msg.Thinking != null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [thinking] {msg.Thinking[..Math.Min(100, msg.Thinking.Length)]}...");
            Console.ResetColor();
        }

        Console.WriteLine();
    }

    static void WriteToFile(List<Conversation> conversations)
    {
        using var writer = new StreamWriter(OutputFile, false);
        foreach (var conv in conversations.OrderBy(c => c.CreatedAt))
        {
            writer.WriteLine($"{'='* 60}");
            writer.WriteLine($"CONVERSATION : {conv.Name}");
            writer.WriteLine($"UUID         : {conv.Uuid}");
            writer.WriteLine($"DATE         : {conv.CreatedAt}");
            writer.WriteLine($"{'='* 60}");

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