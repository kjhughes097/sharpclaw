using System.Runtime.CompilerServices;
using SharpClaw.Agent.Skills;

namespace SharpClaw.Agent.Tests.Skills;

/// <summary>The sample skills in docker/template/skills (skills spec section 12).</summary>
public class ShippedSkillsTests
{
    private static string SkillsDir([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..", "docker", "template", "skills"));

    [Fact]
    public void Every_shipped_skill_loads_without_warnings()
    {
        var log = new ListLogger<SkillLibrary>();
        var dirs = Directory.GetDirectories(SkillsDir()).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();

        var library = SkillLibrary.Load(SkillsDir(), log);

        Assert.Contains("daily-notes", dirs);
        Assert.Contains("research-brief", dirs);
        Assert.Equal(dirs, library.Skills.Select(s => s.Name));
        Assert.DoesNotContain(log.Lines, l => l.Contains("skipped"));
    }

    [Fact]
    public void Each_SKILL_md_stays_under_4_KB()
    {
        foreach (var file in Directory.GetFiles(SkillsDir(), SkillLibrary.SkillFile, SearchOption.AllDirectories))
            Assert.True(new FileInfo(file).Length < 4 * 1024, $"{file} is 4 KB or more");
    }
}
