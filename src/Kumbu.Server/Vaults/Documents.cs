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

    /// <summary>Set while the vault is re-keyed (its two-key period, rekeying design §3): from the start transaction
    /// to the finish. Meanwhile the vault loads only as a placeholder and nothing writes to it.</summary>
    [BsonElement("rekeyStartedAt"), BsonIgnoreIfNull]
    public DateTime? RekeyStartedAt { get; set; }
}

/// <summary>A key slot: the vault key, wrapped with a key derived from one credential.</summary>
public sealed class SlotDocument
{
    public const string ApiKey = "key";
    public const string Recovery = "recovery";

    /// <summary>The vault key wrapped by the owner's user key (accounts design §3); has no recovery slot of its own.</summary>
    public const string User = "user";

    /// <summary>
    /// The new vault key of a re-keying run (rekeying design §3), wrapped as the owner's <see cref="User"/> slot is,
    /// under the same lookup but without a name hash: at most one per vault (unique partial index), from the start
    /// of the run to its finish, which wraps it into the user slot and deletes this one.
    /// </summary>
    public const string UserNext = "user-next";

    /// <summary>A slot of one OAuth token (code, access or refresh) of a connection; vanishes with the token.</summary>
    public const string Connection = "connection";

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

    /// <summary>User slots only: the owner's user id, in the clear, so the admin's user list can count a user's
    /// vaults and their storage without any key. Which user owns which vaults is no secret from the operator.</summary>
    [BsonElement("owner"), BsonIgnoreIfNull]
    public string? Owner { get; set; }

    /// <summary>
    /// Slots of an API key (kind <see cref="ApiKey"/>) only: the key's id. Such a slot seals the vault key together
    /// with the key's right on the vault; the dev token's slots have none and seal the vault key alone.
    /// </summary>
    [BsonElement("apiKey"), BsonIgnoreIfNull]
    public string? KeyId { get; set; }

    /// <summary>Slots of a connection's token (kind <see cref="Connection"/>) only: the connection, so its revocation
    /// finds every slot of it; such a slot seals the vault key with the right, as an API key's does.</summary>
    [BsonElement("connection"), BsonIgnoreIfNull]
    public string? ConnectionId { get; set; }

    /// <summary>Slots of a connection's token only: the token's id (<c>oauth_tokens._id</c>), never the token.</summary>
    [BsonElement("token"), BsonIgnoreIfNull]
    public string? TokenId { get; set; }

    /// <summary>When the token expires, if it does: a TTL index deletes the slot then. Expiry is also checked in code,
    /// since the TTL monitor runs only about once a minute.</summary>
    [BsonElement("expiresAt"), BsonIgnoreIfNull]
    public DateTime? ExpiresAt { get; set; }

    [BsonElement("wrapped")]
    public byte[] Wrapped { get; set; } = [];

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A note (collection <c>notes</c>), a trashed note (<c>trash</c>), a move record (<c>moves</c>), an attachment's record
/// (<c>attachments</c>) or a trashed one (<c>attachment_trash</c>), among others.
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

/// <summary>
/// The bytes of an attachment version (collection <c>blobs</c>), sealed, once per vault and content. In the clear: the
/// vault id, a keyed hash of the bytes (<see cref="UnlockedVault.BlobId"/>, never a plain hash: that would let anyone
/// with the database confirm that a vault holds a known file) and when the bytes were last put, so that a sweep leaves
/// an upload whose record isn't committed yet alone (<see cref="BlobSweep"/>).
/// </summary>
[BsonIgnoreExtraElements]
public sealed class BlobDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("vault")]
    public string Vault { get; set; } = "";

    [BsonElement("bid")]
    public byte[] BlobId { get; set; } = [];

    [BsonElement("blob")]
    public byte[] Blob { get; set; } = [];

    [BsonElement("at")]
    public DateTime At { get; set; }
}

/// <summary>
/// A part of a PDF's analysis (collection <c>pdf_analyses</c>), which belongs to the PDF's blob as the bytes do:
/// part 0 the measurements (or why there are none), parts 1… the page text. Sealed under the vault key, bound to the
/// blob's id and the part number (<see cref="UnlockedVault.SealPdfAnalysis{T}"/>); in the clear only the vault, the
/// blob's id (as in <see cref="BlobDocument"/>), the part and when it was stored.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class PdfAnalysisDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("vault")]
    public string Vault { get; set; } = "";

    [BsonElement("bid")]
    public byte[] BlobId { get; set; } = [];

    [BsonElement("part")]
    public int Part { get; set; }

    [BsonElement("blob")]
    public byte[] Blob { get; set; } = [];

    [BsonElement("at")]
    public DateTime At { get; set; }
}

/// <summary>A PDF's blob waiting for its analysis (collection <c>pdf_jobs</c>): no content, one per vault and blob.</summary>
[BsonIgnoreExtraElements]
public sealed class PdfJobDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("vault")]
    public string Vault { get; set; } = "";

    [BsonElement("bid")]
    public byte[] BlobId { get; set; } = [];

    [BsonElement("at")]
    public DateTime At { get; set; }

    /// <summary>Analyses tried and failed so far.</summary>
    [BsonElement("tries")]
    public int Tries { get; set; }
}
