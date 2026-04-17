using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Mnemos.Core;

public class EmbeddingEngine : IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly Action<string> _log;

    private const int Dimension = 384;
    private const int MaxTokens = 510;
    private const int Overlap = 100;

    public EmbeddingEngine(string modelDir, Action<string> log)
    {
        _log = log;

        string onnxPath = Path.Combine(modelDir, "model.onnx");
        string vocabPath = Path.Combine(modelDir, "vocab.txt");

        if (!File.Exists(onnxPath)) throw new FileNotFoundException($"ONNX model not found in {modelDir}");
        if (!File.Exists(vocabPath)) throw new FileNotFoundException($"vocab.txt not found in {modelDir}");

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

        // Sliding window for texts exceeding max token limit
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

        // Mean pooling over all token embeddings
        float[] pooled = new float[Dimension];
        for (int i = 0; i < seqLen; i++)
            for (int j = 0; j < Dimension; j++)
                pooled[j] += hidden[0, i, j];

        for (int j = 0; j < Dimension; j++) pooled[j] /= seqLen;

        // L2 normalization for cosine similarity
        float norm = (float)Math.Sqrt(pooled.Sum(x => x * x));
        for (int j = 0; j < Dimension; j++) pooled[j] /= Math.Max(norm, 1e-9f);

        return pooled;
    }

    public void Dispose() => _session?.Dispose();
}