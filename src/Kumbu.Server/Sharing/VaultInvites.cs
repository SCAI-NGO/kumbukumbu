// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Sharing;

/// <summary>An invitation just made: the secret for the link, shown this once, the membership and its expiry.</summary>
public sealed record CreatedInvite(string Secret, string MembershipId, DateTime ExpiresAt);

/// <summary>What the share page shows of an invitation before it is accepted (collab design §4.2): the vault's name,
/// its owner's display name as she typed it (to be shown sanitised) and her username, the role, the vault's embedding
/// provider, the alias asked about and whether it is free for the caller.</summary>
public sealed record InvitePeek(
    string Vault, string OwnerDisplay, string OwnerUsername, MemberRole Role, string EmbeddingProvider, string Alias, bool AliasFree, bool Confirm, DateTime ExpiresAt);

/// <summary>An accepted invitation: the membership, the alias the vault has for the member, the role, and the status
/// (<c>active</c>, or <c>pending</c> until the owner confirms).</summary>
public sealed record AcceptedInvite(string MembershipId, string Alias, MemberRole Role, string Status);

/// <summary>A membership as its owner sees it before confirming (collab design §4.2): her label for the person, and who
/// redeemed it, from the redeemer's user document: the display name as typed (to be shown sanitised) and the username,
/// only while it waits for her confirmation. The one place the owner sees a member's username. <c>MemberId</c> is for
/// the server only (the sync hub), never shown.</summary>
public sealed record OwnersMembership(
    string Id, string Label, MemberRole Role, string Status, bool Confirm, DateTime? ExpiresAt, DateTime? RedeemedAt, string? RedeemerDisplay, string? RedeemerUsername,
    string? MemberId);

/// <summary>
/// Vault invitations (collab design §4): a pending membership with a single-use link <c>/share#{secret}</c>. MongoDB
/// holds only an HMAC of the secret under a key derived from the server secret, and an <c>invite</c> slot wrapping the
/// vault key under a key from the secret; neither the secret nor the link is kept.
/// </summary>
public sealed partial class Memberships
{
    public static readonly TimeSpan DefaultInviteExpiry = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxInviteExpiry = TimeSpan.FromDays(30);

    private const int SecretBytes = 32;
    private const string InviteLookupLabel = "kumbu/v1/vault-invite";

    /// <summary>The longest label an owner gives a member.</summary>
    public const int MaxLabel = 64;

    /// <summary>The link to the share page. The secret goes in the fragment, which a browser never sends: so it appears
    /// in no request line, no proxy's log and no Referer (as <see cref="Accounts.Invitations.Link"/>).</summary>
    public static string ShareLink(string? baseUrl, string secret) => $"{baseUrl?.TrimEnd('/')}/share#{secret}";

    /// <summary>
    /// A new invitation to <paramref name="vaultId"/>, which <paramref name="ownerId"/> owns: a pending membership for
    /// <paramref name="role"/>, under the owner's <paramref name="label"/> for the person, valid for
    /// <paramref name="validFor"/>, and its invite slot, in one transaction with the vault document's write, so the
    /// limits counted inside hold (<see cref="PendingLimitException"/>, <see cref="MemberLimitException"/>). Refused
    /// while the vault is closing (<see cref="SharingRefusedException"/>).
    /// </summary>
    public async Task<CreatedInvite> InviteAsync(
        string ownerId, byte[] ownerKey, string vaultId, string label, MemberRole role, TimeSpan validFor, bool confirm, CancellationToken ct)
    {
        if (validFor <= TimeSpan.Zero || validFor > MaxInviteExpiry)
        {
            throw new ArgumentException($"An invitation is valid for 1 to {MaxInviteExpiry.TotalDays} days.", nameof(validFor));
        }

        var cleanLabel = CheckLabel(label);
        await EnsureReadyAsync(ct);
        var vaultKey = await catalog.OwnerVaultKeyAsync(ownerId, ownerKey, vaultId, ct);
        var secretBytes = RandomNumberGenerator.GetBytes(SecretBytes);
        var inviteSecret = Base64Url.EncodeToString(secretBytes);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        // Whole milliseconds, as MongoDB keeps them: the expiry the owner is shown is the one stored.
        var now = Now;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
        var expiresAt = now + validFor;
        var doc = new MembershipDocument
        {
            Id = id,
            Vault = vaultId,
            Owner = ownerId,
            Role = MemberRoles.ToText(role),
            Status = MembershipStatus.Pending,
            Confirm = confirm,
            InviteLookup = await InviteLookupAsync(inviteSecret, ct),
            ExpiresAt = expiresAt,
            CreatedAt = now,
            Revision = 0,
        };
        SealNames(doc, vaultKey, cleanLabel);
        var slot = catalog.InviteSlot(vaultId, id, secretBytes, vaultKey, expiresAt);
        CryptographicOperations.ZeroMemory(secretBytes);

        using var session = await catalog.StartSessionAsync(ct);
        await session.WithTransactionAsync(
            async (s, token) =>
            {
                await MembershipsChangedAsync(s, vaultId, token);
                await RequireNotClosingAsync(s, vaultId, token);
                await catalog.RequireSameVaultKeysAsync(s, ownerId, ownerKey, new Dictionary<string, byte[]> { [vaultId] = vaultKey }, token);
                await RequireUnderLimitsAsync(s, vaultId, pending: true, token);
                await Docs.InsertOneAsync(s, doc, cancellationToken: token);
                await catalog.Slots.InsertOneAsync(s, slot, cancellationToken: token);
                return true;
            },
            cancellationToken: ct);
        log.LogInformation("Vault {VaultId} has a new invitation, membership {MembershipId}, valid until {ExpiresAt:u}", vaultId, id, expiresAt);
        return new CreatedInvite(inviteSecret, id, expiresAt);
    }

