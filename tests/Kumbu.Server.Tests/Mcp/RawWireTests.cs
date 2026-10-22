// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kumbu.Server.Tests.Mcp;

/// <summary>
/// Hand-written JSON-RPC over plain HTTP — no SDK client. These tests document what is on the wire;
/// each one writes the full request and response to the test output.
/// </summary>
[Collection("app")]
public sealed class RawWireTests(KumbuAppFactory app)
{
    // Relaxed escaping keeps the captures readable (' stays ', not \u0027); only used for test output.
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ITestOutputHelper Output =>
        TestContext.Current.TestOutputHelper ?? throw new InvalidOperationException("No test output helper");

    [Fact]
    public async Task Legacy_initialize_is_answered_without_session()
    {
        // Pre-2026-07-28 clients open with the initialize handshake. The stateless server still answers it,
        // but hands out no Mcp-Session-Id: there is no session to come back to.
        const string body = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}
            """;

        var (response, json) = await PostAsync(body, []);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        var result = json["result"]!;
        Assert.NotNull(result["protocolVersion"]);
        Assert.NotNull(result["capabilities"]?["tools"]);
        Assert.Equal("kumbu", result["serverInfo"]?["name"]?.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(result["serverInfo"]?["version"]?.GetValue<string>()));
    }

    [Fact]
    public async Task Current_revision_call_needs_no_handshake()
    {
        // MCP 2026-07-28: no initialize, no session. A single self-describing POST.
        // What the SDK (2.3.0) requires for this to be accepted:
        //   - header MCP-Protocol-Version: 2026-07-28
        //   - header Mcp-Method: tools/call   (missing → JSON-RPC error -32020 "Missing required Mcp-Method header.")
        //   - header Mcp-Name: sh             (missing → HTTP 400, -32020 "Missing required Mcp-Name header.";
        //                                      not in the brief — found by following the SDK's error message)
        //   - params._meta["io.modelcontextprotocol/protocolVersion"] = "2026-07-28"   (missing → -32602)
        //   - params._meta["io.modelcontextprotocol/clientCapabilities"] = {}     (missing → HTTP 400, -32602 "Requests
        //                                      using protocol version '2026-07-28' must include
        //                                      '_meta/io.modelcontextprotocol/clientCapabilities' as a JSON object.";
        //                                      also not in the brief — what initialize used to carry now rides on every request)
        // Mcp-Method / Mcp-Name mirror the JSON-RPC method and the tool name so gateways can route
        // without parsing the body.
        const string body = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"sh","arguments":{"command":"ls /"},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}
            """;

        var (response, json) = await PostAsync(body, [("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", "tools/call"), ("Mcp-Name", "sh")]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = json["result"] ?? throw new Xunit.Sdk.XunitException("No result: " + json.ToJsonString());
        Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
        Assert.Equal("Demo/\nIdeas/", result["content"]![0]!["text"]!.GetValue<string>());
    }

    private async Task<(HttpResponseMessage Response, JsonNode Json)> PostAsync(
        string body, (string Name, string Value)[] headers)
    {
        using var http = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body.Trim(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", KumbuAppFactory.Token);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        var response = await http.SendAsync(request, Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);

        var log = new StringBuilder();
        log.AppendLine("--- request ---");
        log.AppendLine("POST /mcp");
        log.AppendLine("Authorization: Bearer <dev token>");
        log.AppendLine($"Accept: {request.Headers.Accept}");
        log.AppendLine($"Content-Type: {request.Content.Headers.ContentType}");
        foreach (var (name, value) in headers)
        {
            log.AppendLine($"{name}: {value}");
        }

        log.AppendLine();
        log.AppendLine(PrettyPrint(body.Trim()));
        log.AppendLine("--- response ---");
        log.AppendLine($"{(int)response.StatusCode} {response.StatusCode}");
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            log.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        }

        log.AppendLine();
        log.AppendLine(raw.TrimEnd());

        var json = ParseJsonRpc(raw, response.Content.Headers.ContentType?.MediaType);
        log.AppendLine("--- response JSON-RPC message, pretty-printed ---");
        log.AppendLine(json.ToJsonString(Pretty));
        Output.WriteLine(log.ToString());

        return (response, json);
    }

    /// <summary>Either a plain JSON body, or an SSE stream whose <c>data:</c> line carries the JSON-RPC message.</summary>
    private static JsonNode ParseJsonRpc(string raw, string? mediaType)
    {
        if (mediaType == "text/event-stream")
        {
            var data = raw.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
                .Select(l => l["data: ".Length..])
                .Last();
            return JsonNode.Parse(data)!;
        }

        return JsonNode.Parse(raw)!;
    }

    private static string PrettyPrint(string json) => JsonNode.Parse(json)!.ToJsonString(Pretty);
}
