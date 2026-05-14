using System.Collections.Concurrent;
using SharpClaw.Abstractions;
using SharpClaw.Models;

namespace SharpClaw.Registry;

public sealed class SkillRegistry : ISkillRegistry
{
    private readonly ConcurrentDictionary<string, SkillDefinition> _skills = new(StringComparer.OrdinalIgnoreCase);

    public void Register(SkillDefinition skill) =>
        _skills[skill.Name] = skill;

    public SkillDefinition? Get(string name) =>
        _skills.TryGetValue(name, out var skill) ? skill : null;

    public IReadOnlyList<SkillDefinition> GetAll() =>
        _skills.Values.ToList().AsReadOnly();

    public void Clear() =>
        _skills.Clear();
}
