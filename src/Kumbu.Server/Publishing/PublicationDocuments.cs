// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Kumbu.Server.Publishing;

/// <summary>
/// A published copy (collection <c>publications</c>), a mini vault of its own (publish spec §4). In the clear only
/// what MongoDB needs: the vault id (for deletion and storage figures), the lookup id derived from the share code
/// (<see cref="ShareCode.Lookup"/>), and when it was first published. The reader slot holds the publication key under
/// the reader key; the blob holds the copy under the publication key. Nothing names the note, its title or its owner.
/// <see cref="Kind"/> tells a skill's copy (<see cref="PublishedSkillCopy"/>) from a note's, so neither reader opens the other.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class PublicationDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("vault")]
    public string Vault { get; set; } = "";

    [BsonElement("lookup")]
    public byte[] Lookup { get; set; } = [];

    [BsonElement("reader")]
    public byte[] Reader { get; set; } = [];

    [BsonElement("blob")]
    public byte[] Blob { get; set; } = [];

    [BsonElement("at")]
    public DateTime At { get; set; }

    /// <summary><see cref="PublicationKinds.Skill"/> for a skill shared by link; absent for a note.</summary>
    [BsonElement("kind"), BsonIgnoreIfNull]
    public string? Kind { get; set; }
}

/// <summary>A file of a published copy (collection <c>publication_files</c>), sealed under the publication key and
/// bound to its index; no vault blob, so the blob sweep never sees it.</summary>
[BsonIgnoreExtraElements]
public sealed class PublicationFileDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("vault")]
    public string Vault { get; set; } = "";

    [BsonElement("publication")]
    public ObjectId Publication { get; set; }

    [BsonElement("n")]
    public int Index { get; set; }

    [BsonElement("blob")]
    public byte[] Blob { get; set; } = [];
}
