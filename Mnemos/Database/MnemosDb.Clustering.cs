using System.Text.RegularExpressions;

namespace Mnemos.Database;

public partial class MnemosDb
{
    /// <summary>
    /// Generates human-readable names for each cluster (0 to k-1)
    /// based on the most distinctive terms found in their messages.
    /// Uses TF-IDF with automatic stop-word detection.
    /// </summary>
    /// <param name="k">Number of clusters to label (default 16).</param>
    public void UpdateClusterLabels(int k = 16)
    {
        lock (_dbLock)
        {
            var clusterTexts = GetAllClusterTexts(k);
            if (clusterTexts.Count == 0) return;

            var clusterTokenLists = new Dictionary<int, List<string>>(); 
            var clusterDocSets     = new Dictionary<int, HashSet<string>>(); 
            var globalDocFreq      = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); 

            // --- 1. Tokenize all texts per cluster ---
            foreach (var (cid, texts) in clusterTexts)
            {
                var allTokens       = new List<string>();
                var termsInCluster  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var text in texts)
                {
                    var tokens = TokenizeRobust(text);
                    allTokens.AddRange(tokens);
                    foreach (var t in tokens) termsInCluster.Add(t);
                }

                clusterTokenLists[cid] = allTokens;
                clusterDocSets[cid]    = termsInCluster;

                foreach (var term in termsInCluster)
                {
                    if (!globalDocFreq.TryAdd(term, 1)) globalDocFreq[term]++;
                }
            }

            int C = clusterTokenLists.Count;
            if (C == 0) return;

            // --- 2. Automatic stop-word detection ---
            // Words appearing in >50% of clusters are considered stop words.
            var stopwordsAuto = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int threshold = Math.Max(2, (int)Math.Ceiling(C * 0.5));
            foreach (var (term, clusterCount) in globalDocFreq)
            {
                if (clusterCount >= threshold)
                    stopwordsAuto.Add(term);
            }

            double avgClusterSize = clusterTokenLists.Values.Average(v => (double)v.Count);

            // --- 3. Compute TF-IDF per cluster and pick top 3 terms ---
            for (int cid = 0; cid < k; cid++)
            {
                if (!clusterTokenLists.TryGetValue(cid, out var tokens) || tokens.Count == 0)
                    continue;

                int clusterSize = tokens.Count;

                // Term frequency (TF) in this cluster
                var termFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in tokens)
                    if (!termFreq.TryAdd(t, 1)) termFreq[t]++;

                // TF-IDF scoring
                var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var (term, count) in termFreq)
                {
                    if (IsArtifact(term)) continue;
                    if (stopwordsAuto.Contains(term)) continue;
                    if (term.Length < 4) continue;

                    double tf  = (double)count / clusterSize;
                    int    fx  = globalDocFreq.GetValueOrDefault(term, 1);
                    double idf = Math.Log(1.0 + (avgClusterSize / fx));
                    scores[term] = tf * idf;
                }

                var topTerms = scores
                    .OrderByDescending(kv => kv.Value)
                    .Take(3)
                    .Select(kv => kv.Key.ToUpperInvariant())
                    .ToList();

                string label = topTerms.Any()
                    ? string.Join(" · ", topTerms)
                    : $"Cluster {cid + 1}";

                if (label.Length > 35) label = label[..32] + "...";
                label = $"{label} ({clusterSize})";

                ExecuteWithParams(@"
                    INSERT INTO clusters (id, name, keywords, updated_at)
                    VALUES (@id, @name, @kw, @at)
                    ON CONFLICT(id) DO UPDATE SET
                        name = excluded.name,
                        keywords = excluded.keywords,
                        updated_at = excluded.updated_at;",
                    ("@id", cid),
                    ("@name", label),
                    ("@kw", string.Join(", ", topTerms)),
                    ("@at", DateTimeOffset.UtcNow.ToString("o")));
            }
        }
    }

    /// <summary>
    /// Retrieves all message texts grouped by cluster_id for labeling.
    /// </summary>
    private Dictionary<int, List<string>> GetAllClusterTexts(int k)
    {
        var result = new Dictionary<int, List<string>>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT cluster_id, text 
            FROM messages 
            WHERE cluster_id IS NOT NULL AND cluster_id >= 0 AND cluster_id < @k
              AND text IS NOT NULL AND text != '' AND LENGTH(text) > 30";
        cmd.Parameters.AddWithValue("@k", k);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int cid = reader.GetInt32(0);
            if (!result.TryGetValue(cid, out var list))
            {
                list = new List<string>();
                result[cid] = list;
            }
            list.Add(reader.GetString(1));
        }
        return result;
    }

    /// <summary>
    /// Splits text into clean lowercase tokens, removing URLs, code fences,
    /// and HTML tags. Keeps only alphanumeric tokens between 3 and 25 characters.
    /// </summary>
    private static List<string> TokenizeRobust(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        
        // Strip non-text content
        text = Regex.Replace(text, @"https?://\S+", " ");
        text = Regex.Replace(text, @"```[\s\S]*?```", " ");
        text = Regex.Replace(text, @"`[^`]*`", " ");
        text = Regex.Replace(text, @"<[^>]+>", " ");

        var rawTokens = Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+")
            .Where(t => t.Length >= 3 && t.Length <= 25)
            .ToList();

        return rawTokens.Where(t => !IsArtifact(t)).ToList();
    }

    /// <summary>
    /// Returns <c>true</c> if the token looks like a technical artifact
    /// (unicode escape, hex number, pure digits, non-letter symbols).
    /// </summary>
    private static bool IsArtifact(string token)
    {
        if (string.IsNullOrEmpty(token)) return true;
        
        // Unicode escapes like U0022, U0060
        if (Regex.IsMatch(token, @"^u[0-9a-f]{3,}", RegexOptions.IgnoreCase)) return true;
        
        // Pure hex like 000114, 12D, 0xFF
        if (Regex.IsMatch(token, @"^[0-9a-f]{3,8}$", RegexOptions.IgnoreCase)) return true;
        
        // Pure digits
        if (token.All(char.IsDigit)) return true;
        
        // No letters at all (e.g. "___", "&&&")
        if (!token.Any(char.IsLetter)) return true;
        
        // More than 35% digits (e.g. "abc12345")
        int digitCount = token.Count(char.IsDigit);
        if ((double)digitCount / token.Length > 0.35) return true;
        
        return false;
    }
}