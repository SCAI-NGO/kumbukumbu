// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.OAuth;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.OAuth;

/// <summary>
/// Apps are disconnected wherever an API key is revoked (Ruling 2): the owner's Revoke and a vault deletion that
/// leaves a connection no vault are final; a password change that revokes the keys, a recovery, a deactivation and a
/// rights change revoke the tokens and leave the connection to be reconnected; deleting the account leaves nothing.
/// </summary>
[Collection("app")]
public sealed class RevocationCascadeTests(KumbuAppFactory app)
{
    private const string Callback = "https://claude.test/cb";
    private const string Name = "cor.energy";
    private const string NewPassword = "another horse battery staple";
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private T Service<T>() where T : notnull => Api.Services.GetRequiredService<T>();

    private IMongoCollection<BsonDocument> Collection(string name) => app.Database.GetCollection<BsonDocument>(name);

    private sealed record Pair(string Access, string Refresh, string Scope);

    /// <summary>A user with the vault <c>Arbeit</c> and, if asked, <c>Privat</c>, a connection with <paramref name="vaults"/>
    /// (vault name → right), authorised by a fresh DCR client: its flow and first pair.</summary>
    private async Task<(OAuthFlow Flow, Pair Pair)> ConnectedAsync(Dictionary<string, string>? vaults = null, bool privat = false)
    {
        var owner = await RestClient.NewAsync(Api);
        if (privat)
        {
            using var second = await owner.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Privat" });
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        }

        using var created = await owner.SendAsync(HttpMethod.Post, "/api/connections", new { name = Name, vaults = vaults ?? new Dictionary<string, string> { ["Arbeit"] = "write" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
        var flow = await OAuthFlow.StartAsync(Api, owner, id, await OAuthFlow.RegisterAsync(Api, Callback), Callback);
        await flow.AllowAsync();
        using var response = await flow.TokenAsync();
        return (flow, await PairOf(response));
    }

    private static async Task<Pair> PairOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await RestClient.JsonAsync(response);
        return new Pair(json["access_token"]!.GetValue<string>(), json["refresh_token"]!.GetValue<string>(), json["scope"]!.GetValue<string>());
    }

    private static async Task<string> RefreshErrorAsync(OAuthFlow flow, string refresh)
    {
        using var response = await flow.RefreshAsync(refresh);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await RestClient.JsonAsync(response))["error"]!.GetValue<string>();
    }

    private static string Address() => $"198.26.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    /// <summary>The status of an MCP <c>initialize</c> on the connection's URL with <paramref name="access"/>.</summary>
    private async Task<HttpStatusCode> InitializeAsync(OAuthFlow flow, string access)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/c/{flow.ConnectionId}") { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add("X-Forwarded-For", Address());
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    /// <summary>The connection as its owner's list has it, read with the user key (a password change ends the sessions, not the key).</summary>
    private async Task<ConnectionInfo> InfoAsync(OAuthFlow flow) =>
        (await Service<Connections>().ListAsync(flow.Owner.User, flow.Owner.UserKey, Ct)).Single(c => c.Id == flow.ConnectionId);

    private async Task<(ConnectionState, string?)> StateAsync(OAuthFlow flow)
    {
        var info = await InfoAsync(flow);
        return (info.State, info.Reason);
    }

    /// <summary>Neither token of <paramref name="pair"/> works any more: the access token is refused, the refresh gives <c>invalid_grant</c>.</summary>
    private async Task AssertDisconnectedAsync(OAuthFlow flow, Pair pair)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(flow, pair.Access));
        Assert.Equal("invalid_grant", await RefreshErrorAsync(flow, pair.Refresh));
        Assert.Equal(0, await Collection("slots").CountDocumentsAsync(new BsonDocument("connection", flow.ConnectionId), cancellationToken: Ct));
    }

    private async Task<User> AdminAsync()
    {
        var username = "a" + Guid.NewGuid().ToString("N")[..12];
        var (admin, _) = await Service<UserCatalog>().CreateAsync(username, RestClient.Password, Roles.Admin, Ct);
        return admin;
    }

    [Fact]
    public async Task Revoking_in_the_list_is_final()
    {
        var (flow, pair) = await ConnectedAsync();

        using var revoked = await flow.Owner.SendAsync(HttpMethod.Delete, $"/api/connections/{flow.ConnectionId}");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        await AssertDisconnectedAsync(flow, pair);
        Assert.Equal((ConnectionState.Revoked, ConnectionReasons.Revoked), await StateAsync(flow));
        var again = await flow.AgainAsync();
        Assert.Equal("revoked", (await again.ConsentAsync()).Problem);
    }

    [Fact]
    public async Task A_password_change_that_revokes_keys_disconnects_apps()
    {
        var (flow, pair) = await ConnectedAsync();
        var (kept, keptPair) = await ConnectedAsync();
        var users = Service<UserCatalog>();

        Assert.True(await users.ChangePasswordAsync(flow.Owner.User, flow.Owner.UserKey, RestClient.Password, NewPassword, revokeApiKeys: true, Ct));
        Assert.True(await users.ChangePasswordAsync(kept.Owner.User, kept.Owner.UserKey, RestClient.Password, NewPassword, revokeApiKeys: false, Ct));

        await AssertDisconnectedAsync(flow, pair);
        Assert.Equal((ConnectionState.NeedsReconnect, ConnectionReasons.PasswordChanged), await StateAsync(flow));
        // Without revoking the keys, the app stays connected, as the keys do.
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(kept, keptPair.Access));
        using var refreshed = await kept.RefreshAsync(keptPair.Refresh);
        await PairOf(refreshed);
        Assert.Equal((ConnectionState.Connected, (string?)null), await StateAsync(kept));
    }

    [Fact]
    public async Task A_recovery_disconnects_apps()
    {
        var (flow, pair) = await ConnectedAsync();
        var users = Service<UserCatalog>();
        var recoveryKey = (await users.RegenerateRecoveryKeyAsync(flow.Owner.User.Id, flow.Owner.UserKey, RestClient.Password, Ct))!;

        Assert.NotNull(await users.RecoverAsync(flow.Owner.Username, recoveryKey, NewPassword, Ct));

        await AssertDisconnectedAsync(flow, pair);
        Assert.Equal((ConnectionState.NeedsReconnect, ConnectionReasons.Recovery), await StateAsync(flow));
    }

    [Fact]
    public async Task Deactivation_disconnects_apps_and_reactivation_revives_none()
    {
        var (flow, pair) = await ConnectedAsync();
        var admin = await AdminAsync();

        Assert.True(await Service<Admin>().SetActiveAsync(admin, flow.Owner.User.Id, active: false, Ct));

        await AssertDisconnectedAsync(flow, pair);
        Assert.Equal((ConnectionState.NeedsReconnect, ConnectionReasons.Deactivated), await StateAsync(flow));

        Assert.True(await Service<Admin>().SetActiveAsync(admin, flow.Owner.User.Id, active: true, Ct));

        await AssertDisconnectedAsync(flow, pair);
        Assert.Equal((ConnectionState.NeedsReconnect, ConnectionReasons.Deactivated), await StateAsync(flow));
    }

    [Fact]
    public async Task Deleting_a_vault_keeps_a_connection_with_other_vaults()
    {
        var (flow, pair) = await ConnectedAsync(new Dictionary<string, string> { ["Arbeit"] = "write", ["Privat"] = "read" }, privat: true);
        var privat = (await Service<VaultCatalog>().OpenForUserAsync(flow.Owner.User.Id, flow.Owner.UserKey, Ct)).Single(v => v.Name == "Privat").Id;

        Assert.NotNull(await Service<Deletion>().DeleteVaultAsync(flow.Owner.User, flow.Owner.UserKey, "Privat", RestClient.Password, "Privat", Ct));

        var resolved = (await Service<OAuthTokens>().ResolveAccessAsync(pair.Access, flow.ConnectionId, flow.Resource, Ct)).Connection!;
        var opened = await new ConnectionCredential(flow.ConnectionId, resolved.TokenId, pair.Access).OpenWithRightsAsync(Service<VaultCatalog>(), Ct);
        Assert.Equal([("Arbeit", KeyRight.Write)], opened.Select(v => (v.Vault.Name, v.Right)));
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(flow, pair.Access));
        using var refreshed = await flow.RefreshAsync(pair.Refresh);
        Assert.Equal("kumbu:read kumbu:write", (await PairOf(refreshed)).Scope);
        Assert.Equal(0, await Collection("slots").CountDocumentsAsync(new BsonDocument { ["connection"] = flow.ConnectionId, ["vault"] = privat }, cancellationToken: Ct));

        var info = await InfoAsync(flow);
        Assert.Equal(ConnectionState.Connected, info.State);
        Assert.Equal([KeyRight.Write], info.Vaults.Values);
        Assert.DoesNotContain(privat, info.Vaults.Keys);
        var doc = await Collection("connections").Find(new BsonDocument("_id", flow.ConnectionId)).SingleAsync(Ct);
        Assert.DoesNotContain(privat, doc["vaults"].AsBsonArray.Select(v => v.AsString));
        using var listed = await flow.Owner.GetAsync("/api/connections");
        var view = (await RestClient.JsonAsync(listed)).AsArray().Single(c => c!["id"]!.GetValue<string>() == flow.ConnectionId)!;
        Assert.Equal(["Arbeit"], view["vaults"]!.AsObject().Select(v => v.Key));
    }

    /// <summary>
    /// Codes, tokens and connections are made from vault keys or ids read before their transaction; a vault deleted
    /// meanwhile must not end up in a slot or in a connection's vaults. Raced for real, several times: whichever
    /// commits first, the other conflicts or sees it.
    /// </summary>
    [Fact]
    public async Task A_vault_deleted_while_apps_connect_leaves_no_slot_and_no_connection_naming_it()
    {
        var catalog = Service<VaultCatalog>();
        var connections = Service<Connections>();
        for (var round = 0; round < 6; round++)
        {
            var (flow, pair) = await ConnectedAsync(new Dictionary<string, string> { ["Arbeit"] = "write", ["Privat"] = "read" }, privat: true);
            var owner = flow.Owner;
            var vaults = (await catalog.OpenForUserAsync(owner.User.Id, owner.UserKey, Ct)).ToDictionary(v => v.Name, v => v.Id);
            var again = await flow.AgainAsync();
            var both = new Dictionary<string, KeyRight> { [vaults["Arbeit"]] = KeyRight.Read, [vaults["Privat"]] = KeyRight.Read };

            async Task Quietly(Func<Task> action)
            {
                try
                {
                    await action();
                }
                catch (Exception e) when (e is ArgumentException or ConnectionUnavailableException)
                {
                    // Refused because the vault is gone: as it should be.
                }
            }

            await Task.WhenAll(
                Task.Run(() => catalog.DeleteVaultsAsync([vaults["Privat"]], (_, _) => Task.CompletedTask, Ct), Ct),
                Task.Run(async () => { using var _ = await flow.RefreshAsync(pair.Refresh); }, Ct),
                Task.Run(() => Quietly(async () => { using var _ = await again.DecideAsync(allow: true); }), Ct),
                Task.Run(() => Quietly(() => connections.CreateAsync(owner.User, owner.UserKey, DateTime.UtcNow, $"late-{round}", both, Ct)), Ct),
                Task.Run(() => Quietly(() => connections.SetVaultsAsync(owner.User, owner.UserKey, flow.ConnectionId, both, Ct)), Ct));

            Assert.Equal(0, await Collection("slots").CountDocumentsAsync(new BsonDocument("vault", vaults["Privat"]), cancellationToken: Ct));
            Assert.Equal(0, await Collection("connections").CountDocumentsAsync(new BsonDocument("vaults", vaults["Privat"]), cancellationToken: Ct));
            Assert.All(await connections.ListAsync(owner.User, owner.UserKey, Ct), c => Assert.DoesNotContain(vaults["Privat"], c.Vaults.Keys));
        }
    }

    [Fact]
    public async Task Deleting_its_only_vault_revokes_a_connection()
    {
        var (flow, pair) = await ConnectedAsync();

        Assert.NotNull(await Service<Deletion>().DeleteVaultAsync(flow.Owner.User, flow.Owner.UserKey, "Arbeit", RestClient.Password, "Arbeit", Ct));

        await AssertDisconnectedAsync(flow, pair);
        Assert.Equal((ConnectionState.Revoked, ConnectionReasons.VaultDeleted), await StateAsync(flow));
        // Its name is free again.
        using var vault = await flow.Owner.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Neu" });
        Assert.Equal(HttpStatusCode.Created, vault.StatusCode);
        using var created = await flow.Owner.SendAsync(HttpMethod.Post, "/api/connections", new { name = Name, vaults = new Dictionary<string, string> { ["Neu"] = "read" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Deleting_the_account_removes_every_trace()
    {
        var (flow, pair) = await ConnectedAsync();
        // A pending request of the connection, and a second connection never authorised.
        await flow.AgainAsync();
        using var waiting = await flow.Owner.SendAsync(HttpMethod.Post, "/api/connections", new { name = "waiting", vaults = new Dictionary<string, string> { ["Arbeit"] = "read" } });
        Assert.Equal(HttpStatusCode.Created, waiting.StatusCode);
        var ids = (await Collection("connections").Find(new BsonDocument("user", flow.Owner.User.Id)).Project(new BsonDocument("_id", 1)).ToListAsync(Ct)).Select(d => d["_id"]).ToList();
        Assert.Equal(2, ids.Count);
        Assert.NotEqual(0, await Collection("oauth_requests").CountDocumentsAsync(new BsonDocument("connection", flow.ConnectionId), cancellationToken: Ct));

        Assert.True(await Service<Deletion>().DeleteOwnAccountAsync(flow.Owner.User, flow.Owner.UserKey, RestClient.Password, flow.Owner.Username, Ct));

        var ofThese = new BsonDocument("connection", new BsonDocument("$in", new BsonArray(ids)));
        Assert.Equal(0, await Collection("connections").CountDocumentsAsync(new BsonDocument("user", flow.Owner.User.Id), cancellationToken: Ct));
        Assert.Equal(0, await Collection("oauth_tokens").CountDocumentsAsync(ofThese, cancellationToken: Ct));
        Assert.Equal(0, await Collection("oauth_tokens").CountDocumentsAsync(new BsonDocument("user", flow.Owner.User.Id), cancellationToken: Ct));
        Assert.Equal(0, await Collection("oauth_requests").CountDocumentsAsync(ofThese, cancellationToken: Ct));
        Assert.Equal(0, await Collection("slots").CountDocumentsAsync(ofThese, cancellationToken: Ct));
        Assert.Equal(HttpStatusCode.NotFound, await InitializeAsync(flow, pair.Access));
        Assert.Equal("invalid_grant", await RefreshErrorAsync(flow, pair.Refresh));
    }

    [Fact]
    public async Task Changing_rights_disconnects_until_the_next_consent()
    {
        var (flow, pair) = await ConnectedAsync();

        using var changed = await flow.Owner.SendAsync(HttpMethod.Put, $"/api/connections/{flow.ConnectionId}/vaults", new { vaults = new Dictionary<string, string> { ["Arbeit"] = "read" } });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        await AssertDisconnectedAsync(flow, pair);
        Assert.Equal((ConnectionState.NeedsReconnect, ConnectionReasons.RightsChanged), await StateAsync(flow));
        var again = await flow.AgainAsync();
        Assert.Equal(new Dictionary<string, string> { ["Arbeit"] = "read" }, (await again.ConsentAsync()).Connection!.Vaults);
        await again.AllowAsync();
        using var exchanged = await again.TokenAsync();
        var renewed = await PairOf(exchanged);
        Assert.Equal("kumbu:read", renewed.Scope);
        Assert.Equal(HttpStatusCode.OK, await InitializeAsync(flow, renewed.Access));
        Assert.Equal((ConnectionState.Connected, (string?)null), await StateAsync(flow));
    }

    [Fact]
    public async Task An_access_token_in_flight_finishes_but_the_next_is_refused()
    {
        var (flow, pair) = await ConnectedAsync();
        // A request checked before the revocation: its token resolved and its vaults opened, as the middleware does first.
        var resolved = (await Service<OAuthTokens>().ResolveAccessAsync(pair.Access, flow.ConnectionId, flow.Resource, Ct)).Connection!;
        var inFlight = new ConnectionCredential(flow.ConnectionId, resolved.TokenId, pair.Access);
        Assert.Single(await inFlight.OpenWithRightsAsync(Service<VaultCatalog>(), Ct));

        using var revoked = await flow.Owner.SendAsync(HttpMethod.Delete, $"/api/connections/{flow.ConnectionId}");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        // It finishes with what it was checked with ...
        Assert.True(inFlight.CanWrite("Arbeit"));
        Assert.Single(await inFlight.OpenWithRightsAsync(Service<VaultCatalog>(), Ct));
        // ... and the next request checks again.
        var next = await Service<OAuthTokens>().ResolveAccessAsync(pair.Access, flow.ConnectionId, flow.Resource, Ct);
        Assert.Null(next.Connection);
        Assert.True(next.Known);
        Assert.Empty(await new ConnectionCredential(flow.ConnectionId, resolved.TokenId, pair.Access).OpenWithRightsAsync(Service<VaultCatalog>(), Ct));
        Assert.Equal(HttpStatusCode.Unauthorized, await InitializeAsync(flow, pair.Access));
    }
}
