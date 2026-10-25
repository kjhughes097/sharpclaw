using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SharpClaw.Gateway.Endpoints;

namespace SharpClaw.Gateway.Tests;

public class ChatRequestParserTests
{
    private static (ParsedChatRequest? Request, IResult? Error) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ChatRequestParser.Parse(doc.RootElement);
    }

    [Fact]
    public void Valid_request_is_parsed_with_sampling_params()
    {
        var (req, error) = Parse("""
        { "stream": true, "temperature": 0.5, "max_tokens": 100, "top_p": 0.9, "stop": "END",
          "messages": [ { "role": "system", "content": "s" }, { "role": "user", "content": "hi" } ] }
        """);

        Assert.Null(error);
        Assert.Equal(2, req!.Messages.Count);
        Assert.Equal(0.5, req.Temperature);
        Assert.Equal(100, req.MaxTokens);
        Assert.Equal(0.9, req.TopP);
        Assert.Equal(new[] { "END" }, req.Stop);
    }

    [Fact]
    public void Model_from_request_is_never_used()
    {
        var (req, _) = Parse("""{ "model": "x/y", "stream": true, "messages": [ { "role": "user", "content": "hi" } ] }""");
        Assert.Equal("agent/model", req!.ToProviderRequest("agent/model").Model);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{ "stream": false, "messages": [ { "role": "user", "content": "hi" } ] }""")]
    [InlineData("""{ "stream": true }""")]
    [InlineData("""{ "stream": true, "messages": [] }""")]
    [InlineData("""{ "stream": true, "messages": [ { "role": "robot", "content": "hi" } ] }""")]
    [InlineData("""{ "stream": true, "messages": [ { "role": "user" } ] }""")]
    [InlineData("""{ "stream": true, "messages": [ { "role": "user", "content": [ { "type": "text" } ] } ] }""")]
    public void Invalid_requests_return_an_error(string json)
    {
        var (req, error) = Parse(json);
        Assert.Null(req);
        Assert.NotNull(error);
    }
}
