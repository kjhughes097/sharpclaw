using Microsoft.Extensions.Options;

namespace SharpClaw.Gateway.Tests;

/// <summary>Bad configuration stops the gateway at build time with a message naming the setting.</summary>
public class ConfigurationValidationTests
{
    private static WebApplication Build(Dictionary<string, string?> overrides)
    {
        var config = new Dictionary<string, string?>
        {
            ["Auth:AdminKey"] = "k",
            ["Provider:Type"] = "Fake",
            ["Database:Path"] = Path.Combine(Path.GetTempPath(), $"sharpclaw-cfg-{Guid.NewGuid():N}.db"),
            ["Listeners:ClientPort"] = "18080",
            ["Listeners:AgentPort"] = "18081",
        };
        foreach (var (k, v) in overrides)
            config[k] = v;
        return GatewayBuilder.Build(Array.Empty<string>(), b => b.Configuration.AddInMemoryCollection(config));
    }

    [Fact]
    public async Task Valid_configuration_builds()
    {
        await using var app = Build(new());
        Assert.NotNull(app);
    }

    [Theory]
    [InlineData("Auth:AdminKey", "", "Auth:AdminKey is required")]
    [InlineData("Listeners:AgentPort", "18080", "must be different ports")]
    [InlineData("Listeners:ClientPort", "0", "ClientPort")]
    [InlineData("Inbox:ClaimTimeoutSeconds", "-5", "ClaimTimeoutSeconds")]
    [InlineData("Approvals:TimeoutSeconds", "0", "TimeoutSeconds")]
    [InlineData("Events:RetentionDays", "0", "RetentionDays")]
    [InlineData("Chat:MaxLlmCallsPerMessage", "0", "MaxLlmCallsPerMessage")]
    [InlineData("Context:DefaultWindowTokens", "10", "DefaultWindowTokens")]
    [InlineData("Context:ReplyReserveTokens", "40000", "ReplyReserveTokens must be smaller")]
    [InlineData("Auth:Session:IdleTimeoutHours", "0", "IdleTimeoutHours")]
    [InlineData("Auth:Login:MaxFailures", "0", "MaxFailures")]
    [InlineData("Auth:ForwardedHeaders:KnownProxies:0", "not-an-ip", "KnownProxies")]
    [InlineData("Models:Allowed:0", " ", "Models:Allowed must not contain empty entries")]
    [InlineData("ToolBroker:AlwaysApprove:0", "rm_rf", "ToolBroker:AlwaysApprove: unknown tool 'rm_rf'")]
    [InlineData("ToolBroker:Agents:cody:Tools:0", "shell", "ToolBroker:Agents:cody:Tools: unknown tool 'shell'")]
    [InlineData("ToolBroker:Agents:cody:SandboxUrl", "ftp://sandbox", "SandboxUrl must be an absolute http(s) URL")]
    [InlineData("ToolBroker:Agents:cody:SandboxUrl", "http://sandbox-cody:8090", "SandboxSecret must be at least 16")]
    [InlineData("ToolBroker:MaxResponseBytes", "10", "MaxResponseBytes")]
    [InlineData("Egress:Sandboxes:cody:Secret", "short", "Egress:Sandboxes:cody:Secret must be at least 16")]
    [InlineData("Egress:DenyHosts:0", " ", "Egress:DenyHosts must not contain empty entries")]
    public void Invalid_values_refuse_to_start(string key, string value, string expected)
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Build(new() { [key] = value }));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Duplicate_or_overlong_model_ids_refuse_to_start()
    {
        var dup = Assert.Throws<OptionsValidationException>(() =>
            Build(new() { ["Models:Allowed:0"] = "a/b", ["Models:Allowed:1"] = "a/b" }));
        Assert.Contains("duplicates", dup.Message);
        var longId = Assert.Throws<OptionsValidationException>(() =>
            Build(new() { ["Models:Allowed:0"] = new string('m', 201) }));
        Assert.Contains("at most 200", longId.Message);
    }

    [Fact]
    public void OpenRouter_without_a_key_refuses_to_start()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(new() { ["Provider:Type"] = "OpenRouter" }));
        Assert.Contains("OpenRouter:ApiKey", ex.Message);
    }

    [Fact]
    public void Egress_port_must_not_collide_with_the_other_listeners()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => Build(new()
        {
            ["Egress:Port"] = "18081",
            ["Egress:Sandboxes:cody:Secret"] = "proxy-secret-0123456789",
        }));
        Assert.Contains("Egress:Port must differ", ex.Message);
    }

    [Fact]
    public void A_sandbox_proxy_secret_must_differ_from_its_service_secret()
    {
        const string same = "the-same-secret-0123456789";
        var ex = Assert.Throws<OptionsValidationException>(() => Build(new()
        {
            ["Egress:Sandboxes:cody:Secret"] = same,
            ["ToolBroker:Agents:cody:SandboxUrl"] = "http://sandbox-cody:8090",
            ["ToolBroker:Agents:cody:SandboxSecret"] = same,
        }));
        Assert.Contains("must differ from ToolBroker:Agents:cody:SandboxSecret", ex.Message);
    }
}
