using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Mnemos.Database;

namespace Mnemos.Mcp;

[McpServerToolType]
public static class McpTools
{
    private static MnemosDb? _db;

    public static void Init(MnemosDb db) => _db = db;

    [McpServerTool, Description(
         "ATTENTION : Ceci est une recherche FTS5 stricte (mots-clés exacts), PAS une recherche sémantique ! " +
         "Pour trouver une information, tu DOIS obligatoirement utiliser l'opérateur OR avec de multiples synonymes, " +
         "traductions et termes liés. " +
         "Exemples: Au lieu de 'salle', cherche 'salle OR gym OR entrainement OR fitness'. " +
         "Au lieu de 'code', cherche 'code OR script OR python OR c# OR bug'. " +
         "Évite les mots trop courts qui pourraient créer de faux positifs (ex: évite 'pecs' qui matche 'specs').")]
    public static string search_memory(
        [Description("Le texte à rechercher dans l'historique des conversations")]
        string query,
        [Description("Nombre de résultats à retourner (défaut: 5)")]
        int limit = 5)
    {
        if (_db == null) return "DB non initialisée.";

        var results = _db.Search(query, limit);

        if (results.Count == 0)
            return $"Aucun résultat pour '{query}'.";

        var output = new List<object>();
        foreach (var r in results)
        {
            output.Add(new
            {
                sender    = r.Sender,
                text      = r.Text,
                date      = r.CreatedAt[..19],
                relevance = Math.Round(r.Relevance, 3),
                conv_id   = r.ConversationUuid
            });
        }

        return JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    [McpServerTool, Description(
        "Retourne les derniers messages d'une conversation spécifique. " +
        "Utile pour reprendre le contexte exact d'une session de travail.")]
    public static string get_recent_messages(
        [Description("L'UUID de la conversation")]
        string conversation_uuid,
        [Description("Nombre de messages à retourner (défaut: 10)")]
        int n = 10)
    {
        if (_db == null) return "DB non initialisée.";

        var messages = _db.GetRecentMessages(conversation_uuid, n);

        if (messages.Count == 0)
            return "Aucun message trouvé.";

        var output = messages
            .OrderBy(m => m.Index)
            .Select(m => new
            {
                sender  = m.Sender,
                text    = m.Text,
                date    = m.CreatedAt[..19],
                thinking = m.Thinking
            });

        return JsonSerializer.Serialize(output, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    [McpServerTool, Description(
        "Retourne les statistiques de la mémoire Mnemos : " +
        "nombre de conversations et messages indexés.")]
    public static string get_stats()
    {
        if (_db == null) return "DB non initialisée.";
        var (convs, msgs) = _db.GetStats();
        return $"{convs} conversations, {msgs} messages en mémoire.";
    }
}