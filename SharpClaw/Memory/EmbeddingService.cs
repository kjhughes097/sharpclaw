using System.Numerics.Tensors;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using SharpClaw.Configuration;

namespace SharpClaw.Memory;

public sealed class EmbeddingService : IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly int _dimension;
    private readonly ILogger<EmbeddingService> _logger;
    private bool _disposed;

    public EmbeddingService(IOptions<SemanticMemoryOptions> options, ILogger<EmbeddingService> logger)
    {
        _logger = logger;
        _dimension = options.Value.EmbeddingDimension;

        var modelPath = ResolvePath(options.Value.ModelPath);
        var vocabPath = ResolvePath(options.Value.VocabPath);

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX embedding model not found at '{modelPath}'");

        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"WordPiece vocab file not found at '{vocabPath}'. Run scripts/download-embedding-model.sh");

        _tokenizer = BertTokenizer.Create(vocabPath, new BertOptions
        {
            LowerCaseBeforeTokenization = true
        });

        var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        sessionOptions.AppendExecutionProvider_CPU();

        _session = new InferenceSession(modelPath, sessionOptions);
        _logger.LogInformation("Loaded embedding model from {Path} with BertTokenizer (dimension={Dim})", modelPath, _dimension);
    }

    public int Dimension => _dimension;

    public float[] GenerateEmbedding(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
            return new float[_dimension];

        var tokenIds = Tokenize(text);

        var inputIds = new DenseTensor<long>(new[] { 1, tokenIds.Length });
        var attentionMask = new DenseTensor<long>(new[] { 1, tokenIds.Length });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, tokenIds.Length });

        for (var i = 0; i < tokenIds.Length; i++)
        {
            inputIds[0, i] = tokenIds[i];
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
        var embedding = MeanPool(output, tokenIds.Length);

        // L2 normalize
        var norm = MathF.Sqrt(TensorPrimitives.Dot(embedding, embedding));
        if (norm > 0)
        {
            for (var i = 0; i < embedding.Length; i++)
                embedding[i] /= norm;
        }

        return embedding;
    }

    /// <summary>
    /// Tokenizes text using BertTokenizer which handles WordPiece + [CLS]/[SEP] framing.
    /// Truncates to max 512 tokens.
    /// </summary>
    private long[] Tokenize(string text, int maxLength = 512)
    {
        // BertTokenizer.EncodeToIds adds [CLS] and [SEP] automatically
        var ids = _tokenizer.EncodeToIds(text, maxLength, out _, out _);

        var tokens = new long[ids.Count];
        for (var i = 0; i < ids.Count; i++)
            tokens[i] = ids[i];

        return tokens;
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

    private static string ResolvePath(string configPath) =>
        Path.IsPathRooted(configPath)
            ? configPath
            : Path.Combine(AppContext.BaseDirectory, configPath);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
