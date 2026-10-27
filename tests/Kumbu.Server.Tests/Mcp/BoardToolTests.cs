// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Mcp;

/// <summary>The board tool end to end: what an agent experiences when it changes a board.</summary>
[Collection("app")]
public sealed class BoardToolTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    private static Task<CallToolResult> BoardAsync(McpClient client, Dictionary<string, object?> args) =>
        client.CallToolAsync("board", args, cancellationToken: Ct).AsTask();

    private static async Task<string> ShAsync(McpClient client, string command) =>
        Text(await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = command }, cancellationToken: Ct));

    /// <summary>A new user with a vault; a key for it with the given right.</summary>
    private async Task<(string Vault, string WriteKey, string ReadKey)> OwnVaultAsync()
    {
        var keys = app.Services.GetRequiredService<ApiKeys>();
        var users = new UserCatalog(
            app.Database, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), keys,
            app.Services.GetRequiredService<Connections>(), TimeProvider.System, NullLogger<UserCatalog>.Instance);
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, "correct horse battery staple", Roles.User, Ct);
        var (user, userKey, _) = (await users.TrySignInAsync(username, "correct horse battery staple", Ct))!.Value;
        var vault = "Kanban" + Guid.NewGuid().ToString("N")[..6];
        var id = (await app.Services.GetRequiredService<VaultCatalog>().CreateForUserAsync(vault, user.Id, userKey, Ct)).Id;
        var (write, _) = await keys.CreateAsync(user, userKey, DateTime.UtcNow, "writer", new Dictionary<string, KeyRight> { [id] = KeyRight.Write }, Ct);
        var (read, _) = await keys.CreateAsync(user, userKey, DateTime.UtcNow, "reader", new Dictionary<string, KeyRight> { [id] = KeyRight.Read }, Ct);
        return (vault, write, read);
    }

    [Fact]
    public async Task The_tool_is_listed_with_its_description()
    {
        await using var client = await app.ConnectAsync();

        var tool = (await client.ListToolsAsync(cancellationToken: Ct)).Single(t => t.Name == "board");

        var description = string.Join(' ', tool.Description!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("Read the board first with sh `board /<vault>` and follow its Instructions.", description, StringComparison.Ordinal);
        Assert.Contains("A move or update needs no version token and never changes the card's text; change the text with edit.", description, StringComparison.Ordinal);
        Assert.Equal(false, tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.Equal(false, tool.ProtocolTool.Annotations?.DestructiveHint);
        Assert.Equal(false, tool.ProtocolTool.Annotations?.IdempotentHint);
        Assert.Equal(false, tool.ProtocolTool.Annotations?.OpenWorldHint);
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order();
        Assert.Equal(
            ["action", "card", "clear_due", "clear_tags", "column", "due", "from", "position", "reason", "tags", "text", "title", "to", "vault"],
            properties);
        Assert.Equal(["action", "vault"], tool.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray().Select(p => p.GetString()!).Order());
    }

    [Fact]
    public async Task Add_then_move_through_mcp()
    {
        var (vault, key, _) = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(key);

        Assert.StartsWith("board: created", Text(await BoardAsync(client, new() { ["vault"] = vault, ["action"] = "create" })), StringComparison.Ordinal);
        var added = await BoardAsync(client, new() { ["vault"] = vault, ["action"] = "add", ["title"] = "Write release notes", ["column"] = "Backlog", ["text"] = "Body.\n", ["due"] = "2026-12-01", ["tags"] = new[] { "docs" } });
        Assert.NotEqual(true, added.IsError);
        Assert.StartsWith("board: added 'Write release notes' to Backlog", Text(added), StringComparison.Ordinal);

        var moved = await BoardAsync(client, new() { ["vault"] = vault, ["action"] = "move", ["card"] = "Write release notes", ["column"] = "Doing", ["position"] = "top" });
        Assert.StartsWith("board: moved 'Write release notes' to Doing", Text(moved), StringComparison.Ordinal);

        var updated = await BoardAsync(client, new() { ["vault"] = vault, ["action"] = "update", ["card"] = "Write release notes", ["clear_due"] = true, ["tags"] = new[] { "docs", "v1" } });
        Assert.Equal("board: updated 'Write release notes': no due date, tags docs, v1", Text(updated));

        var renamed = await BoardAsync(client, new() { ["vault"] = vault, ["action"] = "rename_column", ["from"] = "Doing", ["to"] = "In progress" });
        Assert.StartsWith("board: renamed column Doing to In progress", Text(renamed), StringComparison.Ordinal);

        var note = await ShAsync(client, $"cat \"/{vault}/Board/Write release notes.md\"");
        Assert.Contains("column: In progress", note, StringComparison.Ordinal);
        Assert.Contains("Body.", note, StringComparison.Ordinal);
        Assert.DoesNotContain("due:", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_argument_names_it()
    {
        var (vault, key, _) = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(key);

        var add = await BoardAsync(client, new() { ["vault"] = vault, ["action"] = "add", ["title"] = "x" });
        var move = await BoardAsync(client, new() { ["vault"] = vault, ["action"] = "move", ["column"] = "Doing" });
        var rename = await BoardAsync(client, new() { ["vault"] = vault, ["action"] = "rename_column", ["from"] = "Doing" });
        var update = await BoardAsync(client, new() { ["vault"] = vault, ["action"] = "update", ["card"] = "x" });
        var date = await BoardAsync(client, new() { ["vault"] = vault, ["action"] = "update", ["card"] = "x", ["due"] = "tomorrow" });
        var unknown = await BoardAsync(client, new() { ["vault"] = vault, ["action"] = "delete" });
        var noAction = await client.CallToolAsync("board", new Dictionary<string, object?> { ["vault"] = vault }, cancellationToken: Ct);

        Assert.True(add.IsError);
        Assert.Equal("board: column is required for add", Text(add));
        Assert.Equal("board: card is required for move", Text(move));
        Assert.Equal("board: to is required for rename_column", Text(rename));
        Assert.Equal("board: update needs at least one of due, tags, clear_due, clear_tags; to remove the due date or the tags, set clear_due or clear_tags", Text(update));
        Assert.Contains("YYYY-MM-DD", Text(date), StringComparison.Ordinal);
        Assert.Contains("add, move, update, create or rename_column", Text(unknown), StringComparison.Ordinal);
        Assert.Equal("board: action is required", Text(noAction));
    }

    [Fact]
    public async Task A_read_key_is_refused()
    {
        var (vault, writeKey, readKey) = await OwnVaultAsync();
        await using (var writer = await app.ConnectAsync(writeKey))
            await BoardAsync(writer, new() { ["vault"] = vault, ["action"] = "create" });
        await using var client = await app.ConnectAsync(readKey);

        var result = await BoardAsync(client, new() { ["vault"] = vault, ["action"] = "add", ["title"] = "Nope", ["column"] = "Ideas" });

        Assert.True(result.IsError);
        Assert.Equal($"board: this key can only read vault {vault}", Text(result));
    }
}
