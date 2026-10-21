// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Notes;

public sealed class MongoNoteStore(IMongoDatabase db) : INoteReader
{
    private readonly IMongoCollection<NoteDocument> _notes = db.GetCollection<NoteDocument>("notes");

    public Task EnsureIndexesAsync(CancellationToken ct) =>
        _notes.Indexes.CreateOneAsync(
            new CreateIndexModel<NoteDocument>(
                Builders<NoteDocument>.IndexKeys.Ascending(n => n.Path),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);

    public async Task UpsertAsync(VaultPath path, string content, CancellationToken ct)
    {
        var update = Builders<NoteDocument>.Update
            .Set(n => n.Vault, path.Vault ?? "")
            .Set(n => n.Content, content)
            .Set(n => n.UpdatedAt, DateTime.UtcNow);
        await _notes.UpdateOneAsync(
            Builders<NoteDocument>.Filter.Eq(n => n.Path, path.Value),
            update,
            new UpdateOptions { IsUpsert = true },
            ct);
    }

    public async Task<string?> ReadAsync(VaultPath path, CancellationToken ct)
    {
        var doc = await _notes
            .Find(Builders<NoteDocument>.Filter.Eq(n => n.Path, path.Value))
            .FirstOrDefaultAsync(ct);
        return doc?.Content;
    }

    public async Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct)
    {
        var prefix = dir.IsRoot ? "/" : dir.Value + "/";
        // Transitional: in step 5 paths are encrypted and an unlocked vault lives in RAM (concept §7).
        var filter = new BsonDocument("path", new BsonDocument("$regex", "^" + Regex.Escape(prefix)));
        var paths = await _notes
            .Find(new BsonDocumentFilterDefinition<NoteDocument>(filter))
            .Project(n => n.Path)
            .ToListAsync(ct);

        var result = new List<VaultPath>(paths.Count);
        foreach (var p in paths)
        {
            if (VaultPath.TryParse(p, out var vp))
            {
                result.Add(vp);
            }
        }

        return result;
    }
}
