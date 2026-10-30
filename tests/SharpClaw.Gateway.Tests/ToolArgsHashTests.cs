using System.Text.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Tests;

/// <summary>The canonical arguments hash the agent and gateway both compute (external tools spec section 4).</summary>
public class ToolArgsHashTests
{
    private static string Hash(string json) => ToolArgsHash.Compute(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Key_order_whitespace_and_escaping_do_not_matter() =>
        Assert.Equal(
            Hash("""{"b":[1,2,{"y":true,"x":null}],"a":"caf\u00e9"}"""),
            Hash("""{ "a" : "café", "b" : [ 1, 2, { "x": null, "y": true } ] }"""));

    [Theory]
    [InlineData("""{"a":"x"}""", """{"a":"y"}""")]
    [InlineData("""{"a":1}""", """{"a":"1"}""")]
    [InlineData("""{"a":[1,2]}""", """{"a":[2,1]}""")]
    [InlineData("""{"a":1}""", """{"a":1,"b":null}""")]
    public void Any_value_change_changes_the_hash(string a, string b) => Assert.NotEqual(Hash(a), Hash(b));

    [Fact]
    public void Hash_is_lowercase_hex_sha256()
    {
        var hash = Hash("{}");
        Assert.True(ToolArgsHash.IsValid(hash));
        Assert.Equal("44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a", hash); // sha256("{}")
        Assert.False(ToolArgsHash.IsValid(hash.ToUpperInvariant()));
    }
}