    /// <summary>
    /// What an invitation shows to <paramref name="userId"/> (collab design §4.2): the invite slot is opened with the
    /// secret only to read the vault's meta, and its key is then forgotten; the vault is neither loaded nor unlocked.
    /// <paramref name="alias"/>, or the vault's own name, is checked against the caller's vault names and aliases.
    /// <see cref="InvitationNotFoundException"/> alike for a secret that isn't one, was used, expired or withdrawn;
    /// <see cref="SharingRefusedException"/> while the owner is inactive or the vault closing.
    /// </summary>
    public async Task<InvitePeek> PeekAsync(string userId, byte[] userKey, string inviteSecret, string? alias, CancellationToken ct)
    {
        var (doc, vaultKey) = await FindInviteAsync(inviteSecret, ct) ?? throw new InvitationNotFoundException();
        InviteVaultMeta? meta;
        try
        {
            meta = await catalog.InviteMetaAsync(null, doc.Vault, vaultKey, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultKey);
        }

        if (meta is null) throw new InvitationNotFoundException();
        var owner = await Users.Find(u => u.Id == doc.Owner).FirstOrDefaultAsync(ct) ?? throw new InvitationNotFoundException();
        if (!owner.Active) throw SharingRefusedException.OwnerInactive();
        if (meta.Closing) throw SharingRefusedException.Closing();
        var asked = string.IsNullOrWhiteSpace(alias) ? meta.Name : alias.Normalize(NormalizationForm.FormC).Trim();
        return new InvitePeek(
            meta.Name, owner.Display, owner.Username, MemberRoles.Parse(doc.Role) ?? MemberRole.Read, meta.EmbeddingProvider, asked,
            await catalog.NameFreeAsync(userId, userKey, asked, ct), doc.Confirm, doc.ExpiresAt!.Value);
    }

