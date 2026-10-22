// SPDX-License-Identifier: AGPL-3.0-or-later
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Mcp;

/// <summary>End to end with the official MCP C# SDK client: what an agent experiences.</summary>
[Collection("app")]
public sealed class McpClientTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<CallToolResult> ShAsync(string command)
    {
        await using var client = await app.ConnectAsync();
        return await client.CallToolAsync(
            "sh", new Dictionary<string, object?> { ["command"] = command }, cancellationToken: Ct);
    }

    private static string Text(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    [Fact]
    public async Task Lists_exactly_the_sh_tool_with_the_manual_as_description()
    {
        await using var client = await app.ConnectAsync();

        var tools = await client.ListToolsAsync(cancellationToken: Ct);

        var tool = Assert.Single(tools);
        Assert.Equal("sh", tool.Name);
        Assert.Contains("ls [path]", tool.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ls_root_lists_the_vaults()
    {
        var result = await ShAsync("ls /");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("Demo/\nIdeas/", Text(result));
    }

    [Fact]
    public async Task Cat_of_a_quoted_path_with_a_space_returns_the_note()
    {
        var expected = (await File.ReadAllTextAsync(Path.Combine(KumbuAppFactory.SamplesPath, "Demo", "Meeting Notes.md"), Ct))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var result = await ShAsync("cat \"/Demo/Meeting Notes.md\"");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(expected, Text(result));
    }

    [Fact]
    public async Task Cat_of_a_missing_note_is_a_tool_error_not_a_protocol_error()
    {
        var result = await ShAsync("cat /Demo/nope.md");

        Assert.True(result.IsError);
        Assert.Equal("cat: /Demo/nope.md: No such file or directory", Text(result));
    }

    // The sample's file name is committed in NFC; the command is typed in NFC too, as a model would.
    [Fact]
    public async Task Ls_of_a_vault_lists_a_note_with_umlauts()
    {
        var result = await ShAsync("ls /Ideas");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal("retrofuturism.md\n\u00DCberlegungen.md", Text(result));
    }

    [Fact]
    public async Task Cat_of_a_note_with_umlauts_returns_its_exact_content()
    {
        var expected = (await File.ReadAllTextAsync(Path.Combine(KumbuAppFactory.SamplesPath, "Ideas", "\u00DCberlegungen.md"), Ct))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("\u00E4", expected, StringComparison.Ordinal);

        var result = await ShAsync("cat \"/Ideas/\u00DCberlegungen.md\"");

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(expected, Text(result));
    }
}
