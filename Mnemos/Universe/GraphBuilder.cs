using Mnemos.Database;

namespace Mnemos.Universe;

// ----- JSON DTOs (serialized to React) -----

/// <summary>A node in the 3D force graph.</summary>
public record GraphNode(string id, string label, int group, int val);

/// <summary>A weighted edge between two nodes.</summary>
public record GraphLink(string source, string target, double value);

/// <summary>Global database statistics for the UI header.</summary>
public record GraphStats(int conversations, int messages, int embedded);

/// <summary>Complete graph payload sent to the React frontend.</summary>
public record GraphPayload(List<GraphNode> nodes, List<GraphLink> links, GraphStats? stats = null);

/// <summary>Progress report sent during graph construction.</summary>
public record BuildProgress(string stage, int current, int total, string message);

// ----- Builder -----

/// <summary>
/// Builds the conversation graph: loads centroids, computes similarities,
/// clusters nodes with Union-Find, and produces a JSON-ready payload.
/// </summary>
public sealed class GraphBuilder
{
    private readonly MnemosDb _db;
    private readonly Action<string> _log;

    // Thresholds
    private const double SimilarityThreshold = 0.55; // minimum cosine similarity for a link
    private const double ClusterThreshold    = 0.70; // minimum similarity to place in the same cluster
    private const int    TopKLinks           = 5;    // max links per node
    private const int    MaxConversations    = 200;  // max conversations to load
    private const int    MessagesPerConv     = 10;   // recent messages used for preview

    public GraphBuilder(MnemosDb db, Action<string> log)
    {
        _db  = db;
        _log = log;
    }

    /// <summary>
    /// Builds the graph asynchronously, reporting progress via <paramref name="onProgress"/>.
    /// </summary>
    public async Task<GraphPayload?> BuildAsync(Action<BuildProgress> onProgress)
    {
        return await Task.Run(() =>
        {
            try
            {
                return Build(onProgress);
            }
            catch (Exception ex)
            {
                _log($"GraphBuilder crashed: {ex.Message}\n{ex.StackTrace}");
                onProgress(new BuildProgress("error", 0, 0, $"Error: {ex.Message}"));
                return null;
            }
        });
    }

    private GraphPayload? Build(Action<BuildProgress> onProgress)
    {
        // 1. Load conversations
        onProgress(new BuildProgress("loading_conversations", 0, 0, "Loading conversations..."));
        var allIds = _db.GetConversationIds(MaxConversations);
        _log($"{allIds.Count} conversations found");

        if (allIds.Count == 0)
        {
            onProgress(new BuildProgress("error", 0, 0, "No conversations found in the database"));
            return null;
        }

        // 2. Load centroids and previews
        onProgress(new BuildProgress("embedding", 0, allIds.Count, "Starting semantic analysis..."));

        var embeddings = new Dictionary<string, float[]>();
        var nodes      = new List<GraphNode>();
        int processed  = 0;

        foreach (var cid in allIds)
        {
            var msgs = _db.GetRecentMessages(cid, MessagesPerConv);
            if (!msgs.Any()) { processed++; continue; }

            var vec = _db.GetConversationCentroid(cid);
            if (vec != null)
            {
                embeddings[cid] = vec;
                var preview = msgs.First().Text;
                if (preview.Length > 50) preview = preview[..50] + "...";
                nodes.Add(new GraphNode(cid, preview, 0, msgs.Count));
            }
            else
            {
                _log($"No centroid for conversation {cid}");
            }

            processed++;
            onProgress(new BuildProgress("embedding", processed, allIds.Count,
                $"Semantic analysis... {processed}/{allIds.Count}"));
        }

        if (nodes.Count == 0)
        {
            onProgress(new BuildProgress("error", 0, 0, "Failed to generate semantic vectors"));
            return null;
        }

        // 3. Compute pairwise similarities and build links
        onProgress(new BuildProgress("linking", 0, nodes.Count, "Detecting connections..."));

        var ids      = embeddings.Keys.ToList();
        var rawLinks = new List<(string a, string b, double score)>();

        for (int i = 0; i < ids.Count; i++)
        {
            var best = new List<(int idx, double score)>();
            for (int j = 0; j < ids.Count; j++)
            {
                if (i == j) continue;
                double sim = CosineSimilarity(embeddings[ids[i]], embeddings[ids[j]]);
                if (sim > SimilarityThreshold) best.Add((j, sim));
            }

            foreach (var b in best.OrderByDescending(x => x.score).Take(TopKLinks))
                rawLinks.Add((ids[i], ids[b.idx], b.score));

            if ((i + 1) % 5 == 0 || i == ids.Count - 1)
                onProgress(new BuildProgress("linking", i + 1, ids.Count,
                    $"{rawLinks.Count} connections found..."));
        }

        // 4. Cluster nodes via Union-Find
        onProgress(new BuildProgress("clustering", 0, nodes.Count, "Grouping by topic..."));

        var uf     = new UnionFind(ids.Count);
        var groups = new Dictionary<int, int>();
        int g      = 0;

        foreach (var (a, b, score) in rawLinks)
        {
            if (score > ClusterThreshold)
                uf.Union(ids.IndexOf(a), ids.IndexOf(b));
        }

        var finalNodes = nodes.Select((n, i) =>
        {
            int root = uf.Find(i);
            if (!groups.ContainsKey(root)) groups[root] = g++;
            return n with { group = groups[root] };
        }).ToList();

        // 5. Build response
        var (convCount, msgCount) = _db.GetStats();
        var stats = new GraphStats(convCount, msgCount, _db.GetEmbeddedCount());

        var payload = new GraphPayload(
            finalNodes,
            rawLinks.Select(l => new GraphLink(l.a, l.b, Math.Round(l.score, 3))).ToList(),
            stats
        );

        _log($"Graph ready: {finalNodes.Count} nodes, {payload.links.Count} links, {g} clusters");
        onProgress(new BuildProgress("done", finalNodes.Count, finalNodes.Count,
            $"Universe ready: {finalNodes.Count} memories"));

        return payload;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na  += a[i] * a[i];
            nb  += b[i] * b[i];
        }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-10);
    }
}

// ----- Union-Find (disjoint-set for clustering) -----

/// <summary>
/// Disjoint-set data structure with path compression.
/// Used to group conversation nodes into clusters.
/// </summary>
internal sealed class UnionFind
{
    private readonly int[] _parent;

    public UnionFind(int n) =>
        _parent = Enumerable.Range(0, n).ToArray();

    public int Find(int x) =>
        _parent[x] == x ? x : _parent[x] = Find(_parent[x]);

    public void Union(int a, int b) =>
        _parent[Find(a)] = Find(b);
}