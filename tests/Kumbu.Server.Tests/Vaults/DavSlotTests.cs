// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>A WebDAV share's slots: sealed as an API key's, under a key of their own, bound to the share's id, and never
/// "May publish".</summary>
[Collection("app")]
public sealed class DavSlotTests(KumbuAppFactory app)
{
    private const string Token = "dav-token-of-the-share-0123456789abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private VaultCatalog Catalog => app.PublicApi.Services.GetRequiredService<VaultCatalog>();

    /// <summary>A fresh share id, four lowercase Crockford Base32 characters, so the tests' slots never meet.</summary>
    private static string NewShareId()
    {
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        return string.Concat(Enumerable.Range(0, 4).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]));
    }

    /// <summary>A share of the client's own vault at <paramref name="right"/>, its slot stored: the share id and the slot.</summary>
    private async Task<(string ShareId, SlotDocument Slot)> ShareAsync(RestClient owner, string vaultId, KeyRight right = KeyRight.Write)
    {
        var shareId = NewShareId();
        var (slots, _) = await Catalog.DavSlotsAsync(owner.User.Id, owner.UserKey, shareId, Token, new Dictionary<string, KeyRight> { [vaultId] = right }, Ct);
        var slot = Assert.Single(slots);
        await Catalog.Slots.InsertOneAsync(slot, cancellationToken: Ct);
        return (shareId, slot);
    }

    private static byte[] DavKek(string token, string vaultId) => VaultCrypto.Derive(token, "kumbu/v1/dav-slot-kek", Encoding.UTF8.GetBytes(vaultId));

    private static string DavContext(string vaultId, string shareId) => $"slot:{vaultId}:dav:{shareId}";

    [Fact]
    public async Task A_share_slot_opens_its_vault_with_the_sealed_right()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var vaultId = await alice.VaultIdAsync();
        var (shareId, slot) = await ShareAsync(alice, vaultId);

        Assert.Equal(SlotDocument.Dav, slot.Kind);
        Assert.Equal(shareId, slot.ShareId);
        Assert.Null(slot.KeyId);
        var opened = Assert.Single(await Catalog.OpenForDavShareAsync(shareId, Token, Ct));
        Assert.Equal(vaultId, opened.Id);
        Assert.Equal(AccessLevel.Write, opened.Level);
        Assert.True(opened.IsOwner);
        Assert.False(opened.MayPublish);
    }

    [Fact]
    public async Task A_share_slot_is_sealed_under_the_dav_key_and_bound_to_the_share()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var vaultId = await alice.VaultIdAsync();
        var (shareId, slot) = await ShareAsync(alice, vaultId, KeyRight.Read);

        using var payload = JsonDocument.Parse(VaultCrypto.Open(DavKek(Token, vaultId), slot.Wrapped, DavContext(vaultId, shareId)));
        Assert.Equal("read", payload.RootElement.GetProperty("rights").GetString());
        Assert.Equal(await alice.VaultKeyAsync(), payload.RootElement.GetProperty("vaultKey").GetBytesFromBase64());
        Assert.False(payload.RootElement.TryGetProperty("publish", out _));
    }

    [Fact]
    public async Task A_share_slot_does_not_open_as_an_api_key_slot()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var (shareId, slot) = await ShareAsync(alice, await alice.VaultIdAsync());

        // The same sealed bytes, relabelled in the database as an API key's slot under the share's id.
        await Catalog.Slots.InsertOneAsync(
            new SlotDocument { Vault = slot.Vault, Kind = SlotDocument.ApiKey, KeyId = shareId, Wrapped = slot.Wrapped, CreatedAt = slot.CreatedAt }, cancellationToken: Ct);

        Assert.Empty(await Catalog.OpenForApiKeyAsync(shareId, Token, Ct));
    }

    [Fact]
    public async Task A_share_slot_opens_nothing_with_another_token()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var (shareId, _) = await ShareAsync(alice, await alice.VaultIdAsync());

        Assert.Empty(await Catalog.OpenForDavShareAsync(shareId, Token + "x", Ct));
    }

    [Fact]
    public async Task A_share_slot_moved_to_another_share_id_opens_nothing()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var (_, slot) = await ShareAsync(alice, await alice.VaultIdAsync());
        var other = NewShareId();

        await Catalog.Slots.UpdateOneAsync(s => s.Id == slot.Id, Builders<SlotDocument>.Update.Set(s => s.ShareId, other), cancellationToken: Ct);

        Assert.Empty(await Catalog.OpenForDavShareAsync(other, Token, Ct));
    }

    [Fact]
    public async Task A_share_slot_with_a_planted_publish_flag_may_not_publish()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var vaultId = await alice.VaultIdAsync();
        var (shareId, slot) = await ShareAsync(alice, vaultId);

        var planted = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["vaultKey"] = await alice.VaultKeyAsync(), ["rights"] = "write", ["v"] = KeyRights.SealedVersion, ["publish"] = true,
        });
        await Catalog.Slots.UpdateOneAsync(
            s => s.Id == slot.Id, Builders<SlotDocument>.Update.Set(s => s.Wrapped, VaultCrypto.Seal(DavKek(Token, vaultId), planted, DavContext(vaultId, shareId))),
            cancellationToken: Ct);

        var opened = Assert.Single(await Catalog.OpenForDavShareAsync(shareId, Token, Ct));
        Assert.True(opened.IsOwner);
        Assert.False(opened.MayPublish);
    }

    [Fact]
    public async Task A_members_share_slot_follows_her_membership()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var vaultId = await alice.VaultIdAsync();
        var membershipId = await Share.MembershipOfAsync(app.Database, bob, vaultId);
        var (shareId, slot) = await ShareAsync(bob, vaultId, KeyRight.Maintain);

        Assert.Equal(membershipId, slot.GrantedVia);
        var opened = Assert.Single(await Catalog.OpenForDavShareAsync(shareId, Token, Ct));
        Assert.Equal("alices-work", opened.Name);
        Assert.False(opened.IsOwner);
        Assert.Equal(AccessLevel.Write, opened.Level);

        await Share.SetRoleAsync(app.Database, membershipId, MemberRole.Read);
        Assert.Equal(AccessLevel.Read, Assert.Single(await Catalog.OpenForDavShareAsync(shareId, Token, Ct)).Level);

        await Share.SetStatusAsync(app.Database, membershipId, MembershipStatus.Revoked);
        Assert.Empty(await Catalog.OpenForDavShareAsync(shareId, Token, Ct));
    }

    [Fact]
    public async Task A_share_slot_whose_membership_was_changed_opens_nothing()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var (shareId, slot) = await ShareAsync(bob, await alice.VaultIdAsync());

        // Dropping grantedVia would dodge the membership check: the slot then doesn't open.
        await Catalog.Slots.UpdateOneAsync(s => s.Id == slot.Id, Builders<SlotDocument>.Update.Unset(s => s.GrantedVia), cancellationToken: Ct);

        Assert.Empty(await Catalog.OpenForDavShareAsync(shareId, Token, Ct));
    }

    [Fact]
    public async Task Deleting_a_shares_slots_leaves_the_api_key_slots()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var vaultId = await alice.VaultIdAsync();
        var (shareId, slot) = await ShareAsync(alice, vaultId);
        var (keySlots, _) = await Catalog.ApiKeySlotsAsync(
            alice.User.Id, alice.UserKey, shareId, Token, new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, new HashSet<string>(), Ct);
        await Catalog.Slots.InsertManyAsync(keySlots, cancellationToken: Ct);

        await Catalog.DeleteDavSlotsAsync(shareId, Ct);

        Assert.Empty(await Catalog.OpenForDavShareAsync(shareId, Token, Ct));
        Assert.Single(await Catalog.OpenForApiKeyAsync(shareId, Token, Ct));
    }
}
