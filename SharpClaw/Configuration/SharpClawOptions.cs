namespace SharpClaw.Configuration;

public sealed class SharpClawOptions
{
    public const string SectionName = "SharpClaw";

    public string AgentsDirectory { get; set; } = "agents";
    public string McpsDirectory { get; set; } = "mcps";
    public string SkillsDirectory { get; set; } = "skills";
    public string ServicesDirectory { get; set; } = "services";
    public string ProjectsDirectory { get; set; } = "projects";
    public string WorkspacePath { get; set; } = string.Empty;
    public int ChatHistoryLimit { get; set; } = 5;
    public string DefaultAgent { get; set; } = string.Empty;
}
