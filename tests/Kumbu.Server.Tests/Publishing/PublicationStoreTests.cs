// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Publishing;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>Where publications are stored (publish spec §4): the vault's sealed record, and each copy as a mini vault
/// that opens with its share code alone.</summary>
[Collection("mongo")]
public sealed class PublicationStoreTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";
    private const string Note = "/Work/Secret plan.md";
    private const string Title = "Questions on routing";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 5, 14, 32, 0, TimeSpan.Zero));
    private readonly Argon2Kdf _kdf = new(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1));
    private VaultCatalog? _catalog;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private VaultCatalog Catalog() => _catalog ??= new(_db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance);

    private PublicationStore Store() => new(Catalog());

    private UserCatalog Users()
    {
        var keys = new ApiKeys(_db, Catalog(), _time, NullLogger<ApiKeys>.Instance);
        var connections = new Connections(_db, Catalog(), _time, NullLogger<Connections>.Instance);
        return new(_db, _kdf, keys, connections, _time, NullLogger<UserCatalog>.Instance, new UserKeyRotation(_db, _kdf, _time, NullLogger<UserKeyRotation>.Instance));
    }

    private IMongoCollection<BsonDocument> Raw(string name) => _db.GetCollection<BsonDocument>(name);

    private sealed record Account(User User, byte[] Key, Dictionary<string, UnlockedVault> Vaults);

    private async Task<Account> AccountAsync(params string[] vaults)
    {
        var users = Users();
        var username = "alice" + Guid.NewGuid().ToString("N")[..8];
        await users.CreateAsync(username, Password, Roles.User, Ct);
        var (user, key, readAt) = (await users.TrySignInAsync(username, Password, Ct))!.Value;
        var opened = new Dictionary<string, UnlockedVault>();
        foreach (var name in vaults) opened[name] = await Catalog().CreateForUserAsync(name, user.Id, key, Ct, readAt);
        return new Account(user, key, opened);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private PublishingRecord Record(string code, byte[] key, string note = Note)
    {
        var by = new WriterRefPayload(true, "session");
        return new PublishingRecord(
            [new PublicationEntry(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), note, code, key, Now, "v3", [new PublishedFile("/Work/img/plan.png", "v1")], "alice", by)],
            ["/Work/Private"],
            [new PublishingLogEntry(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), PublishingKinds.Published, note, Now, "alice", by, null)]);
    }

    private PublishedCopy Copy(string title = Title, int files = 2) =>
        new(title, $"# {title}\n\nWhere does the traffic go?\n", Now, [.. Enumerable.Range(0, files).Select(i => new PublishedCopyFile(i, $"picture-{i}.png", "image/png"))]);

    private static byte[][] Files(int count) => [.. Enumerable.Range(0, count).Select(i => Encoding.UTF8.GetBytes($"bytes of picture {i}"))];

    private async Task InTransactionAsync(Func<IClientSessionHandle, Task> work)
    {
        using var session = await Catalog().StartSessionAsync(Ct);
        session.StartTransaction();
        await work(session);
        await session.CommitTransactionAsync(Ct);
    }

    private Task WriteAsync(UnlockedVault vault, SealedDocument? current, PublishingRecord next) =>
        InTransactionAsync(s => Store().WriteAsync(s, vault, current, next, Ct));

    private Task InsertCopyAsync(string vaultId, string code, byte[] key, string owner, PublishedCopy copy, IReadOnlyList<byte[]> files) =>
        InTransactionAsync(s => Store().InsertCopyAsync(s, vaultId, code, key, owner, copy, files, Ct));

    private Task<long> CountAsync(string collection, string vaultId) =>
        Raw(collection).CountDocumentsAsync(new BsonDocument("vault", vaultId), cancellationToken: Ct);

    [Fact]
    public async Task A_record_opens_only_where_it_was_sealed()
    {
        var a = await AccountAsync("Work", "Gartenarbeit");
        var (work, garden) = (a.Vaults["Work"], a.Vaults["Gartenarbeit"]);
        Assert.Equal((null, Json(PublishingRecord.Empty)), await ReadJsonAsync(work));

        var first = Record(ShareCode.New(), VaultCrypto.NewKey());
        await WriteAsync(work, null, first);
        var (doc, read) = await Store().ReadAsync(null, work, Ct);
        Assert.Equal(1, doc!.Revision);
        Assert.Equal(Json(first), Json(read));

        var second = Record(ShareCode.New(), VaultCrypto.NewKey());
        await WriteAsync(work, doc, second);
        var (doc2, read2) = await Store().ReadAsync(null, work, Ct);
        Assert.Equal(2, doc2!.Revision);
        Assert.Equal(Json(second), Json(read2));
        // A writer that read the first revision: neither inserts nor replaces.
        await Assert.ThrowsAsync<PublishingConflictException>(() => WriteAsync(work, doc, first));
        await Assert.ThrowsAsync<PublishingConflictException>(() => WriteAsync(work, null, first));
        Assert.Equal(Json(second), Json((await Store().ReadAsync(null, work, Ct)).Record));

        // Work's blob in Gartenarbeit's document does not open there.
        await WriteAsync(garden, null, Record(ShareCode.New(), VaultCrypto.NewKey(), "/Gartenarbeit/Beet.md"));
        await Raw("publishing").UpdateOneAsync(new BsonDocument("vault", garden.Id), new BsonDocument("$set", new BsonDocument("blob", doc2.Blob)), cancellationToken: Ct);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Store().ReadAsync(null, garden, Ct));

        // Nor does it open at another revision.
        await Raw("publishing").UpdateOneAsync(new BsonDocument("vault", work.Id), new BsonDocument("$inc", new BsonDocument("rev", 1L)), cancellationToken: Ct);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Store().ReadAsync(null, work, Ct));
    }

    [Fact]
    public async Task A_write_over_a_changed_blob_at_the_same_revision_conflicts()
    {
        var a = await AccountAsync("Work");
        var work = a.Vaults["Work"];
        await WriteAsync(work, null, Record(ShareCode.New(), VaultCrypto.NewKey()));
        var (doc, _) = await Store().ReadAsync(null, work, Ct);
        await Raw("publishing").UpdateOneAsync(new BsonDocument("_id", doc!.Id), new BsonDocument("$set", new BsonDocument("blob", new BsonBinaryData([1, 2, 3]))), cancellationToken: Ct);

        await Assert.ThrowsAsync<PublishingConflictException>(() => WriteAsync(work, doc, PublishingRecord.Empty));
        Assert.Equal(1, (await Raw("publishing").Find(new BsonDocument("_id", doc.Id)).SingleAsync(Ct))["rev"].ToInt64());
    }

    [Fact]
    public async Task A_record_does_not_open_in_another_document_of_its_vault()
    {
        var a = await AccountAsync("Work");
        var work = a.Vaults["Work"];
        await WriteAsync(work, null, Record(ShareCode.New(), VaultCrypto.NewKey()));
        var raw = await Raw("publishing").Find(new BsonDocument("vault", work.Id)).SingleAsync(Ct);

        // The same vault, pid, revision and blob, planted under another _id.
        await Raw("publishing").DeleteOneAsync(new BsonDocument("_id", raw["_id"]), Ct);
        raw["_id"] = ObjectId.GenerateNewId();
        await Raw("publishing").InsertOneAsync(raw, cancellationToken: Ct);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => Store().ReadAsync(null, work, Ct));
    }

    private async Task<(long?, string)> ReadJsonAsync(UnlockedVault vault)
    {
        var (doc, record) = await Store().ReadAsync(null, vault, Ct);
        return (doc?.Revision, Json(record));
    }

    [Fact]
    public async Task Nothing_of_a_record_or_copy_is_in_the_clear()
    {
        var a = await AccountAsync("Work");
        var work = a.Vaults["Work"];
        var code = ShareCode.New();
        var key = VaultCrypto.NewKey();
        await WriteAsync(work, null, Record(code, key));
        await InsertCopyAsync(work.Id, code, key, a.User.Id, Copy(), Files(1));

        var raws = new[]
        {
            await Raw("publishing").Find(new BsonDocument("vault", work.Id)).SingleAsync(Ct),
            await Raw("publications").Find(new BsonDocument("vault", work.Id)).SingleAsync(Ct),
            await Raw("publication_files").Find(new BsonDocument("vault", work.Id)).SingleAsync(Ct),
        };
        string[] secrets = [Note, code, ShareCode.Format(code), Title, a.User.Id];
        foreach (var raw in raws)
        {
            var bytes = raw.ToBson();
            foreach (var secret in secrets)
            {
                var utf8 = Encoding.UTF8.GetBytes(secret);
                Assert.Equal(-1, bytes.AsSpan().IndexOf(utf8));
                Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Convert.ToBase64String(utf8))));
            }

            Assert.Equal(-1, bytes.AsSpan().IndexOf(key));
            Assert.Equal(-1, bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Convert.ToBase64String(key))));
        }
    }

    [Fact]
    public async Task A_copy_opens_with_its_code_alone()
    {
        var a = await AccountAsync("Work");
        var code = ShareCode.New();
        var copy = Copy();
        await InsertCopyAsync(a.Vaults["Work"].Id, code, VaultCrypto.NewKey(), a.User.Id, copy, Files(2));

        var opened = await Store().OpenCopyAsync(code, Ct);
        Assert.NotNull(opened);
        Assert.Equal(a.User.Id, opened.Value.Owner);
        Assert.Equal(Json(copy), Json(opened.Value.Copy));
        Assert.Null(await Store().OpenCopyAsync(ShareCode.New(), Ct));

        var raw = await Raw("publications").Find(new BsonDocument("vault", a.Vaults["Work"].Id)).SingleAsync(Ct);
        Assert.Equal(opened.Value.Id, raw["_id"].AsObjectId);
        Assert.Equal(["_id", "vault", "lookup", "reader", "blob", "at"], raw.Names);
        Assert.Equal(ShareCode.Lookup(code), raw["lookup"].AsByteArray);
        var file = await Raw("publication_files").Find(new BsonDocument("vault", a.Vaults["Work"].Id)).FirstAsync(Ct);
        Assert.Equal(["_id", "vault", "publication", "n", "blob"], file.Names);
    }

    [Fact]
    public async Task Files_open_only_at_their_index()
    {
        var a = await AccountAsync("Work");
        var code = ShareCode.New();
        var files = Files(2);
        await InsertCopyAsync(a.Vaults["Work"].Id, code, VaultCrypto.NewKey(), a.User.Id, Copy(), files);
        for (var i = 0; i < 2; i++)
        {
            var opened = await Store().OpenFileAsync(code, i, Ct);
            Assert.Equal(files[i], opened!.Value.Bytes);
            Assert.Equal("image/png", opened.Value.MediaType);
        }

        Assert.Null(await Store().OpenFileAsync(code, 2, Ct));
        Assert.Null(await Store().OpenFileAsync(ShareCode.New(), 0, Ct));

        var docs = await Raw("publication_files").Find(FilterDefinition<BsonDocument>.Empty).SortBy(d => d["n"]).ToListAsync(Ct);
        await Raw("publication_files").UpdateOneAsync(new BsonDocument("_id", docs[0]["_id"]), new BsonDocument("$set", new BsonDocument("blob", docs[1]["blob"])), cancellationToken: Ct);
        await Raw("publication_files").UpdateOneAsync(new BsonDocument("_id", docs[1]["_id"]), new BsonDocument("$set", new BsonDocument("blob", docs[0]["blob"])), cancellationToken: Ct);
        Assert.Null(await Store().OpenFileAsync(code, 0, Ct));
        Assert.Null(await Store().OpenFileAsync(code, 1, Ct));
    }

    [Fact]
    public async Task A_copy_opens_only_under_its_own_lookup()
    {
        var a = await AccountAsync("Work");
        var (code, other) = (ShareCode.New(), ShareCode.New());
        // Even under one publication key, which two publications never share.
        var key = VaultCrypto.NewKey();
        await InsertCopyAsync(a.Vaults["Work"].Id, code, key, a.User.Id, Copy(), Files(1));
        await InsertCopyAsync(a.Vaults["Work"].Id, other, key, a.User.Id, Copy("Another"), Files(1));
        Assert.Equal("Another", (await Store().OpenCopyAsync(other, Ct))!.Value.Copy.Title);

        // The other copy's blob under this code's lookup does not open.
        var docs = await Raw("publications").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct);
        var (mine, theirs) = (docs.Single(d => d["lookup"].AsByteArray.SequenceEqual(ShareCode.Lookup(code))), docs.Single(d => d["lookup"].AsByteArray.SequenceEqual(ShareCode.Lookup(other))));
        await Raw("publications").UpdateOneAsync(
            new BsonDocument("_id", mine["_id"]), new BsonDocument("$set", new BsonDocument { ["blob"] = theirs["blob"] }), cancellationToken: Ct);
        Assert.Null(await Store().OpenCopyAsync(code, Ct));
    }

    [Fact]
    public async Task Replacing_a_copy_keeps_its_lookup_and_time()
    {
        var a = await AccountAsync("Work");
        var code = ShareCode.New();
        var key = VaultCrypto.NewKey();
        await InsertCopyAsync(a.Vaults["Work"].Id, code, key, a.User.Id, Copy(), Files(2));
        var before = await Raw("publications").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct);

        _time.Advance(TimeSpan.FromHours(3));
        var next = Copy("Answers on routing", files: 1);
        var bytes = Files(1);
        await InTransactionAsync(s => Store().ReplaceCopyAsync(s, code, key, next, bytes, Ct));

        var after = await Raw("publications").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct);
        Assert.Equal(before["_id"], after["_id"]);
        Assert.Equal(before["lookup"], after["lookup"]);
        Assert.Equal(before["at"], after["at"]);
        Assert.NotEqual(before["blob"], after["blob"]);
        var opened = await Store().OpenCopyAsync(code, Ct);
        Assert.Equal(Json(next), Json(opened!.Value.Copy));
        Assert.Equal(a.User.Id, opened.Value.Owner);
        Assert.Equal(bytes[0], (await Store().OpenFileAsync(code, 0, Ct))!.Value.Bytes);
        Assert.Null(await Store().OpenFileAsync(code, 1, Ct));
        Assert.Equal(1, await CountAsync("publication_files", a.Vaults["Work"].Id));
    }

    [Fact]
    public async Task Replacing_a_copy_with_another_key_is_refused()
    {
        var a = await AccountAsync("Work");
        var code = ShareCode.New();
        var copy = Copy();
        await InsertCopyAsync(a.Vaults["Work"].Id, code, VaultCrypto.NewKey(), a.User.Id, copy, Files(1));

        await Assert.ThrowsAsync<PublishingConflictException>(
            () => InTransactionAsync(s => Store().ReplaceCopyAsync(s, code, VaultCrypto.NewKey(), Copy("Answers on routing", files: 1), Files(1), Ct)));

        Assert.Equal(Json(copy), Json((await Store().OpenCopyAsync(code, Ct))!.Value.Copy));
        Assert.NotNull(await Store().OpenFileAsync(code, 0, Ct));
    }

    [Fact]
    public async Task Deleting_a_copy_removes_it_with_its_files_and_nothing_else()
    {
        var a = await AccountAsync("Work", "Gartenarbeit");
        var (code, other, garden) = (ShareCode.New(), ShareCode.New(), ShareCode.New());
        await InsertCopyAsync(a.Vaults["Work"].Id, code, VaultCrypto.NewKey(), a.User.Id, Copy(), Files(2));
        await InsertCopyAsync(a.Vaults["Work"].Id, other, VaultCrypto.NewKey(), a.User.Id, Copy(), Files(1));
        await InsertCopyAsync(a.Vaults["Gartenarbeit"].Id, garden, VaultCrypto.NewKey(), a.User.Id, Copy(), Files(1));

        await InTransactionAsync(s => Store().DeleteCopyAsync(s, code, Ct));

        Assert.Null(await Store().OpenCopyAsync(code, Ct));
        Assert.NotNull(await Store().OpenCopyAsync(other, Ct));
        Assert.Equal(1, await CountAsync("publication_files", a.Vaults["Work"].Id));

        long deleted = 0;
        await InTransactionAsync(async s => deleted = await Store().DeleteCopiesOfVaultsAsync(s, [a.Vaults["Work"].Id], Ct));
        // The one copy left and its file.
        Assert.Equal(2, deleted);
        Assert.Null(await Store().OpenCopyAsync(other, Ct));
        Assert.Equal(0, await CountAsync("publications", a.Vaults["Work"].Id));
        Assert.Equal(0, await CountAsync("publication_files", a.Vaults["Work"].Id));
        Assert.NotNull(await Store().OpenCopyAsync(garden, Ct));
    }

    [Fact]
    public async Task A_rekey_keeps_the_record_and_the_copy()
    {
        var a = await AccountAsync("Work");
        var work = a.Vaults["Work"];
        var code = ShareCode.New();
        var key = VaultCrypto.NewKey();
        var record = Record(code, key);
        await WriteAsync(work, null, record);
        await InsertCopyAsync(work.Id, code, key, a.User.Id, Copy(), Files(1));
        var pid = (await Store().ReadAsync(null, work, Ct)).Doc!.PathId;

        await Catalog().RekeyAsync(work.Id, a.User.Id, a.Key, Ct);

        var rekeyed = (await Catalog().OpenForUserAsync(a.User.Id, a.Key, Ct)).Single(v => v.Name == "Work").Vault;
        Assert.NotSame(work, rekeyed);
        var (doc, read) = await Store().ReadAsync(null, rekeyed, Ct);
        Assert.NotNull(doc);
        Assert.NotEqual(pid, doc.PathId);
        Assert.Equal(Json(record), Json(read));
        Assert.NotNull(await Store().OpenCopyAsync(code, Ct));
        // And the record goes on: the next write replaces the resealed document.
        await WriteAsync(rekeyed, doc, PublishingRecord.Empty);
        Assert.Equal(Json(PublishingRecord.Empty), Json((await Store().ReadAsync(null, rekeyed, Ct)).Record));
    }

    [Fact]
    public async Task Deleting_the_vault_removes_every_publication_document()
    {
        var a = await AccountAsync("Work", "Gartenarbeit");
        var (work, garden) = (a.Vaults["Work"], a.Vaults["Gartenarbeit"]);
        var (code, gardenCode) = (ShareCode.New(), ShareCode.New());
        var key = VaultCrypto.NewKey();
        await WriteAsync(work, null, Record(code, key));
        await InsertCopyAsync(work.Id, code, key, a.User.Id, Copy(), Files(2));
        await WriteAsync(garden, null, Record(gardenCode, key, "/Gartenarbeit/Beet.md"));
        await InsertCopyAsync(garden.Id, gardenCode, key, a.User.Id, Copy(), Files(1));

        await Catalog().DeleteVaultsAsync([work.Id], (_, _) => Task.CompletedTask, Ct);

        foreach (var collection in new[] { "publishing", "publications", "publication_files" })
        {
            Assert.Equal(0, await CountAsync(collection, work.Id));
            Assert.Equal(1, await CountAsync(collection, garden.Id));
        }

        Assert.Null(await Store().OpenCopyAsync(code, Ct));
        Assert.NotNull(await Store().OpenCopyAsync(gardenCode, Ct));
    }
}
