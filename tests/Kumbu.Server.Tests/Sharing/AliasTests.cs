// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Sync;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// The per-caller view (collab design §3.2): each caller sees a vault under their own name for it. Alice's
/// "Gartenarbeit" is "garten" for Bob, everywhere he meets it (listings, paths, search, sync hints, tickets), while
/// the one unlocked vault both work on stays "Gartenarbeit" for Alice.
/// </summary>
[Collection("app")]
public sealed class AliasTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>Alice's "Gartenarbeit" with a.md, shared with Bob (who has his own "Work") under his alias "garten".</summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId)> SharedAsync(MemberRole role = MemberRole.Write)
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("a.md", "Kompost\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "garten", role);
        return (alice, bob, await alice.VaultIdAsync());
    }

    private async Task<string> KeyAsync(RestClient user, params string[] vaultIds)
    {
        var (key, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            user.User, user.UserKey, DateTime.UtcNow, "Claude", vaultIds.ToDictionary(v => v, _ => KeyRight.Maintain), Ct);
        return key;
    }

    private static async Task<IReadOnlyList<string>> VaultNamesAsync(RestClient user)
    {
        using var response = await user.GetAsync("/api/vaults");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await RestClient.JsonAsync(response)).AsArray().Select(v => v!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task A_member_sees_the_shared_vault_under_her_alias()
    {
        var (alice, bob, vaultId) = await SharedAsync(MemberRole.Maintain);

        // The session.
        Assert.Equal(["Work", "garten"], await VaultNamesAsync(bob));
        using (var read = await bob.GetAsync("/api/notes/garten/a.md"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("Kompost\n", (await RestClient.JsonAsync(read))["content"]!.GetValue<string>());
        }

        using (var owners = await bob.GetAsync("/api/notes/Gartenarbeit/a.md")) Assert.Equal(HttpStatusCode.NotFound, owners.StatusCode);
        using (var written = await bob.SendAsync(HttpMethod.Put, "/api/notes/garten/Beete/b.md", new { content = "Tomaten\n" }))
        {
            Assert.Equal(HttpStatusCode.Created, written.StatusCode);
        }

        // Stored in the vault, which Alice sees under her own name.
        Assert.Equal("Tomaten\n", (await alice.ReadAsync("Beete/b.md"))["content"]!.GetValue<string>());

        // His key, over MCP: listings, reads, searches and moves are all under the alias.
        var key = await KeyAsync(bob, await bob.VaultIdAsync(), vaultId);
        var (_, root) = await bob.ShAsync("ls /", key);
        Assert.Contains("garten", root, StringComparison.Ordinal);
        Assert.DoesNotContain("Gartenarbeit", root, StringComparison.Ordinal);
        var (failed, text) = await bob.ShAsync("cat /garten/a.md", key);
        Assert.False(failed, text);
        Assert.Contains("Kompost", text, StringComparison.Ordinal);
        (failed, _) = await bob.ShAsync("cat /Gartenarbeit/a.md", key);
        Assert.True(failed);
        (failed, text) = await bob.ShAsync("rg Tomaten /", key);
        Assert.False(failed, text);
        Assert.Contains("/garten/Beete/b.md", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Gartenarbeit", text, StringComparison.Ordinal);
        (failed, text) = await bob.ShAsync("mv /garten/Beete/b.md /garten/c.md", key);
        Assert.False(failed, text);
        Assert.DoesNotContain("Gartenarbeit", text, StringComparison.Ordinal);
        (failed, text) = await bob.ShAsync("rg --files /garten", key);
        Assert.False(failed, text);
        Assert.Contains("/garten/c.md", text, StringComparison.Ordinal);
        Assert.Equal("Tomaten\n", (await alice.ReadAsync("c.md"))["content"]!.GetValue<string>());

        // The trash, a restore, the history and a checkpoint (his key may maintain): each under the alias too.
        foreach (var command in new[] { "rm /garten/c.md", "trash /garten", "restore /garten/c.md", "history /garten/c.md", "mkcp /garten vorher", "lscp /garten" })
        {
            (failed, text) = await bob.ShAsync(command, key);
            Assert.False(failed, $"{command}: {text}");
            Assert.DoesNotContain("Gartenarbeit", text, StringComparison.Ordinal);
        }

        Assert.Contains("vorher", (await bob.ShAsync("lscp /garten", key)).Text, StringComparison.Ordinal);
        Assert.Equal("Tomaten\n", (await alice.ReadAsync("c.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_owner_still_sees_her_name_while_the_vault_is_open_for_a_member()
    {
        var (alice, bob, vaultId) = await SharedAsync();
        var catalog = Api.Services.GetRequiredService<VaultCatalog>();
        var aliceKey = await KeyAsync(alice, vaultId);
        var bobKey = await KeyAsync(bob, vaultId);

        // In turn, on the one vault in memory.
        var (_, listing) = await bob.ShAsync("ls /", bobKey);
        Assert.Contains("garten", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("Gartenarbeit", listing, StringComparison.Ordinal);
        var shared = catalog.Current(vaultId);
        Assert.NotNull(shared);
        Assert.Equal(["Gartenarbeit"], await VaultNamesAsync(alice));
        Assert.Equal(["Work", "garten"], await VaultNamesAsync(bob));
        var (_, owners) = await alice.ShAsync("cat /Gartenarbeit/a.md", aliceKey);
        Assert.Contains("Kompost", owners, StringComparison.Ordinal);
        var (_, members) = await bob.ShAsync("cat /garten/a.md", bobKey);
        Assert.Contains("Kompost", members, StringComparison.Ordinal);
        Assert.Same(shared, catalog.Current(vaultId));
        Assert.Equal("Gartenarbeit", shared!.Name);

        // The version list, as each one's sync client reads it.
        using var aliceVersions = await alice.GetAsync("/api/vaults/Gartenarbeit/versions");
        Assert.Equal("/Gartenarbeit/a.md", (await RestClient.JsonAsync(aliceVersions))["notes"]![0]!["path"]!.GetValue<string>());
        using var bobVersions = await bob.GetAsync("/api/vaults/garten/versions", bobKey);
        Assert.Equal(HttpStatusCode.OK, bobVersions.StatusCode);
        Assert.Equal("/garten/a.md", (await RestClient.JsonAsync(bobVersions))["notes"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_sync_hint_for_the_member_names_the_alias()
    {
        var (alice, bob, vaultId) = await SharedAsync();
        var bobKey = await KeyAsync(bob, vaultId);
        var aliceKey = await KeyAsync(alice, vaultId);
        using var bobSocket = await ConnectAsync(await TicketAsync(bob, bobKey));
        using var aliceSocket = await ConnectAsync(await TicketAsync(alice, aliceKey));

        await alice.CreateAsync("plan.md", "x\n");

        Assert.Equal(Changed("garten"), await ReceiveAsync(bobSocket, TimeSpan.FromSeconds(3)));
        Assert.Equal(Changed("Gartenarbeit"), await ReceiveAsync(aliceSocket, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task The_ticket_and_the_key_list_name_the_alias()
    {
        var (_, bob, vaultId) = await SharedAsync();
        var key = await KeyAsync(bob, vaultId);

        using var response = await bob.SendAsync(HttpMethod.Post, "/api/ws-ticket", bearer: key);
        var ticket = (await RestClient.JsonAsync(response))["ticket"]!.GetValue<string>();
        Assert.Equal([(vaultId, "garten")], Api.Services.GetRequiredService<SyncTickets>().Take(ticket)!.Vaults);

        using var keys = await bob.GetAsync("/api/keys");
        var listed = (await RestClient.JsonAsync(keys)).AsArray().Single()!["vaults"]!.AsObject();
        Assert.Equal(["garten"], listed.Select(v => v.Key));
    }

    [Fact]
    public async Task A_members_export_is_under_her_alias()
    {
        var (_, bob, _) = await SharedAsync();
        using var ticket = await bob.SendAsync(HttpMethod.Post, "/api/export/ticket", new { password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, ticket.StatusCode);
        var value = (await RestClient.JsonAsync(ticket))["ticket"]!.GetValue<string>();

        using var export = await bob.GetAsync($"/api/export?vault=garten&ticket={Uri.EscapeDataString(value)}");

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var zip = new System.IO.Compression.ZipArchive(await export.Content.ReadAsStreamAsync(Ct));
        Assert.Equal(["garten/a.md"], zip.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName));
    }

    private static async Task<string> TicketAsync(RestClient user, string key)
    {
        using var response = await user.SendAsync(HttpMethod.Post, "/api/ws-ticket", bearer: key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response))["ticket"]!.GetValue<string>();
    }

    private async Task<WebSocket> ConnectAsync(string ticket) =>
        await Api.Server.CreateWebSocketClient().ConnectAsync(new Uri($"wss://kumbu.test/api/ws?ticket={Uri.EscapeDataString(ticket)}"), Ct);

    private static async Task<string?> ReceiveAsync(WebSocket socket, TimeSpan wait)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(wait);
        var buffer = new byte[4096];
        try
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            return Encoding.UTF8.GetString(buffer, 0, result.Count);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string Changed(string vault) => new JsonObject { ["type"] = "changed", ["vault"] = vault }.ToJsonString();
}
