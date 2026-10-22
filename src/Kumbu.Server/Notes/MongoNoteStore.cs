// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Notes;

/// <summary>
/// Notes in MongoDB. Each operation the write rules need is a single-document operation, so it is atomic
/// without transactions: a replace is an update filtered on the revision the writer saw (compare-and-swap).
/// </summary>
public sealed class MongoNoteStore(IMongoDatabase db) : INoteStore
{
    private readonly IMongoCollection<NoteDocument> _notes = db.GetCollection<NoteDocument>("notes");
    private readonly IMongoCollection<NoteDocument> _trash = db.GetCollection<NoteDocument>("trash");

    private volatile bool _indexesReady;

    private static FilterDefinitionBuilder<NoteDocument> F => Builders<NoteDocument>.Filter;

    /// <summary>Creates the unique path index and the trash expiry. Idempotent; writes call it first, so the
    /// server can start before MongoDB is reachable.</summary>
    public async Task EnsureIndexesAsync(CancellationToken ct)
    {
        if (_indexesReady) return;
        await _notes.Indexes.CreateOneAsync(
            new CreateIndexModel<NoteDocument>(
                Builders<NoteDocument>.IndexKeys.Ascending(n => n.Path),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
        await _trash.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<NoteDocument>(Builders<NoteDocument>.IndexKeys.Ascending(n => n.Path)),
                // MongoDB deletes expired documents itself (a background task, about once a minute).
                new CreateIndexModel<NoteDocument>(
                    Builders<NoteDocument>.IndexKeys.Ascending(n => n.DeletedAt),
                    new CreateIndexOptions { ExpireAfter = NoteWriter.TrashRetention }),
            ],
            ct);
        _indexesReady = true;
    }

