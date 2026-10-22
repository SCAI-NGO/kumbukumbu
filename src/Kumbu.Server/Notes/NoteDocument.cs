// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Kumbu.Server.Notes;

/// <summary>A note in the <c>notes</c> collection, or in <c>trash</c> with <see cref="DeletedAt"/> set.
/// Documents written before step 3 lack the revision, writer, history and seed fields; the defaults
/// below read them as a seeded note without history.</summary>
[BsonIgnoreExtraElements]
public sealed class NoteDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("vault")]
    public string Vault { get; set; } = "";

    [BsonElement("path")]
    public string Path { get; set; } = "";

    [BsonElement("content")]
    public string Content { get; set; } = "";

    /// <summary>When the current content was written.</summary>
    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [BsonElement("rev")]
    public long Revision { get; set; }

    [BsonElement("lastWriter"), BsonIgnoreIfNull]
    public string? LastWriter { get; set; }

    [BsonElement("history"), BsonIgnoreIfNull]
    public List<HistoryDocument>? History { get; set; }

    [BsonElement("seedContent"), BsonIgnoreIfNull]
    public string? SeedContent { get; set; }

    [BsonElement("deletedAt"), BsonIgnoreIfNull]
    public DateTime? DeletedAt { get; set; }

    [BsonElement("deletedBy"), BsonIgnoreIfNull]
    public string? DeletedBy { get; set; }
}

public sealed class HistoryDocument
{
    [BsonElement("content")]
    public string Content { get; set; } = "";

    [BsonElement("writtenBy")]
    public string WrittenBy { get; set; } = "";

    [BsonElement("writtenAt")]
    public DateTime WrittenAt { get; set; }
}
