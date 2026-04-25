using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Mnemos.Core;

/// <summary>
/// Generates normalized embedding vectors for text using a local ONNX model.
/// Handles long texts via overlapping sliding window and mean pooling.
/// CUDA is used if available; otherwise falls back to CPU.
/// </summary>
public class EmbeddingEngine : IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly Action<string> _log;

    // Model-specific constants (MiniLM-L6-v2)
    private const int Dimension = 384;  // embedding size
    private const int MaxTokens = 510;   // max tokens per chunk 
    private const int Overlap = 100;     // token overlap between consecutive chunks

    /// <summary>
    /// Initializes the engine by loading the ONNX model and vocabulary.
    /// </summary>
    /// <param name="modelDir">Directory containing model.onnx and vocab.txt.</param>
    /// <param name="log">Logging callback.</param>
    /// <exception cref="FileNotFoundException">
    /// Thrown if model.onnx or vocab.txt is missing.
    /// </exception>
    public EmbeddingEngine(string modelDir, Action<string> log)
    {
        _log = log;

        string onnxPath = Path.Combine(modelDir, "model.onnx");
        string vocabPath = Path.Combine(modelDir, "vocab.txt");

        if (!File.Exists(onnxPath))
            throw new FileNotFoundException($"ONNX model not found in {modelDir}");
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"vocab.txt not found in {modelDir}");

        var options = new SessionOptions();
        try
        {
            options.AppendExecutionProvider_CUDA(0);
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            _log("[ONNX] CUDA activated.");
        }
        catch (Exception ex)
        {
            _log($"[ONNX] CUDA failed ({ex.Message}), fallback to CPU.");
            options = new SessionOptions();
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        }

        _session = new InferenceSession(onnxPath, options);
        _tokenizer = BertTokenizer.Create(vocabPath);
    }

    /// <summary>
    /// Generates embedding vectors for the given text.
    /// If the text is longer than <see cref="MaxTokens"/> tokens, it is split
    /// into overlapping chunks and a separate vector is returned for each chunk.
    /// </summary>
    /// <param name="text">Input text (may be empty or null).</param>
    /// <returns>A list of embedding vectors (each of length <see cref="Dimension"/>).
    /// Returns an empty list if the input is null or whitespace.</returns>
    public List<float[]> GenerateEmbeddings(string text)
    {
        var results = new List<float[]>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        var encoding = _tokenizer.EncodeToIds(text);
        int totalTokens = encoding.Count;

        if (totalTokens <= MaxTokens)
        {
            results.Add(ProcessChunk(encoding.ToArray()));
            return results;
        }

        // Sliding window for texts exceeding max token limit.
        int stride = MaxTokens - Overlap;
        for (int i = 0; i < totalTokens; i += stride)
        {
            int len = Math.Min(MaxTokens, totalTokens - i);
            var chunkIds = encoding.Skip(i).Take(len).ToArray();
            results.Add(ProcessChunk(chunkIds));
            if (i + len >= totalTokens) break;
        }
        return results;
    }

    /// <summary>
    /// Runs the ONNX model on a single chunk of token IDs and returns
    /// the mean-pooled + L2-normalized embedding.
    /// </summary>
    private float[] ProcessChunk(int[] ids)
    {
        int seqLen = ids.Length;
        var inputIds = new DenseTensor<long>(new[] { 1, seqLen });
        var attentionMask = new DenseTensor<long>(new[] { 1, seqLen });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, seqLen });

        for (int i = 0; i < seqLen; i++)
        {
            inputIds[0, i] = ids[i];
            attentionMask[0, i] = 1;
            tokenTypeIds[0, i] = 0;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds)
        };

        using var results = _session.Run(inputs);
        var hidden = results.First(v => v.Name == "last_hidden_state").AsTensor<float>();

        // Mean pooling over all token embeddings.
        float[] embedding = new float[Dimension];
        for (int i = 0; i < seqLen; i++)
            for (int j = 0; j < Dimension; j++)
                embedding[j] += hidden[0, i, j];

        for (int j = 0; j < Dimension; j++)
            embedding[j] /= seqLen;

        // L2 normalization for cosine similarity.
        float norm = (float)Math.Sqrt(embedding.Sum(x => x * x));
        for (int j = 0; j < Dimension; j++)
            embedding[j] /= Math.Max(norm, 1e-9f);

        return embedding;
    }

    /// <summary>
    /// Releases the underlying ONNX inference session.
    /// </summary>
    public void Dispose() => _session?.Dispose();
}