    /// <summary>
    /// <paramref name="userId"/> accepts the invitation <paramref name="inviteSecret"/> is the secret of, under
    /// <paramref name="alias"/> (collab design §4.2). One transaction: the membership is claimed (pending, unexpired,
    /// redeemed by nobody: of two acceptances one wins), the vault key opened from the invite slot, the member's
    /// keypair made if she has none, the member slot written under her alias, the invite slot deleted, and the pin,
    /// <c>providerSeen</c> and, unless the owner confirms, <c>active</c> set. The vault document and the user document
    /// are written too (§13 M8): a concurrent acceptance, grant or the limits' count, and a concurrent deletion of her
    /// account or rotation of her user key, conflict with it. Any refusal aborts the whole transaction, the claim with
    /// it, so the link stays usable: a taken alias (<see cref="VaultNameTakenException"/>), her own vault
    /// (<see cref="NotPermittedException"/>), a second membership (<see cref="AlreadyMemberException"/>), an inactive
    /// owner or a closing vault (<see cref="SharingRefusedException"/>). <see cref="InvitationNotFoundException"/> as peek's.
    /// </summary>
    public async Task<AcceptedInvite> AcceptAsync(string userId, byte[] userKey, string inviteSecret, string alias, CancellationToken ct)
    {
        if (DecodeSecret(inviteSecret) is not { } bytes) throw new InvitationNotFoundException();
        await EnsureReadyAsync(ct);
        var lookup = await InviteLookupAsync(inviteSecret, ct);
        AcceptedInvite? accepted = null;
        using var session = await catalog.StartSessionAsync(ct);
        try
        {
            await session.WithTransactionAsync(
                async (s, token) =>
                {
                    var now = Now;
                    var claimed = await ClaimAsync(s, lookup, userId, now, token) ?? throw new InvitationNotFoundException();
                    if (claimed.Owner == userId) throw new NotPermittedException("This is your own vault; you can't accept an invitation to it.");
                    if (!await Users.Find(s, u => u.Id == claimed.Owner && u.Active).AnyAsync(token)) throw SharingRefusedException.OwnerInactive();
                    var vaultKey = await OpenInviteSlotAsync(s, claimed, bytes, now, token) ?? throw new InvitationNotFoundException();
                    await MembershipsChangedAsync(s, claimed.Vault, token);
                    // The key the invite slot holds is still the vault's: its meta opens with it, outside a re-keying.
                    var meta = await catalog.InviteMetaAsync(s, claimed.Vault, vaultKey, token) ?? throw new InvitationNotFoundException();
                    if (meta.Closing) throw SharingRefusedException.Closing();
                    if (FaultAt is { } fault) await fault("accept-before-user");
                    await CountAcceptedAsync(s, userId, userKey, token);

                    var memberPublic = await MemberPublicKeyAsync(s, userId, userKey, token);
                    await InsertMemberSlotAsync(s, catalog.MemberSlot(claimed.Vault, claimed.Id, userId, userKey, memberPublic, vaultKey, alias), alias, token);
                    await catalog.Slots.DeleteManyAsync(s, sl => sl.Kind == SlotDocument.Invite && sl.Membership == claimed.Id, cancellationToken: token);
                    var update = Builders<MembershipDocument>.Update
                        .Set(m => m.Pin, SealPin(vaultKey, claimed.Vault, claimed.Id, userId, memberPublic))
                        .Set(m => m.ProviderSeen, meta.EmbeddingProvider);
                    // With confirm it stays pending, redeemed, until the owner confirms or declines.
                    if (!claimed.Confirm) update = update.Set(m => m.Status, MembershipStatus.Active).Set(m => m.AcceptedAt, now);
                    await Docs.UpdateOneAsync(s, m => m.Id == claimed.Id, update, cancellationToken: token);
                    if (FaultAt is { } inside) await inside("inside-accept");
                    accepted = new AcceptedInvite(
                        claimed.Id, alias, MemberRoles.Parse(claimed.Role) ?? MemberRole.Read, claimed.Confirm ? MembershipStatus.Pending : MembershipStatus.Active);
                    return true;
                },
                cancellationToken: ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        log.LogInformation("User {UserId} accepted the invitation of membership {MembershipId} ({Status})", userId, accepted!.MembershipId, accepted.Status);
        return accepted;
    }

    /// <summary>
    /// The membership <paramref name="membershipId"/> of <paramref name="vaultId"/>, a vault <paramref name="ownerId"/>
    /// owns, as she sees it before confirming; <see cref="MembershipNotFoundException"/> if the vault has none of that
    /// id (collab design §6.0: an id of another vault's is none).
    /// </summary>
    public async Task<OwnersMembership> OwnersViewAsync(string ownerId, byte[] ownerKey, string vaultId, string membershipId, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var vaultKey = await catalog.OwnerVaultKeyAsync(ownerId, ownerKey, vaultId, ct);
        var doc = await Docs.Find(m => m.Id == membershipId && m.Vault == vaultId).FirstOrDefaultAsync(ct) ?? throw new MembershipNotFoundException();
        return await OwnersViewAsync(doc, vaultKey, ct);
    }

    private async Task<OwnersMembership> OwnersViewAsync(MembershipDocument doc, byte[] vaultKey, CancellationToken ct)
    {
        var label = Encoding.UTF8.GetString(VaultCatalog.OpenForVault(vaultKey, doc.Vault, LabelPurpose, doc.Label, $"membership-label:{doc.Vault}:{doc.Id}"));
        // Who redeemed it only for the confirm dialog (collab design §4.2): the one place the owner sees a member's
        // username, and only when she asked to confirm. An invitation without confirm keeps the member's identity hers.
        var redeemer = doc is { Status: MembershipStatus.Pending, Confirm: true, Member: { } member }
            ? await Users.Find(u => u.Id == member).FirstOrDefaultAsync(ct)
            : null;
        return new OwnersMembership(
            doc.Id, label, MemberRoles.Parse(doc.Role) ?? MemberRole.Read, doc.Status, doc.Confirm, doc.ExpiresAt, doc.RedeemedAt, redeemer?.Display, redeemer?.Username,
            doc.Member);
    }

    /// <summary>
    /// The owner confirms a redeemed invitation of her vault (collab design §4.2): the membership becomes active. One
    /// transaction with the vault document's write (an activation, §13 M8), filtered on the membership's id, its vault
    /// and its status (pending, redeemed); <see cref="MembershipNotFoundException"/> if nothing matches. Returns the
    /// membership as the owner sees it now: active, without the redeemer, whom the dialog showed before.
    /// </summary>
    public async Task<OwnersMembership> ConfirmAsync(string ownerId, byte[] ownerKey, string vaultId, string membershipId, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var vaultKey = await catalog.OwnerVaultKeyAsync(ownerId, ownerKey, vaultId, ct);
        var f = Builders<MembershipDocument>.Filter;
        using var session = await catalog.StartSessionAsync(ct);
        await session.WithTransactionAsync(
            async (s, token) =>
            {
                await MembershipsChangedAsync(s, vaultId, token);
                await RequireNotClosingAsync(s, vaultId, token);
                var confirmed = await Docs.UpdateOneAsync(
                    s,
                    BoundFilter(membershipId, MembershipScope.OfVault(vaultId), MembershipStatus.Pending, MembershipStatus.Active) & f.Ne(m => m.Member, null),
                    Builders<MembershipDocument>.Update.Set(m => m.Status, MembershipStatus.Active).Set(m => m.AcceptedAt, Now).Inc(m => m.Revision, 1),
                    cancellationToken: token);
                if (confirmed.MatchedCount == 0) throw new MembershipNotFoundException();
                return true;
            },
            cancellationToken: ct);
        log.LogInformation("Membership {MembershipId} of vault {VaultId} was confirmed", membershipId, vaultId);
        return await OwnersViewAsync(await Docs.Find(m => m.Id == membershipId).FirstAsync(ct), vaultKey, ct);
    }

    /// <summary>
    /// The owner declines a redeemed invitation (collab design §4.2): the membership becomes <c>revoked</c>, without
    /// its pin, and its member slot goes, in one transaction. Needs no password window: reducing access is always open.
    /// <see cref="MembershipNotFoundException"/> unless it is a pending, redeemed membership of <paramref name="vaultId"/>.
    /// </summary>
    public Task DeclineAsync(string vaultId, string membershipId, CancellationToken ct) =>
        EndPendingAsync(vaultId, membershipId, redeemed: true, ct);

    /// <summary>
    /// The owner withdraws an invitation nobody redeemed (collab design §4.3): the membership becomes <c>revoked</c>,
    /// without its lookup and expiry, and its invite slot goes, in one transaction.
    /// <see cref="MembershipNotFoundException"/> unless it is a pending membership of <paramref name="vaultId"/> without a member.
    /// </summary>
    public Task WithdrawAsync(string vaultId, string membershipId, CancellationToken ct) =>
        EndPendingAsync(vaultId, membershipId, redeemed: false, ct);

    private async Task EndPendingAsync(string vaultId, string membershipId, bool redeemed, CancellationToken ct)
    {
        await EnsureReadyAsync(ct);
        var f = Builders<MembershipDocument>.Filter;
        using var session = await catalog.StartSessionAsync(ct);
        await session.WithTransactionAsync(
            async (s, token) =>
            {
                var ended = await Docs.UpdateOneAsync(
                    s,
                    BoundFilter(membershipId, MembershipScope.OfVault(vaultId), MembershipStatus.Pending, MembershipStatus.Revoked) & (redeemed ? f.Ne(m => m.Member, null) : f.Eq(m => m.Member, null)),
                    Builders<MembershipDocument>.Update
                        .Set(m => m.Status, MembershipStatus.Revoked).Set(m => m.RevokedAt, Now)
                        .Unset(m => m.InviteLookup).Unset(m => m.ExpiresAt).Unset(m => m.Pin)
                        // Its only way in goes in this transaction: nothing is left for the cleanup.
                        .Set(m => m.CleanedAt, Now)
                        .Inc(m => m.Revision, 1),
                    cancellationToken: token);
                if (ended.MatchedCount == 0) throw new MembershipNotFoundException();
                // Its way in: the invite slot of an open invitation, the member slot of a redeemed one.
                var kind = redeemed ? SlotDocument.Member : SlotDocument.Invite;
                await catalog.Slots.DeleteManyAsync(s, sl => sl.Kind == kind && sl.Membership == membershipId && sl.Vault == vaultId, cancellationToken: token);
                return true;
            },
            cancellationToken: ct);
        log.LogInformation("Membership {MembershipId} of vault {VaultId} was {Ended}", membershipId, vaultId, redeemed ? "declined" : "withdrawn");
    }

    /// <summary>
    /// The claim of an acceptance, inside its transaction: the open invitation with <paramref name="lookup"/> gets its
    /// member and <c>redeemedAt</c>, and loses <c>inviteLookup</c> and <c>expiresAt</c>, in one conditional update; null
    /// if there is none. <see cref="AlreadyMemberException"/> if the user has a membership of the vault already (the
    /// unique index on vault and member).
    /// </summary>
    private async Task<MembershipDocument?> ClaimAsync(IClientSessionHandle s, byte[] lookup, string userId, DateTime now, CancellationToken ct)
    {
        try
        {
            return await Docs.FindOneAndUpdateAsync(
                s,
                OpenInvite(lookup, now),
                Builders<MembershipDocument>.Update
                    .Set(m => m.Member, userId).Set(m => m.RedeemedAt, now).Unset(m => m.InviteLookup).Unset(m => m.ExpiresAt).Inc(m => m.Revision, 1),
                new FindOneAndUpdateOptions<MembershipDocument> { ReturnDocument = ReturnDocument.Before },
                ct);
        }
        catch (MongoCommandException e) when (e.Code == 11000)
        {
            throw new AlreadyMemberException();
        }
    }

    /// <summary>
    /// The write to the new member's user document inside the transaction that makes her a member (collab design §13
    /// M8): counted only while she is active and <paramref name="userKey"/> is still her user key, so a deletion of her
    /// account or a rotation of her user key, before or at the same time, refuses or conflicts with it.
    /// <see cref="CredentialsChangedException"/> if not.
    /// </summary>
    private async Task CountAcceptedAsync(IClientSessionHandle s, string userId, byte[] userKey, CancellationToken ct)
    {
        var keyId = UserCatalog.KeyId(userKey, userId);
        var counted = await Users.UpdateOneAsync(
            s, u => u.Id == userId && u.Active && u.KeyId == keyId, Builders<UserDocument>.Update.Inc(u => u.MembershipsAccepted, 1), cancellationToken: ct);
        if (counted.MatchedCount == 0) throw new CredentialsChangedException();
    }

    /// <summary>
    /// The open invitation <paramref name="inviteSecret"/> is the secret of, with the vault key its invite slot holds;
    /// null alike for a secret that isn't one and for an invitation used, expired (checked here: the TTL monitor
    /// runs only about once a minute) or withdrawn.
    /// </summary>
    private async Task<(MembershipDocument Doc, byte[] VaultKey)?> FindInviteAsync(string inviteSecret, CancellationToken ct)
    {
        if (DecodeSecret(inviteSecret) is not { } bytes) return null;
        await EnsureReadyAsync(ct);
        var lookup = await InviteLookupAsync(inviteSecret, ct);
        var now = Now;
        var doc = await Docs.Find(OpenInvite(lookup, now)).FirstOrDefaultAsync(ct);
        if (doc is null) return null;
        var key = await OpenInviteSlotAsync(null, doc, bytes, now, ct);
        CryptographicOperations.ZeroMemory(bytes);
        return key is null ? null : (doc, key);
    }

    /// <summary>An invitation that can still be used: pending, unexpired, redeemed by nobody.</summary>
    private static FilterDefinition<MembershipDocument> OpenInvite(byte[] lookup, DateTime now)
    {
        var f = Builders<MembershipDocument>.Filter;
        return f.Eq(m => m.InviteLookup, lookup) & f.Eq(m => m.Status, MembershipStatus.Pending) & f.Gt(m => m.ExpiresAt, now) & f.Eq(m => m.Member, null);
    }

    /// <summary>The vault key of <paramref name="doc"/>'s invite slot, opened with the secret's bytes; null if there is
    /// none on its vault, it expired, or it doesn't open.</summary>
    private async Task<byte[]?> OpenInviteSlotAsync(IClientSessionHandle? session, MembershipDocument doc, byte[] secretBytes, DateTime now, CancellationToken ct)
    {
        var f = Builders<SlotDocument>.Filter;
        var filter = f.Eq(sl => sl.Kind, SlotDocument.Invite) & f.Eq(sl => sl.Membership, doc.Id) & f.Eq(sl => sl.Vault, doc.Vault) & f.Gt(sl => sl.ExpiresAt, now);
        var slot = await (session is null ? catalog.Slots.Find(filter) : catalog.Slots.Find(session, filter)).FirstOrDefaultAsync(ct);
        if (slot is null) return null;
        try
        {
            return VaultCatalog.OpenInviteSlot(secretBytes, slot);
        }
        catch (CryptographicException)
        {
            log.LogWarning("The invite slot of membership {MembershipId} on vault {VaultId} did not open with its secret", doc.Id, doc.Vault);
            return null;
        }
    }

    /// <summary>The owner's label for a member: trimmed, 1 to <see cref="MaxLabel"/> characters, no control or format
    /// characters; <see cref="ArgumentException"/> otherwise.</summary>
    private static string CheckLabel(string label)
    {
        var clean = (label ?? "").Normalize(NormalizationForm.FormC).Trim();
        if (clean.Length is 0 or > MaxLabel) throw new ArgumentException($"A name for the person has 1 to {MaxLabel} characters.", nameof(label));
        if (clean.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
        {
            throw new ArgumentException("A name for the person can't contain control characters.", nameof(label));
        }

        return clean;
    }

    /// <summary>What a membership seals under the vault key from its start: the owner's label, empty writers, and the
    /// writer id of the member's sessions.</summary>
    private static void SealNames(MembershipDocument doc, byte[] vaultKey, string label)
    {
        var (vaultId, id) = (doc.Vault, doc.Id);
        doc.Label = VaultCatalog.SealForVault(vaultKey, vaultId, LabelPurpose, Encoding.UTF8.GetBytes(label), $"membership-label:{vaultId}:{id}");
        doc.Writers = VaultCatalog.SealForVault(vaultKey, vaultId, WritersPurpose, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>()), $"membership-writers:{vaultId}:{id}");
        doc.WriterIds = VaultCatalog.SealForVault(
            vaultKey, vaultId, WriterIdsPurpose, JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { [NewWriterId()] = SessionWriter }), $"membership-writer-ids:{vaultId}:{id}");
    }

    /// <summary>Inside a transaction that takes a new member: refused while the owner's account is gone and the vault
    /// waits out its closing period (collab design §7.3).</summary>
    private async Task RequireNotClosingAsync(IClientSessionHandle s, string vaultId, CancellationToken ct)
    {
        if (await catalog.Vaults.Find(s, v => v.Id == vaultId && v.ClosingAt != null).AnyAsync(ct)) throw SharingRefusedException.Closing();
    }

    /// <summary>The keyed hash an invite secret is found by: <c>HMAC(HKDF(server secret, "kumbu/v1/vault-invite"), secret)</c>.</summary>
    private async Task<byte[]> InviteLookupAsync(string inviteSecret, CancellationToken ct) => VaultCrypto.Mac(await secret.KeyAsync(InviteLookupLabel, ct), inviteSecret);

    /// <summary>The secret's 32 bytes, or null for anything that isn't one.</summary>
    private static byte[]? DecodeSecret(string? inviteSecret)
    {
        if (inviteSecret is null || inviteSecret.Length != Base64Url.GetEncodedLength(SecretBytes) || !Base64Url.IsValid(inviteSecret)) return null;
        var bytes = new byte[SecretBytes];
        return Base64Url.TryDecodeFromChars(inviteSecret, bytes, out var written) && written == SecretBytes ? bytes : null;
    }
}

/// <summary>An invitation secret that isn't one, or one used, expired or withdrawn: all alike, so nothing tells them apart.</summary>
public sealed class InvitationNotFoundException() : Exception("This invitation link is not valid (any more).");

/// <summary>No such membership of the vault, or not in a status the request may move it from.</summary>
public sealed class MembershipNotFoundException() : Exception("This vault has no such membership waiting for that.");

/// <summary>A vault that can't take a new member now, with the refusal's <see cref="Refusals"/> code.</summary>
public sealed class SharingRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static SharingRefusedException Closing() => new(Refusals.Closing, "This vault is closing: its owner deleted their account, and it takes no new members.");

    public static SharingRefusedException OwnerInactive() => new(Refusals.OwnerInactive, "The owner's account is deactivated; this invitation can't be used until it is active again.");
}
