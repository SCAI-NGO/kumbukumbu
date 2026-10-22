// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Kumbu.Server.Notes;

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

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}
