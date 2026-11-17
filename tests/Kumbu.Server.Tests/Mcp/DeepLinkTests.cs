// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Tests.Mcp;

/// <summary>The link into the web app that write, edit, replace and cat return next to a note's version.</summary>
[Collection("app")]
public sealed class DeepLinkTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // No PublicUrl on the plain test app: the request's own origin, which the in-memory server has as http://localhost.
    private string Origin => app.Server.BaseAddress.GetLeftPart(UriPartial.Authority);

    private sealed record Owner(string Vault, string VaultId, string Key, (string Id, byte[] Key) Identity);

    private async Task<(string Id, byte[] Key, string Username)> NewUserAsync(UserCatalog users)
    {
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, "correct horse battery staple", Roles.User, Ct);
        var (user, userKey, _) = (await users.TrySignInAsync(username, "correct horse battery staple", Ct))!.Value;
        return (user.Id, userKey, username);
    }

    private UserCatalog Users() => new(
        app.Database, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), app.Services.GetRequiredService<ApiKeys>(),
        app.Services.GetRequiredService<Connections>(), TimeProvider.System, NullLogger<UserCatalog>.Instance);

    /// <summary>A new user with a vault and a write key for it.</summary>
    private async Task<Owner> OwnVaultAsync()
    {
        var keys = app.Services.GetRequiredService<ApiKeys>();
        var users = Users();
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, "correct horse battery staple", Roles.User, Ct);
        var (user, userKey, _) = (await users.TrySignInAsync(username, "correct horse battery staple", Ct))!.Value;
        var vault = "Links" + Guid.NewGuid().ToString("N")[..6];
        var id = (await app.Services.GetRequiredService<VaultCatalog>().CreateForUserAsync(vault, user.Id, userKey, Ct)).Id;
        var (write, _) = await keys.CreateAsync(user, userKey, DateTime.UtcNow, "writer", new Dictionary<string, KeyRight> { [id] = KeyRight.Write }, Ct);
        return new Owner(vault, id, write, (user.Id, userKey));
    }

    private static async Task<CallToolResult> CallAsync(McpClient client, string tool, Dictionary<string, object?> args) =>
        await client.CallToolAsync(tool, args, cancellationToken: Ct);

    private static string[] Texts(CallToolResult result) => [.. result.Content.OfType<TextContentBlock>().Select(b => b.Text)];

    private static string[] Links(CallToolResult result) => [.. Texts(result).Where(t => t.StartsWith("\nlink to ", StringComparison.Ordinal))];

    [Fact]
    public async Task Write_returns_the_link_with_the_vault_id()
    {
        var o = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(o.Key);

        var result = await CallAsync(client, "write", new() { ["path"] = $"/{o.Vault}/Plan.md", ["content"] = "# Plan\n" });

        Assert.Contains($"\nlink to /{o.Vault}/Plan.md: {Origin}/n/{o.VaultId}/Plan.md", Texts(result));
    }

    [Fact]
    public async Task Edit_and_cat_and_cat_of_a_section_return_it()
    {
        var o = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(o.Key);
        var path = $"/{o.Vault}/Plan.md";
        var link = $"\nlink to {path}: {Origin}/n/{o.VaultId}/Plan.md";
        var written = await CallAsync(client, "write", new() { ["path"] = path, ["content"] = "# Plan\n\n## Goals\n\nOne\n" });
        var version = Texts(written).Single(t => t.Contains("version of", StringComparison.Ordinal)).Split(": ")[1];

        var edited = await CallAsync(client, "edit", new() { ["path"] = path, ["version"] = version, ["old_text"] = "One", ["new_text"] = "Two" });
        var cat = await CallAsync(client, "sh", new() { ["command"] = $"cat {path}" });
        var section = await CallAsync(client, "sh", new() { ["command"] = $"cat \"{path}#Goals\"" });

        Assert.Equal([link], Links(edited));
        Assert.Equal([link], Links(cat));
        Assert.Equal([link], Links(section));
    }

    [Fact]
    public async Task Replace_returns_one_link_per_changed_note()
    {
        var o = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(o.Key);
        await CallAsync(client, "write", new() { ["path"] = $"/{o.Vault}/A.md", ["content"] = "Hecate\n" });
        await CallAsync(client, "write", new() { ["path"] = $"/{o.Vault}/B.md", ["content"] = "Hecate\n" });
        var preview = await CallAsync(client, "replace", new() { ["path"] = $"/{o.Vault}", ["old_text"] = "Hecate", ["new_text"] = "Hekate" });
        var plan = System.Text.RegularExpressions.Regex.Match(string.Join('\n', Texts(preview)), "plan[^A-Za-z0-9]+([A-Za-z0-9_-]{8,})").Groups[1].Value;

        var applied = await CallAsync(client, "replace", new() { ["path"] = $"/{o.Vault}", ["old_text"] = "Hecate", ["new_text"] = "Hekate", ["plan"] = plan });

        Assert.Equal(
            [$"\nlink to /{o.Vault}/A.md: {Origin}/n/{o.VaultId}/A.md", $"\nlink to /{o.Vault}/B.md: {Origin}/n/{o.VaultId}/B.md"],
            Links(applied).Order());
    }

    [Fact]
    public async Task A_member_gets_the_same_id_under_her_alias()
    {
        var o = await OwnVaultAsync();
        var users = Users();
        var member = await NewUserAsync(users);
        var alias = "Shared" + Guid.NewGuid().ToString("N")[..6];
        await Share.GrantAsync(
            app.Services.GetRequiredService<VaultCatalog>(), app.Services.GetRequiredService<Memberships>(), o.Identity, o.VaultId, (member.Id, member.Key), alias, MemberRole.Write);
        var keys = app.Services.GetRequiredService<ApiKeys>();
        var user = (await users.TrySignInAsync(member.Username, "correct horse battery staple", Ct))!.Value.User;
        var (key, _) = await keys.CreateAsync(user, member.Key, DateTime.UtcNow, "member", new Dictionary<string, KeyRight> { [o.VaultId] = KeyRight.Write }, Ct);
        await using var client = await app.ConnectAsync(key);

        var result = await CallAsync(client, "write", new() { ["path"] = $"/{alias}/Plan.md", ["content"] = "Hi\n" });

        Assert.Equal([$"\nlink to /{alias}/Plan.md: {Origin}/n/{o.VaultId}/Plan.md"], Links(result));
    }

    [Fact]
    public async Task Encodes_each_segment()
    {
        var o = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(o.Key);

        var result = await CallAsync(client, "write", new() { ["path"] = $"/{o.Vault}/Garten Ü/Rosen #1.md", ["content"] = "Hi\n" });

        Assert.Equal([$"\nlink to /{o.Vault}/Garten Ü/Rosen #1.md: {Origin}/n/{o.VaultId}/Garten%20%C3%9C/Rosen%20%231.md"], Links(result));
    }

    [Fact]
    public async Task Mkdir_prints_no_link_for_the_folders_marker()
    {
        var o = await OwnVaultAsync();
        await using var client = await app.ConnectAsync(o.Key);

        var result = await CallAsync(client, "sh", new() { ["command"] = $"mkdir /{o.Vault}/Folder" });

        Assert.NotEqual(true, result.IsError);
        Assert.Empty(Links(result));
    }

    [Fact]
    public async Task PublicUrl_wins_over_the_request_origin()
    {
        var o = await OwnVaultAsync();
        using var factory = app.WithWebHostBuilder(b => b.UseSetting("Kumbu:PublicUrl", "https://kumbu.example.org/"));
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(factory.Server.BaseAddress, "mcp"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {o.Key}" },
            },
            factory.CreateClient(), NullLoggerFactory.Instance, ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct);

        var result = await CallAsync(client, "write", new() { ["path"] = $"/{o.Vault}/Plan.md", ["content"] = "Hi\n" });

        Assert.Equal([$"\nlink to /{o.Vault}/Plan.md: https://kumbu.example.org/n/{o.VaultId}/Plan.md"], Links(result));
    }

    [Fact]
    public async Task No_link_without_access()
    {
        await using var client = await app.ConnectAsync();
        var vault = (await CallAsync(client, "sh", new() { ["command"] = "ls /" })).Content.OfType<TextContentBlock>().First().Text.Split('\n')[0].Trim('/', ' ');

        var result = await CallAsync(client, "write", new() { ["path"] = $"/{vault}/Dev-{Guid.NewGuid():N}.md", ["content"] = "Hi\n" });

        Assert.Empty(Links(result));
    }
}
