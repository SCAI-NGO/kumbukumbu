// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Bson.Serialization.Attributes;

namespace Kumbu.Server.Sharing;

/// <summary>What a member may do on a shared vault; ownership is never a role.</summary>
public enum MemberRole
{
    Read,
    Write,
    Maintain,
}

public static class MemberRoles
{
    public static string ToText(MemberRole role) => role switch
    {
        MemberRole.Read => "read",
        MemberRole.Write => "write",
        MemberRole.Maintain => "maintain",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static MemberRole? Parse(string? text) => text switch
    {
        "read" => MemberRole.Read,
        "write" => MemberRole.Write,
        "maintain" => MemberRole.Maintain,
        _ => null,
    };
}

/// <summary>The statuses of a membership. They only move forward (<see cref="Memberships.TransitionFilter"/>).</summary>
public static class MembershipStatus
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Revoked = "revoked";
    public const string Left = "left";
}

/// <summary>
/// One person's membership of one vault (collection <c>memberships</c>, collab design §2.1). The sharing graph is in
/// the clear, since the server reads it on every request without a key; what names a person (the owner's label, the
/// member's writers) and the pinned public key are sealed under the vault key. A revoked or left membership stays as
/// a tombstone.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class MembershipDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("vault")]
    public string Vault { get; set; } = "";

    /// <summary>The vault's owner, from her user slot, for her listings and her account's deletion.</summary>
    [BsonElement("owner")]
    public string Owner { get; set; } = "";

    /// <summary>Null while an invitation is pending and nobody redeemed it.</summary>
    [BsonElement("member")]
    public string? Member { get; set; }

    /// <summary><see cref="MemberRoles.ToText"/> of the role.</summary>
    [BsonElement("role")]
    public string Role { get; set; } = "";

    [BsonElement("status")]
    public string Status { get; set; } = "";

    /// <summary>The owner confirms the redemption before the membership becomes active.</summary>
    [BsonElement("confirm")]
    public bool Confirm { get; set; }

    /// <summary>The owner's handle for the member ("Bob"), sealed under the vault key.</summary>
    [BsonElement("label")]
    public byte[] Label { get; set; } = [];

    /// <summary>A keyed hash of the invite secret, only while pending; unique while it exists.</summary>
    [BsonElement("inviteLookup"), BsonIgnoreIfNull]
    public byte[]? InviteLookup { get; set; }

    [BsonElement("expiresAt"), BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    [BsonElement("acceptedAt"), BsonIgnoreIfNull]
    public DateTime? AcceptedAt { get; set; }

    [BsonElement("revokedAt"), BsonIgnoreIfNull]
    public DateTime? RevokedAt { get; set; }

    [BsonElement("roleChangedAt"), BsonIgnoreIfNull]
    public DateTime? RoleChangedAt { get; set; }

    /// <summary>The member's public key as pinned at acceptance, sealed under the vault key (design §5.4).</summary>
    [BsonElement("pin"), BsonIgnoreIfNull]
    public byte[]? Pin { get; set; }

    /// <summary>Writer id → credential id, sealed under the vault key: members' writes carry only the writer id.</summary>
    [BsonElement("writerIds")]
    public byte[] WriterIds { get; set; } = [];

    /// <summary>The embedding provider the member has acknowledged (design §8.5).</summary>
    [BsonElement("providerSeen"), BsonIgnoreIfNull]
    public string? ProviderSeen { get; set; }

    [BsonElement("redeemedAt"), BsonIgnoreIfNull]
    public DateTime? RedeemedAt { get; set; }

    /// <summary>Credential id → the agent or device part of its name, sealed under the vault key.</summary>
    [BsonElement("writers")]
    public byte[] Writers { get; set; } = [];

    /// <summary>A sentinel for transactions that derive slots through the membership (design §3.4).</summary>
    [BsonElement("revision")]
    public int Revision { get; set; }
}
