// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>Attachments in an encrypted vault: sealed records with the notes, their bytes in content-keyed blobs.</summary>
[Collection("mongo")]
public sealed class AttachmentStoreTests(MongoFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime T0 = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _time = new(T0);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private async Task<(VaultWorld World, VaultCatalog Catalog, EncryptedNoteStore Store)> WorldAsync()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog(time: _time);
        await catalog.CreateAsync("Work", Token, (_, _) => Task.CompletedTask, Ct);
        return (world, catalog, Store(catalog));
    }

    private static byte[] Png(int size, byte fill = 7)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, fill);
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        return bytes;
    }

    private async Task<StoredAttachment> AttachmentAsync(EncryptedNoteStore store, string path, byte[] bytes, Changeset? changeset = null)
    {
        var blobId = await store.PutBlobAsync("Work", bytes, Ct);
        var state = new AttachmentState("image/png", bytes.Length, 640, 480, VersionToken.Of(bytes), blobId);
        return new StoredAttachment(P(path), state, 1, Writers.Dev, Now, [], changeset);
    }

    private async Task<StoredAttachment> CreateAsync(EncryptedNoteStore store, string path, byte[] bytes, Changeset? changeset = null)
    {
        var attachment = await AttachmentAsync(store, path, bytes, changeset);
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentCreates = { attachment } }, Writers.Dev, Now, Ct));
        return attachment;
    }

    private static async Task<byte[]?> ReadBlobAsync(EncryptedNoteStore store, string blobId)
    {
        await using var stream = await store.OpenBlobAsync("Work", blobId, Ct);
        if (stream is null) return null;
        using var read = new MemoryStream();
        await stream.CopyToAsync(read, Ct);
        return read.ToArray();
    }

    private static IMongoCollection<BsonDocument> Raw(VaultWorld world, string name) => world.Db.GetCollection<BsonDocument>(name);

    private BlobSweep Sweep(VaultCatalog catalog) => new(catalog, NullLogger.Instance);

    private static async Task<UnlockedVault> VaultAsync(VaultCatalog catalog) => Assert.Single(await catalog.OpenAsync(Token, Ct));

    [Fact]
    public async Task An_attachment_round_trips_in_a_second_process()
    {
        var (world, _, store) = await WorldAsync();
        var bytes = Png(3000);
        var older = Png(2000, 9);
        var changeset = Changeset.New(T0);
        var attachment = await AttachmentAsync(store, "/Work/Screens/shot.png", bytes, changeset);
        var olderState = new AttachmentState("image/png", older.Length, 10, 20, VersionToken.Of(older), await store.PutBlobAsync("Work", older, Ct));
        attachment = attachment with { Revision = 2, History = [new AttachmentVersion(olderState, Writers.Seed, T0.AddDays(-1), null)] };
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentCreates = { attachment } }, Writers.Dev, T0, Ct));

        var later = Store(world.Catalog(time: _time));
        var read = await later.GetAttachmentAsync(P("/Work/Screens/shot.png"), Ct);

        Assert.NotNull(read);
        Assert.Equal(attachment with { History = [] }, read with { History = [] });
        Assert.Equal(attachment.History, read.History);
        Assert.Equal([P("/Work/Screens/shot.png")], (await later.ListAttachmentsUnderAsync(P("/Work/Screens"), Ct)).Select(a => a.Path));
        Assert.Empty(await later.ListAttachmentsUnderAsync(P("/Work/Other"), Ct));
        Assert.Equal(bytes, await ReadBlobAsync(later, read.Current.BlobId));
        Assert.Equal(older, await ReadBlobAsync(later, read.History[0].State.BlobId));
        // Notes and attachments share one changeset per writer.
        Assert.Equal((changeset, T0), await later.LatestChangesetAsync("Work", Writers.Dev, Ct));
    }

    [Fact]
    public async Task Replacing_and_trashing_an_attachment_and_restoring_it_from_the_trash()
    {
        var (world, _, store) = await WorldAsync();
        var first = await CreateAsync(store, "/Work/a.png", Png(100));
        var second = await AttachmentAsync(store, "/Work/a.png", Png(100, 8));
        var next = second with { Revision = 2, History = [new AttachmentVersion(first.Current, first.LastWriter, first.LastWriteAt, null)] };
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentReplaces = { (first, next) } }, Writers.Dev, Now, Ct));
        // A stale revision is refused.
        Assert.Equal([P("/Work/a.png")], await store.TryCommitAsync(new NoteBatch { AttachmentReplaces = { (first, next with { Revision = 3 }) } }, Writers.Dev, Now, Ct));

        var changeset = Changeset.New(Now);
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentTrashes = { next }, Changeset = changeset }, "lea", Now.AddMinutes(1), Ct));
        Assert.Null(await store.GetAttachmentAsync(P("/Work/a.png"), Ct));
        Assert.Equal((changeset, Now.AddMinutes(1)), await store.LatestChangesetAsync("Work", "lea", Ct));

        var later = Store(world.Catalog(time: _time));
        var trashed = Assert.Single(await later.ListAttachmentTrashAsync(P("/Work"), Ct));
        Assert.Equal(next.Current, trashed.Attachment.Current);
        Assert.Equal(("lea", Now.AddMinutes(1), changeset), (trashed.DeletedBy, trashed.DeletedAt, trashed.Changeset));
        Assert.Equal((changeset, Now.AddMinutes(1)), await later.LatestChangesetAsync("Work", "lea", Ct));

        var restored = trashed.Attachment with { Revision = trashed.Attachment.Revision + 1, Changeset = null };
        Assert.Empty(await later.TryCommitAsync(new NoteBatch { AttachmentRestores = { (trashed, restored) } }, Writers.Dev, Now, Ct));
        // The same trash entry can't come back twice.
        Assert.NotEmpty(await later.TryCommitAsync(new NoteBatch { AttachmentRestores = { (trashed, restored) } }, Writers.Dev, Now, Ct));

        var again = Store(world.Catalog(time: _time));
        Assert.Equal(restored.Current, (await again.GetAttachmentAsync(P("/Work/a.png"), Ct))!.Current);
        Assert.Empty(await again.ListAttachmentTrashAsync(null, Ct));
    }

    [Fact]
    public async Task A_note_and_an_attachment_commit_together_or_not_at_all()
    {
        var (world, _, store) = await WorldAsync();
        Assert.True(await store.TryCreateAsync(new StoredNote(P("/Work/a.md"), "a", 1, Writers.Dev, T0, []), Ct));
        var attachment = await AttachmentAsync(store, "/Work/x.png", Png(100));

        var conflicts = await store.TryCommitAsync(
            new NoteBatch { Creates = { new StoredNote(P("/Work/a.md"), "again", 1, Writers.Dev, T0, []) }, AttachmentCreates = { attachment } }, Writers.Dev, T0, Ct);

        Assert.Equal([P("/Work/a.md")], conflicts);
        Assert.Null(await store.GetAttachmentAsync(P("/Work/x.png"), Ct));
        Assert.Null(await Store(world.Catalog()).GetAttachmentAsync(P("/Work/x.png"), Ct));
        Assert.Equal(0, await Raw(world, "attachments").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
    }

    [Fact]
    public async Task An_attachment_moves_with_a_note_in_one_transaction_and_is_recorded_as_moved()
    {
        var (world, _, store) = await WorldAsync();
        var note = new StoredNote(P("/Work/a.md"), "a", 1, Writers.Dev, T0, []);
        Assert.True(await store.TryCreateAsync(note, Ct));
        var shot = await CreateAsync(store, "/Work/Screens/shot.png", Png(100));
        var changeset = Changeset.New(T0);
        var batch = new NoteBatch
        {
            Moves = { (note, note with { Path = P("/Work/Trip/a.md"), Revision = 2 }) },
            AttachmentMoves = { (shot, shot with { Path = P("/Work/Trip/shot.png"), Revision = 2 }) },
            Changeset = changeset,
        };

        Assert.Empty(await store.TryCommitAsync(batch, Writers.Dev, T0.AddMinutes(1), Ct));

        var later = Store(world.Catalog(time: _time));
        Assert.Null(await later.GetAttachmentAsync(P("/Work/Screens/shot.png"), Ct));
        var moved = await later.GetAttachmentAsync(P("/Work/Trip/shot.png"), Ct);
        Assert.Equal((shot.Current, 2L), (moved!.Current, moved.Revision));
        Assert.Equal(VersionToken.Of(Png(100)), moved.Version);
        // Sync and 'mv' hints read the moves: an attachment's move is recorded as a note's is.
        var record = await later.FindMoveAsync(P("/Work/Screens/shot.png"), Ct);
        Assert.Equal((P("/Work/Trip/shot.png"), Writers.Dev, T0.AddMinutes(1), changeset.Id), (record!.To, record.MovedBy, record.MovedAt, record.Changeset?.Id));
        Assert.Equal(
            [P("/Work/Trip/a.md"), P("/Work/Trip/shot.png")],
            (await later.ListMovesAsync(P("/Work"), Ct)).Select(m => m.To).OrderBy(p => p.Value));
    }

    [Fact]
    public async Task An_attachment_move_onto_an_existing_attachment_changes_nothing()
    {
        var (world, _, store) = await WorldAsync();
        var note = new StoredNote(P("/Work/a.md"), "a", 1, Writers.Dev, T0, []);
        Assert.True(await store.TryCreateAsync(note, Ct));
        var shot = await CreateAsync(store, "/Work/shot.png", Png(100));
        await CreateAsync(store, "/Work/taken.png", Png(200));

        var conflicts = await store.TryCommitAsync(
            new NoteBatch
            {
                Moves = { (note, note with { Path = P("/Work/b.md"), Revision = 2 }) },
                AttachmentMoves = { (shot, shot with { Path = P("/Work/taken.png"), Revision = 2 }) },
            },
            Writers.Dev, T0, Ct);

        Assert.NotEmpty(conflicts);
        var later = Store(world.Catalog(time: _time));
        Assert.NotNull(await later.GetAsync(P("/Work/a.md"), Ct));
        Assert.Null(await later.GetAsync(P("/Work/b.md"), Ct));
        Assert.NotNull(await later.GetAttachmentAsync(P("/Work/shot.png"), Ct));
        Assert.Equal(VersionToken.Of(Png(200)), (await later.GetAttachmentAsync(P("/Work/taken.png"), Ct))!.Version);
        Assert.Empty(await later.ListMovesAsync(P("/Work"), Ct));
        Assert.Null(await store.FindMoveAsync(P("/Work/a.md"), Ct));
    }

    [Fact]
    public async Task Identical_bytes_at_two_paths_are_one_blob_and_survive_removing_one()
    {
        var (world, catalog, store) = await WorldAsync();
        var bytes = Png(5000);
        var a = await CreateAsync(store, "/Work/a.png", bytes);
        var b = await CreateAsync(store, "/Work/Copy/b.png", bytes.ToArray());
        Assert.Equal(a.Current.BlobId, b.Current.BlobId);
        Assert.Equal(1, await Raw(world, "blobs").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));

        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentTrashes = { a } }, Writers.Dev, Now, Ct));
        _time.Advance(TimeSpan.FromDays(31));
        await Sweep(catalog).RunAsync(await VaultAsync(catalog), Now, Ct);

        Assert.Empty(await store.ListAttachmentTrashAsync(null, Ct));
        Assert.Equal(0, await Raw(world, "attachment_trash").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
        Assert.Equal(bytes, await ReadBlobAsync(Store(world.Catalog(time: _time)), b.Current.BlobId));

        // Once the last reference has gone past the trash's retention too, so do the bytes.
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentTrashes = { b } }, Writers.Dev, Now, Ct));
        _time.Advance(TimeSpan.FromDays(29));
        await Sweep(catalog).RunAsync(await VaultAsync(catalog), Now, Ct);
        Assert.NotNull(await ReadBlobAsync(store, b.Current.BlobId));
        _time.Advance(TimeSpan.FromDays(2));
        await Sweep(catalog).RunAsync(await VaultAsync(catalog), Now, Ct);
        Assert.Null(await ReadBlobAsync(store, b.Current.BlobId));
    }

    [Fact]
    public async Task A_blob_id_in_the_database_is_not_a_plain_sha256()
    {
        var (world, _, store) = await WorldAsync();
        var bytes = Png(1000);
        await CreateAsync(store, "/Work/a.png", bytes);

        var doc = Assert.Single(await Raw(world, "blobs").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct));
        var bid = doc["bid"].AsByteArray;
        Assert.Equal(32, bid.Length);
        Assert.NotEqual(SHA256.HashData(bytes), bid);
        // The bytes are sealed: not even their signature is in the clear.
        Assert.DoesNotContain(Convert.ToHexString(bytes.AsSpan(0, 16)), Convert.ToHexString(doc["blob"].AsByteArray), StringComparison.Ordinal);
        // The record is sealed too: no path, size or blob id in the clear.
        var record = Assert.Single(await Raw(world, "attachments").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct));
        Assert.Equal(["_id", "vault", "pid", "rev", "blob"], record.Names);
    }

    [Fact]
    public async Task A_blob_that_was_changed_in_the_database_does_not_open()
    {
        var (world, _, store) = await WorldAsync();
        var a = await CreateAsync(store, "/Work/a.png", Png(1000));
        var other = await CreateAsync(store, "/Work/b.png", Png(1000, 3));
        var blobs = Raw(world, "blobs");
        var otherDoc = await blobs.Find(Builders<BsonDocument>.Filter.Eq("bid", Convert.FromBase64String(other.Current.BlobId))).FirstAsync(Ct);

        // Another blob's sealed bytes under this blob's id: the associated data binds the id.
        await blobs.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("bid", Convert.FromBase64String(a.Current.BlobId)),
            Builders<BsonDocument>.Update.Set("blob", otherDoc["blob"]),
            cancellationToken: Ct);

        await Assert.ThrowsAsync<VaultUnavailableException>(() => ReadBlobAsync(store, a.Current.BlobId));
    }

    [Fact]
    public async Task Blobs_are_not_loaded_with_the_vault()
    {
        var (world, _, store) = await WorldAsync();
        var before = (await VaultAsync(world.Catalog(time: _time))).ApproxBytes;

        await CreateAsync(store, "/Work/big.png", Png(5 * 1024 * 1024));

        var after = (await VaultAsync(world.Catalog(time: _time))).ApproxBytes;
        Assert.InRange(after - before, 1, 1023);
    }

    [Fact]
    public async Task A_sweep_releases_a_blob_nothing_references_and_keeps_a_fresh_one()
    {
        var (world, catalog, store) = await WorldAsync();
        var orphan = await store.PutBlobAsync("Work", Png(100, 1), Ct);
        var kept = await CreateAsync(store, "/Work/kept.png", Png(100, 2));
        _time.Advance(TimeSpan.FromMinutes(9));
        var fresh = await store.PutBlobAsync("Work", Png(100, 3), Ct);
        _time.Advance(TimeSpan.FromMinutes(2));

        await Sweep(catalog).RunAsync(await VaultAsync(catalog), Now, Ct);

        Assert.Null(await ReadBlobAsync(store, orphan));
        Assert.NotNull(await ReadBlobAsync(store, kept.Current.BlobId));
        // Younger than ten minutes: an upload whose record isn't committed yet.
        Assert.NotNull(await ReadBlobAsync(store, fresh));
        Assert.Equal(2, await Raw(world, "blobs").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));

        // Putting bytes the vault holds already makes their blob fresh again.
        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(fresh, await store.PutBlobAsync("Work", Png(100, 3), Ct));
        await Sweep(catalog).RunAsync(await VaultAsync(catalog), Now, Ct);
        Assert.NotNull(await ReadBlobAsync(store, fresh));
    }

    [Fact]
    public async Task A_sweep_keeps_a_blob_another_process_references()
    {
        var (world, catalog, _) = await WorldAsync();
        var vault = await VaultAsync(catalog);
        // Another process holds the vault too and writes an attachment this one's working set doesn't know.
        var other = Store(world.Catalog(time: _time));
        var attachment = await CreateAsync(other, "/Work/elsewhere.png", Png(100));
        _time.Advance(TimeSpan.FromMinutes(11));

        await Sweep(catalog).RunAsync(vault, Now, Ct);

        Assert.NotNull(await ReadBlobAsync(other, attachment.Current.BlobId));
    }

    [Fact]
    public async Task A_checkpoint_keeps_its_blobs_until_it_is_removed()
    {
        var (world, catalog, _) = await WorldAsync();
        // This process knows the vault from before the checkpoint: only what MongoDB holds tells it about one.
        var stale = await VaultAsync(catalog);
        var otherCatalog = world.Catalog(time: _time);
        var other = Store(otherCatalog);
        var shot = await CreateAsync(other, "/Work/shot.png", Png(100));
        var checkpoint = new Checkpoint("before", P("/Work"), true, Writers.Dev, Now, []) { Attachments = [new(shot.Path, shot.Current)] };
        Assert.True(await other.TryAddCheckpointAsync(checkpoint, [], Ct));
        // The attachment is removed and its trash entry expires: the checkpoint alone references the bytes.
        Assert.Empty(await other.TryCommitAsync(new NoteBatch { AttachmentTrashes = { shot } }, Writers.Dev, Now, Ct));
        _time.Advance(TimeSpan.FromDays(31));

        await Sweep(catalog).RunAsync(stale, Now, Ct);
        await Sweep(otherCatalog).RunAsync(await VaultAsync(otherCatalog), Now, Ct);

        Assert.Empty(await other.ListAttachmentTrashAsync(null, Ct));
        Assert.Equal(Png(100), await ReadBlobAsync(other, shot.Current.BlobId));
        // A second process reads the checkpoint with its attachment.
        var held = Assert.Single(Assert.Single(await Store(world.Catalog(time: _time)).ListCheckpointsAsync(P("/Work"), Ct)).Attachments);
        Assert.Equal((shot.Path, shot.Current), (held.Path, held.State));

        Assert.True(await other.TryRemoveCheckpointAsync("Work", "before", Ct));
        await Sweep(otherCatalog).RunAsync(await VaultAsync(otherCatalog), Now, Ct);
        Assert.Null(await ReadBlobAsync(other, shot.Current.BlobId));
    }

    [Fact]
    public async Task History_versions_keep_their_blobs()
    {
        var (_, catalog, store) = await WorldAsync();
        var first = await CreateAsync(store, "/Work/a.png", Png(100));
        var second = await AttachmentAsync(store, "/Work/a.png", Png(100, 8));
        var next = second with { Revision = 2, History = [new AttachmentVersion(first.Current, first.LastWriter, first.LastWriteAt, null)] };
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentReplaces = { (first, next) } }, Writers.Dev, Now, Ct));
        _time.Advance(TimeSpan.FromHours(1));

        await Sweep(catalog).RunAsync(await VaultAsync(catalog), Now, Ct);

        Assert.NotNull(await ReadBlobAsync(store, first.Current.BlobId));
        Assert.NotNull(await ReadBlobAsync(store, next.Current.BlobId));
    }

    [Fact]
    public async Task A_sealed_blob_of_the_size_limit_fits_in_one_document()
    {
        var (_, _, store) = await WorldAsync();
        var bytes = Png((int)AttachmentFormat.MaxBytes);

        var attachment = await CreateAsync(store, "/Work/limit.png", bytes);

        Assert.Equal(bytes, await ReadBlobAsync(store, attachment.Current.BlobId));
    }

    [Fact]
    public async Task A_restore_names_its_trash_entry_by_value_not_by_instance()
    {
        var (world, _, store) = await WorldAsync();
        var a = await CreateAsync(store, "/Work/a.png", Png(100));
        var next = a with { Revision = 2, History = [new AttachmentVersion(a.Current, a.LastWriter, a.LastWriteAt, null)] };
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentReplaces = { (a, next) } }, Writers.Dev, Now, Ct));
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentTrashes = { next } }, Writers.Dev, Now, Ct));
        var listed = Assert.Single(await store.ListAttachmentTrashAsync(null, Ct));
        // Equal, but with its own history list: as a request that rebuilt it from what it was shown would have it.
        var copy = listed with { Attachment = listed.Attachment with { History = [.. listed.Attachment.History] } };
        Assert.NotEqual(listed, copy);

        Assert.Empty(await store.TryCommitAsync(
            new NoteBatch { AttachmentRestores = { (copy, copy.Attachment with { Revision = 3 }) } }, Writers.Dev, Now, Ct));

        Assert.Equal(3, (await Store(world.Catalog(time: _time)).GetAttachmentAsync(P("/Work/a.png"), Ct))!.Revision);
    }

    [Fact]
    public async Task A_damaged_record_is_marked_damaged_until_it_opens_again()
    {
        var (world, _, store) = await WorldAsync();
        await CreateAsync(store, "/Work/a.png", Png(100));
        var records = Raw(world, "attachments");
        var original = (await records.Find(FilterDefinition<BsonDocument>.Empty).FirstAsync(Ct))["blob"].AsByteArray;
        var tampered = original.ToArray();
        tampered[^1] ^= 1;
        await records.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("blob", tampered), cancellationToken: Ct);

        var later = Store(world.Catalog(time: _time));
        Assert.Null(await later.GetAttachmentAsync(P("/Work/a.png"), Ct));
        Assert.True(await later.IsDamagedAsync(P("/Work/a.png"), Ct));
        Assert.Equal(1, await later.CountDamagedAsync(P("/Work"), Ct));

        // Repaired in the database: the next read finds it, without a restart.
        await records.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("blob", original), cancellationToken: Ct);
        Assert.NotNull(await later.GetAttachmentAsync(P("/Work/a.png"), Ct));
        Assert.False(await later.IsDamagedAsync(P("/Work/a.png"), Ct));
        Assert.Equal(0, await later.CountDamagedAsync(P("/Work"), Ct));
    }
}
