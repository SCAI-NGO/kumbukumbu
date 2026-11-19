// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Mcp;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Mcp;

/// <summary>The upload tool end to end: what an agent gets before it PUTs a file, and what it is refused.</summary>
[Collection("app")]
public sealed class UploadToolTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    private Task<CallToolResult> UploadAsync(McpClient client, string path, string? version = null)
    {
        var args = new Dictionary<string, object?> { ["path"] = path };
        if (version is not null) args["version"] = version;
        return client.CallToolAsync("upload", args, cancellationToken: Ct).AsTask();
    }

    private int Tickets => app.Services.GetRequiredService<UploadTickets>().Count;

    /// <summary>A new user with a vault that holds a file, /vault/kept.png; a read key and a write key for it.</summary>
    private async Task<(string Vault, string ReadKey, string WriteKey, string KeptVersion)> OwnVaultAsync()
    {
        var keys = app.Services.GetRequiredService<ApiKeys>();
        var users = new UserCatalog(
            app.Database, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), keys,
            app.Services.GetRequiredService<Connections>(), TimeProvider.System, NullLogger<UserCatalog>.Instance);
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, "correct horse battery staple", Roles.User, Ct);
        var (user, userKey, _) = (await users.TrySignInAsync(username, "correct horse battery staple", Ct))!.Value;
        var vault = "Up" + Guid.NewGuid().ToString("N")[..6];
        var id = (await app.Services.GetRequiredService<VaultCatalog>().CreateForUserAsync(vault, user.Id, userKey, Ct)).Id;
        var (write, _) = await keys.CreateAsync(user, userKey, DateTime.UtcNow, "writer", new Dictionary<string, KeyRight> { [id] = KeyRight.Write }, Ct);
        var (read, _) = await keys.CreateAsync(user, userKey, DateTime.UtcNow, "reader", new Dictionary<string, KeyRight> { [id] = KeyRight.Read }, Ct);

        // One kept file, put through the route an agent's PUT will take.
        using var http = app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", write);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        using var put = await http.PutAsync($"/api/files/{vault}/kept.png", new ByteArrayContent(png), Ct);
        put.EnsureSuccessStatusCode();
        var version = System.Text.Json.JsonDocument.Parse(await put.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("version").GetString()!;
        return (vault, read, write, version);
    }

    [Fact]
    public async Task Answers_a_url_and_a_curl_line()
    {
        var (vault, _, key, _) = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(key);
        var before = Tickets;

        var result = await UploadAsync(client, $"/{vault}/photo.png");

        Assert.NotEqual(true, result.IsError);
        var text = Text(result);
        Assert.Matches(new Regex(@"/api/agent-upload/[A-Za-z0-9_-]{43}\b"), text);
        Assert.Contains("curl -sS -X PUT --data-binary @<local file> '", text, StringComparison.Ordinal);
        Assert.Contains("at most 1 GiB", text, StringComparison.Ordinal);
        Assert.Contains("once", text, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"^expires \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", RegexOptions.Multiline), text);
        Assert.Matches(new Regex(@"^https?://\S+/api/agent-upload/", RegexOptions.Multiline), text);
        Assert.Equal(before + 1, Tickets);
    }

    [Fact]
    public async Task Answers_a_url_for_an_existing_file_with_its_version()
    {
        var (vault, _, key, version) = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(key);

        var result = await UploadAsync(client, $"/{vault}/kept.png", version);

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("/api/agent-upload/", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_a_read_only_key()
    {
        var (vault, key, _, _) = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(key);
        var before = Tickets;

        var result = await UploadAsync(client, $"/{vault}/photo.png");

        Assert.True(result.IsError);
        Assert.Equal(before, Tickets);
    }

    [Theory]
    [InlineData("report.xlsx")]
    [InlineData("Makefile")]
    public async Task Answers_a_url_for_any_file(string name)
    {
        var (vault, _, key, _) = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(key);

        var result = await UploadAsync(client, $"/{vault}/{name}");

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("/api/agent-upload/", Text(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("_skills/x/tool.bin")]
    [InlineData("_skills/x/run.py")]
    [InlineData("a/.vaultkeep")]
    [InlineData("x.md")]
    [InlineData("../x.png")]
    public async Task Refuses_a_path_it_cannot_take(string name)
    {
        var (vault, _, key, _) = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(key);
        var before = Tickets;

        var result = await UploadAsync(client, $"/{vault}/{name}");

        Assert.True(result.IsError);
        Assert.Equal(before, Tickets);
    }

    [Fact]
    public async Task Names_a_file_a_skills_folder_refuses_once()
    {
        var (vault, _, key, _) = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(key);

        var result = await UploadAsync(client, $"/{vault}/_skills/x/tool.bin");

        Assert.Equal($"upload: /{vault}/_skills/x/tool.bin: {Kumbu.Core.Attachments.AttachmentFormat.SkillFolderMessage}", Text(result));
    }

    [Fact]
    public async Task Refuses_a_note_path_and_points_at_write()
    {
        var (vault, _, key, _) = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(key);

        var result = await UploadAsync(client, $"/{vault}/x.md");

        Assert.True(result.IsError);
        Assert.Contains("write", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refuses_a_path_without_a_file_name()
    {
        await using var client = await app.ConnectAsync();
        var before = Tickets;

        var result = await UploadAsync(client, "/");

        Assert.True(result.IsError);
        Assert.Equal(before, Tickets);
    }

    [Fact]
    public async Task Refuses_an_existing_file_without_version()
    {
        var (vault, _, key, _) = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(key);
        var before = Tickets;

        var result = await UploadAsync(client, $"/{vault}/kept.png");

        Assert.True(result.IsError);
        Assert.Contains("version", Text(result), StringComparison.Ordinal);
        Assert.Equal(before, Tickets);
    }

    [Fact]
    public async Task Refuses_a_wrong_version()
    {
        var (vault, _, key, _) = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(key);
        var before = Tickets;

        var result = await UploadAsync(client, $"/{vault}/kept.png", "not-the-version");

        Assert.True(result.IsError);
        Assert.Equal(before, Tickets);
    }

    [Fact]
    public async Task Refuses_a_version_for_a_file_that_does_not_exist()
    {
        var (vault, _, key, version) = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(key);
        var before = Tickets;

        var result = await UploadAsync(client, $"/{vault}/other.png", version);

        Assert.True(result.IsError);
        Assert.Equal(before, Tickets);
    }

    [Fact]
    public async Task Refuses_a_vault_the_caller_has_none_of()
    {
        var (_, _, key, _) = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(key);
        var before = Tickets;

        var result = await UploadAsync(client, "/NoSuchVault/x.png");

        Assert.True(result.IsError);
        Assert.Equal(before, Tickets);
    }

    [Fact]
    public async Task Refuses_a_request_without_a_bearer_credential()
    {
        var tickets = app.Services.GetRequiredService<UploadTickets>();
        var before = Tickets;
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        // Before anything else is asked: no caller nor store is needed to refuse it.
        var result = await UploadTool.Upload(tickets, null!, null!, http, Options.Create(new KumbuOptions()), "/Work/x.png", ct: Ct);

        Assert.True(result.IsError);
        Assert.Contains("no bearer credential", Text(result), StringComparison.Ordinal);
        Assert.Equal(before, Tickets);
    }

    [Fact]
    public async Task The_tool_is_listed_with_its_description()
    {
        await using var client = await app.ConnectAsync();

        var tool = (await client.ListToolsAsync(cancellationToken: Ct)).Single(t => t.Name == "upload");

        var description = string.Join(' ', tool.Description!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("curl", description, StringComparison.Ordinal);
        Assert.Contains("shell", description, StringComparison.Ordinal);
        Assert.Contains("once", description, StringComparison.Ordinal);
        Assert.Contains("1 GiB unless its admin set another", description, StringComparison.Ordinal);
        Assert.Contains("call upload again", description, StringComparison.Ordinal);
        Assert.Equal(false, tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.Equal(false, tool.ProtocolTool.Annotations?.OpenWorldHint);
        Assert.Equal(["path", "version"], tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(["path"], tool.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray().Select(p => p.GetString()!));
    }
}
