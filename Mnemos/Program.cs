using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mnemos
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("╔═══════════════════════════════════════════════════╗");
            Console.WriteLine("║      MNEMOS v3.0 - INTERCEPTION LEVELDB DIRECTE   ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════╝\n");
            Console.ResetColor();

            // 1. Trouver le dossier et le fichier .log le plus récent
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string levelDbDir = Path.Combine(appData, @"Claude\IndexedDB\https_claude.ai_0.indexeddb.leveldb");

            if (!Directory.Exists(levelDbDir))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[!] Dossier LevelDB introuvable : {levelDbDir}");
                Console.ResetColor();
                return;
            }

            string currentLogFile = GetLatestLogFile(levelDbDir);
            if (currentLogFile == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[!] Aucun fichier .log trouvé.");
                Console.ResetColor();
                return;
            }

            Console.WriteLine($"[i] Surveillance attachée à : {Path.GetFileName(currentLogFile)}");
            Console.WriteLine(new string('-', 50));

            // Gestion de l'arrêt propre (Ctrl+C)
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                Console.WriteLine("\n[i] Arrêt de Mnemos...");
                e.Cancel = true;
                cts.Cancel();
            };

            // 2. Démarrer le lecteur
            using var reader = new LevelDbLogReader(currentLogFile);

            try
            {
                // Boucle asynchrone pour lire les événements en temps réel
                await foreach (var record in reader.ReadRecordsAsync(cts.Token))
                {
                    // Si ce n'est pas une insertion (c'est une suppression), on ignore
                    if (!record.IsLive) continue;

                    string key = record.Key;
                    string rawValue = record.ValueString;

                    if (key.Contains("chat-draft"))
                    {
                        ProcessDraft(key, rawValue);
                    }
                    else if (key.Contains("react-query-cache"))
                    {
                        ProcessCache(key, rawValue);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Fin normale via Ctrl+C
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[!] Erreur critique : {ex.Message}");
                Console.ResetColor();
            }
        }

        private static string GetLatestLogFile(string dir)
        {
            var files = new DirectoryInfo(dir).GetFiles("*.log");
            if (files.Length == 0) return null;
            
            // Trie par date de modification décroissante
            Array.Sort(files, (a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
            return files[0].FullName;
        }

        private static void ProcessDraft(string key, string rawValue)
        {
            // Les brouillons sont généralement en clair ou facilement parsables
            try
            {
                // On nettoie les caractères binaires du début/fin si nécessaire
                string cleanJson = Regex.Replace(rawValue, @"[^\x20-\x7E\xA0-\xFF]", "");
                
                // Extraction rapide du texte si ça ressemble à du JSON
                var match = Regex.Match(cleanJson, @"text"":""(.*?)""");
                if (match.Success)
                {
                    string text = match.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(text) && text.Length > 3)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"\n[BROUILLON EN DIRECT] {DateTime.Now:HH:mm:ss}");
                        Console.ResetColor();
                        Console.WriteLine(text);
                    }
                }
            }
            catch { /* Ignorer les erreurs de parse */ }
        }

        private static void ProcessCache(string key, string rawValue)
        {
            // Le cache (react-query) contient souvent le fameux "Blink SSV" + "V8 Serialized"
            // On utilise la technique du Sniper Regex pour extraire le texte français/anglais lisible
            MatchCollection matches = Regex.Matches(rawValue, @"[a-zA-Z0-9éèàêîôû' \.,!?]{30,}");

            foreach (Match match in matches)
            {
                string phrase = match.Value.Trim();

                // On évite d'afficher le code interne de l'application ou les UUID
                if (!phrase.Contains("u00") && 
                    !phrase.Contains("http") && 
                    !phrase.Contains("tipTap") &&
                    !Regex.IsMatch(phrase, @"^[a-f0-9\-]{36}$"))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"\n[MESSAGE CACHE] {DateTime.Now:HH:mm:ss}");
                    Console.ResetColor();
                    Console.WriteLine(phrase);
                }
            }
        }
    }
}