    /// <summary>Writes a note the way seeding did before step 3: no revision, writer or history.
    /// Kept for tests of such documents.</summary>
    public async Task UpsertAsync(VaultPath path, string content, CancellationToken ct)
    {
        var update = Builders<NoteDocument>.Update
            .Set(n => n.Vault, path.Vault ?? "")
            .Set(n => n.Content, content)
            .Set(n => n.UpdatedAt, DateTime.UtcNow);
        await _notes.UpdateOneAsync(F.Eq(n => n.Path, path.Value), update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task<string?> ReadAsync(VaultPath path, CancellationToken ct)
    {
        var content = await _notes
            .Find(F.Eq(n => n.Path, path.Value))
            .Project(n => n.Content)
            .FirstOrDefaultAsync(ct);
        return content;
    }

    public async Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct)
    {
        var paths = await _notes
            .Find(Below(dir))
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

    public async Task<IReadOnlyList<Note>> ReadNotesUnderAsync(VaultPath dir, CancellationToken ct)
    {
        var docs = await _notes
            .Find(Below(dir))
            .Project(n => new { n.Path, n.Content })
            .ToListAsync(ct);

        var result = new List<Note>(docs.Count);
        foreach (var doc in docs)
        {
            if (VaultPath.TryParse(doc.Path, out var vp))
            {
                result.Add(new Note(vp, doc.Content));
            }
        }

        return result;
    }

    public async Task<StoredNote?> GetAsync(VaultPath path, CancellationToken ct)
    {
        var doc = await _notes.Find(F.Eq(n => n.Path, path.Value)).FirstOrDefaultAsync(ct);
        return doc is null ? null : ToNote(path, doc);
    }

    public async Task<bool> TryCreateAsync(StoredNote note, CancellationToken ct)
    {
        // Without the unique index, two creates of the same path would both succeed.
        await EnsureIndexesAsync(ct);
        try
        {
            await _notes.InsertOneAsync(ToDocument(note), cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<bool> TryReplaceAsync(StoredNote current, StoredNote next, CancellationToken ct)
    {
        var update = Builders<NoteDocument>.Update
            .Set(n => n.Content, next.Content)
            .Set(n => n.Revision, next.Revision)
            .Set(n => n.LastWriter, next.LastWriter)
            .Set(n => n.UpdatedAt, next.LastWriteAt)
            .Set(n => n.History, [.. next.History.Select(ToDocument)])
            .Set(n => n.SeedContent, next.SeedContent);
        var result = await _notes.UpdateOneAsync(AtRevision(current), update, cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task<bool> TryTrashAsync(StoredNote current, string deletedBy, DateTime deletedAt, CancellationToken ct)
    {
        // Copy first, then delete only if nobody wrote in between. A crash in between leaves a copy
        // in the trash and the note in place: nothing is lost.
        await EnsureIndexesAsync(ct);
        var copy = ToDocument(current);
        copy.DeletedAt = deletedAt;
        copy.DeletedBy = deletedBy;
        await _trash.InsertOneAsync(copy, cancellationToken: ct);

        var deleted = await _notes.DeleteOneAsync(AtRevision(current), ct);
        if (deleted.DeletedCount == 1) return true;

        await _trash.DeleteOneAsync(F.Eq(n => n.Id, copy.Id), ct);
        return false;
    }

    public async Task<IReadOnlyList<TrashedNote>> ListTrashAsync(VaultPath path, CancellationToken ct)
    {
        var docs = await _trash
            .Find(F.Or(F.Eq(n => n.Path, path.Value), Below(path)))
            .SortByDescending(n => n.DeletedAt)
            .ToListAsync(ct);
        return [.. docs.Select(ToTrashed).OfType<TrashedNote>()];
    }

    public async Task<StoredNote?> TryRestoreFromTrashAsync(VaultPath path, string? version, string writer, DateTime at, CancellationToken ct)
    {
        // Tokens are computed, not stored; a path has few trash entries, so compare them here.
        var docs = await _trash
            .Find(F.Eq(n => n.Path, path.Value))
            .SortByDescending(n => n.DeletedAt)
            .ToListAsync(ct);
        var doc = docs.FirstOrDefault(d => version is null || VersionToken.Of(d.Content) == version);
        if (doc is null) return null;

        var restored = ToNote(path, doc) with { Revision = doc.Revision + 1, LastWriter = writer, LastWriteAt = at };
        if (!await TryCreateAsync(restored, ct)) return null;

        await _trash.DeleteOneAsync(F.Eq(n => n.Id, doc.Id), ct);
        return restored;
    }

    private static FilterDefinition<NoteDocument> Below(VaultPath dir)
    {
        var prefix = dir.IsRoot ? "/" : dir.Value + "/";
        // Transitional: in step 5 paths are encrypted and an unlocked vault lives in RAM (concept §7).
        var filter = new BsonDocument("path", new BsonDocument("$regex", "^" + Regex.Escape(prefix)));
        return new BsonDocumentFilterDefinition<NoteDocument>(filter);
    }

    private static FilterDefinition<NoteDocument> AtRevision(StoredNote note)
    {
        // Documents from before step 3 have no "rev" field; they count as revision 0.
        var revision = note.Revision == 0
            ? F.Or(F.Eq(n => n.Revision, 0), F.Exists(n => n.Revision, false))
            : F.Eq(n => n.Revision, note.Revision);
        return F.And(F.Eq(n => n.Path, note.Path.Value), revision);
    }

    private static StoredNote ToNote(VaultPath path, NoteDocument doc) => new(
        path,
        doc.Content,
        doc.Revision,
        doc.LastWriter ?? Writers.Seed,
        DateTime.SpecifyKind(doc.UpdatedAt, DateTimeKind.Utc),
        [.. (doc.History ?? []).Select(h => new HistoryEntry(h.Content, h.WrittenBy, DateTime.SpecifyKind(h.WrittenAt, DateTimeKind.Utc)))],
        doc.SeedContent);

    private static TrashedNote? ToTrashed(NoteDocument doc) =>
        VaultPath.TryParse(doc.Path, out var path)
            ? new TrashedNote(ToNote(path, doc), doc.DeletedBy ?? "", DateTime.SpecifyKind(doc.DeletedAt ?? default, DateTimeKind.Utc))
            : null;

    private static NoteDocument ToDocument(StoredNote note) => new()
    {
        Vault = note.Path.Vault ?? "",
        Path = note.Path.Value,
        Content = note.Content,
        UpdatedAt = note.LastWriteAt,
        Revision = note.Revision,
        LastWriter = note.LastWriter,
        History = [.. note.History.Select(ToDocument)],
        SeedContent = note.SeedContent,
    };

    private static HistoryDocument ToDocument(HistoryEntry entry) =>
        new() { Content = entry.Content, WrittenBy = entry.WrittenBy, WrittenAt = entry.WrittenAt };
}
