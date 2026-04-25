using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Mnemos.Database;
using Mnemos.Core;
using Mnemos.Models;

namespace Mnemos.Mcp;

/// <summary>
/// Static container for all MCP tool handlers.
/// Initialized once with the database and embedding engine references.
/// </summary>
[McpServerToolType]
public static class McpTools
{
    private static MnemosDb? _db;
    private static EmbeddingEngine? _embedder;
    private static Action<string, string>? _logger;

    /// <summary>
    /// Injects the shared database and embedding engine into the tool handlers.
    /// Must be called once on startup.
    /// </summary>
    /// <param name="db">The Mnemos database connection.</param>
    /// <param name="embedder">Optional semantic embedding engine.</param>
    /// <param name="logger">Optional callback for diagnostic logs (message, level).</param>
    public static void Init(MnemosDb db, EmbeddingEngine? embedder = null, Action<string, string>? logger = null)
    {
        _db       = db;
        _embedder = embedder;
        _logger   = logger;
    }

    // ── Hybrid search (unfiltered) ─────────────────────────

    /// <summary>
    /// Hybrid search combining full-text (FTS5/BM25) and semantic (cosine similarity)
    /// results using Reciprocal Rank Fusion.
    /// </summary>
    /// <param name="query">Natural language query.</param>
    /// <param name="limit">Number of results (default 5).</param>
    /// <returns>JSON array of results with role, content, date, relevance, and conversation ID.</returns>
    [McpServerTool, Description(
        "Hybrid search tool (FTS5 + semantic vectors). " +
        "Perfect for finding concepts, ideas, or specific messages " +
        "even when the exact words differ.")]
    public static string search_hybrid(
        [Description("Natural language query (e.g.: 'studies in japan', 'python bug')")] string query,
        [Description("Number of results (default: 5)")] int limit = 5)
    {
        return Search(query, "all", limit);
    }

    // ── Hybrid search (filtered by language) ────────────────

    /// <summary>
    /// Hybrid search with an additional programming language filter
    /// (only messages containing code blocks of that language).
    /// </summary>
    /// <param name="query">Natural language query.</param>
    /// <param name="filter">Language filter (e.g. "csharp", "python").</param>
    /// <param name="limit">Number of results (default 10).</param>
    [McpServerTool, Description(
        "Hybrid search filtered by programming language. " +
        "Returns only messages containing code blocks of the specified language.")]
    public static string search_hybrid_filtered(
        [Description("Natural language query")] string query,
        [Description("Language filter (e.g. csharp, python)")] string filter,
        [Description("Number of results (default: 10)")] int limit = 10)
    {
        return Search(query, filter, limit);
    }

    // ── Core search engine (shared) ─────────────────────────

