using Mnemos.Database;
using Mnemos.Models;
using UMAP;

namespace Mnemos.Universe;

/// <summary>
/// Handles incremental UMAP projection of new message embeddings into 3D
/// and re-clusters all points in the original 384D space after each batch.
/// </summary>
public sealed class UmapEngine
{
    private readonly MnemosDb _db;
    private readonly Action<string> _log;

    private const int Dimensions   = 3;
    private const int NNeighbors   = 15;
    private const float MinDist    = 0.3f;
    private const int BatchSize    = 500;

    /// <summary>Determines the number of K-Means clusters from the number of points.</summary>
    private static int ComputeK(int n) =>
        Math.Clamp((int)Math.Round(Math.Sqrt(n / 2.0)), 8, 24);

    public UmapEngine(MnemosDb db, Action<string> log)
    {
        _db  = db;
        _log = log;
    }

    /// <summary>
    /// Asynchronously computes UMAP coordinates for pending messages,
    /// re-clusters all points in 384D space, and persists the result.
    /// </summary>
    /// <param name="onProgress">Optional progress callback (current, total).</param>
    /// <returns>The number of messages processed in this batch.</returns>
    public async Task<int> ComputeIncrementalAsync(Action<int, int>? onProgress = null)
    {
        return await Task.Run(() => ComputeIncremental(onProgress));
    }

    /// <summary>
    /// Synchronous core: projects new vectors with UMAP, clusters everything,
    /// and saves coordinates + cluster labels.
    /// </summary>
    private int ComputeIncremental(Action<int, int>? onProgress)
    {
        var pending = _db.GetMessagesWithoutUmap(BatchSize);
        if (pending.Count == 0) return 0;

        onProgress?.Invoke(0, pending.Count);

        // 1. UMAP 3D projection for new points only
        var newVectors = pending.Select(p => p.Embedding).ToArray();
        var umap = new Umap(
            distance: Umap.DistanceFunctions.CosineForNormalizedVectors,
            dimensions: Dimensions,
            numberOfNeighbors: Math.Min(NNeighbors, Math.Max(1, newVectors.Length - 1))
        );

        int epochs = umap.InitializeFit(newVectors);
        for (int i = 0; i < epochs; i++)
        {
            umap.Step();
            if (i % 10 == 0) onProgress?.Invoke(i, epochs);
        }

        var newCoords = Normalize(umap.GetEmbedding());
        var pendingIndex = pending
            .DistinctBy(p => p.Uuid)
            .Select((p, i) => (p.Uuid, i))
            .ToDictionary(x => x.Uuid, x => x.i);

        // 2. Re-cluster EVERY message in 384D embedding space
        _log("Semantic clustering in 384D...");
        var allEmbeddings = _db.GetAllEmbeddings();
        var allVectors    = allEmbeddings.Select(e => e.Embedding).ToArray();
        var allUuids      = allEmbeddings.Select(e => e.Uuid).ToList();
        int k = ComputeK(allEmbeddings.Count);
        _log($"K = {k} clusters for {allEmbeddings.Count} messages");
        var globalClusters = AssignClusters(allVectors, k);

        // 3. Merge new 3D coordinates with existing ones
        var existingCoords = _db.GetUmapPoints()
            .ToDictionary(p => p.Uuid, p => new[] { p.X, p.Y, p.Z });

        var pointsToSave = allUuids
            .Select((uuid, i) =>
            {
                float[] coords;
                if (pendingIndex.TryGetValue(uuid, out int newIdx))
                    coords = newCoords[newIdx];
                else if (existingCoords.TryGetValue(uuid, out var old))
                    coords = old;
                else
                    return ((string, float, float, float, int)?)null;

                return (uuid, coords[0], coords[1], coords[2], globalClusters[i]);
            })
            .Where(x => x.HasValue)
            .Select(x => x!.Value)
            .ToList();

        _db.SaveUmapCoordinates(pointsToSave);
        _db.UpdateClusterLabels(k);

        onProgress?.Invoke(pending.Count, pending.Count);
        return pending.Count;
    }

