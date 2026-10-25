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

    [Fact]
    public void Tools_tool_choice_and_tool_messages_are_parsed()
    {
        var (req, error) = Parse("""
        { "stream": true, "tool_choice": "auto",
          "tools": [ { "type": "function", "function": { "name": "workspace_read", "description": "Read",
                       "parameters": { "type": "object" } } } ],
          "messages": [
            { "role": "user", "content": "read it" },
            { "role": "assistant", "content": null,
              "tool_calls": [ { "id": "call_1", "type": "function", "function": { "name": "workspace_read", "arguments": "{}" } } ] },
            { "role": "tool", "tool_call_id": "call_1", "content": "{\"ok\":true}" } ] }
        """);

        Assert.Null(error);
        var tool = Assert.Single(req!.Tools!);
        Assert.Equal("workspace_read", tool.Name);
        Assert.Equal("Read", tool.Description);
        Assert.Contains("object", tool.ParametersJson);
        Assert.Equal("auto", req.ToolChoice);
        Assert.Null(req.Messages[1].Content);
        Assert.Equal("call_1", Assert.Single(req.Messages[1].ToolCalls!).Id);
        Assert.Equal("call_1", req.Messages[2].ToolCallId);
    }

    [Theory]
    [InlineData("""{ "stream": true, "tool_choice": "required", "messages": [ { "role": "user", "content": "x" } ] }""")]
    [InlineData("""{ "stream": true, "tools": {}, "messages": [ { "role": "user", "content": "x" } ] }""")]
    [InlineData("""{ "stream": true, "tools": [ { "type": "function", "function": { "name": "has space" } } ], "messages": [ { "role": "user", "content": "x" } ] }""")]
    [InlineData("""{ "stream": true, "tools": [ { "type": "function", "function": { "name": "ok", "parameters": "x" } } ], "messages": [ { "role": "user", "content": "x" } ] }""")]
    [InlineData("""{ "stream": true, "messages": [ { "role": "tool", "content": "x" } ] }""")]
    [InlineData("""{ "stream": true, "messages": [ { "role": "assistant", "content": null } ] }""")]
    [InlineData("""{ "stream": true, "messages": [ { "role": "assistant", "content": null, "tool_calls": [ { "id": "c" } ] } ] }""")]
    public void Invalid_tool_requests_return_an_error(string json)
    {
        var (req, error) = Parse(json);
        Assert.Null(req);
        Assert.NotNull(error);
    }
}
