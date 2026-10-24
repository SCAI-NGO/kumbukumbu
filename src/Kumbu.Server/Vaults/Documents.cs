// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Kumbu.Server.Vaults;

/// <summary>A vault: a random id and its sealed name. Everything else about it lives in its documents.</summary>
public sealed class VaultDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("meta")]
    public byte[] Meta { get; set; } = [];

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>A key slot: the vault key, wrapped with a key derived from one credential.</summary>
public sealed class SlotDocument
{
    public const string ApiKey = "key";
    public const string Recovery = "recovery";

    /// <summary>The vault key wrapped by the owner's user key (accounts design §3); has no recovery slot of its own.</summary>
    public const string User = "user";

    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("vault")]
    public string Vault { get; set; } = "";

    [BsonElement("kind")]
    public string Kind { get; set; } = "";

    /// <summary>Identifies which credential the slot belongs to; derived, never the credential itself.</summary>
    [BsonElement("lookup"), BsonIgnoreIfNull]
    public byte[]? Lookup { get; set; }

    /// <summary>User slots only: a keyed hash of the vault's normalised name, so MongoDB can keep a user's vault
    /// names unique (partial unique index with <see cref="Lookup"/>) without ever seeing a name.</summary>
    [BsonElement("nameId"), BsonIgnoreIfNull]
    public byte[]? NameId { get; set; }

    [BsonElement("wrapped")]
    public byte[] Wrapped { get; set; } = [];

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A note (collection <c>notes</c>), a trashed note (<c>trash</c>) or a move record (<c>moves</c>).
/// Only what MongoDB itself needs is in the clear: the vault id, a keyed hash of the path (for the unique
/// index and lookups), the revision (for compare-and-swap) and the times the TTL indexes expire on.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class SealedDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("vault")]
    public string Vault { get; set; } = "";

    [BsonElement("pid")]
    public byte[] PathId { get; set; } = [];

    [BsonElement("rev")]
    public long Revision { get; set; }

    [BsonElement("blob")]
    public byte[] Blob { get; set; } = [];

    [BsonElement("deletedAt"), BsonIgnoreIfNull]
    public DateTime? DeletedAt { get; set; }

    [BsonElement("at"), BsonIgnoreIfNull]
    public DateTime? At { get; set; }
}
