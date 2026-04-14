namespace Mnemos.Search;

public static class SearchHandler
{
    public static void Run(Database.MnemosDb db)
    {
        Console.WriteLine("[SEARCH] Recherche dans la mémoire Mnemos\n");

        while (true)
        {
            Console.Write("Requête : ");
            string? query = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(query)) break;

            var results = db.Search(query, limit: 10);

            if (results.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Aucun résultat.\n");
                Console.ResetColor();
                continue;
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n{results.Count} résultat(s) :\n");
            Console.ResetColor();

            foreach (var r in results)
            {
                Console.ForegroundColor = r.Sender == "human"
                    ? ConsoleColor.White
                    : ConsoleColor.Green;
                Console.WriteLine($"[{r.Sender.ToUpper()}] {r.CreatedAt[..19]}");
                Console.ResetColor();
                Console.WriteLine(r.Text);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  → {r.ConversationUuid}\n");
                Console.ResetColor();
            }
        }
    }
}