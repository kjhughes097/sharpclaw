using System.Text;
using SharpClaw.Agent.Skills;

namespace SharpClaw.Agent.Tests.Skills;

public class SkillLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}");
    private readonly ListLogger<SkillLibrary> _log = new();

    public SkillLibraryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Writes a SKILL.md for <paramref name="dir"/>; the name defaults to the directory.</summary>
    private void Put(string dir, string? description = "Does a thing. Use when asked.", string? name = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, dir));
        File.WriteAllText(Path.Combine(_root, dir, SkillLibrary.SkillFile),
            $"---\nname: {name ?? dir}\ndescription: {description}\n---\n# {dir}\nBODY_TEXT\n");
    }

    private SkillLibrary Load() => SkillLibrary.Load(_root, _log);

    private string Warning(string dir) => Assert.Single(_log.Lines, l => l.StartsWith($"skill {dir} skipped: "));

    [Fact]
    public void Loads_valid_skills_in_ordinal_order()
    {
        Put("research-brief");
        Put("daily-notes", "Keep notes.");
        Put("a2");

        var lib = Load();

        Assert.Equal(new[] { "a2", "daily-notes", "research-brief" }, lib.Skills.Select(s => s.Name));
        Assert.True(lib.TryGet("daily-notes", out var skill));
        Assert.Equal("Keep notes.", skill.Description);
        Assert.Equal(Path.Combine(_root, "daily-notes"), skill.Directory);
        Assert.False(lib.TryGet("missing", out _));
        Assert.Contains(_log.Lines, l => l == "skills loaded: 3 a2,daily-notes,research-brief");
    }

    [Fact]
    public void Prompt_index_lists_each_skill_after_the_header()
    {
        Put("daily-notes", "Keep notes.");
        Put("research-brief", "Write briefs.");

        var index = Load().PromptIndex!;

        Assert.StartsWith("## Skills\n", index);
        Assert.Contains("call\n`skill_read` with its name", index);
        Assert.EndsWith("\n- daily-notes: Keep notes.\n- research-brief: Write briefs.\n", index);
        Assert.DoesNotContain("BODY_TEXT", index);
    }

    [Fact]
    public void Missing_or_empty_directory_gives_no_skills_and_no_index()
    {
        var missing = SkillLibrary.Load(Path.Combine(_root, "nope"), _log);
        Assert.Empty(missing.Skills);
        Assert.Null(missing.PromptIndex);
        Assert.Contains("skills loaded: 0", Assert.Single(_log.Lines));

        Assert.Null(Load().PromptIndex);
    }

    [Fact]
    public void Name_must_match_the_directory()
    {
        Put("notes", name: "daily-notes");
        Assert.Empty(Load().Skills);
        Assert.Contains("does not match the directory", Warning("notes"));
    }

    [Theory]
    [InlineData("Bad-Name")]
    [InlineData("double--hyphen")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("under_score")]
    public void Name_must_match_the_pattern(string name)
    {
        Put(name);
        Assert.Empty(Load().Skills);
        Assert.Contains("lowercase letters", Warning(name));
    }

    [Fact]
    public void Name_is_at_most_64_characters()
    {
        Put(new string('a', 64));
        Put(new string('b', 65));
        Assert.Equal(new string('a', 64), Assert.Single(Load().Skills).Name);
        Assert.Contains("longer than 64", Warning(new string('b', 65)));
    }

    [Fact]
    public void Description_length_is_checked_after_trimming()
    {
        Put("ok", new string('d', 1024));
        Put("long", new string('d', 1025));
        Put("blank", "''");
        Put("absent", null);

        Assert.Equal("ok", Assert.Single(Load().Skills).Name);
        Assert.Contains("longer than 1024", Warning("long"));
        Assert.Contains("description is missing", Warning("blank"));
        Assert.Contains("description is missing", Warning("absent"));
    }

    [Fact]
    public void Literal_block_description_is_folded_to_one_line()
    {
        Directory.CreateDirectory(Path.Combine(_root, "x"));
        File.WriteAllText(Path.Combine(_root, "x", "SKILL.md"), "---\nname: x\ndescription: |\n  One\n  two\n---\n");
        Assert.Equal("One two", Assert.Single(Load().Skills).Description);
    }

    [Fact]
    public void Missing_or_oversized_skill_file_is_skipped()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty-dir"));
        Put("big");
        File.AppendAllText(Path.Combine(_root, "big", "SKILL.md"), new string('x', SkillLibrary.MaxSkillFileBytes));

        Assert.Empty(Load().Skills);
        Assert.Contains("no SKILL.md", Warning("empty-dir"));
        Assert.Contains("larger than 64 KB", Warning("big"));
    }

    [Fact]
    public void Symlinked_skill_file_is_skipped_with_a_warning()
    {
        Put("real");
        Directory.CreateDirectory(Path.Combine(_root, "linked"));
        File.CreateSymbolicLink(Path.Combine(_root, "linked", "SKILL.md"), Path.Combine(_root, "real", "SKILL.md"));

        Assert.Equal("real", Assert.Single(Load().Skills).Name);
        Assert.Contains("symbolic link", Warning("linked"));
    }

    [Fact]
    public void Symlinked_and_dot_directories_are_skipped_silently()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "SKILL.md"), "---\nname: linked\ndescription: d\n---\n");
            Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside);
            Put(".hidden", name: "hidden");

            Assert.Empty(Load().Skills);
            Assert.DoesNotContain(_log.Lines, l => l.Contains("skipped"));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void One_invalid_skill_does_not_affect_the_others()
    {
        Put("good-one");
        Directory.CreateDirectory(Path.Combine(_root, "broken"));
        File.WriteAllText(Path.Combine(_root, "broken", "SKILL.md"), "no frontmatter");
        Put("good-two");

        Assert.Equal(new[] { "good-one", "good-two" }, Load().Skills.Select(s => s.Name));
        Assert.Contains("must start with", Warning("broken"));
    }

    [Fact]
    public void At_most_100_skills_load_in_ordinal_order()
    {
        for (var i = 0; i < 102; i++)
            Put($"s{i:000}", "d");

        var lib = Load();

        Assert.Equal(100, lib.Skills.Count);
        Assert.Equal("s099", lib.Skills[^1].Name);
        Assert.Contains("more than 100 skills", Warning("s100"));
        Assert.Contains("more than 100 skills", Warning("s101"));
    }

    [Fact]
    public void Index_is_capped_at_16_KB()
    {
        // Each line is about 1 KB, so roughly 15 fit after the header.
        for (var i = 0; i < 20; i++)
            Put($"s{i:00}", new string('d', 1000));

        var lib = Load();

        Assert.InRange(lib.Skills.Count, 14, 16);
        Assert.True(Encoding.UTF8.GetByteCount(lib.PromptIndex!) <= SkillLibrary.MaxIndexBytes);
        var firstSkipped = $"s{lib.Skills.Count:00}";
        Assert.Contains("would exceed 16 KB", Warning(firstSkipped));
        Assert.Equal(20 - lib.Skills.Count, _log.Lines.Count(l => l.Contains("would exceed 16 KB")));
    }

    [Fact]
    public void Logs_contain_names_but_never_bodies_or_descriptions()
    {
        Put("daily-notes", "SECRET_DESCRIPTION");
        Load();
        var all = string.Join("\n", _log.Lines);
        Assert.Contains("daily-notes", all);
        Assert.DoesNotContain("SECRET_DESCRIPTION", all);
        Assert.DoesNotContain("BODY_TEXT", all);
    }
}
