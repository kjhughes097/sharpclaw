namespace SharpClaw.Configuration;

public sealed class SemanticMemoryOptions
{
    public const string SectionName = "SemanticMemory";

    public bool Enabled { get; set; }
    public string ModelPath { get; set; } = "models/all-MiniLM-L6-v2.onnx";
    public string TokenizerPath { get; set; } = "models/tokenizer.json";
    public string DatabasePath { get; set; } = "data/semantic-memory.db";
    public int TopK { get; set; } = 5;
    public float MinScore { get; set; } = 0.3f;
    public int EmbeddingDimension { get; set; } = 384;
    public int MaxContextTokens { get; set; } = 1500;
}
