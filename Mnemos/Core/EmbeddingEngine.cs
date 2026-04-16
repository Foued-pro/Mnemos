using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mnemos.Core;

public class EmbeddingEngine : IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    
    // MiniLM-L6-v2 specific constants
    private const int Dimension = 384;
    private const int MaxTokens = 510;
    private const int Overlap = 100;   

    public EmbeddingEngine(string modelDir)
    {
        string onnxPath = Path.Combine(modelDir, "model.onnx");
        string vocabPath = Path.Combine(modelDir, "vocab.txt");

        if (!File.Exists(onnxPath)) throw new FileNotFoundException($"ONNX model not found in {modelDir}");
        if (!File.Exists(vocabPath)) throw new FileNotFoundException($"vocab.txt not found in {modelDir}");

        var options = new SessionOptions();
        try
        {
            options.AppendExecutionProvider_CUDA(0);
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            Console.Error.WriteLine("[ONNX] HW: NVIDIA CUDA activated!");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ONNX] CUDA failed ({ex.Message}), fallback to CPU.");
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

        //  Average all token embeddings to get a single sentence vector
        float[] pooled = new float[Dimension];
        for (int i = 0; i < seqLen; i++)
            for (int j = 0; j < Dimension; j++)
                pooled[j] += hidden[0, i, j];

        for (int j = 0; j < Dimension; j++) pooled[j] /= seqLen;

        // Scale vector to unit length for Cosine Similarity
        float norm = (float)Math.Sqrt(pooled.Sum(x => x * x));
        for (int j = 0; j < Dimension; j++) pooled[j] /= Math.Max(norm, 1e-9f);

        return pooled;
    }

    public void Dispose() => _session?.Dispose();
}