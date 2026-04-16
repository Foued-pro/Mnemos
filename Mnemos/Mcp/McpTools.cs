using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Mnemos.Database;
using Mnemos.Core;

namespace Mnemos.Mcp;

[McpServerToolType]
public static class McpTools
{
    private static MnemosDb? _db;
    private static EmbeddingEngine? _embedder;

    public static void Init(MnemosDb db, EmbeddingEngine? embedder = null)
    {
        _db = db;
        _embedder = embedder;
    }

    [McpServerTool, Description(
        "Outil de recherche hybride (FTS5 + Vecteurs sémantiques). " +
        "Idéal pour retrouver des concepts, des idées ou des messages spécifiques " +
        "même si les mots exacts diffèrent, grâce à la recherche par similarité cosinus.")]
    public static string search_hybrid(
        [Description("La requête en langage naturel (ex: 'études au japon', 'bug python')")] string query,
        [Description("Nombre de résultats (défaut: 5)")] int limit = 5)
    {
        if (_db == null || _embedder == null) 
            return "Système sémantique non initialisé. Vérifie les logs GPU/CPU.";

        // 1. Keyword Search (FTS5)  Sanitize input to prevent SQLite syntax errors
        string safeQuery = System.Text.RegularExpressions.Regex.Replace(query, @"[^\w\s]", " ");
        var keywordResults = _db.Search(safeQuery, 20);

        // 2. Semantic Search (Embeddings)  Use the first chunk for short queries
        var queryVectors = _embedder.GenerateEmbeddings(query);
        if (queryVectors.Count == 0) return "Requête trop courte ou invalide.";
        
        var queryEmb = queryVectors[0];
        var semanticResults = _db.SearchByEmbedding(queryEmb, 25);

        // 3. Reciprocal Rank Fusion (RRF) to combine keyword and semantic results
        var rrfScores = new Dictionary<string, (double Score, SearchResult Data)>();
        const double k = 60.0; // Standard RRF constant

        // Process semantic results first
        for (int i = 0; i < semanticResults.Count; i++)
        {
            var r = semanticResults[i];
            rrfScores[r.Uuid] = (1.0 / (k + i + 1), r);
        }

        // Merge with keyword results
        for (int i = 0; i < keywordResults.Count; i++)
        {
            var r = keywordResults[i];
            if (rrfScores.TryGetValue(r.Uuid, out var existing))
            {
                rrfScores[r.Uuid] = (existing.Score + (1.0 / (k + i + 1)), existing.Data);
            }
            else
            {
                rrfScores[r.Uuid] = (1.0 / (k + i + 1), r);
            }
        }

        // Sort by fused RRF score
        var finalResults = rrfScores.Values
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .ToList();

        if (finalResults.Count == 0)
            return $"Désolé, je n'ai rien trouvé pour '{query}'. Essaie avec d'autres termes.";

        return JsonSerializer.Serialize(
            finalResults.Select(r => new
            {
                role = r.Data.Sender == "human" ? "Utilisateur" : "Assistant",
                content = r.Data.Text,
                date = r.Data.CreatedAt[..10],
                relevance = Math.Round(r.Score * 100, 2),
                conversation_id = r.Data.ConversationUuid
            }),
            new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Donne l'état de santé de la mémoire de Mnemos (Stats et % d'indexation).")]
    public static string get_stats()
    {
        if (_db == null) return "Base de données inaccessible.";
        
        var (convs, msgs) = _db.GetStats();
        int embedded = _db.GetEmbeddedCount();
        double percent = msgs > 0 ? Math.Round((double)embedded / msgs * 100, 1) : 0;
        
        return $"📊 Mémoire Mnemos :\n- Conversations : {convs}\n- Messages : {msgs}\n- Indexation Sémantique : {percent}% ({embedded}/{msgs})";
    }

    [McpServerTool, Description("Récupère le fil d'une discussion spécifique via son ID.")]
    public static string get_recent_messages(string conversation_uuid, int n = 15)
    {
        if (_db == null) return "Base de données inaccessible.";
        
        var messages = _db.GetRecentMessages(conversation_uuid, n);
        if (messages.Count == 0) return "Conversation introuvable.";

        return JsonSerializer.Serialize(
            messages.OrderBy(m => m.Index).Select(m => new {
                sender = m.Sender,
                text = m.Text,
                date = m.CreatedAt,
                thinking = m.Thinking
            }), new JsonSerializerOptions { WriteIndented = true });
    }
}