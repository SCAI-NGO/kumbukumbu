// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// A member's WebDAV shares on a shared vault (WebDAV design §3.2, §4), and the owner's own around a re-keying or a
/// deletion: a member's share is derived through her membership as her API keys are, opens at most at her role, ends
/// with the membership, shows on the owner's member panel and writes under a writer id of its own; a re-keying or a
/// deletion takes what it takes from a share as it does from an API key.
/// </summary>
[Collection("app")]
public sealed class DerivedShareTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    private IMongoCollection<SlotDocument> Slots => app.Database.GetCollection<SlotDocument>("slots");

    /// <summary>Alice's "Gartenarbeit", shared with Bob (who has his own "Work") as "garten".</summary>
    private async Task<(RestClient Alice, RestClient Bob, string VaultId, string MembershipId)> SharedAsync(MemberRole role = MemberRole.Write)
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "# Beete\n");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        var membership = await alice.ShareWithAsync(bob, "garten", role);
        return (alice, bob, await alice.VaultIdAsync(), membership);
    }

    /// <summary>A share of <paramref name="user"/>: its id and its password, normalised as the server reads it.</summary>
    private async Task<(string Id, string Token)> ShareAsync(RestClient user, string label, IReadOnlyDictionary<string, KeyRight> vaults)
    {
        var (_, password, info) = await Api.Services.GetRequiredService<DavShares>().CreateAsync(user.User, user.UserKey, DateTime.UtcNow, label, vaults, Ct);
        return (info.Id, DavTokens.Normalise(password)!);
    }

    private async Task<string> SecondVaultAsync(RestClient user, string name)
    {
        await Catalog.CreateForUserAsync(name, user.User.Id, user.UserKey, Ct);
        return (await Catalog.FindForUserAsync(user.User.Id, user.UserKey, name, Ct))!;
    }

    private Task<DavShareDocument> ShareDocAsync(string shareId) =>
        app.Database.GetCollection<DavShareDocument>(DavShares.CollectionName).Find(d => d.Id == shareId).SingleAsync(Ct);

    private async Task<string[]> SlotVaultsAsync(string shareId) =>
        [.. (await Slots.Find(s => s.Kind == SlotDocument.Dav && s.ShareId == shareId).ToListAsync(Ct)).Select(s => s.Vault).Order(StringComparer.Ordinal)];

    private async Task<string[]> OpenedAsync((string Id, string Token) share) =>
        [.. (await Catalog.OpenForDavShareAsync(share.Id, share.Token, Ct)).Select(a => a.Vault.Id).Order(StringComparer.Ordinal)];

    /// <summary>A run of the owner's vault that stops right after its start, which is where credentials lose it.</summary>
    private async Task StopAfterStartAsync(RestClient owner, string vaultId)
    {
        Catalog.RekeyFaultAt = p => p == "after-start" ? throw new InvalidOperationException("crash after the start") : Task.CompletedTask;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, Ct));
        }
        finally
        {
            Catalog.RekeyFaultAt = null;
        }
    }

    [Fact]
    public async Task A_member_share_opens_at_most_at_the_role()
    {
        var (_, bob, vaultId, membershipId) = await SharedAsync(MemberRole.Read);

        var share = await ShareAsync(bob, "Garden PC", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write });

        Assert.Equal(membershipId, (await Slots.Find(s => s.Kind == SlotDocument.Dav && s.ShareId == share.Id).SingleAsync(Ct)).GrantedVia);
        var access = Assert.Single(await Catalog.OpenForDavShareAsync(share.Id, share.Token, Ct));
        Assert.Equal(AccessLevel.Read, access.Level);
        Assert.False(access.IsOwner);
        Assert.Equal("garten", access.Name);
    }

    [Fact]
    public async Task Ending_the_membership_ends_the_shares_access()
    {
        var (alice, bob, vaultId, membershipId) = await SharedAsync();
        var work = await bob.VaultIdAsync();
        var both = await ShareAsync(bob, "Laptop", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write, [work] = KeyRight.Write });
        var only = await ShareAsync(bob, "Garden PC", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write });
        var memberships = Api.Services.GetRequiredService<Memberships>();

        await memberships.RevokeAsync(vaultId, membershipId, Ct);

        // From the revocation's commit, before any cleanup: the membership check skips the vault.
        Assert.Equal([work], await OpenedAsync(both));
        Assert.Empty(await OpenedAsync(only));

        await memberships.CleanupAsync(membershipId, Ct);

        // The slots through the membership are gone; a share left with no vault is revoked, the other keeps its own.
        Assert.Equal([work], await SlotVaultsAsync(both.Id));
        Assert.Null((await ShareDocAsync(both.Id)).RevokedAt);
        Assert.Empty(await SlotVaultsAsync(only.Id));
        var revoked = await ShareDocAsync(only.Id);
        Assert.NotNull(revoked.RevokedAt);
        Assert.Null(revoked.NameId);
        // The tombstone keeps its writers, as for keys: the owner's history still names the share's writes.
        var writers = Memberships.OpenWriters(await Share.MembershipAsync(app.Database, membershipId), await alice.VaultKeyAsync());
        Assert.Equal("Garden PC (dav)", writers[DavShares.CredentialId(only.Id)]);
    }

    [Fact]
    public async Task The_owners_member_panel_lists_the_share()
    {
        var (alice, bob, vaultId, _) = await SharedAsync();
        await ShareAsync(bob, "Garden PC", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write });
        await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, Ct);

        using var response = await alice.GetAsync("/api/vaults/Gartenarbeit/access");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var credentials = (await RestClient.JsonAsync(response))["members"]!.AsArray().Single()!["credentials"]!.AsArray()
            .Select(c => (c!["name"]!.GetValue<string>(), c["kind"]!.GetValue<string>())).ToList();

        Assert.Equal([("Claude", MemberCredentialKinds.Key), ("Garden PC (dav)", MemberCredentialKinds.Share)], credentials);
        Assert.Equal("share", MemberCredentialKinds.Share);
    }

    [Fact]
    public async Task Rekeying_revokes_the_owners_share_whole()
    {
        var (alice, _, vaultId, _) = await SharedAsync();
        var privat = await SecondVaultAsync(alice, "Privat");
        var both = await ShareAsync(alice, "Laptop", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write, [privat] = KeyRight.Read });
        var elsewhere = await ShareAsync(alice, "Desktop", new Dictionary<string, KeyRight> { [privat] = KeyRight.Write });

        await Catalog.RekeyAsync(vaultId, alice.User.Id, alice.UserKey, Ct);

        // The server can't seal the new key for a share without its password: it goes whole, its other vault too.
        Assert.NotNull((await ShareDocAsync(both.Id)).RevokedAt);
        Assert.Null((await ShareDocAsync(both.Id)).NameId);
        Assert.Empty(await SlotVaultsAsync(both.Id));
        Assert.Empty(await OpenedAsync(both));
        // A share without a slot on the vault is not touched.
        Assert.Null((await ShareDocAsync(elsewhere.Id)).RevokedAt);
        Assert.Equal([privat], await OpenedAsync(elsewhere));
    }

    [Fact]
    public async Task Rekeying_takes_the_vault_from_a_members_share_and_revokes_it_if_none_is_left()
    {
        var (alice, bob, vaultId, _) = await SharedAsync();
        var work = await bob.VaultIdAsync();
        var both = await ShareAsync(bob, "Laptop", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write, [work] = KeyRight.Write });
        var only = await ShareAsync(bob, "Garden PC", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Read });

        await StopAfterStartAsync(alice, vaultId);

        // Only the slots on this vault go: the owner's re-key doesn't reach into Bob's own vault.
        Assert.Null((await ShareDocAsync(both.Id)).RevokedAt);
        Assert.Equal([work], await SlotVaultsAsync(both.Id));
        Assert.Equal([work], await OpenedAsync(both));
        Assert.NotNull((await ShareDocAsync(only.Id)).RevokedAt);
        Assert.Empty(await SlotVaultsAsync(only.Id));
        // Both are among what the run ended, under an id that can't meet a key's, as Bob's keys are.
        var vault = await app.Database.GetCollection<VaultDocument>("vaults").Find(v => v.Id == vaultId).SingleAsync(Ct);
        Assert.Contains(DavShares.CredentialId(both.Id), vault.RekeyEnded!);
        Assert.Contains(DavShares.CredentialId(only.Id), vault.RekeyEnded!);

        // Resumed and finished, Bob's share has not come back to the vault.
        await Catalog.RekeyAsync(vaultId, alice.User.Id, alice.UserKey, Ct);
        Assert.Equal([work], await OpenedAsync(both));
    }

    [Fact]
    public async Task Re_encrypting_revokes_a_members_share_even_when_revoking_the_keys_fails()
    {
        var (_, bob, vaultId, _) = await SharedAsync();
        var share = await ShareAsync(bob, "Garden PC", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write });
        var rekeying = Api.Services.GetRequiredService<Rekeying>();
        rekeying.RevokeFault = () => throw new InvalidOperationException("revocation failed");
        try
        {
            using var response = await bob.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password = RestClient.Password });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            rekeying.RevokeFault = null;
        }

        // No re-keying of Bob's own vaults reaches his share's slot on Alice's: only the revocation does.
        Assert.NotNull((await ShareDocAsync(share.Id)).RevokedAt);
        Assert.Empty(await OpenedAsync(share));
    }

    [Fact]
    public async Task The_rekey_preview_lists_shares()
    {
        var (alice, bob, vaultId, _) = await SharedAsync();
        var privat = await SecondVaultAsync(alice, "Privat");
        await ShareAsync(alice, "Laptop", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write });
        await ShareAsync(alice, "Desktop", new Dictionary<string, KeyRight> { [privat] = KeyRight.Write });
        var gone = await ShareAsync(alice, "Old PC", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write });
        Assert.True(await Api.Services.GetRequiredService<DavShares>().RevokeAsync(alice.User, gone.Id, Ct));
        await ShareAsync(bob, "Garden PC", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write });

        var (_, _, shareIds) = await Catalog.AccessAsync(vaultId, Ct);
        using var response = await alice.GetAsync("/api/vaults/Gartenarbeit/access");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await RestClient.JsonAsync(response);

        // Her own shares that would lose access; Bob's is on his member panel, not among hers.
        Assert.Equal(2, shareIds.Count);
        Assert.Equal(["Laptop"], view["shares"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["Garden PC (dav)"], view["members"]!.AsArray().Single()!["credentials"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Deleting_the_vault_revokes_a_share_left_without_vaults()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        var garten = await alice.VaultIdAsync();
        var privat = await SecondVaultAsync(alice, "Privat");
        var both = await ShareAsync(alice, "Laptop", new Dictionary<string, KeyRight> { [garten] = KeyRight.Write, [privat] = KeyRight.Write });
        var only = await ShareAsync(alice, "Desktop", new Dictionary<string, KeyRight> { [privat] = KeyRight.Write });

        Assert.NotNull(await Api.Services.GetRequiredService<Deletion>().DeleteVaultAsync(alice.User, alice.UserKey, "Privat", RestClient.Password, "Privat", Ct));

        Assert.NotNull((await ShareDocAsync(only.Id)).RevokedAt);
        Assert.Null((await ShareDocAsync(only.Id)).NameId);
        Assert.Empty(await SlotVaultsAsync(only.Id));
        Assert.Null((await ShareDocAsync(both.Id)).RevokedAt);
        Assert.Equal([garten], await OpenedAsync(both));
    }

    [Fact]
    public async Task A_member_shares_writes_carry_its_writer_id_and_label_in_the_vault()
    {
        var (alice, bob, vaultId, membershipId) = await SharedAsync();
        await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, Ct);
        var share = await ShareAsync(bob, "Garden PC", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write });

        var access = Assert.Single(await Catalog.OpenForDavShareAsync(share.Id, share.Token, Ct));

        // Its own writer id, which the membership maps to the share and the share to its marked label.
        var doc = await Share.MembershipAsync(app.Database, membershipId);
        var vaultKey = await alice.VaultKeyAsync();
        Assert.NotNull(access.WriterId);
        Assert.Equal(DavShares.CredentialId(share.Id), Memberships.OpenWriterIds(doc, vaultKey)[access.WriterId!]);
        Assert.Equal("Garden PC (dav)", Memberships.OpenWriters(doc, vaultKey)[DavShares.CredentialId(share.Id)]);
        Assert.NotEqual(Memberships.WriterIdOf(doc, vaultKey, Memberships.SessionWriter), access.WriterId);
    }
}