    /// <summary>
    /// Performs the actual hybrid search: strips punctuation, runs FTS5 and
    /// semantic queries, merges them via Reciprocal Rank Fusion, and returns JSON.
    /// </summary>
    private static string Search(string query, string filter, int limit)
    {
        _logger?.Invoke($"[MCP] search_hybrid (query: \"{query}\", filter: \"{filter}\")", "INFO");

        if (_db == null || _embedder == null)
            return "Semantic system not initialized.";

        // Sanitize the query for FTS5
        string safeQuery = Regex.Replace(query, @"[^\w\s]", " ").Trim();

        // Full-text search
        var keywordResults = new List<SearchResult>();
        try
        {
            if (!string.IsNullOrEmpty(safeQuery))
                keywordResults = _db.Search(safeQuery, filter, 20);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[BM25] Non-fatal error: {ex.Message}", "WARN");
        }

        // Semantic search
        var semanticResults = new List<SearchResult>();
        try
        {
            var queryVectors = _embedder.GenerateEmbeddings(query);
            if (queryVectors.Count > 0)
                semanticResults = _db.SearchByEmbedding(queryVectors[0], filter, 25);
        }
        catch (Exception ex)
        {
            _logger?.Invoke($"[ONNX] Non-fatal error: {ex.Message}", "WARN");
        }

        // Merge with Reciprocal Rank Fusion
        var finalResults = FusionResults(keywordResults, semanticResults, limit);

        if (finalResults.Count == 0)
            return "No results found.";

        var response = finalResults.Select(r => new
        {
            role            = r.Data.Sender == "human" ? "User" : "Assistant",
            content         = r.Data.Text,
            thinking        = r.Data.Thinking,
            date            = r.Data.CreatedAt[..10],
            relevance       = Math.Round(r.Score * 100, 2),
            conversation_id = r.Data.ConversationUuid
        });

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Reciprocal Rank Fusion ──────────────────────────────

    /// <summary>
    /// Merges two ranked result lists using Reciprocal Rank Fusion (RRF).
    /// The constant k dampens the impact of high rankings outliers.
    /// </summary>
    private static List<(double Score, SearchResult Data)> FusionResults(
        List<SearchResult> keyword,
        List<SearchResult> semantic,
        int limit)
    {
        var rrfScores = new Dictionary<string, (double Score, SearchResult Data)>();
        const double k = 60.0;

        for (int i = 0; i < semantic.Count; i++)
        {
            rrfScores[semantic[i].Uuid] = (1.0 / (k + i + 1), semantic[i]);
        }

        for (int i = 0; i < keyword.Count; i++)
        {
            var r = keyword[i];
            if (rrfScores.TryGetValue(r.Uuid, out var existing))
                rrfScores[r.Uuid] = (existing.Score + 1.0 / (k + i + 1), existing.Data);
            else
                rrfScores[r.Uuid] = (1.0 / (k + i + 1), r);
        }

        return rrfScores.Values
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .ToList();
    }

    // ── Health check ────────────────────────────────────────

    /// <summary>
    /// Returns the current memory statistics (conversations, messages, embedding coverage).
    /// </summary>
    [McpServerTool, Description("Returns the Mnemos memory health status (stats and semantic indexing percentage).")]
    public static string get_stats()
    {
        if (_db == null) return "Database inaccessible.";

        var (convs, msgs) = _db.GetStats();
        int embedded = _db.GetEmbeddedCount();
        double percent = msgs > 0 ? Math.Round((double)embedded / msgs * 100, 1) : 0;

        return $"Mnemos Memory:\n" +
               $"- Conversations: {convs}\n" +
               $"- Messages: {msgs}\n" +
               $"- Semantic Indexing: {percent}% ({embedded}/{msgs})";
    }

    // ── Conversation retrieval ──────────────────────────────

    /// <summary>
    /// Retrieves recent messages from a specific conversation.
    /// </summary>
    /// <param name="conversation_uuid">The conversation UUID.</param>
    /// <param name="n">Number of messages (default 15).</param>
    [McpServerTool, Description("Retrieves the message thread of a specific conversation by its ID.")]
    public static string get_recent_messages(
        [Description("Conversation UUID")] string conversation_uuid,
        [Description("Number of messages (default: 15)")] int n = 15)
    {
        if (_db == null) return "Database inaccessible.";

        var messages = _db.GetRecentMessages(conversation_uuid, n);
        if (messages.Count == 0) return "Conversation not found.";

        return JsonSerializer.Serialize(
            messages.OrderBy(m => m.Index).Select(m => new
            {
                sender   = m.Sender,
                text     = m.Text,
                date     = m.CreatedAt,
                thinking = m.Thinking
            }),
            new JsonSerializerOptions { WriteIndented = true });
    }

    // ── File history ────────────────────────────────────────

    /// <summary>
    /// Returns the complete version history of a virtual file tracked across messages.
    /// </summary>
    /// <param name="file_path">Virtual file path or name.</param>
    [McpServerTool, Description("Returns the complete version history of a virtual file.")]
    public static string get_file_history(
        [Description("Virtual path/filename")] string file_path)
    {
        if (_db == null) return "Database inaccessible.";

        var history = _db.GetFileHistory(file_path);
        if (history.Count == 0) return $"No history found for file '{file_path}'.";

        var response = new
        {
            file           = file_path,
            total_versions = history.Count,
            versions = history.Select((v, i) => new
            {
                version_number = i + 1,
                date           = v.Date,
                hash           = v.Hash[..8],
                language       = v.Language,
                code_preview   = v.Code.Length > 200 ? v.Code[..200] + "..." : v.Code,
                full_code      = v.Code
            })
        };

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }
}