using System.Numerics.Tensors;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SharpClaw.Configuration;

namespace SharpClaw.Memory;

public sealed class EmbeddingService : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _dimension;
    private readonly ILogger<EmbeddingService> _logger;
    private bool _disposed;

    public EmbeddingService(IOptions<SemanticMemoryOptions> options, ILogger<EmbeddingService> logger)
    {
        _logger = logger;
        _dimension = options.Value.EmbeddingDimension;

        var modelPath = Path.IsPathRooted(options.Value.ModelPath)
            ? options.Value.ModelPath
            : Path.Combine(AppContext.BaseDirectory, options.Value.ModelPath);

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX embedding model not found at '{modelPath}'");

        var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        sessionOptions.AppendExecutionProvider_CPU();

        _session = new InferenceSession(modelPath, sessionOptions);
        _logger.LogInformation("Loaded embedding model from {Path} (dimension={Dim})", modelPath, _dimension);
    }

    public int Dimension => _dimension;

    public float[] GenerateEmbedding(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
            return new float[_dimension];

        // Simple whitespace tokenization with [CLS]/[SEP] — for MiniLM we use token IDs
        // In production, use a proper tokenizer. For now, we encode using simple word-piece approximation.
        var tokens = SimpleTokenize(text);

        var inputIds = new DenseTensor<long>(new[] { 1, tokens.Length });
        var attentionMask = new DenseTensor<long>(new[] { 1, tokens.Length });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, tokens.Length });

        for (var i = 0; i < tokens.Length; i++)
        {
            inputIds[0, i] = tokens[i];
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

        // MiniLM outputs last_hidden_state [1, seq_len, 384] — mean pool over sequence
        var output = results.First().AsTensor<float>();
        var embedding = MeanPool(output, tokens.Length);

        // L2 normalize
        var norm = MathF.Sqrt(TensorPrimitives.Dot(embedding, embedding));
        if (norm > 0)
        {
            for (var i = 0; i < embedding.Length; i++)
                embedding[i] /= norm;
        }

        return embedding;
    }

    private float[] MeanPool(Microsoft.ML.OnnxRuntime.Tensors.Tensor<float> hiddenState, int seqLen)
    {
        var embedding = new float[_dimension];
        for (var i = 0; i < seqLen; i++)
        {
            for (var j = 0; j < _dimension; j++)
            {
                embedding[j] += hiddenState[0, i, j];
            }
        }

        for (var j = 0; j < _dimension; j++)
            embedding[j] /= seqLen;

        return embedding;
    }

    /// <summary>
    /// Simple tokenization: [CLS] + char-level token IDs + [SEP], capped at 512 tokens.
    /// This is a placeholder — proper WordPiece tokenization should be added for better quality.
    /// </summary>
    private static long[] SimpleTokenize(string text, int maxLength = 512)
    {
        // For BERT-family models, we approximate with character-based encoding
        // [CLS]=101, [SEP]=102, [UNK]=100
        var chars = text.ToLowerInvariant().ToCharArray();
        var tokenCount = Math.Min(chars.Length, maxLength - 2);
        var tokens = new long[tokenCount + 2];

        tokens[0] = 101; // [CLS]
        for (var i = 0; i < tokenCount; i++)
        {
            var c = chars[i];
            // Map ASCII printable to token range; non-ASCII to [UNK]
            tokens[i + 1] = c is >= ' ' and <= '~' ? c + 900 : 100;
        }
        tokens[tokenCount + 1] = 102; // [SEP]

        return tokens;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
