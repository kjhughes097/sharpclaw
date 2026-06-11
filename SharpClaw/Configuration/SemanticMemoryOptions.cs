namespace SharpClaw.Configuration;

public sealed class SemanticMemoryOptions
{
    public const string SectionName = "SemanticMemory";

    public bool Enabled { get; set; }
    public string ModelPath { get; set; } = "models/all-MiniLM-L6-v2.onnx";
    public string VocabPath { get; set; } = "models/vocab.txt";
    public string DatabasePath { get; set; } = "data/semantic-memory.db";
    public int TopK { get; set; } = 5;
    public float MinScore { get; set; } = 0.3f;
    public int EmbeddingDimension { get; set; } = 384;
    public int MaxContextTokens { get; set; } = 1500;

    // Phase 2: Auto-capture extraction
    public bool ExtractionEnabled { get; set; } = true;
    public string ExtractionModel { get; set; } = "claude-haiku-4-20250414";
    public int ExtractionMaxTokens { get; set; } = 1024;
    public int MinPromptLengthForExtraction { get; set; } = 20;
    public int MinResponseLengthForExtraction { get; set; } = 50;
}
