// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Sh;
using Kumbu.Server.Accounts;
using Kumbu.Server.Mcp;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Mcp;

/// <summary>The skills section of the instructions: built for <c>initialize</c> and <c>server/discover</c> only, from
/// what the credential may read, and never for any other request.</summary>
[Collection("app")]
public sealed class HandshakeInstructionsTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task PutAsync(RestClient user, string path, string content)
    {
        using var put = await user.SendAsync(HttpMethod.Put, "/api/notes" + path, new { content });
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
    }

    /// <summary>Alice with a skill in each of Skills, Work and Private, and a key reading <paramref name="vaults"/>.</summary>
    private async Task<(RestClient Alice, string Key)> AliceAsync(params string[] vaults)
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        (await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Private" })).Dispose();
        await PutAsync(alice, "/Skills/write-as-alice/SKILL.md", "---\nname: write-as-alice\ndescription: Writes posts in Alice's voice.\n---\n");
        await PutAsync(alice, "/Work/_skills/release-notes/SKILL.md", "---\nname: release-notes\ndescription: Drafts release notes.\n---\n");
        await PutAsync(alice, "/Private/_skills/secret-diary/SKILL.md", "---\nname: secret-diary\ndescription: Keeps the diary.\n---\n");
        var ids = (await app.PublicApi.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(alice.User.Id, alice.UserKey, Ct))
            .Where(a => vaults.Contains(a.Name)).ToDictionary(a => a.Id, _ => KeyRight.Read);
        Assert.Equal(vaults.Length, ids.Count);
        var (key, _) = await app.PublicApi.Services.GetRequiredService<ApiKeys>().CreateAsync(alice.User, alice.UserKey, DateTime.UtcNow, "Claude Code", ids, Ct);
        return (alice, key);
    }

    private async Task<McpClient> ClientAsync(string bearer)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(app.PublicApi.Server.BaseAddress, "mcp"), AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {bearer}" } },
            app.PublicApi.CreateClient(), NullLoggerFactory.Instance, ownsHttpClient: false);
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private async Task<string> InstructionsAsync(string bearer)
    {
        await using var client = await ClientAsync(bearer);
        return client.ServerInstructions!;
    }

    /// <summary>A hand-written JSON-RPC request; the JSON-RPC message of the answer, plain JSON or one SSE event.</summary>
    private async Task<JsonNode> RawAsync(string bearer, string body, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new("Bearer", bearer);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        foreach (var (name, value) in headers) request.Headers.Add(name, value);
        using var response = await app.PublicApi.CreateClient().SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            raw = raw.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => l["data: ".Length..]).Last();
        }

        return JsonNode.Parse(raw)!;
    }

    [Fact]
    public async Task Initialize_lists_what_the_key_may_see()
    {
        var (_, key) = await AliceAsync("Skills", "Work");

        var text = await InstructionsAsync(key);

        Assert.StartsWith(ServerInstructions.Text, text);
        Assert.Contains("- create-skill (system): ", text);
        Assert.Contains("- write-as-alice (/Skills/write-as-alice, ", text);
        Assert.Contains("- release-notes (/Work/_skills/release-notes, ", text);
        Assert.DoesNotContain("secret-diary", text);
        Assert.DoesNotContain("Keeps the diary", text);
    }

    [Fact]
    public async Task A_key_without_Skills_sees_none_of_its_skills()
    {
        var (_, key) = await AliceAsync("Work");

        var text = await InstructionsAsync(key);

        Assert.DoesNotContain("write-as-alice", text);
        Assert.Contains("- release-notes (", text);
    }

    [Fact]
    public async Task Hint_only_sends_no_list()
    {
        var (alice, key) = await AliceAsync("Skills", "Work");
        using var keys = await alice.GetAsync("/api/keys");
        var id = (await RestClient.JsonAsync(keys)).AsArray().Single(k => k!["name"]!.GetValue<string>() == "Claude Code")!["id"]!.GetValue<string>();
        using var put = await alice.SendAsync(HttpMethod.Put, $"/api/keys/{id}/instructions", new { mode = "hint" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var text = await InstructionsAsync(key);

        Assert.StartsWith(ServerInstructions.Text, text);
        Assert.Contains("skills find", text);
        Assert.DoesNotContain("- create-skill", text);
        Assert.DoesNotContain("write-as-alice", text);
    }

    [Fact]
    public async Task Server_discover_carries_the_section()
    {
        var (_, key) = await AliceAsync("Skills");
        const string body = """{"jsonrpc":"2.0","id":7,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""";

        var json = await RawAsync(key, body, ("MCP-Protocol-Version", "2026-07-28"), ("Mcp-Method", "server/discover"));

        var text = json["result"]!["instructions"]!.GetValue<string>();
        Assert.StartsWith(ServerInstructions.Text, text);
        Assert.Contains("- write-as-alice (/Skills/write-as-alice, ", text);
    }

    [Fact]
    public async Task Initialize_over_the_older_revision_carries_it()
    {
        var (_, key) = await AliceAsync("Skills");
        const string body = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""";

        var json = await RawAsync(key, body);

        var text = json["result"]!["instructions"]!.GetValue<string>();
        Assert.StartsWith(ServerInstructions.Text, text);
        Assert.Contains("- write-as-alice (/Skills/write-as-alice, ", text);
    }

    [Fact]
    public async Task A_tool_call_builds_nothing()
    {
        var (_, key) = await AliceAsync("Skills");
        var counter = app.PublicApi.Services.GetRequiredService<HandshakeCounter>();
        await using var client = await ClientAsync(key);

        var before = counter.Builds;
        await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = "ls /" }, cancellationToken: Ct);
        await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = "skills ls" }, cancellationToken: Ct);
        await client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(before, counter.Builds);
    }

    [Fact]
    public async Task The_list_is_a_snapshot()
    {
        var (alice, key) = await AliceAsync("Skills");
        await using var client = await ClientAsync(key);
        var first = client.ServerInstructions!;

        await PutAsync(alice, "/Skills/late/SKILL.md", "---\nname: late\ndescription: Came later.\n---\n");
        var ls = await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = "skills ls" }, cancellationToken: Ct);

        Assert.Contains("late (/Skills/late", Assert.IsType<TextContentBlock>(ls.Content[0]).Text);
        Assert.Equal(first, client.ServerInstructions);
        Assert.DoesNotContain("late (", first);
    }

    [Fact]
    public async Task The_dev_token_gets_the_system_skills()
    {
        await using var client = await app.ConnectAsync();

        Assert.StartsWith(ServerInstructions.Text, client.ServerInstructions);
        Assert.Contains("- create-skill (system)", client.ServerInstructions);
    }

    [Fact]
    public void The_exception_to_data_not_instructions_is_worded() =>
        Assert.Contains("the exception is a skill whose description matches the user's task", ServerInstructions.Text);
}
