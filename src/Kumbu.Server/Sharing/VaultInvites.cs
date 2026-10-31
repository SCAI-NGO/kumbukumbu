// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Server.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Sharing;

/// <summary>An invitation just made: the secret for the link, shown this once, the membership and its expiry.</summary>
public sealed record CreatedInvite(string Secret, string MembershipId, DateTime ExpiresAt);

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
        var now = Now;
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

/// <summary>A vault that can't take a new member now, with the refusal's <see cref="Refusals"/> code.</summary>
public sealed class SharingRefusedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static SharingRefusedException Closing() => new(Refusals.Closing, "This vault is closing: its owner deleted their account, and it takes no new members.");

    public static SharingRefusedException OwnerInactive() => new(Refusals.OwnerInactive, "The owner's account is deactivated; this invitation can't be used until it is active again.");
}
