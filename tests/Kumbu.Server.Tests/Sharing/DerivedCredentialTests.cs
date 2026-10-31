// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.OAuth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// A member's own API keys, devices and connections on a shared vault (collab design §3.3, §3.4): their slots are
/// derived through the membership (<c>grantedVia</c>), created in a transaction that also writes the membership, so a
/// revocation committed first refuses them, and the membership records each credential's display name, sealed.
/// </summary>
[Collection("app")]
public sealed class DerivedCredentialTests(KumbuAppFactory app)
{
    private const string Callback = "https://claude.test/cb";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private IMongoCollection<SlotDocument> Slots => app.Database.GetCollection<SlotDocument>("slots");

    /// <summary>Alice's "Gartenarbeit", shared with Bob (who has his own "Work") as "garten".</summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId, string MembershipId)> SharedAsync(MemberRole role = MemberRole.Write)
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var membership = await alice.ShareWithAsync(bob, "garten", role);
        return (alice, bob, await alice.VaultIdAsync(), membership);
    }

    private Task<(string Key, ApiKeyInfo Info)> KeyAsync(RestClient user, string name, IReadOnlyDictionary<string, KeyRight> vaults) =>
        Api.Services.GetRequiredService<ApiKeys>().CreateAsync(user.User, user.UserKey, DateTime.UtcNow, name, vaults, Ct);

    [Fact]
    public async Task A_member_may_give_a_key_the_shared_vault()
    {
        var (alice, bob, vaultId, membershipId) = await SharedAsync(MemberRole.Write);
        var own = await bob.VaultIdAsync();

        // Maintain, above his role: the effective level is worked out per request.
        var (_, info) = await KeyAsync(bob, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Maintain, [own] = KeyRight.Write });
        var (_, owners) = await KeyAsync(alice, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write });

        var slots = await Slots.Find(s => s.KeyId == info.Id).ToListAsync(Ct);
        Assert.Equal(membershipId, slots.Single(s => s.Vault == vaultId).GrantedVia);
        Assert.Null(slots.Single(s => s.Vault == own).GrantedVia);
        Assert.Null((await Slots.Find(s => s.KeyId == owners.Id).SingleAsync(Ct)).GrantedVia);
        Assert.Equal(KeyRight.Maintain, info.Vaults[vaultId]);
        Assert.Equal(1, (await Share.MembershipAsync(app.Database, membershipId)).Revision);
    }

    [Fact]
    public async Task A_key_created_while_the_membership_is_revoked_fails()
    {
        var (_, bob, vaultId, membershipId) = await SharedAsync();
        var catalog = Api.Services.GetRequiredService<VaultCatalog>();
        // Revoked after the slot was wrapped from the member slot (still active then), before the key's transaction.
        catalog.IssueFaultAt = async point =>
        {
            if (point == "after-wrap") await Share.SetStatusAsync(app.Database, membershipId, MembershipStatus.Revoked);
        };
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => KeyAsync(bob, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }));
        }
        finally
        {
            catalog.IssueFaultAt = null;
        }

        Assert.False(await Slots.Find(s => s.Vault == vaultId && s.Kind == SlotDocument.ApiKey).AnyAsync(Ct));
        Assert.False(await app.Database.GetCollection<ApiKeyDocument>(ApiKeys.CollectionName).Find(k => k.User == bob.User.Id).AnyAsync(Ct));
        Assert.Equal(0, (await Share.MembershipAsync(app.Database, membershipId)).Revision);
    }

    [Fact]
    public async Task The_membership_records_the_credentials_display_name_sealed()
    {
        var (alice, bob, vaultId, membershipId) = await SharedAsync();

        var (_, info) = await KeyAsync(bob, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write });

        var doc = await Share.MembershipAsync(app.Database, membershipId);
        var vaultKey = await alice.VaultKeyAsync();
        Assert.Equal(new Dictionary<string, string> { [info.Id] = "Claude" }, Memberships.OpenWriters(doc, vaultKey));
        // Beside the one her sessions write under, from the grant (task 1.11).
        var writerIds = Memberships.OpenWriterIds(doc, vaultKey);
        Assert.Equal(new HashSet<string> { Memberships.SessionWriter, info.Id }, writerIds.Values.ToHashSet());
        var writerId = writerIds.Single(w => w.Value == info.Id);
        Assert.NotEqual(writerIds.Single(w => w.Value == Memberships.SessionWriter).Key, writerId.Key);
        Assert.Matches("^[0-9a-f]{32}$", writerId.Key);
        // Only sealed: the raw document names neither the credential nor its writer id.
        var raw = (await app.Database.GetCollection<BsonDocument>(Memberships.CollectionName).Find(new BsonDocument("_id", membershipId)).SingleAsync(Ct)).ToJson();
        Assert.DoesNotContain("Claude", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(writerId.Key, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_is_recorded_by_its_device_name_only()
    {
        var (alice, bob, vaultId, membershipId) = await SharedAsync();
        var pairings = Api.Services.GetRequiredService<Pairings>();

        var code = pairings.Create(bob.User, (byte[])bob.UserKey.Clone(), DateTime.UtcNow, vaultId, "garten", null);
        var redeemed = await pairings.RedeemAsync(code.Code, "Laptop", Ct);

        Assert.Equal("garten – Obsidian Laptop", redeemed!.KeyName);
        var slot = await Slots.Find(s => s.Vault == vaultId && s.Kind == SlotDocument.ApiKey).SingleAsync(Ct);
        Assert.Equal(membershipId, slot.GrantedVia);
        var writers = Memberships.OpenWriters(await Share.MembershipAsync(app.Database, membershipId), await alice.VaultKeyAsync());
        Assert.Equal(new Dictionary<string, string> { [slot.KeyId!] = "Laptop" }, writers);
    }

    [Fact]
    public async Task A_member_connection_gets_GrantedVia_slots()
    {
        var (alice, bob, vaultId, membershipId) = await SharedAsync();
        var own = await bob.VaultIdAsync();
        var connection = await Api.Services.GetRequiredService<Connections>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write, [own] = KeyRight.Write }, Ct);

        var flow = await OAuthFlow.StartAsync(Api, bob, connection.Id, await OAuthFlow.RegisterAsync(Api, Callback), Callback);
        await flow.AllowAsync();
        var codeSlots = await Slots.Find(s => s.ConnectionId == connection.Id).ToListAsync(Ct);
        Assert.Equal(membershipId, codeSlots.Single(s => s.Vault == vaultId).GrantedVia);
        Assert.Null(codeSlots.Single(s => s.Vault == own).GrantedVia);

        // The access and refresh tokens' slots, wrapped again from the code's, carry it on.
        using var exchanged = await flow.TokenAsync();
        Assert.Equal(HttpStatusCode.OK, exchanged.StatusCode);
        var tokenSlots = await Slots.Find(s => s.ConnectionId == connection.Id).ToListAsync(Ct);
        Assert.Equal(4, tokenSlots.Count);
        Assert.All(tokenSlots, s => Assert.Equal(s.Vault == vaultId ? membershipId : null, s.GrantedVia));

        var writers = Memberships.OpenWriters(await Share.MembershipAsync(app.Database, membershipId), await alice.VaultKeyAsync());
        Assert.Equal(new Dictionary<string, string> { [connection.Id] = "claude" }, writers);
    }

    [Fact]
    public async Task A_derived_slot_records_its_last_use()
    {
        var (_, bob, vaultId, _) = await SharedAsync();
        var own = await bob.VaultIdAsync();
        var (key, info) = await KeyAsync(bob, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write, [own] = KeyRight.Write });

        var (failed, text) = await bob.ShAsync("ls /", key);
        Assert.False(failed, text);

        // Written beside the request, not in it.
        SlotDocument derived;
        var waited = 0;
        while ((derived = await Slots.Find(s => s.KeyId == info.Id && s.Vault == vaultId).SingleAsync(Ct)).UsedAt is null && waited++ < 50) await Task.Delay(100, Ct);
        Assert.NotNull(derived.UsedAt);
        Assert.Null((await Slots.Find(s => s.KeyId == info.Id && s.Vault == own).SingleAsync(Ct)).UsedAt);
    }
}
