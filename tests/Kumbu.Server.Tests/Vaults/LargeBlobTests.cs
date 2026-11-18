// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>Files larger than 15 MB (files spec, decisions 9–10): a sealed head in <c>blobs</c>, its chunks in
/// <c>blob_chunks</c>, each under the file's own key and bound to its place.</summary>
[Collection("mongo")]
public sealed class LargeBlobTests(MongoFixture fixture)
{
    private const int Chunk = 4 * 1024 * 1024;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(VaultWorld World, VaultCatalog Catalog, EncryptedNoteStore Store)> WorldAsync(
        int fileReads = 4, long maxFileBytes = VaultOptions.DefaultMaxFileBytes, int fileWrites = 4)
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog(fileReads: fileReads, maxFileBytes: maxFileBytes, fileWrites: fileWrites);
        await catalog.CreateAsync("Work", Token, (_, _) => Task.CompletedTask, Ct);
        return (world, catalog, Store(catalog));
    }

    private static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static Task<string> PutAsync(EncryptedNoteStore store, byte[] bytes, long? declared = null) =>
        store.PutBlobAsync("Work", new MemoryStream(bytes, writable: false), declared, Ct);

    private static async Task<byte[]?> ReadAsync(EncryptedNoteStore store, string blobId)
    {
        await using var stream = await store.OpenBlobAsync("Work", blobId, Ct);
        if (stream is null) return null;
        using var read = new MemoryStream();
        await stream.CopyToAsync(read, Ct);
        return read.ToArray();
    }

    private static IMongoCollection<BsonDocument> Raw(VaultWorld world, string name) => world.Db.GetCollection<BsonDocument>(name);

    private static FilterDefinitionBuilder<BsonDocument> F => Builders<BsonDocument>.Filter;

    private static long Count(VaultWorld world, string name) =>
        Raw(world, name).CountDocuments(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

    private static Task<BsonDocument> HeadAsync(VaultWorld world, string blobId) =>
        Raw(world, "blobs").Find(F.Eq("bid", Convert.FromBase64String(blobId))).FirstAsync(Ct);

    private static async Task<List<BsonDocument>> ChunksAsync(VaultWorld world, string blobId)
    {
        var head = await HeadAsync(world, blobId);
        return await Raw(world, "blob_chunks").Find(F.Eq("head", head["_id"])).SortBy(d => d["n"]).ToListAsync(Ct);
    }

    [Fact]
    public async Task A_40_MB_stream_round_trips_byte_for_byte()
    {
        var (world, _, store) = await WorldAsync();
        var bytes = Random(40_000_000, 1);

        var id = await PutAsync(store, bytes);

        Assert.Equal(bytes, await ReadAsync(store, id));
        Assert.Equal(bytes, await ReadAsync(Store(world.Catalog()), id));
        var head = await HeadAsync(world, id);
        Assert.Equal(["_id", "vault", "bid", "blob", "at", "chunks"], head.Names);
        Assert.Equal(10, head["chunks"].AsInt32);
        // The head is small whatever the file's size: the bytes are in the chunks.
        Assert.InRange(head["blob"].AsByteArray.Length, 1, 1024);
        var chunks = await ChunksAsync(world, id);
        Assert.Equal(Enumerable.Range(0, 10), chunks.Select(c => c["n"].AsInt32));
        Assert.Equal(["_id", "vault", "head", "n", "blob", "at"], chunks[0].Names);
        Assert.All(chunks.SkipLast(1), c => Assert.Equal(Chunk + 29, c["blob"].AsByteArray.Length));
    }

    [Fact]
    public async Task The_same_bytes_twice_are_stored_once()
    {
        var (world, _, store) = await WorldAsync();
        var bytes = Random(20_000_000, 2);

        var first = await PutAsync(store, bytes);
        var second = await PutAsync(store, bytes, bytes.Length);

        Assert.Equal(first, second);
        Assert.Equal(1, Count(world, "blobs"));
        Assert.Equal(5, Count(world, "blob_chunks"));
        Assert.Equal(bytes, await ReadAsync(store, second));
        // Other bytes of the same size get another id.
        Assert.NotEqual(first, await PutAsync(store, Random(20_000_000, 3)));
    }

    [Fact]
    public async Task A_stream_that_fails_half_way_leaves_no_file()
    {
        var (world, _, store) = await WorldAsync();

        var e = await Assert.ThrowsAsync<IOException>(() => store.PutBlobAsync("Work", new FailingStream(Random(30_000_000, 6), 25_000_000), null, Ct));

        Assert.Equal("The client went away", e.Message);
        Assert.Equal(0, Count(world, "blobs"));
        Assert.Equal(0, Count(world, "blob_chunks"));
    }

    [Fact]
    public async Task An_upload_whose_early_chunks_were_swept_meanwhile_is_refused()
    {
        var (world, _, store) = await WorldAsync();
        var chunks = Raw(world, "blob_chunks");
        // Half way, its first chunks go, as the sweep takes those of an upload that waited past the grace.
        var content = new FailingStream(Random(20_000_000, 15), int.MaxValue)
        {
            At = (18_000_000, () => chunks.DeleteMany(F.Eq("n", 0), Ct)),
        };

        await Assert.ThrowsAsync<ContentMissingException>(() => store.PutBlobAsync("Work", content, null, Ct));

        Assert.Equal(0, Count(world, "blobs"));
        Assert.Equal(0, Count(world, "blob_chunks"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_head_left_without_its_chunks_is_replaced_by_the_next_upload_of_its_bytes(int kept)
    {
        // As an upload leaves it whose head's insert reached the server while the call failed, and whose chunks went.
        var (world, _, store) = await WorldAsync();
        var bytes = Random(20_000_000, 16);
        var id = await PutAsync(store, bytes);
        var broken = (await HeadAsync(world, id))["_id"];
        await Raw(world, "blob_chunks").DeleteManyAsync(F.Eq("head", broken) & F.Gte("n", kept), Ct);
        await Assert.ThrowsAsync<VaultUnavailableException>(() => ReadAsync(Store(world.Catalog()), id));

        Assert.Equal(id, await PutAsync(store, bytes));

        Assert.Equal(bytes, await ReadAsync(Store(world.Catalog()), id));
        Assert.NotEqual(broken, (await HeadAsync(world, id))["_id"]);
        Assert.Equal(1, Count(world, "blobs"));
        Assert.Equal(5, Count(world, "blob_chunks"));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(0)]
    public async Task A_body_shorter_than_its_declared_length_is_refused(int size)
    {
        var (world, _, store) = await WorldAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => PutAsync(store, Random(size, 17), 20_000_000));

        Assert.Equal(0, Count(world, "blobs"));
        Assert.Equal(0, Count(world, "blob_chunks"));
    }

    [Fact]
    public async Task A_body_other_than_its_declared_length_is_refused_at_any_size()
    {
        var (world, _, store) = await WorldAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => PutAsync(store, Random(2000, 18), 1000));
        await Assert.ThrowsAsync<InvalidDataException>(() => PutAsync(store, Random(1000, 18), 2000));
        await Assert.ThrowsAsync<InvalidDataException>(() => PutAsync(store, Random(20_000_000, 18), 19_000_000));
        await Assert.ThrowsAsync<InvalidDataException>(() => PutAsync(store, Random(20_000_000, 18), 21_000_000));

        Assert.Equal(0, Count(world, "blobs"));
        Assert.Equal(0, Count(world, "blob_chunks"));
    }

    [Fact]
    public async Task A_negative_declared_length_is_refused()
    {
        var (world, _, store) = await WorldAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PutAsync(store, Random(1000, 19), -1));

        Assert.Equal(0, Count(world, "blobs"));
    }

    [Fact]
    public async Task Large_uploads_wait_for_a_write_slot()
    {
        var (world, _, store) = await WorldAsync(fileWrites: 1);
        var held = new TaskCompletionSource();
        var first = Task.Run(() => store.PutBlobAsync("Work", new FailingStream(Random(20_000_000, 20), int.MaxValue) { Wait = (18_000_000, held.Task) }, null, Ct), Ct);
        await WaitForAsync(() => Count(world, "blob_chunks") > 0);

        // A second large upload waits while the first holds the one slot; a small one doesn't.
        var second = PutAsync(store, Random(20_000_000, 21));
        await PutAsync(store, Random(1000, 22));
        await Task.Delay(500, Ct);
        Assert.False(second.IsCompleted);
        // Only the first's chunks so far.
        Assert.Single(await (await Raw(world, "blob_chunks").DistinctAsync<ObjectId>("head", F.Empty, cancellationToken: Ct)).ToListAsync(Ct));

        held.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(10, Count(world, "blob_chunks"));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(20, Ct);
        Assert.True(condition());
    }

    private static async Task<UnlockedVault> VaultAsync(VaultCatalog catalog) => Assert.Single(await catalog.OpenAsync(Token, Ct)).Vault;

    private static BlobSweep Sweep(VaultCatalog catalog) => new(catalog, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

    [Fact]
    public async Task The_sweep_releases_an_unreferenced_large_file_with_its_chunks()
    {
        var (world, catalog, store) = await WorldAsync();
        await PutAsync(store, Random(1000, 23));
        await PutAsync(store, Random(20_000_000, 24));
        var vault = await VaultAsync(catalog);

        // Within its grace it stays: its record may be on the way.
        Assert.Equal(0, await Sweep(catalog).RunAsync(vault, DateTime.UtcNow, Ct));
        Assert.Equal(5, Count(world, "blob_chunks"));

        Assert.Equal(2, await Sweep(catalog).RunAsync(vault, DateTime.UtcNow + TimeSpan.FromHours(1), Ct));
        Assert.Equal(0, Count(world, "blobs"));
        Assert.Equal(0, Count(world, "blob_chunks"));
    }

    [Fact]
    public async Task The_sweep_takes_chunks_without_a_head_once_the_newest_is_past_its_grace()
    {
        var (world, catalog, store) = await WorldAsync();
        var kept = await PutAsync(store, Random(20_000_000, 25));
        var headless = await HeadAsync(world, await PutAsync(store, Random(20_000_000, 26)));
        // As an upload leaves them that stopped half way: chunks no head names.
        await Raw(world, "blobs").DeleteOneAsync(F.Eq("_id", headless["_id"]), Ct);
        var orphans = F.Eq("head", headless["_id"]);
        var at = (await Raw(world, "blob_chunks").Find(orphans).SortByDescending(d => d["at"]).FirstAsync(Ct))["at"].ToUniversalTime();
        // Its first chunk long ago, as a slow upload's: the newest decides.
        await Raw(world, "blob_chunks").UpdateOneAsync(orphans & F.Eq("n", 0), Builders<BsonDocument>.Update.Set("at", at - TimeSpan.FromHours(2)), cancellationToken: Ct);
        var vault = await VaultAsync(catalog);

        Assert.Equal(0, await Sweep(catalog).RunAsync(vault, at + BlobSweep.Grace - TimeSpan.FromSeconds(1), Ct));
        Assert.Equal(10, Count(world, "blob_chunks"));

        // Past the grace, they go; the stored file is referenced by nothing either, but still within its own grace.
        Raw(world, "blobs").UpdateMany(F.Empty, Builders<BsonDocument>.Update.Set("at", at + TimeSpan.FromHours(1)), cancellationToken: Ct);
        Assert.Equal(1, await Sweep(catalog).RunAsync(vault, at + BlobSweep.Grace + TimeSpan.FromSeconds(1), Ct));
        Assert.Equal(5, Count(world, "blob_chunks"));
        Assert.Equal(Random(20_000_000, 25), await ReadAsync(store, kept));
    }

    [Fact]
    public async Task Deleting_a_vault_deletes_its_chunks_and_the_sweep_those_written_after()
    {
        var (world, catalog, store) = await WorldAsync();
        await catalog.CreateAsync("Other", Token, (_, _) => Task.CompletedTask, Ct);
        await PutAsync(store, Random(20_000_000, 27));
        await store.PutBlobAsync("Other", new MemoryStream(Random(20_000_000, 28)), null, Ct);
        var work = (await catalog.OpenAsync(Token, Ct)).Single(a => a.Vault.Name == "Work").Vault.Id;
        var stray = await Raw(world, "blob_chunks").Find(F.Eq("vault", work)).FirstAsync(Ct);

        await catalog.DeleteVaultsAsync([work], (_, _) => Task.CompletedTask, Ct);

        Assert.Equal(0, await Raw(world, "blob_chunks").CountDocumentsAsync(F.Eq("vault", work), cancellationToken: Ct));
        // As an upload that raced the deletion leaves it, past the deletion's own sweep.
        await Raw(world, "blob_chunks").InsertOneAsync(stray, cancellationToken: Ct);

        await Sweep(catalog).ReleaseChunksOfDeletedVaultsAsync(Ct);

        Assert.Equal(0, await Raw(world, "blob_chunks").CountDocumentsAsync(F.Eq("vault", work), cancellationToken: Ct));
        Assert.Equal(5, Count(world, "blob_chunks"));
    }

    [Fact]
    public async Task A_file_over_the_bound_is_refused()
    {
        var (world, _, store) = await WorldAsync(maxFileBytes: 20 * 1024 * 1024);

        var e = await Assert.ThrowsAsync<FileTooLargeException>(() => PutAsync(store, Random(21 * 1024 * 1024, 7)));
        Assert.Equal(20 * 1024 * 1024, e.Limit);
        Assert.Equal(0, Count(world, "blob_chunks"));

        // Declared, before a byte is read.
        await Assert.ThrowsAsync<FileTooLargeException>(() => store.PutBlobAsync("Work", new FailingStream([], 0), 21 * 1024 * 1024, Ct));
        // At the bound it fits.
        var bytes = Random(20 * 1024 * 1024, 8);
        Assert.Equal(bytes, await ReadAsync(store, await PutAsync(store, bytes)));
    }

    [Fact]
    public async Task A_large_file_is_read_a_chunk_at_a_time_each_with_a_slot()
    {
        // One slot: a stream that held it for its whole file would keep the second from starting.
        var (_, _, store) = await WorldAsync(fileReads: 1);
        var a = Random(20_000_000, 9);
        var b = Random(20_000_000, 10);
        var ids = (await PutAsync(store, a), await PutAsync(store, b));

        await using var first = (await store.OpenBlobAsync("Work", ids.Item1, Ct))!;
        await using var second = (await store.OpenBlobAsync("Work", ids.Item2, Ct))!;
        var buffer = new byte[1000];
        await first.ReadExactlyAsync(buffer, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(a[..1000], buffer);
        await second.ReadExactlyAsync(buffer, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(b[..1000], buffer);
    }

    [Fact]
    public async Task Whole_bytes_of_a_large_file_are_refused()
    {
        var (_, catalog, store) = await WorldAsync();
        var id = await PutAsync(store, Random(16 * 1024 * 1024, 11));
        var vault = Assert.Single(await catalog.OpenAsync(Token, Ct)).Vault;
        var state = new AttachmentState("application/pdf", 16 * 1024 * 1024, null, null, "v", id);

        await Assert.ThrowsAsync<TooLargeToReadException>(() => catalog.ReadBlobAsync(vault, Convert.FromBase64String(id), Ct));
        await Assert.ThrowsAsync<TooLargeToReadException>(() => StoreAttachments.ReadBytesAsync(store, P("/Work/big.pdf"), state, Ct));
    }

    /// <summary>Two files of five chunks each; <paramref name="tamper"/> edits the documents of the first, whose read
    /// must then fail as damaged, never return other bytes.</summary>
    private async Task AssertDamagedAsync(Func<VaultWorld, List<BsonDocument>, List<BsonDocument>, Task> tamper)
    {
        var (world, _, store) = await WorldAsync();
        var bytes = Random(20_000_000, 12);
        var id = await PutAsync(store, bytes);
        var other = await PutAsync(store, Random(20_000_000, 13));
        Assert.Equal(bytes, await ReadAsync(store, id));

        await tamper(world, await ChunksAsync(world, id), await ChunksAsync(world, other));

        await Assert.ThrowsAsync<VaultUnavailableException>(() => ReadAsync(Store(world.Catalog()), id));
    }

    private static Task SetAsync(VaultWorld world, BsonDocument chunk, string field, BsonValue value) =>
        Raw(world, "blob_chunks").UpdateOneAsync(F.Eq("_id", chunk["_id"]), Builders<BsonDocument>.Update.Set(field, value), cancellationToken: Ct);

    [Fact]
    public async Task Two_chunks_swapped_within_a_file_are_damage() => await AssertDamagedAsync(async (world, own, _) =>
    {
        await SetAsync(world, own[1], "blob", own[2]["blob"]);
        await SetAsync(world, own[2], "blob", own[1]["blob"]);
    });

    [Fact]
    public async Task A_chunk_from_another_file_is_damage() =>
        await AssertDamagedAsync((world, own, other) => SetAsync(world, own[1], "blob", other[1]["blob"]));

    [Fact]
    public async Task A_missing_last_chunk_is_damage() =>
        await AssertDamagedAsync((world, own, _) => Raw(world, "blob_chunks").DeleteOneAsync(F.Eq("_id", own[^1]["_id"]), Ct));

    [Fact]
    public async Task A_truncated_file_with_its_count_lowered_is_damage() => await AssertDamagedAsync(async (world, own, _) =>
    {
        await Raw(world, "blob_chunks").DeleteOneAsync(F.Eq("_id", own[^1]["_id"]), Ct);
        await Raw(world, "blobs").UpdateOneAsync(F.Eq("_id", own[0]["head"]), Builders<BsonDocument>.Update.Set("chunks", own.Count - 1), cancellationToken: Ct);
    });

    [Fact]
    public async Task An_extra_chunk_is_damage() => await AssertDamagedAsync(async (world, own, _) =>
    {
        var extra = own[0].DeepClone().AsBsonDocument;
        extra["_id"] = ObjectId.GenerateNewId();
        extra["n"] = own.Count;
        await Raw(world, "blob_chunks").InsertOneAsync(extra, cancellationToken: Ct);
    });

    [Fact]
    public async Task A_renumbered_chunk_is_damage() =>
        await AssertDamagedAsync((world, own, _) => SetAsync(world, own[^1], "n", own.Count));

    [Fact]
    public async Task A_changed_chunk_count_on_the_head_is_damage() =>
        await AssertDamagedAsync((world, own, _) =>
            Raw(world, "blobs").UpdateOneAsync(F.Eq("_id", own[0]["head"]), Builders<BsonDocument>.Update.Set("chunks", own.Count + 1), cancellationToken: Ct));

    [Fact]
    public async Task A_head_without_its_chunk_count_is_damage() =>
        await AssertDamagedAsync((world, own, _) =>
            Raw(world, "blobs").UpdateOneAsync(F.Eq("_id", own[0]["head"]), Builders<BsonDocument>.Update.Unset("chunks"), cancellationToken: Ct));

    [Fact]
    public async Task An_inline_blob_given_a_chunk_count_is_damage()
    {
        var (world, _, store) = await WorldAsync();
        var id = await PutAsync(store, Random(1000, 14));

        await Raw(world, "blobs").UpdateOneAsync(F.Eq("bid", Convert.FromBase64String(id)), Builders<BsonDocument>.Update.Set("chunks", 1), cancellationToken: Ct);

        await Assert.ThrowsAsync<VaultUnavailableException>(() => ReadAsync(Store(world.Catalog()), id));
    }

    [Fact]
    public async Task A_chunks_seal_moved_onto_another_number_with_its_count_is_damage() => await AssertDamagedAsync(async (world, own, _) =>
    {
        // The last chunk's seal in place of an earlier one: only the last may say it is the last.
        await SetAsync(world, own[3], "blob", own[^1]["blob"]);
    });

    /// <summary>Serves <paramref name="bytes"/> up to <paramref name="failAt"/>, then fails as a client that went away.</summary>
    private sealed class FailingStream(byte[] bytes, int failAt) : Stream
    {
        private int _position;

        /// <summary>Something to do once, when the reads pass a position.</summary>
        public (int Position, Action Then)? At { get; init; }

        /// <summary>Something to wait for once, when the reads pass a position.</summary>
        public (int Position, Task Until)? Wait { get; init; }

        private bool _done;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= failAt) throw new IOException("The client went away");
            if (At is { } at && !_done && _position >= at.Position)
            {
                _done = true;
                at.Then();
            }

            if (Wait is { } wait && _position >= wait.Position) wait.Until.Wait();

            var n = Math.Min(count, Math.Min(failAt, bytes.Length) - _position);
            Array.Copy(bytes, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
