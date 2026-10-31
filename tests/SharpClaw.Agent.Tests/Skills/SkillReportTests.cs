using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Agent.Skills;

namespace SharpClaw.Agent.Tests.Skills;

/// <summary>The startup report of loaded skills (skills spec section 9.1).</summary>
public class SkillReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}");

    public SkillReportTests()
    {
        foreach (var name in new[] { "research-brief", "daily-notes" })
        {
            Directory.CreateDirectory(Path.Combine(_root, name));
            File.WriteAllText(Path.Combine(_root, name, "SKILL.md"), $"---\nname: {name}\ndescription: >\n  About\n  {name}.\n---\nBODY\n");
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SkillLibrary Library() => SkillLibrary.Load(_root, NullLogger.Instance);

    [Fact]
    public async Task Sends_names_and_folded_descriptions_once()
    {
        var gateway = new RecordingGateway();

        await SkillReport.SendAsync(gateway, Library(), NullLogger.Instance, CancellationToken.None);

        var report = Assert.Single(gateway.SkillReports);
        Assert.Equal(new[] { "daily-notes", "research-brief" }, report.Select(s => s.Name));
        Assert.Equal("About daily-notes.", report[0].Description);
    }

    [Fact]
    public async Task An_empty_library_reports_an_empty_list()
    {
        var gateway = new RecordingGateway();
        await SkillReport.SendAsync(gateway, SkillLibrary.Load(Path.Combine(_root, "none"), NullLogger.Instance),
            NullLogger.Instance, CancellationToken.None);
        Assert.Empty(Assert.Single(gateway.SkillReports));
    }

    [Fact]
    public async Task A_refusal_is_logged_with_the_status_code_only_and_not_retried()
    {
        var gateway = new RecordingGateway
        {
            SkillReportException = new HttpRequestException("RESPONSE_BODY", null, HttpStatusCode.BadRequest),
        };
        var log = new ListLogger<SkillReportTests>();

        await SkillReport.SendAsync(gateway, Library(), log, CancellationToken.None);

        Assert.Single(gateway.SkillReports);
        var line = Assert.Single(log.Lines);
        Assert.Equal("could not report skills to the gateway: status=400", line);
    }

    [Fact]
    public async Task Any_other_failure_is_logged_and_the_agent_keeps_running()
    {
        var gateway = new RecordingGateway { SkillReportException = new TaskCanceledException("timeout") };
        var log = new ListLogger<SkillReportTests>();

        await SkillReport.SendAsync(gateway, Library(), log, CancellationToken.None);

        Assert.Equal("could not report skills to the gateway: TaskCanceledException", Assert.Single(log.Lines));
    }
}
