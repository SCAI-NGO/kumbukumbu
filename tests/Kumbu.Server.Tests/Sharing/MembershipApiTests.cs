// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>The owner's side of a membership over REST (collab design §4.2, §4.3): confirming, declining, withdrawing.</summary>
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
}
