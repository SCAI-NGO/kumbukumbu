// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Kumbu.Server.Accounts;

public static class Roles
{
    /// <summary>Exactly one; can't be removed, demoted or deactivated.</summary>
    public const string PrimaryAdmin = "primary_admin";
    public const string Admin = "admin";
    public const string User = "user";

    public static bool IsKnown(string role) => role is PrimaryAdmin or Admin or User;
}

public sealed record User(
    string Id, string Username, string Display, string Role, bool Active, DateTime CreatedAt, DateTime? LastSignInAt, DateTime? PreviousSignInAt);

/// <summary>A user (collection <c>users</c>). Nothing here is secret; the user key lives only in the slots.</summary>
[BsonIgnoreExtraElements]
public sealed class UserDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    /// <summary>Lower case, with a unique index: "Chris" and "chris" are one user.</summary>
    [BsonElement("username")]
    public string Username { get; set; } = "";

    /// <summary>The username as typed at sign-up.</summary>
    [BsonElement("display")]
    public string Display { get; set; } = "";

    [BsonElement("role")]
    public string Role { get; set; } = "";

    [BsonElement("active")]
    public bool Active { get; set; }

    /// <summary>Identifies the user key (HKDF of it), so a key handed in can be checked without any credential.</summary>
    [BsonElement("keyId")]
    public byte[] KeyId { get; set; } = [];

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    [BsonElement("lastSignInAt"), BsonIgnoreIfNull]
    public DateTime? LastSignInAt { get; set; }

    [BsonElement("previousSignInAt"), BsonIgnoreIfNull]
    public DateTime? PreviousSignInAt { get; set; }

    /// <summary>When the password last changed (also by recovery); sessions started before it are refused.</summary>
    [BsonElement("credentialsChangedAt"), BsonIgnoreIfNull]
    public DateTime? CredentialsChangedAt { get; set; }

    public User ToUser() => new(Id, Username, Display, Role, Active, CreatedAt, LastSignInAt, PreviousSignInAt);
}

/// <summary>A slot of the user key (collection <c>user_slots</c>): the user key, wrapped with a key derived from one credential.</summary>
[BsonIgnoreExtraElements]
public sealed class UserSlotDocument
{
    public const string Password = "password";
    public const string Recovery = "recovery";
    public const string Session = "session";

    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("user")]
    public string User { get; set; } = "";

    [BsonElement("kind")]
    public string Kind { get; set; } = "";

    /// <summary>The Argon2id salt, for a password slot.</summary>
    [BsonElement("salt"), BsonIgnoreIfNull]
    public byte[]? Salt { get; set; }

    /// <summary>Identifies which credential the slot belongs to (sessions); derived, never the credential itself.</summary>
    [BsonElement("lookup"), BsonIgnoreIfNull]
    public byte[]? Lookup { get; set; }

    [BsonElement("wrapped")]
    public byte[] Wrapped { get; set; } = [];

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Sessions only. A TTL index (<see cref="Sessions"/>) makes MongoDB delete every slot that has this field
    /// once the time has passed, so a slot of another kind must never set it.
    /// </summary>
    [BsonElement("expiresAt"), BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }

    [BsonElement("lastUsedAt"), BsonIgnoreIfNull]
    public DateTime? LastUsedAt { get; set; }
}
