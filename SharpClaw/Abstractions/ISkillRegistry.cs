using SharpClaw.Models;

namespace SharpClaw.Abstractions;

public interface ISkillRegistry
{
    void Register(SkillDefinition skill);
    SkillDefinition? Get(string name);
    IReadOnlyList<SkillDefinition> GetAll();
    void Clear();
}
