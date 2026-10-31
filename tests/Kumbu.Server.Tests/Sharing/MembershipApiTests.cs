// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Sync;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.OAuth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>A membership over REST (collab design §4.2, §4.3, §6): confirming, declining, withdrawing, revoking, leaving.</summary>
[Collection("app")]
public sealed class MembershipApiTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The time of the timed host, starting at the real time in whole seconds (MongoDB keeps milliseconds).</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program> Timed() => app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    /// <summary>31 minutes without a request: a remembered session's window closes, the session stays.</summary>
    private void CloseWindow() => _time.Advance(TimeSpan.FromMinutes(31));

    private IMongoCollection<SlotDocument> Slots => app.Database.GetCollection<SlotDocument>("slots");

    private Task<long> SlotsAsync(string kind, string membershipId) =>
        Slots.CountDocumentsAsync(s => s.Kind == kind && s.Membership == membershipId, cancellationToken: Ct);

    /// <summary>Alice (remembered) invites Bob to "Gartenarbeit", with <paramref name="confirm"/>; with
    /// <paramref name="accept"/>, Bob accepts as "Garten".</summary>
    private async Task<(RestClient Alice, RestClient Bob, CreatedInvite Invite)> InvitedAsync(WebApplicationFactory<Program> api, bool confirm = true, bool accept = true)
    {
        var alice = await RestClient.NewAsync(api, vault: "Gartenarbeit", remember: true);
        await alice.CreateAsync("plan.md", "# Beete\n");
        var bob = await RestClient.NewAsync(api, vault: "Work", remember: true);
        var memberships = api.Services.GetRequiredService<Memberships>();
        var invite = await memberships.InviteAsync(alice.User.Id, alice.UserKey, await alice.VaultIdAsync(), "Bob", MemberRole.Write, TimeSpan.FromDays(7), confirm, Ct);
        if (accept) await memberships.AcceptAsync(bob.User.Id, bob.UserKey, invite.Secret, "Garten", Ct);
        return (alice, bob, invite);
    }

    private static string Url(RestClient owner, string membershipId, string? action = null) =>
        $"/api/vaults/{Uri.EscapeDataString(owner.Vault)}/memberships/{membershipId}" + (action is null ? "" : "/" + action);

    private static async Task<string[]> VaultNamesAsync(RestClient user)
    {
        using var response = await user.GetAsync("/api/vaults");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await RestClient.JsonAsync(response)).AsArray().Select(v => v!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Confirming_activates_and_shows_the_redeemers_username()
    {
        await using var api = Timed();
        var (alice, bob, invite) = await InvitedAsync(api);
        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == bob.User.Id, Builders<UserDocument>.Update.Set(u => u.Display, "Bob\nExample​"), cancellationToken: Ct);

        // What the owner is asked before granting: who redeemed it, by display name and username.
        using (var before = await alice.GetAsync(Url(alice, invite.MembershipId)))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            var json = await RestClient.JsonAsync(before);
            Assert.Equal(("Bob", "pending", "\"BobExample\"", bob.Username), (
                json["label"]!.GetValue<string>(), json["status"]!.GetValue<string>(), json["redeemer"]!["name"]!.GetValue<string>(), json["redeemer"]!["username"]!.GetValue<string>()));
        }

        Assert.Equal(["Work"], await VaultNamesAsync(bob));
        using var confirmed = await alice.SendAsync(HttpMethod.Post, Url(alice, invite.MembershipId, "confirm"));

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var view = await RestClient.JsonAsync(confirmed);
        Assert.Equal("active", view["status"]!.GetValue<string>());
        // Once granted, who he is was the dialog's only.
        Assert.Null(view["redeemer"]);
        using (var after = await alice.GetAsync(Url(alice, invite.MembershipId))) Assert.Null((await RestClient.JsonAsync(after))["redeemer"]);
        var doc = await Share.MembershipAsync(app.Database, invite.MembershipId);
        Assert.Equal(MembershipStatus.Active, doc.Status);
        Assert.NotNull(doc.AcceptedAt);
        Assert.Equal(["Garten", "Work"], await VaultNamesAsync(bob));
        using (var note = await bob.GetAsync("/api/notes/Garten/plan.md")) Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        // Once: an active membership isn't confirmed again.
        using var again = await alice.SendAsync(HttpMethod.Post, Url(alice, invite.MembershipId, "confirm"));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Confirming_needs_the_password_window()
    {
        await using var api = Timed();
        var (alice, bob, invite) = await InvitedAsync(api);
        CloseWindow();

        using (var refused = await alice.SendAsync(HttpMethod.Post, Url(alice, invite.MembershipId, "confirm")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("password-required", (await RestClient.JsonAsync(refused))["type"]!.GetValue<string>());
        }

        Assert.Equal(MembershipStatus.Pending, (await Share.MembershipAsync(app.Database, invite.MembershipId)).Status);
        using (var confirm = await alice.SendAsync(HttpMethod.Post, "/api/session/confirm", new { password = RestClient.Password })) Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);
        using var confirmed = await alice.SendAsync(HttpMethod.Post, Url(alice, invite.MembershipId, "confirm"));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(["Garten", "Work"], await VaultNamesAsync(bob));
    }

    [Fact]
    public async Task Declining_needs_no_password_window_and_deletes_the_member_slot()
    {
        await using var api = Timed();
        var (alice, bob, invite) = await InvitedAsync(api);
        Assert.Equal(1, await SlotsAsync(SlotDocument.Member, invite.MembershipId));
        CloseWindow();

        using var declined = await alice.SendAsync(HttpMethod.Post, Url(alice, invite.MembershipId, "decline"));

        Assert.Equal(HttpStatusCode.NoContent, declined.StatusCode);
        var doc = await Share.MembershipAsync(app.Database, invite.MembershipId);
        Assert.Equal(MembershipStatus.Revoked, doc.Status);
        Assert.NotNull(doc.RevokedAt);
        Assert.Null(doc.Pin);
        Assert.Equal(0, await SlotsAsync(SlotDocument.Member, invite.MembershipId));
        // And it can't be confirmed afterwards.
        using (var confirm = await alice.SendAsync(HttpMethod.Post, "/api/session/confirm", new { password = RestClient.Password })) Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);
        using var late = await alice.SendAsync(HttpMethod.Post, Url(alice, invite.MembershipId, "confirm"));
        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);
        Assert.Equal(["Work"], await VaultNamesAsync(bob));
    }

    [Fact]
    public async Task Withdrawing_deletes_the_invite_slot()
    {
        await using var api = Timed();
        var (alice, bob, invite) = await InvitedAsync(api, confirm: false, accept: false);
        Assert.Equal(1, await SlotsAsync(SlotDocument.Invite, invite.MembershipId));
        CloseWindow();

        using var withdrawn = await alice.SendAsync(HttpMethod.Delete, Url(alice, invite.MembershipId));

        Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        var doc = await Share.MembershipAsync(app.Database, invite.MembershipId);
        Assert.Equal(MembershipStatus.Revoked, doc.Status);
        Assert.Null(doc.InviteLookup);
        Assert.Null(doc.ExpiresAt);
        Assert.Equal(0, await SlotsAsync(SlotDocument.Invite, invite.MembershipId));
        // The link is dead.
        using var peek = await bob.SendAsync(HttpMethod.Post, "/api/vault-invitations/peek", new { secret = invite.Secret });
        Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
    }

    [Fact]
    public async Task Each_route_moves_only_its_own_memberships()
    {
        await using var api = Timed();
        var (alice, bob, redeemed) = await InvitedAsync(api);
        var open = await api.Services.GetRequiredService<Memberships>().InviteAsync(
            alice.User.Id, alice.UserKey, await alice.VaultIdAsync(), "Carol", MemberRole.Read, TimeSpan.FromDays(7), confirm: false, Ct);
        // Carol's vault, with a pending membership of its own, which Alice's vault's URL doesn't reach.
        var carol = await RestClient.NewAsync(api, vault: "Notizen");
        var foreign = await api.Services.GetRequiredService<Memberships>().InviteAsync(
            carol.User.Id, carol.UserKey, await carol.VaultIdAsync(), "Bob", MemberRole.Read, TimeSpan.FromDays(7), confirm: false, Ct);

        // Withdrawing is for one nobody redeemed, declining for a redeemed one; another vault's is none of Alice's.
        foreach (var (method, url) in new[]
        {
            (HttpMethod.Delete, Url(alice, redeemed.MembershipId)),
            (HttpMethod.Post, Url(alice, open.MembershipId, "decline")),
            (HttpMethod.Post, Url(alice, open.MembershipId, "confirm")),
            (HttpMethod.Delete, Url(alice, foreign.MembershipId)),
            (HttpMethod.Get, Url(alice, foreign.MembershipId)),
        })
        {
            using var response = await alice.SendAsync(method, url);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(MembershipStatus.Pending, (await Share.MembershipAsync(app.Database, redeemed.MembershipId)).Status);
        Assert.Equal(MembershipStatus.Pending, (await Share.MembershipAsync(app.Database, open.MembershipId)).Status);
        Assert.Equal(MembershipStatus.Pending, (await Share.MembershipAsync(app.Database, foreign.MembershipId)).Status);
        // Nor does a member's alias reach the owner's routes.
        using var asMember = await bob.SendAsync(HttpMethod.Delete, $"/api/vaults/Garten/memberships/{open.MembershipId}");
        Assert.Equal(HttpStatusCode.NotFound, asMember.StatusCode);
    }

    [Fact]
    public async Task Without_confirm_the_owner_never_sees_who_redeemed()
    {
        await using var api = Timed();
        var (alice, _, invite) = await InvitedAsync(api, confirm: false);

        using var response = await alice.GetAsync(Url(alice, invite.MembershipId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await RestClient.JsonAsync(response);
        Assert.Equal(("Bob", "active"), (json["label"]!.GetValue<string>(), json["status"]!.GetValue<string>()));
        Assert.Null(json["redeemer"]);
    }

    [Fact]
    public async Task A_declined_person_can_be_invited_again_but_holds_one_live_membership()
    {
        await using var api = Timed();
        var (alice, bob, declined) = await InvitedAsync(api);
        using (var response = await alice.SendAsync(HttpMethod.Post, Url(alice, declined.MembershipId, "decline"))) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var memberships = api.Services.GetRequiredService<Memberships>();
        var vaultId = await alice.VaultIdAsync();

        var again = await memberships.InviteAsync(alice.User.Id, alice.UserKey, vaultId, "Bob", MemberRole.Read, TimeSpan.FromDays(7), confirm: false, Ct);
        var accepted = await memberships.AcceptAsync(bob.User.Id, bob.UserKey, again.Secret, "Garten", Ct);

        Assert.Equal(MembershipStatus.Active, accepted.Status);
        Assert.Equal(["Garten", "Work"], await VaultNamesAsync(bob));
        // The tombstone stays beside it; a second live one doesn't.
        Assert.Equal(MembershipStatus.Revoked, (await Share.MembershipAsync(app.Database, declined.MembershipId)).Status);
        var third = await memberships.InviteAsync(alice.User.Id, alice.UserKey, vaultId, "Bob", MemberRole.Read, TimeSpan.FromDays(7), confirm: false, Ct);
        await Assert.ThrowsAsync<AlreadyMemberException>(() => memberships.AcceptAsync(bob.User.Id, bob.UserKey, third.Secret, "Garten2", Ct));
    }

    private const string Callback = "https://claude.test/cb";

    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""";

    /// <summary>Alice's "Gartenarbeit" (with plan.md), shared with Bob (who has his own "Work") as "Garten", active.</summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId, string MembershipId)> SharedAsync(WebApplicationFactory<Program> api, MemberRole role = MemberRole.Write)
    {
        var alice = await RestClient.NewAsync(api, vault: "Gartenarbeit", remember: true);
        await alice.CreateAsync("plan.md", "# Beete\n");
        var bob = await RestClient.NewAsync(api, vault: "Work", remember: true);
        var membership = await alice.ShareWithAsync(bob, "Garten", role, label: "Bob");
        return (alice, bob, await alice.VaultIdAsync(), membership);
    }

    private static async Task<(string Key, ApiKeyInfo Info)> KeyAsync(WebApplicationFactory<Program> api, RestClient user, string name, params string[] vaultIds) =>
        await api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            user.User, user.UserKey, DateTime.UtcNow, name, vaultIds.ToDictionary(v => v, _ => KeyRight.Write), Ct);

    private static string Revoke(RestClient owner, string membershipId) => Url(owner, membershipId, "revoke");

    /// <summary>A connection of <paramref name="user"/> on <paramref name="vaultIds"/>, authorised, and its access token.</summary>
    private static async Task<(string Id, string Access)> ConnectionAsync(WebApplicationFactory<Program> api, RestClient user, params string[] vaultIds)
    {
        var connection = await api.Services.GetRequiredService<Connections>().CreateAsync(
            user.User, user.UserKey, DateTime.UtcNow, "claude", vaultIds.ToDictionary(v => v, _ => KeyRight.Write), Ct);
        var flow = await OAuthFlow.StartAsync(api, user, connection.Id, await OAuthFlow.RegisterAsync(api, Callback), Callback);
        await flow.AllowAsync();
        using var exchanged = await flow.TokenAsync();
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        return (connection.Id, (await RestClient.JsonAsync(exchanged))["access_token"]!.GetValue<string>());
    }

    private static async Task<HttpResponseMessage> InitializeAsync(WebApplicationFactory<Program> api, string connectionId, string bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/mcp/c/{connectionId}") { Content = new StringContent(Initialize, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Forwarded-For", $"198.26.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });
        return await client.SendAsync(request, Ct);
    }

    /// <summary>A sync socket of <paramref name="key"/>.</summary>
    private static async Task<WebSocket> SocketAsync(WebApplicationFactory<Program> api, RestClient user, string key)
    {
        using var response = await user.SendAsync(HttpMethod.Post, "/api/ws-ticket", bearer: key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ticket = (await RestClient.JsonAsync(response))["ticket"]!.GetValue<string>();
        return await api.Server.CreateWebSocketClient().ConnectAsync(new Uri($"wss://kumbu.test/api/ws?ticket={Uri.EscapeDataString(ticket)}"), Ct);
    }

    /// <summary>The next message on the socket, "close N" for a close, or null if none comes within <paramref name="wait"/>.</summary>
    private static async Task<string?> ReceiveAsync(WebSocket socket, TimeSpan wait)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(wait);
        var buffer = new byte[4096];
        try
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            return result.MessageType == WebSocketMessageType.Close ? $"close {(int?)result.CloseStatus}" : Encoding.UTF8.GetString(buffer, 0, result.Count);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private Task<ApiKeyDocument> KeyDocAsync(string keyId) =>
        app.Database.GetCollection<ApiKeyDocument>(ApiKeys.CollectionName).Find(k => k.Id == keyId).SingleAsync(Ct);

    private Task<ConnectionDocument> ConnectionDocAsync(string id) =>
        app.Database.GetCollection<ConnectionDocument>(Connections.CollectionName).Find(c => c.Id == id).SingleAsync(Ct);

    [Fact]
    public async Task Revoking_ends_every_access_at_once()
    {
        var api = app.PublicApi;
        var (alice, bob, vaultId, membershipId) = await SharedAsync(api);
        var (key, _) = await KeyAsync(api, bob, "Laptop", vaultId);
        var (connection, access) = await ConnectionAsync(api, bob, vaultId);
        using (var note = await bob.GetAsync("/api/notes/Garten/plan.md")) Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        using (var before = await InitializeAsync(api, connection, access)) Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using var socket = await SocketAsync(api, bob, key);
        var revision = (await Share.MembershipAsync(app.Database, membershipId)).Revision;

        using var revoked = await alice.SendAsync(HttpMethod.Post, Revoke(alice, membershipId));

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        // The socket closes with the request, not at the hub's 30-second check.
        Assert.Equal($"close {(int)SyncHub.KeyInactive}", await ReceiveAsync(socket, TimeSpan.FromSeconds(3)));
        using (var note = await bob.GetAsync("/api/notes/Garten/plan.md")) Assert.Equal(HttpStatusCode.NotFound, note.StatusCode);
        Assert.Equal(["Work"], await VaultNamesAsync(bob));
        // The key had no other vault: revoked by the cleanup.
        using (var byKey = await bob.GetAsync("/api/vaults", key)) Assert.Equal(HttpStatusCode.Unauthorized, byKey.StatusCode);
        using (var after = await InitializeAsync(api, connection, access)) Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        var doc = await Share.MembershipAsync(app.Database, membershipId);
        Assert.Equal((MembershipStatus.Revoked, revision + 1), (doc.Status, doc.Revision));
        Assert.NotNull(doc.RevokedAt);
        Assert.Null(doc.Pin);
        Assert.NotNull(doc.CleanedAt);
        Assert.False(await Slots.Find(s => s.Vault == vaultId && (s.Membership == membershipId || s.GrantedVia == membershipId)).AnyAsync(Ct));
        Assert.Equal(ConnectionReasons.SharingEnded, (await ConnectionDocAsync(connection)).RevokedReason);
        // Once: a tombstone isn't revoked again.
        using var again = await alice.SendAsync(HttpMethod.Post, Revoke(alice, membershipId));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Revoking_revokes_Bobs_keys_left_with_no_vault_and_keeps_the_others()
    {
        var api = app.PublicApi;
        var (alice, bob, vaultId, membershipId) = await SharedAsync(api);
        var carol = await RestClient.NewAsync(api, vault: "Notizen");
        await carol.ShareWithAsync(bob, "Notizen");
        var carols = await carol.VaultIdAsync();
        var own = await bob.VaultIdAsync();
        var (_, only) = await KeyAsync(api, bob, "only", vaultId);
        var (_, withOwn) = await KeyAsync(api, bob, "with-own", vaultId, own);
        var (_, withCarols) = await KeyAsync(api, bob, "with-carols", vaultId, carols);
        var (bobsConnection, _) = await ConnectionAsync(api, bob, vaultId, own);
        var (_, alicesKey) = await KeyAsync(api, alice, "Alice's", vaultId);
        var (alicesConnection, _) = await ConnectionAsync(api, alice, vaultId);

        using var revoked = await alice.SendAsync(HttpMethod.Post, Revoke(alice, membershipId));

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.NotNull((await KeyDocAsync(only.Id)).RevokedAt);
        // A slot on another shared vault is a vault as much as one on his own.
        Assert.Null((await KeyDocAsync(withOwn.Id)).RevokedAt);
        Assert.Null((await KeyDocAsync(withCarols.Id)).RevokedAt);
        Assert.Equal([own], (await Slots.Find(s => s.KeyId == withOwn.Id).ToListAsync(Ct)).Select(s => s.Vault));
        Assert.Equal([carols], (await Slots.Find(s => s.KeyId == withCarols.Id).ToListAsync(Ct)).Select(s => s.Vault));
        var bobs = await ConnectionDocAsync(bobsConnection);
        Assert.Equal([own], bobs.Vaults);
        Assert.Null(bobs.RevokedAt);
        // The owner's own credentials on the vault are hers, untouched.
        Assert.Null((await KeyDocAsync(alicesKey.Id)).RevokedAt);
        Assert.True(await Slots.Find(s => s.KeyId == alicesKey.Id && s.Vault == vaultId).AnyAsync(Ct));
        var alices = await ConnectionDocAsync(alicesConnection);
        Assert.Equal([vaultId], alices.Vaults);
        Assert.Null(alices.RevokedAt);
    }

    [Fact]
    public async Task Leaving_is_revoking_by_the_member()
    {
        var api = app.PublicApi;
        var (alice, bob, vaultId, membershipId) = await SharedAsync(api);
        var (_, only) = await KeyAsync(api, bob, "Laptop", vaultId);
        var revision = (await Share.MembershipAsync(app.Database, membershipId)).Revision;

        // Not through the owner's routes: her vault isn't his.
        using (var asOwner = await bob.SendAsync(HttpMethod.Post, $"/api/vaults/Garten/memberships/{membershipId}/revoke")) Assert.Equal(HttpStatusCode.NotFound, asOwner.StatusCode);
        using var left = await bob.SendAsync(HttpMethod.Post, $"/api/memberships/{membershipId}/leave");

        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        var doc = await Share.MembershipAsync(app.Database, membershipId);
        Assert.Equal((MembershipStatus.Left, revision + 1), (doc.Status, doc.Revision));
        Assert.NotNull(doc.RevokedAt);
        Assert.Null(doc.Pin);
        Assert.Equal(["Work"], await VaultNamesAsync(bob));
        Assert.NotNull((await KeyDocAsync(only.Id)).RevokedAt);
        Assert.False(await Slots.Find(s => s.Membership == membershipId || s.GrantedVia == membershipId).AnyAsync(Ct));
        // Alice can't leave for him, nor he twice.
        using (var again = await bob.SendAsync(HttpMethod.Post, $"/api/memberships/{membershipId}/leave")) Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        using (var notTheOwners = await alice.SendAsync(HttpMethod.Post, Revoke(alice, membershipId))) Assert.Equal(HttpStatusCode.NotFound, notTheOwners.StatusCode);
    }

    [Fact]
    public async Task A_failed_cleanup_is_finished_by_the_sweep()
    {
        var api = app.PublicApi;
        var (alice, bob, vaultId, membershipId) = await SharedAsync(api);
        var (_, only) = await KeyAsync(api, bob, "Laptop", vaultId);
        var memberships = api.Services.GetRequiredService<Memberships>();
        memberships.FaultAt = point => point == "inside-cleanup" ? throw new InvalidOperationException("crash") : Task.CompletedTask;
        try
        {
            using var revoked = await alice.SendAsync(HttpMethod.Post, Revoke(alice, membershipId));
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        }
        finally
        {
            memberships.FaultAt = null;
        }

        // The atomic step stands, the cleanup didn't commit: the slots are there, and open nothing.
        var doc = await Share.MembershipAsync(app.Database, membershipId);
        Assert.Equal((MembershipStatus.Revoked, null), (doc.Status, doc.CleanedAt));
        // The pin goes with the atomic step itself: a tombstone flipped back to active has no key to wrap to.
        Assert.Null(doc.Pin);
        Assert.Equal(1, await SlotsAsync(SlotDocument.Member, membershipId));
        Assert.Null((await KeyDocAsync(only.Id)).RevokedAt);
        Assert.Equal(["Work"], await VaultNamesAsync(bob));

        await api.Services.GetRequiredService<MembershipSweeper>().SweepAsync(Ct);

        Assert.NotNull((await Share.MembershipAsync(app.Database, membershipId)).CleanedAt);
        Assert.Equal(0, await SlotsAsync(SlotDocument.Member, membershipId));
        Assert.False(await Slots.Find(s => s.GrantedVia == membershipId).AnyAsync(Ct));
        Assert.NotNull((await KeyDocAsync(only.Id)).RevokedAt);
    }

    [Fact]
    public async Task The_tombstone_keeps_the_owners_view_of_Bob()
    {
        var api = app.PublicApi;
        var (alice, bob, vaultId, membershipId) = await SharedAsync(api);
        var (key, info) = await KeyAsync(api, bob, "Claude", vaultId);
        using (var written = await bob.SendAsync(HttpMethod.Put, "/api/notes/Garten/k.md", new { content = "Kohl\n" }, bearer: key)) Assert.Equal(HttpStatusCode.Created, written.StatusCode);

        using var revoked = await alice.SendAsync(HttpMethod.Post, Revoke(alice, membershipId));

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        var doc = await Share.MembershipAsync(app.Database, membershipId);
        var vaultKey = await alice.VaultKeyAsync();
        Assert.Equal(new Dictionary<string, string> { [info.Id] = "Claude" }, Memberships.OpenWriters(doc, vaultKey));
        using var note = await alice.GetAsync("/api/notes/Gartenarbeit/k.md");
        Assert.Equal("Bob (removed) · Claude", (await RestClient.JsonAsync(note))["lastWriter"]!.GetValue<string>());
        using var view = await alice.GetAsync(Url(alice, membershipId));
        var json = await RestClient.JsonAsync(view);
        Assert.Equal(("Bob", "revoked"), (json["label"]!.GetValue<string>(), json["status"]!.GetValue<string>()));
    }

    private static Task<HttpResponseMessage> RoleAsync(RestClient owner, string membershipId, string role) =>
        owner.SendAsync(HttpMethod.Put, Url(owner, membershipId, "role"), new { role });

    [Fact]
    public async Task Raising_a_role_needs_the_password_window()
    {
        await using var api = Timed();
        var (alice, _, _, membershipId) = await SharedAsync(api, MemberRole.Read);
        var revision = (await Share.MembershipAsync(app.Database, membershipId)).Revision;
        CloseWindow();

        using (var refused = await RoleAsync(alice, membershipId, "maintain"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("password-required", (await RestClient.JsonAsync(refused))["type"]!.GetValue<string>());
        }

        var unchanged = await Share.MembershipAsync(app.Database, membershipId);
        Assert.Equal(("read", revision, null), (unchanged.Role, unchanged.Revision, unchanged.RoleChangedAt));
        Assert.Empty(await VaultEvents.OfMemberAsync(app.Database, unchanged.Vault, unchanged.Member!, Ct));
        using (var confirm = await alice.SendAsync(HttpMethod.Post, "/api/session/confirm", new { password = RestClient.Password })) Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);
        using var raised = await RoleAsync(alice, membershipId, "maintain");
        Assert.Equal(HttpStatusCode.OK, raised.StatusCode);
        Assert.Equal("maintain", (await RestClient.JsonAsync(raised))["role"]!.GetValue<string>());
        var doc = await Share.MembershipAsync(app.Database, membershipId);
        Assert.Equal(("maintain", revision + 1), (doc.Role, doc.Revision));
        Assert.NotNull(doc.RoleChangedAt);
    }

    [Fact]
    public async Task Lowering_a_role_needs_none_and_caps_Bobs_key_at_once()
    {
        await using var api = Timed();
        var (alice, bob, vaultId, membershipId) = await SharedAsync(api, MemberRole.Write);
        var (key, _) = await api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Maintain }, Ct);
        using (var written = await bob.SendAsync(HttpMethod.Put, "/api/notes/Garten/a.md", new { content = "Beet\n" }, bearer: key)) Assert.Equal(HttpStatusCode.Created, written.StatusCode);
        CloseWindow();

        using var lowered = await RoleAsync(alice, membershipId, "read");

        Assert.Equal(HttpStatusCode.OK, lowered.StatusCode);
        Assert.Equal("read", (await Share.MembershipAsync(app.Database, membershipId)).Role);
        using var refused = await bob.SendAsync(HttpMethod.Put, "/api/notes/Garten/b.md", new { content = "Beet\n" }, bearer: key);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("read-only", (await RestClient.JsonAsync(refused))["code"]!.GetValue<string>());
        using (var read = await bob.GetAsync("/api/notes/Garten/a.md", key)) Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task A_role_change_is_an_event_Bob_sees()
    {
        var api = app.PublicApi;
        var (alice, bob, vaultId, membershipId) = await SharedAsync(api, MemberRole.Read);
        var carol = await RestClient.NewAsync(api, vault: "Notizen");
        await alice.ShareWithAsync(carol, "Garten");

        using (var raised = await RoleAsync(alice, membershipId, "write")) Assert.Equal(HttpStatusCode.OK, raised.StatusCode);
        // The same role again changes nothing and records nothing.
        using (var same = await RoleAsync(alice, membershipId, "write")) Assert.Equal(HttpStatusCode.OK, same.StatusCode);

        var seen = Assert.Single(await VaultEvents.OfMemberAsync(app.Database, vaultId, bob.User.Id, Ct));
        Assert.Equal((VaultEvents.RoleChanged, membershipId, "write"), (seen.Kind, seen.Membership, seen.Role));
        Assert.Empty(await VaultEvents.OfMemberAsync(app.Database, vaultId, carol.User.Id, Ct));
        // Ids, a kind, a time and a role: nothing that names anyone.
        var raw = (await app.Database.GetCollection<BsonDocument>(VaultEvents.CollectionName).Find(new BsonDocument("membership", membershipId)).SingleAsync(Ct)).ToJson();
        Assert.DoesNotContain("Bob", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(bob.Username, raw, StringComparison.Ordinal);
    }
}