    /// <summary>Returns all 3D points currently stored in the database.</summary>
    public List<UniversePoint> GetPoints() => _db.GetUmapPoints();

    // ── Normalization ─────────────────────────────────────

    /// <summary>Scales coordinates to the [-1, +1] range on each axis.</summary>
    private static float[][] Normalize(float[][] coords)
    {
        if (coords.Length == 0) return coords;

        float minX = coords.Min(c => c[0]), maxX = coords.Max(c => c[0]);
        float minY = coords.Min(c => c[1]), maxY = coords.Max(c => c[1]);
        float minZ = coords.Min(c => c[2]), maxZ = coords.Max(c => c[2]);

        float rangeX = Math.Max(maxX - minX, 1e-6f);
        float rangeY = Math.Max(maxY - minY, 1e-6f);
        float rangeZ = Math.Max(maxZ - minZ, 1e-6f);

        return coords.Select(c => new[]
        {
            (c[0] - minX) / rangeX * 2f - 1f,
            (c[1] - minY) / rangeY * 2f - 1f,
            (c[2] - minZ) / rangeZ * 2f - 1f
        }).ToArray();
    }

    /// <summary>Squared Euclidean distance between two vectors.</summary>
    private static float Dist(float[] a, float[] b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float d = a[i] - b[i];
            sum += d * d;
        }
        return sum;
    }

    // ── K-Means clustering ────────────────────────────────

    /// <summary>
    /// K-Means clustering with fixed seed (42) for reproducibility.
    /// Returns a cluster index for each input vector.
    /// </summary>
    private static int[] AssignClusters(float[][] coords, int k)
    {
        int n = coords.Length;
        if (n == 0 || k <= 0) return Array.Empty<int>();
        if (k == 1) return new int[n];

        int dimensions      = coords[0].Length;
        int[] assignments   = new int[n];
        float[][] centroids = new float[k][];

        var rand = new Random(42);
        var initialIndices = Enumerable.Range(0, n)
            .OrderBy(_ => rand.Next())
            .Take(k)
            .ToList();

        for (int i = 0; i < k; i++)
        {
            centroids[i] = new float[dimensions];
            Array.Copy(coords[initialIndices[i]], centroids[i], dimensions);
        }

        bool changed = true;
        int maxIterations = 50;
        int iterations = 0;

        while (changed && iterations < maxIterations)
        {
            changed = false;
            iterations++;

            // Assignment step
            for (int i = 0; i < n; i++)
            {
                float minDist = float.MaxValue;
                int bestCluster = 0;

                for (int c = 0; c < k; c++)
                {
                    float d = Dist(coords[i], centroids[c]);
                    if (d < minDist)
                    {
                        minDist = d;
                        bestCluster = c;
                    }
                }

                if (assignments[i] != bestCluster)
                {
                    assignments[i] = bestCluster;
                    changed = true;
                }
            }

            // Update step
            int[] clusterSizes = new int[k];
            float[][] newCentroids = new float[k][];
            for (int i = 0; i < k; i++)
                newCentroids[i] = new float[dimensions];

            for (int i = 0; i < n; i++)
            {
                int cluster = assignments[i];
                clusterSizes[cluster]++;
                for (int d = 0; d < dimensions; d++)
                    newCentroids[cluster][d] += coords[i][d];
            }

            for (int c = 0; c < k; c++)
            {
                if (clusterSizes[c] > 0)
                {
                    for (int d = 0; d < dimensions; d++)
                        centroids[c][d] = newCentroids[c][d] / clusterSizes[c];
                }
                else
                {
                    // Dead centroid: reinitialize with a random point
                    int randomIndex = rand.Next(n);
                    Array.Copy(coords[randomIndex], centroids[c], dimensions);
                }
            }
        }

        return assignments;
    }
}