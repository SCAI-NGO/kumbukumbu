// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kumbu.Core.Attachments;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Search;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;
using FakeEmbedder = Kumbu.Server.Tests.Search.FakeEmbedder;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>Stage 2 of re-keying (rekeying design §3–§5): a vault's documents sealed again under a new vault key, in
/// batches with a two-key period, safe against crashes, stale writers and deletions.</summary>
[Collection("mongo")]
public sealed class RekeyTests(MongoFixture fixture) : IAsyncDisposable
{
    private const string Password = "correct horse battery staple";

    private static readonly string[] Collections = ["notes", "trash", "moves", "embeddings", "checkpoints", "checkpoint_texts", "attachments", "attachment_trash"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
    private readonly Argon2Kdf _kdf = new(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1));
    private readonly CapturedLog _log = new();
    private readonly FakeEmbedder _remote = new(model: "remote-1", dimensions: 8);
    private readonly TestLifetime _life = new();
    private VaultCatalog? _catalog;
    private SearchIndex? _index;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>How many texts of notes (not queries) went to the external provider.</summary>
    private int RemoteNotes
    {
        get
        {
            lock (_remote.Seen) return _remote.Seen.Count(s => !s.Query);
        }
    }

    private ILoggerFactory Logs => LoggerFactory.Create(b => b.AddProvider(_log));

    private VaultCatalog Catalog() => _catalog ??= NewCatalog();

    /// <summary>Another server process on the same database.</summary>
    private VaultCatalog NewCatalog() => new(_db, Options.Create(new KumbuOptions()), _time, Logs.CreateLogger<VaultCatalog>());

    private SearchIndex Index() =>
        _index ??= new SearchIndex(Catalog(), EmbeddingProviders.Of(new FakeEmbedder(), new EmbeddingProvider("remote", "Remote", null, _remote)), NullLogger<SearchIndex>.Instance);

    private ApiKeys Keys() => new(_db, Catalog(), _time, NullLogger<ApiKeys>.Instance);

    private Connections Connections() => new(_db, Catalog(), _time, NullLogger<Connections>.Instance);

    private UserKeyRotation Rotation() => new(_db, _kdf, _time, NullLogger<UserKeyRotation>.Instance);

    private UserCatalog Users() => new(_db, _kdf, Keys(), Connections(), _time, NullLogger<UserCatalog>.Instance, Rotation());

    private readonly List<RekeyRunner> _runners = [];

    /// <summary>A runner, started as the host starts it; stopped with the test.</summary>
    private async Task<RekeyRunner> RunnerAsync()
    {
        var runner = new RekeyRunner(Catalog(), _life, _time, Logs.CreateLogger<RekeyRunner>());
        await runner.StartAsync(Ct);
        _runners.Add(runner);
        return runner;
    }

    private IMongoCollection<SlotDocument> Slots => _db.GetCollection<SlotDocument>("slots");

    private IMongoCollection<SealedDocument> Docs(string collection) => _db.GetCollection<SealedDocument>(collection);

    private IMongoCollection<BsonDocument> Raw(string collection) => _db.GetCollection<BsonDocument>(collection);

    public async ValueTask DisposeAsync()
    {
        if (_index is not null)
        {
            await _index.StopAsync(CancellationToken.None);
            _index.Dispose();
        }

        foreach (var runner in _runners) await runner.StopAsync(CancellationToken.None);
        _life.Stop();
    }

    private sealed record Account(User User, byte[] Key, DateTime ReadAt, Dictionary<string, string> Vaults)
    {
        public string Id(string vault) => Vaults[vault];
    }

    /// <summary>A new user, signed in, with a vault of their own for each name.</summary>
    private async Task<Account> AccountAsync(params string[] vaults)
    {
        var users = Users();
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, Password, Roles.User, Ct);
        var (user, key, readAt) = (await users.TrySignInAsync(username, Password, Ct))!.Value;
        var ids = new Dictionary<string, string>();
        foreach (var name in vaults) ids[name] = (await Catalog().CreateForUserAsync(name, user.Id, key, Ct, readAt)).Id;
        return new Account(user, key, readAt, ids);
    }

    /// <summary>A request of the user: its own store, as every request has.</summary>
    private EncryptedNoteStore Store(Account a, VaultCatalog? catalog = null, byte[]? key = null) =>
        new(catalog ?? Catalog(), new UserCredential(a.User.Id, key ?? a.Key), _index);

    private StoredNote New(string path, string content) => new(P(path), content, 1, "lea", Now, [], Ref: WriterRef.Server);

    private static Checkpoint Cp(string name, string path, params StoredNote[] notes) =>
        new(name, P(path), !path.EndsWith(".md", StringComparison.Ordinal), "lea", new DateTime(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc), [.. notes.Select(n => new CheckpointNote(n.Path, n.Version))]) { Ref = WriterRef.Server };

    /// <summary>
    /// The vault "Arbeit" as the brief has it: notes, one with history, a trashed note, a move, embeddings by an external
    /// provider the owner chose, and two checkpoints that share a text.
    /// </summary>
    private async Task<Account> FilledAsync(params string[] more)
    {
        var a = await AccountAsync(["Arbeit", .. more]);
        await Index().StartAsync(Ct);
        var store = Store(a);
        var vault = (await store.UnlockedVaultsAsync(Ct)).Single(v => v.Name == "Arbeit");
        await Catalog().SetEmbeddingProviderAsync(vault, "remote", Ct);

        Assert.True(await store.TryCreateAsync(New("/Arbeit/plan.md", "# Plan\n\nalpha beta gamma\n"), Ct));
        for (var i = 1; i <= 3; i++)
        {
            var current = (await store.GetAsync(P("/Arbeit/plan.md"), Ct))!;
            Assert.True(await store.TryReplaceAsync(current, NoteWriter.Next(current, $"# Plan\n\nalpha beta gamma, take {i}\n", "lea", WriterRef.Server, Now.AddMinutes(20 * i)), Ct));
        }

        Assert.True(await store.TryCreateAsync(New("/Arbeit/ideen/a.md", "the same text\n"), Ct));
        Assert.True(await store.TryCreateAsync(New("/Arbeit/b.md", "the same text\n"), Ct));
        Assert.True(await store.TryCreateAsync(New("/Arbeit/weg.md", "gone soon\n"), Ct));
        Assert.True(await store.TryTrashAsync((await store.GetAsync(P("/Arbeit/weg.md"), Ct))!, "lea", WriterRef.Server, Now, Ct));
        Assert.True(await store.TryCreateAsync(New("/Arbeit/alt.md", "moving delta\n"), Ct));
        var old = (await store.GetAsync(P("/Arbeit/alt.md"), Ct))!;
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { Moves = { (old, old with { Path = P("/Arbeit/neu.md"), Revision = old.Revision + 1 }) } }, "lea", WriterRef.Server, Now, Ct));

        var na = (await store.GetAsync(P("/Arbeit/ideen/a.md"), Ct))!;
        var nb = (await store.GetAsync(P("/Arbeit/b.md"), Ct))!;
        Assert.True(await store.TryAddCheckpointAsync(Cp("eins", "/Arbeit", na, nb), [new Note(na.Path, na.Content), new Note(nb.Path, nb.Content)], Ct));
        Assert.True(await store.TryAddCheckpointAsync(Cp("zwei", "/Arbeit/ideen/a.md", na), [new Note(na.Path, na.Content)], Ct));
        await Index().IdleAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(4, await Docs("embeddings").CountDocumentsAsync(d => d.Vault == a.Id("Arbeit"), cancellationToken: Ct));
        return a;
    }

    /// <summary>What the user sees of every vault, as one text: two equal snapshots read the same.</summary>
    private async Task<string> SnapshotAsync(EncryptedNoteStore store)
    {
        var root = Kumbu.Core.Paths.VaultPath.Root;
        var notes = new List<object>();
        foreach (var path in (await store.ListNotesUnderAsync(root, Ct)).OrderBy(p => p.Value, StringComparer.Ordinal))
        {
            var n = (await store.GetAsync(path, Ct))!;
            notes.Add(new { Path = n.Path.Value, n.Content, n.Revision, n.LastWriter, n.LastWriteAt, History = n.History.Select(h => new { h.Content, h.WrittenBy, h.WrittenAt }) });
        }

        var checkpoints = new List<object>();
        foreach (var c in await store.ListCheckpointsAsync(root, Ct))
        {
            checkpoints.Add(new { c.Name, c.Scope, c.CreatedAt, Notes = (await store.ReadCheckpointAsync(c, Ct)).Select(n => new { Path = n.Path.Value, n.Content }) });
        }

        var search = await store.SearchAsync(root, "alpha delta", 10, Ct);
        return JsonSerializer.Serialize(new
        {
            Vaults = (await store.UnlockedVaultsAsync(Ct)).OrderBy(v => v.Name, StringComparer.Ordinal).Select(v => new { v.Name, v.EmbeddingProvider }),
            Notes = notes,
            Trash = (await store.ListTrashAsync(root, Ct)).Select(t => new { Path = t.Note.Path.Value, t.Note.Content, t.DeletedBy, t.DeletedAt }),
            Moves = (await store.ListMovesAsync(root, Ct)).Select(m => new { From = m.From.Value, To = m.To.Value, m.MovedBy }),
            Checkpoints = checkpoints,
            Search = new { search.State, search.Pending, Hits = search.Hits.Select(h => new { Path = h.Path.Value, h.StartLine, Score = Math.Round(h.Score, 5) }) },
        });
    }

    /// <summary>The vault key in the vault's slot of <paramref name="kind"/>, under <paramref name="userKey"/>.</summary>
    private async Task<byte[]> VaultKeyAsync(byte[] userKey, string vaultId, string kind = SlotDocument.User)
    {
        var slot = await Slots.Find(s => s.Vault == vaultId && s.Kind == kind).SingleAsync(Ct);
        return VaultCrypto.Open(VaultCrypto.Derive(userKey, "kumbu/v1/user-slot-kek", Encoding.UTF8.GetBytes(vaultId)), slot.Wrapped, $"slot:{vaultId}:{kind}");
    }

    /// <summary>Whether the document opens under <paramref name="vaultKey"/>, with its pid matching what it holds.</summary>
    private static bool Opens(byte[] vaultKey, string collection, SealedDocument doc)
    {
        var v = new UnlockedVault(doc.Vault, "", vaultKey, DateTime.UtcNow, new VaultLocks());
        try
        {
            var pid = collection switch
            {
                "notes" => v.PathId(v.OpenNote(doc).Path),
                "trash" => v.PathId(v.OpenTrash(doc).Note.Path),
                "moves" => v.PathId(v.OpenMove(doc).From),
                "embeddings" => v.PathId(v.OpenEmbeddings(doc).Path),
                "checkpoints" => v.NameId(v.OpenCheckpoint(doc).Checkpoint.Name),
                "attachments" => v.PathId(v.OpenAttachment(doc).Path),
                "attachment_trash" => v.PathId(v.OpenAttachmentTrash(doc).Attachment.Path),
                _ => v.TextId(v.OpenText(doc)),
            };
            return CryptographicOperations.FixedTimeEquals(pid, doc.PathId);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private async Task<List<(string Collection, SealedDocument Doc)>> DocumentsAsync(string vaultId)
    {
        var all = new List<(string, SealedDocument)>();
        foreach (var c in Collections) all.AddRange((await Docs(c).Find(d => d.Vault == vaultId).ToListAsync(Ct)).Select(d => (c, d)));
        return all;
    }

    /// <summary>Every document of the vault, raw, to compare before and after.</summary>
    private async Task<string> RawAsync(string vaultId)
    {
        var all = new List<string>();
        foreach (var c in Collections)
        {
            all.AddRange((await Raw(c).Find(new BsonDocument("vault", vaultId)).Sort(new BsonDocument("_id", 1)).ToListAsync(Ct)).Select(d => c + ":" + d.ToJson()));
        }

        return string.Join('\n', all);
    }

    /// <summary>Per document of the vault's notes and embeddings: its pid and the size of its blob, as MongoDB holds them.</summary>
    private async Task<Dictionary<string, (string Pid, long Size)>> PidsAsync(string vaultId)
    {
        var result = new Dictionary<string, (string, long)>();
        foreach (var c in new[] { "notes", "embeddings" })
        {
            var docs = await Raw(c).Aggregate()
                .Match(new BsonDocument("vault", vaultId))
                .Project(new BsonDocument { ["pid"] = 1, ["size"] = new BsonDocument("$binarySize", "$blob") })
                .ToListAsync(Ct);
            foreach (var d in docs) result[c + ":" + d["_id"]] = (Convert.ToBase64String(d["pid"].AsByteArray), d["size"].ToInt64());
        }

        return result;
    }

    private async Task<BsonDocument> UserDocAsync(Account a) => await Raw("users").Find(new BsonDocument("_id", a.User.Id)).SingleAsync(Ct);

    private static Task WaitUntilAsync(Func<bool> condition, int seconds = 10) => WaitUntilAsync(() => Task.FromResult(condition()), seconds);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition did not hold in time");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>Throws at <paramref name="point"/> once, as a crash there would stop the run.</summary>
    private void CrashOnceAt(string point)
    {
        var crashed = false;
        Catalog().RekeyFaultAt = p =>
        {
            if (p != point || crashed) return Task.CompletedTask;
            crashed = true;
            throw new InvalidOperationException("crash at " + point);
        };
    }

    private static byte[] Png(int size, byte fill)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, fill);
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private async Task<AttachmentState> StateAsync(EncryptedNoteStore store, byte[] bytes) =>
        new("image/png", bytes.Length, 1, 1, VersionToken.Of(bytes), await store.PutBlobAsync("Arbeit", bytes, Ct));

    private async Task<StoredAttachment> AttachAsync(EncryptedNoteStore store, string path, byte[] bytes)
    {
        var attachment = new StoredAttachment(P(path), await StateAsync(store, bytes), 1, "lea", Now, [], null, Ref: WriterRef.Server);
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentCreates = { attachment } }, "lea", WriterRef.Server, Now, Ct));
        return attachment;
    }

    /// <summary>
    /// In "Arbeit": a.png, whose earlier bytes are in its history, and b.png with the same bytes as a.png now, and c.png
    /// with them too, trashed. Two blobs: the shared bytes and the earlier ones.
    /// </summary>
    private async Task<Account> SharedBlobsAsync()
    {
        var a = await AccountAsync("Arbeit");
        var store = Store(a);
        var first = await AttachAsync(store, "/Arbeit/a.png", Png(2000, 2));
        var next = first with { Current = await StateAsync(store, Png(3000, 1)), Revision = 2, History = [new AttachmentVersion(first.Current, "lea", Now, null, Ref: WriterRef.Server)] };
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentReplaces = { (first, next) } }, "lea", WriterRef.Server, Now, Ct));
        await AttachAsync(store, "/Arbeit/b.png", Png(3000, 1));
        var c = await AttachAsync(store, "/Arbeit/c.png", Png(3000, 1));
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentTrashes = { c } }, "lea", WriterRef.Server, Now, Ct));
        Assert.Equal(2, await Raw("blobs").CountDocumentsAsync(new BsonDocument("vault", a.Id("Arbeit")), cancellationToken: Ct));
        return a;
    }

    /// <summary>Every reference to a blob of "Arbeit" (current states, history, trash, checkpoints), with a hash of the
    /// bytes it opens to (null if there is no such blob), as a second process reads them.</summary>
    private async Task<List<(string Where, string BlobId, string? Bytes)>> BlobReferencesAsync(Account a)
    {
        var store = Store(a, NewCatalog());
        var states = new List<(string, AttachmentState)>();
        foreach (var attachment in (await store.ListAttachmentsUnderAsync(Kumbu.Core.Paths.VaultPath.Root, Ct)).OrderBy(x => x.Path.Value, StringComparer.Ordinal))
        {
            states.Add((attachment.Path.Value, attachment.Current));
            states.AddRange(attachment.History.Select((h, i) => ($"{attachment.Path.Value} history {i}", h.State)));
        }

        states.AddRange((await store.ListAttachmentTrashAsync(null, Ct)).Select(t => ($"{t.Attachment.Path.Value} trash", t.Attachment.Current)));
        foreach (var checkpoint in await store.ListCheckpointsAsync(Kumbu.Core.Paths.VaultPath.Root, Ct))
        {
            states.AddRange(checkpoint.Attachments.Select(c => ($"{c.Path.Value} checkpoint {checkpoint.Name}", c.State)));
        }

        var result = new List<(string, string, string?)>();
        foreach (var (where, state) in states)
        {
            await using var stream = await store.OpenBlobAsync("Arbeit", state.BlobId, Ct);
            string? hash = null;
            if (stream is not null) hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, Ct));
            result.Add((where, state.BlobId, hash));
        }

        return result;
    }

    /// <summary>Of the vault's blobs, how many open under <paramref name="vaultKey"/>.</summary>
    private async Task<int> BlobsOpeningAsync(byte[] vaultKey, string vaultId)
    {
        var v = new UnlockedVault(vaultId, "", vaultKey, DateTime.UtcNow, new VaultLocks());
        var opening = 0;
        foreach (var doc in await _db.GetCollection<BlobDocument>("blobs").Find(d => d.Vault == vaultId).ToListAsync(Ct))
        {
            try
            {
                v.OpenBlob(doc);
                opening++;
            }
            catch (Exception e) when (e is CryptographicException or InvalidDataException)
            {
            }
        }

        return opening;
    }

    [Fact]
    public async Task Everything_opens_with_the_new_key_and_reads_the_same()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        // As stored: in memory a fresh embedding has full floats, MongoDB holds halves.
        var before = await SnapshotAsync(Store(a, NewCatalog()));
        var oldKey = await VaultKeyAsync(a.Key, id);
        var embedded = RemoteNotes;

        var summary = await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);

        // notes (6 with the moved one gone: plan, a, b, neu), trash 1, moves 1, embeddings 4, checkpoints 2, texts 1.
        Assert.Equal(13, summary.Documents);
        Assert.Equal(0, summary.Damaged);
        Assert.NotEqual(oldKey, await VaultKeyAsync(a.Key, id));
        Assert.Equal(before, await SnapshotAsync(Store(a)));
        // A second process loads it with the new key too.
        Assert.Equal(before, await SnapshotAsync(Store(a, NewCatalog())));
        // Nothing is embedded again: the resealed embeddings are current.
        await Index().IdleAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(embedded, RemoteNotes);
        var user = await UserDocAsync(a);
        Assert.DoesNotContain(id, user.GetValue("rekeyPending", new BsonArray()).AsBsonArray.Select(v => v.AsString));
        Assert.Equal(2, user["vaultsRekeyed"].ToInt64());
        Assert.False(await Raw("vaults").Find(new BsonDocument { ["_id"] = id, ["rekeyStartedAt"] = new BsonDocument("$exists", true) }).AnyAsync(Ct));
        Assert.Equal(0, await Slots.CountDocumentsAsync(s => s.Kind == SlotDocument.UserNext, cancellationToken: Ct));
    }

    [Fact]
    public async Task Nothing_is_sealed_under_the_old_key_any_more()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        var oldKey = await VaultKeyAsync(a.Key, id);
        var before = (await DocumentsAsync(id)).ToDictionary(d => d.Doc.Id, d => d.Doc.PathId);
        Assert.All(await DocumentsAsync(id), d => Assert.True(Opens(oldKey, d.Collection, d.Doc), d.Collection));

        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);

        var newKey = await VaultKeyAsync(a.Key, id);
        var after = await DocumentsAsync(id);
        Assert.Equal(before.Keys.Order(), after.Select(d => d.Doc.Id).Order());
        foreach (var (collection, doc) in after)
        {
            Assert.False(Opens(oldKey, collection, doc), collection);
            Assert.True(Opens(newKey, collection, doc), collection);
            Assert.NotEqual(before[doc.Id], doc.PathId);
            // Nor does the blob open under the old key with any pid.
            var old = new UnlockedVault(id, "", oldKey, Now, new VaultLocks());
            Assert.Null(old.Reseal(collection, doc, old, new UnlockedVault.RekeyIds()));
        }

        var meta = (await Raw("vaults").Find(new BsonDocument("_id", id)).SingleAsync(Ct))["meta"].AsByteArray;
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(VaultCrypto.Derive(oldKey, "kumbu/v1/content", []), meta, $"vault:{id}"));
        VaultCrypto.Open(VaultCrypto.Derive(newKey, "kumbu/v1/content", []), meta, $"vault:{id}");
        // The only slot left on the vault is the owner's, with the new key.
        Assert.Equal([SlotDocument.User], (await Slots.Find(s => s.Vault == id).ToListAsync(Ct)).Select(s => s.Kind));
    }

    [Fact]
    public async Task Api_keys_with_the_vault_are_revoked_and_connections_disconnected()
    {
        var a = await AccountAsync("Arbeit", "Privat");
        var (both, bothInfo) = await Keys().CreateAsync(
            a.User, a.Key, a.ReadAt, "both", new Dictionary<string, KeyRight> { [a.Id("Arbeit")] = KeyRight.Write, [a.Id("Privat")] = KeyRight.Read }, Ct);
        var (other, otherInfo) = await Keys().CreateAsync(a.User, a.Key, a.ReadAt, "other", new Dictionary<string, KeyRight> { [a.Id("Privat")] = KeyRight.Read }, Ct);
        var claude = await Connections().CreateAsync(a.User, a.Key, a.ReadAt, "claude", new Dictionary<string, KeyRight> { [a.Id("Arbeit")] = KeyRight.Write }, Ct);
        var elsewhere = await Connections().CreateAsync(a.User, a.Key, a.ReadAt, "elsewhere", new Dictionary<string, KeyRight> { [a.Id("Privat")] = KeyRight.Read }, Ct);
        var revoked = await Connections().CreateAsync(a.User, a.Key, a.ReadAt, "revoked", new Dictionary<string, KeyRight> { [a.Id("Arbeit")] = KeyRight.Read }, Ct);
        Assert.True(await Connections().RevokeAsync(a.User, revoked.Id, Ct));
        var (keysWithAccess, connectionsWithAccess) = await Catalog().AccessAsync(a.Id("Arbeit"), Ct);
        Assert.Equal([bothInfo.Id], keysWithAccess);
        Assert.Equal([claude.Id], connectionsWithAccess);

        var summary = await Catalog().RekeyAsync(a.Id("Arbeit"), a.User.Id, a.Key, Ct);

        Assert.Equal([bothInfo.Id], summary.RevokedApiKeys);
        Assert.Equal([claude.Id], summary.DisconnectedConnections);
        // Revoked whole: its slot on the other vault went too, and its name is free.
        Assert.Null(await Keys().ResolveAsync(both, Ct));
        Assert.Equal(0, await Slots.CountDocumentsAsync(s => s.KeyId == bothInfo.Id, cancellationToken: Ct));
        Assert.NotNull((await Keys().ListAsync(a.User, a.Key, Ct)).Single(i => i.Id == bothInfo.Id).RevokedAt);
        await Keys().CreateAsync(a.User, a.Key, a.ReadAt, "both", new Dictionary<string, KeyRight> { [a.Id("Privat")] = KeyRight.Read }, Ct);
        Assert.NotNull(await Keys().ResolveAsync(other, Ct));
        Assert.Equal([a.Id("Privat")], (await Catalog().OpenForApiKeyAsync(otherInfo.Id, other, Ct)).Select(v => v.Vault.Id));

        var connections = (await Connections().ListAsync(a.User, a.Key, Ct)).ToDictionary(i => i.Id);
        var claudeDoc = await Raw("connections").Find(new BsonDocument("_id", claude.Id)).SingleAsync(Ct);
        Assert.Equal(ConnectionReasons.Rekeyed, claudeDoc["tokensRevokedReason"].AsString);
        // The start and the finish each wrote it: a code being issued at the same time conflicts.
        Assert.Equal(2, claudeDoc["wraps"].ToInt64());
        Assert.Null(connections[claude.Id].RevokedAt);
        Assert.Null(connections[elsewhere.Id].Reason);
        Assert.Equal(ConnectionReasons.Revoked, connections[revoked.Id].Reason);
        Assert.Equal(0, await Slots.CountDocumentsAsync(s => s.Vault == a.Id("Arbeit") && s.Kind != SlotDocument.User, cancellationToken: Ct));
    }

    [Fact]
    public async Task An_old_credential_with_an_old_backup_cannot_read_later_writes()
    {
        var a = await AccountAsync("Arbeit");
        var id = a.Id("Arbeit");
        Assert.True(await Store(a).TryCreateAsync(New("/Arbeit/early.md", "written before"), Ct));
        var (apiKey, info) = await Keys().CreateAsync(a.User, a.Key, a.ReadAt, "agent", new Dictionary<string, KeyRight> { [id] = KeyRight.Write }, Ct);
        // The backup: everything of the vault, its slots included.
        var backup = new Dictionary<string, List<BsonDocument>>();
        foreach (var c in Collections.Append("slots").Append("vaults"))
        {
            backup[c] = await Raw(c).Find(new BsonDocument(c == "vaults" ? "_id" : "vault", id)).ToListAsync(Ct);
        }

        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);
        Assert.True(await Store(a).TryCreateAsync(New("/Arbeit/later.md", "written after the re-keying"), Ct));
        var later = await Raw("notes").Find(new BsonDocument("vault", id)).ToListAsync(Ct);

        // The old backup, with the documents written since added to it, opened with the revoked API key.
        var restored = fixture.NewDatabase();
        foreach (var (c, docs) in backup.Where(b => b.Value.Count > 0)) await restored.GetCollection<BsonDocument>(c).InsertManyAsync(docs, cancellationToken: Ct);
        var known = backup["notes"].Select(d => d["_id"]).ToHashSet();
        await restored.GetCollection<BsonDocument>("notes").InsertManyAsync(later.Where(d => !known.Contains(d["_id"])), cancellationToken: Ct);
        var oldWorld = new VaultCatalog(restored, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance);
        var store = new EncryptedNoteStore(oldWorld, new ApiKeyCredential(info.Id, apiKey));

        Assert.Equal("written before", await store.ReadAsync(P("/Arbeit/early.md"), Ct));
        Assert.Null(await store.ReadAsync(P("/Arbeit/later.md"), Ct));
        Assert.Equal(1, await store.CountDamagedAsync(P("/Arbeit"), Ct));
    }

    [Fact]
    public async Task A_vault_being_deleted_is_not_rekeyed()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        Task<RekeySummary>? during = null;

        await Catalog().DeleteVaultsAsync([id], (_, _) =>
        {
            during = Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);
            return Task.CompletedTask;
        }, Ct);

        await Assert.ThrowsAsync<VaultDeletedException>(() => during!);
        await Assert.ThrowsAsync<VaultDeletedException>(() => Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct));
        // In another process, without the tombstone: the vault document is gone.
        await Assert.ThrowsAsync<VaultDeletedException>(() => NewCatalog().RekeyAsync(id, a.User.Id, a.Key, Ct));
        Assert.Equal(0, await Slots.CountDocumentsAsync(s => s.Vault == id, cancellationToken: Ct));
        Assert.Empty(await DocumentsAsync(id));
    }

    [Theory]
    [InlineData("after-start")]
    [InlineData("after-batch")]
    [InlineData("inside-finish")]
    [InlineData("after-finish")]
    public async Task A_crash_at_any_point_leaves_one_consistent_key(string point)
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        // As stored: in memory a fresh embedding has full floats, MongoDB holds halves.
        var before = await SnapshotAsync(Store(a, NewCatalog()));
        var oldKey = await VaultKeyAsync(a.Key, id);
        Catalog().BatchDocuments = 3;
        CrashOnceAt(point);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct));

        Assert.False(Catalog().IsRekeying(id));
        var done = point == "after-finish";
        var newKey = await VaultKeyAsync(a.Key, id, done ? SlotDocument.User : SlotDocument.UserNext);
        Assert.NotEqual(oldKey, newKey);
        var docs = await DocumentsAsync(id);
        Assert.All(docs, d => Assert.True(Opens(oldKey, d.Collection, d.Doc) ^ Opens(newKey, d.Collection, d.Doc), $"{d.Collection} {d.Doc.Id}"));
        var underNew = docs.Count(d => Opens(newKey, d.Collection, d.Doc));
        if (point == "after-start") Assert.Equal(0, underNew);
        else if (point == "after-batch") Assert.InRange(underNew, 1, docs.Count - 1);
        else Assert.Equal(docs.Count, underNew);
        var pending = (await UserDocAsync(a)).GetValue("rekeyPending", new BsonArray()).AsBsonArray.Select(v => v.AsString).ToList();
        Assert.Equal(!done, pending.Contains(id));
        if (!done)
        {
            // The user slot and the meta still agree on the old key; the vault answers "being re-encrypted".
            Assert.Equal(oldKey, await VaultKeyAsync(a.Key, id));
            var busy = await Assert.ThrowsAsync<VaultBusyException>(() => Store(a).ReadAsync(P("/Arbeit/plan.md"), Ct));
            Assert.Equal(VaultBusyException.BeingReencrypted, busy.Message);

            // Resumed by another process, as after a restart.
            Catalog().RekeyFaultAt = null;
            var resumed = await NewCatalog().RekeyAsync(id, a.User.Id, a.Key, Ct);
            Assert.Empty(resumed.RevokedApiKeys);
            Assert.Equal(newKey, await VaultKeyAsync(a.Key, id));
        }

        Assert.All(await DocumentsAsync(id), d => Assert.True(Opens(newKey, d.Collection, d.Doc), d.Collection));
        Assert.Equal(before, await SnapshotAsync(Store(a, NewCatalog())));
        Assert.Equal(before, await SnapshotAsync(Store(a)));
        Assert.DoesNotContain(id, (await UserDocAsync(a)).GetValue("rekeyPending", new BsonArray()).AsBsonArray.Select(v => v.AsString));
    }

    [Fact]
    public async Task A_finish_that_matches_nothing_keeps_the_vault_pending()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        var oldKey = await VaultKeyAsync(a.Key, id);
        // While the run is in its passes, the user slot is wrapped again (the same key, a new seal), as by a writer the
        // run doesn't know of.
        Catalog().RekeyFaultAt = async p =>
        {
            if (p != "after-start") return;
            var kek = VaultCrypto.Derive(a.Key, "kumbu/v1/user-slot-kek", Encoding.UTF8.GetBytes(id));
            await Slots.UpdateOneAsync(
                s => s.Vault == id && s.Kind == SlotDocument.User, Builders<SlotDocument>.Update.Set(s => s.Wrapped, VaultCrypto.Seal(kek, oldKey, $"slot:{id}:user")), cancellationToken: Ct);
        };

        var refused = await Assert.ThrowsAsync<VaultBusyException>(() => Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct));

        Assert.Equal(VaultBusyException.BeingReencrypted, refused.Message);
        Assert.False(Catalog().IsRekeying(id));
        Assert.Equal(oldKey, await VaultKeyAsync(a.Key, id));
        Assert.Equal(1, await Slots.CountDocumentsAsync(s => s.Vault == id && s.Kind == SlotDocument.UserNext, cancellationToken: Ct));
        Assert.True(await Raw("vaults").Find(new BsonDocument { ["_id"] = id, ["rekeyStartedAt"] = new BsonDocument("$exists", true) }).AnyAsync(Ct));
        Assert.Contains(id, (await UserDocAsync(a))["rekeyPending"].AsBsonArray.Select(v => v.AsString));
        await Assert.ThrowsAsync<VaultBusyException>(() => Store(a).ReadAsync(P("/Arbeit/plan.md"), Ct));

        Catalog().RekeyFaultAt = null;
        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);
        Assert.Equal("# Plan\n\nalpha beta gamma, take 3\n", await Store(a).ReadAsync(P("/Arbeit/plan.md"), Ct));
    }

    [Fact]
    public async Task A_stale_object_writes_nothing_and_destroys_nothing()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        // A request that opened the vault before the run, and writes during it and after it.
        var stale = Store(a);
        var plan = (await stale.GetAsync(P("/Arbeit/plan.md"), Ct))!;
        var staleVault = (await stale.UnlockedVaultsAsync(Ct)).Single();
        var checkpoint = (await stale.ListCheckpointsAsync(P("/Arbeit"), Ct)).First();
        var writes = new (string Name, Func<Task> Write)[]
        {
            ("create", () => stale.TryCreateAsync(New("/Arbeit/new.md", "new"), Ct)),
            ("replace", () => stale.TryReplaceAsync(plan, NoteWriter.Next(plan, "overwritten", "lea", WriterRef.Server, Now), Ct)),
            ("trash", () => stale.TryTrashAsync(plan, "lea", WriterRef.Server, Now, Ct)),
            ("restore", () => stale.TryRestoreFromTrashAsync(P("/Arbeit/weg.md"), null, "lea", WriterRef.Server, Now, Ct)),
            ("move", () => stale.TryCommitAsync(new NoteBatch { Moves = { (plan, plan with { Path = P("/Arbeit/x.md"), Revision = plan.Revision + 1 }) } }, "lea", WriterRef.Server, Now, Ct)),
            ("checkpoint", () => stale.TryAddCheckpointAsync(Cp("drei", "/Arbeit/plan.md", plan), [new Note(plan.Path, plan.Content)], Ct)),
            ("uncheckpoint", () => stale.TryRemoveCheckpointAsync("Arbeit", checkpoint.Name, Ct)),
            ("provider", () => Catalog().SetEmbeddingProviderAsync(staleVault, EmbeddingProviders.Internal, Ct)),
            ("index", async () =>
            {
                await Index().IndexAsync(staleVault, P("/Arbeit/plan.md"), Ct);
                throw new VaultUnavailableException("indexing does nothing, as it should");
            }),
        };
        var during = new List<string>();
        Catalog().RekeyFaultAt = async p =>
        {
            if (p != "after-start") return;
            foreach (var (name, write) in writes)
            {
                // Refused at once, not queued behind the run, which holds the gates.
                var e = await Assert.ThrowsAnyAsync<VaultUnavailableException>(() => write().WaitAsync(TimeSpan.FromSeconds(5), Ct));
                if (e is VaultBusyException) during.Add(e.Message);
            }
        };

        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);
        var after = await RawAsync(id);
        var embedded = RemoteNotes;

        Assert.Equal(Enumerable.Repeat(VaultBusyException.BeingReencrypted, writes.Length - 1), during);
        foreach (var (name, write) in writes)
        {
            var e = await Assert.ThrowsAnyAsync<VaultUnavailableException>(write);
            if (name != "index") Assert.Equal(VaultBusyException.JustReencrypted, e.Message);
        }

        Assert.Equal(after, await RawAsync(id));
        Assert.Equal(embedded, RemoteNotes);
        Assert.Equal("# Plan\n\nalpha beta gamma, take 3\n", await Store(a).ReadAsync(P("/Arbeit/plan.md"), Ct));
    }

    [Fact]
    public async Task The_fences_refuse_a_write_with_the_old_pid()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        // Another process holds the vault with the old key and knows nothing of the run: only the database can refuse it.
        var other = NewCatalog();
        var stale = Store(a, other);
        var plan = (await stale.GetAsync(P("/Arbeit/plan.md"), Ct))!;
        var b = (await stale.GetAsync(P("/Arbeit/b.md"), Ct))!;
        var checkpoint = (await stale.ListCheckpointsAsync(P("/Arbeit"), Ct)).First();

        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);
        var after = await RawAsync(id);

        Assert.False(await stale.TryReplaceAsync(plan, NoteWriter.Next(plan, "overwritten", "lea", WriterRef.Server, Now), Ct));
        Assert.False(await stale.TryTrashAsync(b, "lea", WriterRef.Server, Now, Ct));
        Assert.NotEmpty(await stale.TryCommitAsync(new NoteBatch { Moves = { (plan, plan with { Path = P("/Arbeit/x.md"), Revision = plan.Revision + 1 }) } }, "lea", WriterRef.Server, Now, Ct));
        Assert.Null(await stale.TryRestoreFromTrashAsync(P("/Arbeit/weg.md"), null, "lea", WriterRef.Server, Now, Ct));
        Assert.False(await stale.TryRemoveCheckpointAsync("Arbeit", checkpoint.Name, Ct));

        Assert.Equal(after, await RawAsync(id));
        var store = Store(a);
        Assert.Equal("# Plan\n\nalpha beta gamma, take 3\n", await store.ReadAsync(P("/Arbeit/plan.md"), Ct));
        Assert.Equal("the same text\n", await store.ReadAsync(P("/Arbeit/b.md"), Ct));
        Assert.Single(await store.ListTrashAsync(P("/Arbeit"), Ct));
        Assert.Equal(2, (await store.ListCheckpointsAsync(P("/Arbeit"), Ct)).Count);
    }

    [Fact]
    public async Task A_document_sealed_under_the_old_key_with_a_new_pid_is_resealed()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        var oldKey = await VaultKeyAsync(a.Key, id);
        var path = P("/Arbeit/b.md");
        Catalog().RekeyFaultAt = async p =>
        {
            if (p != "after-start") return;
            // As a writer of another process might leave it: the new key's pid on a blob sealed under the old key.
            var next = new UnlockedVault(id, "", await VaultKeyAsync(a.Key, id, SlotDocument.UserNext), Now, new VaultLocks());
            var old = new UnlockedVault(id, "", oldKey, Now, new VaultLocks());
            await Docs("notes").UpdateOneAsync(d => d.Vault == id && d.PathId == old.PathId(path), Builders<SealedDocument>.Update.Set(d => d.PathId, next.PathId(path)), cancellationToken: Ct);
        };

        var summary = await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);

        Assert.Equal(0, summary.Damaged);
        var newKey = await VaultKeyAsync(a.Key, id);
        Assert.All(await DocumentsAsync(id), d => Assert.True(Opens(newKey, d.Collection, d.Doc), d.Collection));
        Assert.Equal("the same text\n", await Store(a).ReadAsync(path, Ct));
    }

    [Fact]
    public async Task A_duplicate_pid_keeps_the_higher_revision()
    {
        var a = await AccountAsync("Arbeit");
        var id = a.Id("Arbeit");
        Assert.True(await Store(a).TryCreateAsync(New("/Arbeit/plan.md", "older"), Ct));
        // A second document for the same path, newer, under a pid of its own (written outside Kumbu², say).
        var k = new UnlockedVault(id, "", await VaultKeyAsync(a.Key, id), Now, new VaultLocks());
        var newer = ObjectId.GenerateNewId();
        await Docs("notes").InsertOneAsync(
            new SealedDocument { Id = newer, Vault = id, PathId = RandomNumberGenerator.GetBytes(32), Revision = 5, Blob = k.SealNote(newer, New("/Arbeit/plan.md", "newer") with { Revision = 5 }) },
            cancellationToken: Ct);

        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);

        var left = Assert.Single(await Docs("notes").Find(d => d.Vault == id).ToListAsync(Ct));
        Assert.Equal(newer, left.Id);
        Assert.Equal("newer", await Store(a, NewCatalog()).ReadAsync(P("/Arbeit/plan.md"), Ct));
        Assert.Contains(_log.Lines, l => l.Contains("claim one pid", StringComparison.Ordinal) && l.Contains(newer.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_damaged_document_is_counted_and_left()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        var damaged = ObjectId.GenerateNewId();
        await Docs("notes").InsertOneAsync(new SealedDocument { Id = damaged, Vault = id, PathId = RandomNumberGenerator.GetBytes(32), Revision = 1, Blob = RandomNumberGenerator.GetBytes(64) }, cancellationToken: Ct);
        var raw = (await Raw("notes").Find(new BsonDocument("_id", damaged)).SingleAsync(Ct)).ToJson();

        var summary = await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);

        Assert.Equal(1, summary.Damaged);
        Assert.Equal(13, summary.Documents);
        Assert.Equal(raw, (await Raw("notes").Find(new BsonDocument("_id", damaged)).SingleAsync(Ct)).ToJson());
        Assert.Single(_log.Lines, l => l.Contains($"Document {damaged} of vault {id}", StringComparison.Ordinal) && l.Contains("neither key", StringComparison.Ordinal));
        Assert.Equal(1, await Store(a).CountDamagedAsync(P("/Arbeit"), Ct));
    }

    [Fact]
    public async Task A_deletion_during_a_run_wins_within_one_batch()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        Catalog().BatchDocuments = 2;
        var batches = 0;
        var batchesAfterDeletion = 0;
        Task<long>? deletion = null;
        Catalog().RekeyBatchCommitted = (_, _, _) =>
        {
            batches++;
            if (deletion is not null) batchesAfterDeletion++;
        };
        Catalog().RekeyFaultAt = async p =>
        {
            if (p != "after-batch" || deletion is not null) return;
            deletion = Catalog().DeleteVaultsAsync([id], (_, _) => Task.CompletedTask, Ct);
            await WaitUntilAsync(() => Catalog().IsDeleted(id));
        };

        await Assert.ThrowsAsync<VaultDeletedException>(() => Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct));
        await deletion!.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(1, batches);
        Assert.Equal(0, batchesAfterDeletion);
        Assert.DoesNotContain(id, (await UserDocAsync(a)).GetValue("rekeyPending", new BsonArray()).AsBsonArray.Select(v => v.AsString));
        Assert.Empty(await DocumentsAsync(id));
        Assert.Equal(0, await Slots.CountDocumentsAsync(s => s.Vault == id, cancellationToken: Ct));
        Assert.Empty(await Store(a).ListVaultsAsync(Ct));
    }

    [Fact]
    public async Task A_large_document_goes_alone_in_its_batch()
    {
        var a = await AccountAsync("Arbeit");
        var id = a.Id("Arbeit");
        var store = Store(a);
        for (var i = 0; i < 6; i++) Assert.True(await store.TryCreateAsync(New($"/Arbeit/{i}.md", new string((char)('a' + i), 10_000)), Ct));
        Assert.True(await store.TryCreateAsync(New("/Arbeit/big.md", new string('z', 100_000)), Ct));
        Catalog().BatchBytes = 30_000;
        var batches = new List<(int Documents, long Bytes)>();
        Catalog().RekeyBatchCommitted = (_, n, bytes) => batches.Add((n, bytes));

        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);

        Assert.Contains(batches, b => b is { Documents: 1, Bytes: > 100_000 });
        Assert.All(batches.Where(b => b.Bytes > 30_000), b => Assert.Equal(1, b.Documents));
        Assert.Equal(7, batches.Sum(b => b.Documents));
        Assert.True(batches.Count >= 4, $"{batches.Count} batches");
        Assert.Equal(new string('z', 100_000), await Store(a).ReadAsync(P("/Arbeit/big.md"), Ct));
    }

    [Fact]
    public async Task Rekeying_a_vault_with_a_blob_larger_than_a_batch_keeps_every_attachment()
    {
        var a = await AccountAsync("Arbeit");
        var id = a.Id("Arbeit");
        var store = Store(a);
        await AttachAsync(store, "/Arbeit/big.png", Png(200_000, 1));
        for (var i = 0; i < 5; i++) await AttachAsync(store, $"/Arbeit/{i}.png", Png(10_000, (byte)(10 + i)));
        var before = await BlobReferencesAsync(a);
        var oldKey = await VaultKeyAsync(a.Key, id);
        Catalog().BatchBytes = 30_000;
        var (batches, reads) = (new List<(int Documents, long Bytes)>(), new List<(int Documents, long Bytes)>());
        Catalog().RekeyBatchCommitted = (_, n, bytes) => batches.Add((n, bytes));
        Catalog().RekeyChunkRead = (_, n, bytes) => reads.Add((n, bytes));

        var summary = await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);

        // Six blobs and six records.
        Assert.Equal((12, 0), (summary.Documents, summary.Damaged));
        Assert.Contains(batches, b => b is { Documents: 1, Bytes: > 200_000 });
        Assert.All(batches.Where(b => b.Bytes > 30_000), b => Assert.Equal(1, b.Documents));
        // Nor is more read at once: the large blob alone.
        Assert.Contains(reads, r => r is { Documents: 1, Bytes: > 200_000 });
        Assert.All(reads.Where(r => r.Bytes > 30_000), r => Assert.Equal(1, r.Documents));
        var after = await BlobReferencesAsync(a);
        Assert.Equal(before.Select(r => (r.Where, r.Bytes)), after.Select(r => (r.Where, r.Bytes)));
        Assert.All(after, r => Assert.NotNull(r.Bytes));
        Assert.Empty(after.Select(r => r.BlobId).Intersect(before.Select(r => r.BlobId)));
        var newKey = await VaultKeyAsync(a.Key, id);
        Assert.Equal((6, 0), (await BlobsOpeningAsync(newKey, id), await BlobsOpeningAsync(oldKey, id)));
        Assert.All(await DocumentsAsync(id), d => Assert.True(Opens(newKey, d.Collection, d.Doc), d.Collection));
    }

    [Fact]
    public async Task Rekeying_remaps_shared_blob_ids_for_every_referrer()
    {
        var a = await SharedBlobsAsync();
        var id = a.Id("Arbeit");
        var before = await BlobReferencesAsync(a);
        Assert.Equal(["/Arbeit/a.png", "/Arbeit/a.png history 0", "/Arbeit/b.png", "/Arbeit/c.png trash"], before.Select(r => r.Where));

        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);

        var after = await BlobReferencesAsync(a);
        Assert.Equal(before.Select(r => (r.Where, r.Bytes)), after.Select(r => (r.Where, r.Bytes)));
        Assert.All(after, r => Assert.NotNull(r.Bytes));
        Assert.Empty(after.Select(r => r.BlobId).Intersect(before.Select(r => r.BlobId)));
        // Still shared: a.png, b.png and the trashed c.png name one blob, and it is stored once.
        Assert.Single(after.Where(r => r.Where != "/Arbeit/a.png history 0").Select(r => r.BlobId).Distinct());
        Assert.Equal(2, await Raw("blobs").CountDocumentsAsync(new BsonDocument("vault", id), cancellationToken: Ct));

        // A sweep afterwards finds every blob referenced.
        _time.Advance(TimeSpan.FromHours(1));
        var vault = (await Store(a).UnlockedVaultsAsync(Ct)).Single();
        Assert.Equal(0, await new BlobSweep(Catalog(), NullLogger.Instance).RunAsync(vault, Now, Ct));
        Assert.Equal(after, await BlobReferencesAsync(a));
    }

    [Fact]
    public async Task Rekeying_remaps_the_blob_ids_a_checkpoint_holds()
    {
        var a = await AccountAsync("Arbeit");
        var id = a.Id("Arbeit");
        var store = Store(a);
        var x = await AttachAsync(store, "/Arbeit/x.png", Png(2000, 3));
        var y = await AttachAsync(store, "/Arbeit/y.png", Png(2000, 3));
        var checkpoint = new Checkpoint("cp", P("/Arbeit"), true, "lea", Now, []) { Attachments = [new(x.Path, x.Current), new(y.Path, y.Current)], Ref = WriterRef.Server };
        Assert.True(await store.TryAddCheckpointAsync(checkpoint, [], Ct));
        // x.png goes to the trash, y.png gets other bytes and keeps no history: the shared bytes are left to the trash
        // entry and the checkpoint.
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentTrashes = { x } }, "lea", WriterRef.Server, Now, Ct));
        var other = y with { Current = await StateAsync(store, Png(3000, 4)), Revision = 2 };
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentReplaces = { (y, other) } }, "lea", WriterRef.Server, Now, Ct));
        var before = await BlobReferencesAsync(a);
        Assert.Equal(
            ["/Arbeit/y.png", "/Arbeit/x.png trash", "/Arbeit/x.png checkpoint cp", "/Arbeit/y.png checkpoint cp"],
            before.Select(r => r.Where));

        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);

        var after = await BlobReferencesAsync(a);
        Assert.Equal(before.Select(r => (r.Where, r.Bytes)), after.Select(r => (r.Where, r.Bytes)));
        Assert.All(after, r => Assert.NotNull(r.Bytes));
        Assert.Empty(after.Select(r => r.BlobId).Intersect(before.Select(r => r.BlobId)));
        // The trash entry and the checkpoint still name one blob, stored once.
        Assert.Single(after.Skip(1).Select(r => r.BlobId).Distinct());
        Assert.Equal(2, await Raw("blobs").CountDocumentsAsync(new BsonDocument("vault", id), cancellationToken: Ct));

        // A sweep afterwards finds every blob referenced.
        _time.Advance(TimeSpan.FromHours(1));
        var vault = (await Store(a).UnlockedVaultsAsync(Ct)).Single();
        Assert.Equal(0, await new BlobSweep(Catalog(), NullLogger.Instance).RunAsync(vault, Now, Ct));
        Assert.Equal(after, await BlobReferencesAsync(a));
    }

    [Fact]
    public async Task An_interrupted_rekeying_of_blobs_resumes_and_no_sweep_releases_them_meanwhile()
    {
        var a = await SharedBlobsAsync();
        var id = a.Id("Arbeit");
        var before = await BlobReferencesAsync(a);
        var oldKey = await VaultKeyAsync(a.Key, id);
        // Every blob past its grace: a sweep that ran would release what it doesn't see referenced.
        _time.Advance(TimeSpan.FromHours(1));
        var stale = (await Store(a).UnlockedVaultsAsync(Ct)).Single();
        var sweep = new BlobSweep(Catalog(), NullLogger.Instance);
        Catalog().BatchDocuments = 1;
        int? sweptDuringRun = null;
        Catalog().RekeyFaultAt = async p =>
        {
            if (p != "after-batch" || sweptDuringRun is not null) return;
            sweptDuringRun = await sweep.RunAsync(stale, Now, Ct);
            throw new InvalidOperationException("crash after the first blob");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct));

        Assert.Equal(0, sweptDuringRun);
        var newKey = await VaultKeyAsync(a.Key, id, SlotDocument.UserNext);
        // One blob under each key, the records all under the old one: nothing is served meanwhile, nor swept.
        Assert.Equal((1, 1), (await BlobsOpeningAsync(newKey, id), await BlobsOpeningAsync(oldKey, id)));
        Assert.Equal(0, await sweep.RunAsync(stale, Now, Ct));
        await Assert.ThrowsAsync<VaultBusyException>(() => Store(a).OpenBlobAsync("Arbeit", before[0].BlobId, Ct));

        // Resumed by another process, as after a restart.
        Catalog().RekeyFaultAt = null;
        await NewCatalog().RekeyAsync(id, a.User.Id, a.Key, Ct);

        var after = await BlobReferencesAsync(a);
        Assert.Equal(before.Select(r => (r.Where, r.Bytes)), after.Select(r => (r.Where, r.Bytes)));
        Assert.All(after, r => Assert.NotNull(r.Bytes));
        Assert.Equal((2, 0), (await BlobsOpeningAsync(newKey, id), await BlobsOpeningAsync(oldKey, id)));
    }

    [Fact]
    public async Task An_interrupted_rekeying_of_a_pdf_analysis_resumes_and_it_opens_under_the_new_key_and_id()
    {
        var a = await AccountAsync("Arbeit");
        var id = a.Id("Arbeit");
        var pdf = "%PDF-1.7 analysed before the re-keying"u8.ToArray();
        var attachment = await AttachAsync(Store(a), "/Arbeit/paper.pdf", pdf);
        var vault = (await Store(a).UnlockedVaultsAsync(Ct)).Single();
        var texts = Enumerable.Range(1, 3).Select(n => $"Seite {n} " + new string('x', 600_000)).ToList();
        var book = new Kumbu.Server.Attachments.SidecarAnalysis(
            new PdfAnalysis(Kumbu.Server.Attachments.PdfAnalyses.Analyser, 3, 3, null, null,
                [.. Enumerable.Range(1, 3).Select(n => new PdfPage(n, 595, 842, 0, PageType.Text, 600_000, 0.5, 0, 0, 0, []))]),
            texts);
        var analyses = new Kumbu.Server.Attachments.PdfAnalyses(Catalog(), NullLogger<Kumbu.Server.Attachments.PdfAnalyses>.Instance);
        Assert.NotNull(await analyses.StoreAsync(vault, Convert.FromBase64String(attachment.Current.BlobId), book, Ct));
        var parts = Raw("pdf_analyses");
        var count = (int)await parts.CountDocumentsAsync(new BsonDocument("vault", id), cancellationToken: Ct);
        Assert.True(count >= 3, $"{count} parts");
        var oldKey = await VaultKeyAsync(a.Key, id);
        // One document a batch: the blob's batch, then the first part of the analysis, then the crash.
        Catalog().BatchDocuments = 1;
        var batches = 0;
        Catalog().RekeyFaultAt = p => p == "after-batch" && ++batches == 2 ? throw new InvalidOperationException("crash inside the analysis") : Task.CompletedTask;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct));

        var newKey = await VaultKeyAsync(a.Key, id, SlotDocument.UserNext);
        int Opening(byte[] key, IEnumerable<PdfAnalysisDocument> docs) => docs.Count(d =>
        {
            try
            {
                new UnlockedVault(id, "", key, Now, new VaultLocks()).OpenPdfAnalysis<JsonElement>(d);
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
        });
        var midway = await _db.GetCollection<PdfAnalysisDocument>("pdf_analyses").Find(d => d.Vault == id).ToListAsync(Ct);
        Assert.Equal((1, count - 1), (Opening(newKey, midway), Opening(oldKey, midway)));

        // Resumed by another process, as after a restart.
        Catalog().RekeyFaultAt = null;
        var other = NewCatalog();
        await other.RekeyAsync(id, a.User.Id, a.Key, Ct);

        var store = Store(a, other);
        var current = (await store.GetAttachmentAsync(P("/Arbeit/paper.pdf"), Ct))!.Current;
        Assert.NotEqual(attachment.Current.BlobId, current.BlobId);
        var after = await _db.GetCollection<PdfAnalysisDocument>("pdf_analyses").Find(d => d.Vault == id).ToListAsync(Ct);
        Assert.Equal(count, after.Count);
        Assert.All(after, d => Assert.Equal(current.BlobId, Convert.ToBase64String(d.BlobId)));
        Assert.Equal((count, 0), (Opening(newKey, after), Opening(oldKey, after)));
        var reopened = (await store.UnlockedVaultsAsync(Ct)).Single();
        var read = new Kumbu.Server.Attachments.PdfAnalyses(other, NullLogger<Kumbu.Server.Attachments.PdfAnalyses>.Instance);
        Assert.Equal(texts, await read.TextAsync(reopened, Convert.FromBase64String(current.BlobId), 1, 3, Ct));
    }

    [Fact]
    public async Task Rekeys_2000_notes_in_batches_within_the_limits()
    {
        var a = await AccountAsync("Arbeit");
        var id = a.Id("Arbeit");
        var k = new UnlockedVault(id, "", await VaultKeyAsync(a.Key, id), Now, new VaultLocks());
        // 2000 notes, each with 10 earlier versions of 2 KB, and embeddings: written straight to the database.
        var random = new Random(7);
        string Text(int n) => string.Concat(Enumerable.Range(0, n / 8).Select(_ => (char)('a' + random.Next(26)) + "bcdefg "));
        for (var start = 0; start < 2000; start += 200)
        {
            var notes = new List<SealedDocument>();
            var embeddings = new List<SealedDocument>();
            for (var i = start; i < start + 200; i++)
            {
                var path = P($"/Arbeit/notes/{i:D4}.md");
                var note = new StoredNote(path, Text(2048), 11, "lea", Now, [.. Enumerable.Range(0, 10).Select(h => new HistoryEntry(Text(2048), "lea", Now.AddMinutes(-h)))], Ref: WriterRef.Server);
                var docId = ObjectId.GenerateNewId();
                notes.Add(new SealedDocument { Id = docId, Vault = id, PathId = k.PathId(path), Revision = 11, Blob = k.SealNote(docId, note) });
                var vectors = new NoteEmbeddings("arctic-m", note.Version, [new EmbeddedChunk(0, "h" + i, [.. Enumerable.Range(0, 768).Select(_ => (float)random.NextDouble())])]);
                embeddings.Add(new SealedDocument { Id = ObjectId.GenerateNewId(), Vault = id, PathId = k.PathId(path), Blob = k.SealEmbeddings(path, vectors) });
            }

            await Docs("notes").InsertManyAsync(notes, cancellationToken: Ct);
            await Docs("embeddings").InsertManyAsync(embeddings, cancellationToken: Ct);
        }

        var batches = new List<(int Documents, long Bytes)>();
        Catalog().RekeyBatchCommitted = (_, n, bytes) => batches.Add((n, bytes));
        // Independently of the run's own count: after each batch, the documents whose pid changed since the last one,
        // and the sizes of their blobs as MongoDB stores them. Time spent here doesn't count.
        var measured = new List<(int Documents, long Bytes)>();
        var pids = await PidsAsync(id);
        var measuring = new Stopwatch();
        Catalog().RekeyFaultAt = async p =>
        {
            if (p != "after-batch") return;
            measuring.Start();
            var now = await PidsAsync(id);
            var moved = now.Where(d => !pids.TryGetValue(d.Key, out var before) || before.Pid != d.Value.Pid).ToList();
            measured.Add((moved.Count, moved.Sum(d => d.Value.Size)));
            pids = now;
            measuring.Stop();
        };
        var clock = Stopwatch.StartNew();

        var summary = await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);

        clock.Stop();
        var elapsed = clock.Elapsed - measuring.Elapsed;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Re-keyed 2000 notes with embeddings in {elapsed.TotalSeconds:0.0} s: {measured.Sum(b => b.Bytes) / 1024.0 / 1024.0:0.0} MiB stored, "
            + $"{measured.Count} batches, largest {measured.Max(b => b.Bytes) / 1024.0 / 1024.0:0.00} MiB, at most {measured.Max(b => b.Documents)} documents");
        Assert.True(elapsed < TimeSpan.FromSeconds(50), $"took {elapsed}");
        Assert.Equal(4000, summary.Documents);
        // The run's own count and the measured one agree, batch by batch, and every batch keeps to the budget.
        Assert.Equal(batches, measured);
        Assert.Equal(4000, measured.Sum(b => b.Documents));
        Assert.All(measured, b => Assert.True(b.Bytes <= Catalog().BatchBytes && b.Documents <= Catalog().BatchDocuments, $"{b.Documents} documents, {b.Bytes} bytes"));
        var store = Store(a);
        Assert.Equal(2000, (await store.ListNotesUnderAsync(P("/Arbeit"), Ct)).Count);
        Assert.Equal(10, (await store.GetAsync(P("/Arbeit/notes/1234.md"), Ct))!.History.Count);
    }

    [Fact]
    public async Task A_pending_vault_answers_being_reencrypted()
    {
        var a = await FilledAsync("Privat");
        var id = a.Id("Arbeit");
        Assert.True(await Store(a).TryCreateAsync(New("/Privat/p.md", "private"), Ct));
        CrashOnceAt("after-start");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct));
        var unlocked = new List<string>();
        Catalog().Unlocked += v => unlocked.Add(v.Id);
        var found = new List<(string, string, byte[])>();
        Catalog().RekeyPendingFound += (v, u, k) => found.Add((v, u, k));
        var store = Store(a);

        // The name lists keep it.
        Assert.Equal(["Arbeit", "Privat"], (await store.ListVaultsAsync(Ct)).Order(StringComparer.Ordinal));
        var placeholder = (await store.UnlockedVaultsAsync(Ct)).Single(v => v.Name == "Arbeit");
        Assert.True(placeholder.Pending);
        Assert.Equal("remote", placeholder.EmbeddingProvider);
        Assert.Empty(placeholder.Notes);
        Assert.Equal([(id, a.User.Id)], found.Select(f => (f.Item1, f.Item2)));
        Assert.Equal(a.Key, found[0].Item3);
        Assert.Contains((await Catalog().OpenForUserAsync(a.User.Id, a.Key, Ct)).Select(v => (v.Name, v.Pending)), v => v == ("Arbeit", true));
        Assert.Equal([id], await Catalog().PendingVaultIdsAsync(a.User.Id, a.Key, Ct));
        // The key and connection lists and the consent page name it too.
        var caller = Kumbu.Server.Auth.RequestCaller.ForSession(a.User, a.Key, a.ReadAt, windowOpen: true, Catalog());
        Assert.Equal("Arbeit", (await Kumbu.Server.Api.AccountApi.VaultNamesAsync(caller, Ct))[id]);

        // Every single-vault path refuses it.
        var reads = new Func<Task>[]
        {
            () => store.ReadAsync(P("/Arbeit/plan.md"), Ct),
            () => store.ListNotesUnderAsync(P("/Arbeit"), Ct),
            () => store.ListTrashAsync(P("/Arbeit"), Ct),
            () => store.ListCheckpointsAsync(P("/Arbeit"), Ct),
            () => store.CountDamagedAsync(P("/Arbeit"), Ct),
            () => store.SearchAsync(P("/Arbeit"), "alpha", 5, Ct),
            () => store.TryCreateAsync(New("/Arbeit/new.md", "new"), Ct),
            () => store.TryRemoveCheckpointAsync("Arbeit", "eins", Ct),
            () => store.LatestChangesetAsync("Arbeit", "lea", Ct),
            () => Catalog().SetEmbeddingProviderAsync(placeholder, EmbeddingProviders.Internal, Ct),
        };
        foreach (var read in reads) Assert.Equal(VaultBusyException.BeingReencrypted, (await Assert.ThrowsAsync<VaultBusyException>(read)).Message);

        // Cross-vault paths leave it out.
        Assert.Equal(["/Privat/p.md"], (await store.ListNotesUnderAsync(Kumbu.Core.Paths.VaultPath.Root, Ct)).Select(p => p.Value));
        Assert.Empty(await store.ListTrashAsync(Kumbu.Core.Paths.VaultPath.Root, Ct));
        Assert.Empty(await store.ListCheckpointsAsync(Kumbu.Core.Paths.VaultPath.Root, Ct));
        Assert.Equal(0, await store.CountDamagedAsync(Kumbu.Core.Paths.VaultPath.Root, Ct));

        // Indexing through it does nothing.
        var embeddings = await RawAsync(id);
        await Index().IndexAsync(placeholder, P("/Arbeit/plan.md"), Ct);
        Assert.Equal(embeddings, await RawAsync(id));
        Assert.DoesNotContain(id, unlocked);
        Assert.Equal(1, Catalog().OpenCount);
    }

    [Fact]
    public async Task Indexing_resumes_after_a_finish()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        var before = Catalog().InterruptToken(id);
        Catalog().RekeyFaultAt = p =>
        {
            if (p == "after-start") Assert.True(Catalog().InterruptToken(id).IsCancellationRequested);
            return Task.CompletedTask;
        };

        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);

        Assert.True(before.IsCancellationRequested);
        Assert.False(Catalog().InterruptToken(id).IsCancellationRequested);
        var store = Store(a);
        var plan = (await store.GetAsync(P("/Arbeit/plan.md"), Ct))!;
        var embedded = RemoteNotes;
        Assert.True(await store.TryReplaceAsync(plan, NoteWriter.Next(plan, "# Plan\n\nsomething else entirely\n", "lea", WriterRef.Server, Now.AddHours(2)), Ct));
        await Index().IdleAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(RemoteNotes > embedded);
        var vault = (await Store(a, NewCatalog()).UnlockedVaultsAsync(Ct)).Single();
        Assert.Equal((await Store(a).GetAsync(P("/Arbeit/plan.md"), Ct))!.Version, vault.Embeddings[P("/Arbeit/plan.md")].Version);
    }

    [Fact]
    public async Task A_rotation_during_the_two_key_period_rewraps_the_new_key_too()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        CrashOnceAt("after-batch");
        Catalog().BatchDocuments = 3;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct));
        var next = await VaultKeyAsync(a.Key, id, SlotDocument.UserNext);

        var rotation = (await Rotation().RotateAsync(a.User, a.Key, Password, null, Ct))!;

        Assert.Equal(next, await VaultKeyAsync(rotation.NewUserKey, id, SlotDocument.UserNext));
        await Assert.ThrowsAsync<VaultNotRekeyableException>(() => Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct));
        await Catalog().RekeyAsync(id, a.User.Id, rotation.NewUserKey, Ct);
        Assert.Equal(next, await VaultKeyAsync(rotation.NewUserKey, id));
        Assert.Equal("# Plan\n\nalpha beta gamma, take 3\n", await Store(a, key: rotation.NewUserKey).ReadAsync(P("/Arbeit/plan.md"), Ct));
    }

    [Fact]
    public async Task A_stale_cached_key_answers_just_reencrypted()
    {
        var a = await AccountAsync("Arbeit");
        var id = a.Id("Arbeit");
        Assert.True(await Store(a).TryCreateAsync(New("/Arbeit/plan.md", "plan"), Ct));
        var (apiKey, info) = await Keys().CreateAsync(a.User, a.Key, a.ReadAt, "agent", new Dictionary<string, KeyRight> { [id] = KeyRight.Write }, Ct);
        // The key's slot as a restored backup would bring it back: it still wraps the old vault key.
        var old = await Slots.Find(s => s.KeyId == info.Id).ToListAsync(Ct);

        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);
        await Slots.InsertManyAsync(old, cancellationToken: Ct);

        // Not in memory: the vault's meta doesn't open under the old key.
        Assert.Equal(VaultBusyException.JustReencrypted, (await Assert.ThrowsAsync<VaultBusyException>(() => Catalog().OpenForApiKeyAsync(info.Id, apiKey, Ct))).Message);
        // In memory under the new key: the old key is not the cached one.
        Assert.Single(await Store(a).UnlockedVaultsAsync(Ct));
        Assert.Equal(VaultBusyException.JustReencrypted, (await Assert.ThrowsAsync<VaultBusyException>(() => Catalog().OpenForApiKeyAsync(info.Id, apiKey, Ct))).Message);
        Assert.Equal("plan", await Store(a).ReadAsync(P("/Arbeit/plan.md"), Ct));
    }

    [Fact]
    public async Task The_index_queue_moves_to_the_new_object()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        // An index that doesn't run yet: what is queued stays queued until it starts.
        await _index!.StopAsync(Ct);
        _index.Dispose();
        var index = _index = new SearchIndex(Catalog(), EmbeddingProviders.Of(new FakeEmbedder(), new EmbeddingProvider("remote", "Remote", null, _remote)), NullLogger<SearchIndex>.Instance);
        var stale = Store(a);
        var plan = (await stale.GetAsync(P("/Arbeit/plan.md"), Ct))!;
        Assert.True(await stale.TryReplaceAsync(plan, NoteWriter.Next(plan, "# Plan\n\nwritten just before the run\n", "lea", WriterRef.Server, Now.AddHours(1)), Ct));
        Assert.Equal(1, index.Queued);

        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);
        // The new object's catch-up finds the note queued already, for the old object.
        var current = (await Store(a).UnlockedVaultsAsync(Ct)).Single();
        await index.StartAsync(Ct);
        await index.IdleAsync(TimeSpan.FromSeconds(10), Ct);

        var note = (await Store(a).GetAsync(P("/Arbeit/plan.md"), Ct))!;
        Assert.Equal("# Plan\n\nwritten just before the run\n", note.Content);
        Assert.Equal(note.Version, current.Embeddings[P("/Arbeit/plan.md")].Version);
        var reloaded = (await Store(a, NewCatalog()).UnlockedVaultsAsync(Ct)).Single();
        Assert.Equal(note.Version, reloaded.Embeddings[P("/Arbeit/plan.md")].Version);
    }

    [Fact]
    public async Task An_embedding_call_under_way_is_cancelled_by_the_mark()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        _remote.Hang = true;
        var store = Store(a);
        var plan = (await store.GetAsync(P("/Arbeit/plan.md"), Ct))!;
        Assert.True(await store.TryReplaceAsync(plan, NoteWriter.Next(plan, "# Plan\n\nembedded while the run begins\n", "lea", WriterRef.Server, Now.AddHours(1)), Ct));
        await _remote.Hanging.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // The call holds the index gate, which the run waits for: the mark cancels it.
        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        _remote.Hang = false;
        var current = (await Store(a).UnlockedVaultsAsync(Ct)).Single();
        await Index().IdleAsync(TimeSpan.FromSeconds(10), Ct);
        var note = (await Store(a).GetAsync(P("/Arbeit/plan.md"), Ct))!;
        Assert.Equal(note.Version, current.Embeddings[P("/Arbeit/plan.md")].Version);
    }

    [Fact]
    public async Task Only_VaultBusyException_answers_503()
    {
        static async Task<(int? Status, string? RetryAfter, string? Message)> AnswerAsync(Exception e)
        {
            var http = new DefaultHttpContext();
            var result = await Kumbu.Server.Api.ApiErrors.Filter(new DefaultEndpointFilterInvocationContext(http), _ => throw e);
            var json = Assert.IsType<JsonHttpResult<Kumbu.Server.Api.ApiError>>(result);
            return (json.StatusCode, http.Response.Headers.RetryAfter.ToString(), json.Value!.Error);
        }

        Assert.Equal((503, "5", VaultBusyException.BeingReencrypted), await AnswerAsync(VaultBusyException.Being()));
        Assert.Equal((503, "5", VaultBusyException.JustReencrypted), await AnswerAsync(VaultBusyException.Just()));
        Assert.Equal((404, "", new VaultDeletedException().Message), await AnswerAsync(new VaultDeletedException()));
        // Any other unavailable vault stays what it was: a fault, not "try again".
        var other = new VaultUnavailableException("MongoDB runs standalone");
        Assert.Same(other, await Assert.ThrowsAsync<VaultUnavailableException>(() => AnswerAsync(other)));
        Assert.Throws<ArgumentException>(() => new VaultBusyException("Something else."));
    }

    [Fact]
    public async Task The_runner_runs_one_task_per_vault_and_backs_off()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        var attempts = 0;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Catalog().RekeyFaultAt = async p =>
        {
            if (p != "after-start") return;
            if (Interlocked.Increment(ref attempts) <= 2)
            {
                await hold.Task;
                throw new InvalidOperationException("transient");
            }
        };
        var runner = await RunnerAsync();

        for (var i = 0; i < 5; i++) runner.Start(id, a.User.Id, a.Key);
        await WaitUntilAsync(() => runner.IsRunning(id));
        for (var i = 0; i < 5; i++) runner.Start(id, a.User.Id, a.Key);
        hold.SetResult();

        // First failure: a minute's wait.
        await WaitUntilAsync(() => !runner.IsRunning(id));
        await Task.Delay(200, Ct);
        Assert.Equal(1, attempts);
        _time.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(200, Ct);
        Assert.Equal(1, attempts);
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => attempts == 2);

        // Second failure: two minutes.
        await WaitUntilAsync(() => !runner.IsRunning(id));
        _time.Advance(TimeSpan.FromSeconds(119));
        await Task.Delay(200, Ct);
        Assert.Equal(2, attempts);
        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => attempts == 3);
        await WaitUntilAsync(() => !runner.IsRunning(id));
        await WaitUntilAsync(async () => (await Catalog().PendingVaultIdsAsync(a.User.Id, a.Key, Ct)).Count == 0);
        Assert.Equal("# Plan\n\nalpha beta gamma, take 3\n", await Store(a).ReadAsync(P("/Arbeit/plan.md"), Ct));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task The_runner_serves_one_vault_at_a_time_in_order()
    {
        var a = await AccountAsync("Eins", "Zwei", "Drei");
        var ids = new[] { a.Id("Eins"), a.Id("Zwei"), a.Id("Drei") };
        var order = new List<string>();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var most = 0;
        Catalog().RekeyFaultAt = async p =>
        {
            if (p != "after-start") return;
            var running = ids.Where(Catalog().IsRekeying).ToList();
            most = Math.Max(most, running.Count);
            lock (order) order.Add(running.Single());
            if (order.Count == 1) await hold.Task;
        };
        var runner = await RunnerAsync();

        var runs = ids.Select(id => runner.RunAsync(id, a.User.Id, a.Key)).ToList();
        await WaitUntilAsync(() => order.Count == 1);
        await Task.Delay(200, Ct);
        Assert.Single(order);
        Assert.True(runner.IsRunning(ids[0]));
        Assert.False(runner.IsRunning(ids[1]));
        hold.SetResult();
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(ids, order);
        Assert.Equal(1, most);
    }

    [Fact]
    public async Task Start_during_backoff_swaps_the_key_and_keeps_the_backoff()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        CrashOnceAt("after-start");
        var crash = Catalog().RekeyFaultAt!;
        var attempts = 0;
        Catalog().RekeyFaultAt = p =>
        {
            if (p == "after-start") Interlocked.Increment(ref attempts);
            return crash(p);
        };
        var runner = await RunnerAsync();
        runner.Start(id, a.User.Id, a.Key);
        await WaitUntilAsync(() => attempts == 1);
        await WaitUntilAsync(() => !runner.IsRunning(id));

        // Meanwhile the user key is rotated (a recovery, say): only the new key opens the vault's slots now.
        var rotation = (await Rotation().RotateAsync(a.User, a.Key, Password, null, Ct))!;
        _time.Advance(TimeSpan.FromSeconds(30));
        runner.Start(id, a.User.Id, rotation.NewUserKey);
        await Task.Delay(200, Ct);
        Assert.Equal(1, attempts);
        _time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(200, Ct);
        Assert.Equal(1, attempts);

        _time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => attempts == 2);
        await WaitUntilAsync(async () => (await Catalog().PendingVaultIdsAsync(a.User.Id, rotation.NewUserKey, Ct)).Count == 0);
        Assert.Equal("# Plan\n\nalpha beta gamma, take 3\n", await Store(a, key: rotation.NewUserKey).ReadAsync(P("/Arbeit/plan.md"), Ct));
    }

    [Fact]
    public async Task Start_during_a_run_swaps_the_key_and_retries_at_once_if_the_run_fails()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        Catalog().RekeyFaultAt = async p =>
        {
            if (p != "after-start" || Interlocked.Increment(ref attempts) != 1) return;
            reached.TrySetResult();
            await release.Task;
        };
        var runner = await RunnerAsync();
        runner.Start(id, a.User.Id, a.Key);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.True(runner.IsRunning(id));

        // A rotation commits mid-run: the key the run holds no longer opens the vault's slots, and its finish fails.
        var rotation = (await Rotation().RotateAsync(a.User, a.Key, Password, null, Ct))!;
        runner.Start(id, a.User.Id, rotation.NewUserKey);
        release.TrySetResult();

        // No backoff: the next attempt follows at once, with the newer key.
        await WaitUntilAsync(async () => (await Catalog().PendingVaultIdsAsync(a.User.Id, rotation.NewUserKey, Ct)).Count == 0);
        Assert.Equal(2, attempts);
        Assert.Equal("# Plan\n\nalpha beta gamma, take 3\n", await Store(a, key: rotation.NewUserKey).ReadAsync(P("/Arbeit/plan.md"), Ct));
    }

    [Fact]
    public async Task RunAsync_rethrows_a_failure_then_backs_off()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        var attempts = 0;
        Catalog().RekeyFaultAt = p =>
        {
            if (p == "after-start" && Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("transient");
            return Task.CompletedTask;
        };
        var runner = await RunnerAsync();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(id, a.User.Id, a.Key));
        Assert.Equal("transient", failure.Message);

        // Backing off: a background trigger doesn't end the wait.
        runner.Start(id, a.User.Id, a.Key);
        await Task.Delay(300, Ct);
        Assert.Equal(1, attempts);
        Assert.False(runner.IsRunning(id));

        // The owner's request does, and waits for the run.
        var summary = await runner.RunAsync(id, a.User.Id, a.Key).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(2, attempts);
        Assert.Equal(13, summary.Documents);
        // A vault that isn't the user's ends the entry, and the wait, at once.
        await Assert.ThrowsAsync<VaultNotRekeyableException>(() => runner.RunAsync(id, a.User.Id, VaultCrypto.NewKey()).WaitAsync(TimeSpan.FromSeconds(30), Ct));
    }

    [Fact]
    public async Task The_runner_drops_a_vault_that_is_gone_from_the_pending_ones()
    {
        var a = await AccountAsync("Arbeit", "Privat");
        var gone = a.Id("Arbeit");
        var rotation = (await Rotation().RotateAsync(a.User, a.Key, Password, null, Ct))!;
        Assert.Equal(new[] { gone, a.Id("Privat") }.Order(StringComparer.Ordinal), (await UserDocAsync(a))["rekeyPending"].AsBsonArray.Select(v => v.AsString).Order(StringComparer.Ordinal));
        await Catalog().DeleteVaultsAsync([gone], (_, _) => Task.CompletedTask, Ct);
        // The deletion takes it out of the pending vaults itself; one deleted by another process before that, say, isn't.
        Assert.Equal([a.Id("Privat")], (await UserDocAsync(a))["rekeyPending"].AsBsonArray.Select(v => v.AsString));
        await Raw("users").UpdateOneAsync(new BsonDocument("_id", a.User.Id), new BsonDocument("$addToSet", new BsonDocument("rekeyPending", gone)), cancellationToken: Ct);

        await Assert.ThrowsAsync<VaultDeletedException>(async () => await (await RunnerAsync()).RunAsync(gone, a.User.Id, rotation.NewUserKey).WaitAsync(TimeSpan.FromSeconds(30), Ct));

        Assert.Equal([a.Id("Privat")], (await UserDocAsync(a))["rekeyPending"].AsBsonArray.Select(v => v.AsString));
    }

    [Theory]
    [InlineData("after-start")]
    [InlineData("after-batch")]
    public async Task After_a_run_stops_in_its_two_key_period_an_old_object_writes_nothing(string point)
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        // A request that opened the vault before the run.
        var stale = Store(a);
        var plan = (await stale.GetAsync(P("/Arbeit/plan.md"), Ct))!;
        var staleVault = (await stale.UnlockedVaultsAsync(Ct)).Single();
        Catalog().BatchDocuments = 3;
        CrashOnceAt(point);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct));
        Assert.False(Catalog().IsRekeying(id));
        var before = await RawAsync(id);
        var embedded = RemoteNotes;

        foreach (var write in new Func<Task>[]
        {
            () => stale.TryCreateAsync(New("/Arbeit/new.md", "new"), Ct),
            () => stale.TryReplaceAsync(plan, NoteWriter.Next(plan, "overwritten", "lea", WriterRef.Server, Now), Ct),
            () => stale.TryAddCheckpointAsync(Cp("drei", "/Arbeit/plan.md", plan), [new Note(plan.Path, plan.Content)], Ct),
            () => Catalog().SetEmbeddingProviderAsync(staleVault, EmbeddingProviders.Internal, Ct),
        })
        {
            Assert.Equal(VaultBusyException.BeingReencrypted, (await Assert.ThrowsAsync<VaultBusyException>(write)).Message);
        }

        await Index().IndexAsync(staleVault, P("/Arbeit/plan.md"), Ct);
        await Index().IndexAsync(staleVault, P("/Arbeit/gone.md"), Ct);
        Assert.Equal(before, await RawAsync(id));
        Assert.Equal(embedded, RemoteNotes);
        // A new request meets the placeholder, not the working set made with the old key.
        await Assert.ThrowsAsync<VaultBusyException>(() => Store(a).ReadAsync(P("/Arbeit/plan.md"), Ct));

        // Once a resumed run finishes, the old object is one of an old key.
        Catalog().RekeyFaultAt = null;
        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);
        Assert.Equal(VaultBusyException.JustReencrypted, (await Assert.ThrowsAsync<VaultBusyException>(() => stale.TryCreateAsync(New("/Arbeit/new.md", "new"), Ct))).Message);
    }

    [Fact]
    public async Task A_start_that_is_refused_leaves_the_vault_writable()
    {
        var a = await AccountAsync("Arbeit");
        var store = Store(a);
        Assert.True(await store.TryCreateAsync(New("/Arbeit/a.md", "a"), Ct));

        await Assert.ThrowsAsync<VaultNotRekeyableException>(() => Catalog().RekeyAsync(a.Id("Arbeit"), a.User.Id, VaultCrypto.NewKey(), Ct));

        Assert.True(await Store(a).TryCreateAsync(New("/Arbeit/b.md", "b"), Ct));
        Assert.True(await store.TryCreateAsync(New("/Arbeit/c.md", "c"), Ct));
    }

    [Fact]
    public async Task A_resume_refused_for_a_replaced_user_key_keeps_the_old_key_out()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        var stale = Store(a);
        var plan = (await stale.GetAsync(P("/Arbeit/plan.md"), Ct))!;
        CrashOnceAt("after-start");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct));
        var rotation = (await Rotation().RotateAsync(a.User, a.Key, Password, null, Ct))!;
        var before = await RawAsync(id);

        // A runner still holding the key the rotation replaced.
        await Assert.ThrowsAsync<VaultNotRekeyableException>(() => Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct));

        Assert.Equal(VaultBusyException.BeingReencrypted, (await Assert.ThrowsAsync<VaultBusyException>(() => stale.TryReplaceAsync(plan, NoteWriter.Next(plan, "overwritten", "lea", WriterRef.Server, Now), Ct))).Message);
        await Index().IndexAsync((await stale.UnlockedVaultsAsync(Ct)).Single(), P("/Arbeit/plan.md"), Ct);
        Assert.Equal(before, await RawAsync(id));
        await Catalog().RekeyAsync(id, a.User.Id, rotation.NewUserKey, Ct);
        Assert.Equal("# Plan\n\nalpha beta gamma, take 3\n", await Store(a, key: rotation.NewUserKey).ReadAsync(P("/Arbeit/plan.md"), Ct));
    }

    [Fact]
    public async Task A_retry_after_an_attempt_that_failed_before_its_start_rekeys_for_real()
    {
        var a = await FilledAsync();
        var id = a.Id("Arbeit");
        var oldKey = await VaultKeyAsync(a.Key, id);
        var runner = await RunnerAsync();
        runner.AttemptTimeout = TimeSpan.FromMinutes(1);
        // A writer holds the vault's gate: the first attempt waits for it until its time is up, before any start.
        var gate = Catalog().LocksFor(id).Gate;
        await gate.WaitAsync(Ct);
        Task<RekeySummary> first;
        try
        {
            first = runner.RunAsync(id, a.User.Id, a.Key);
            await WaitUntilAsync(() => Catalog().IsRekeying(id));
            _time.Advance(TimeSpan.FromMinutes(1));
            await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            await WaitUntilAsync(() => !Catalog().IsRekeying(id));
        }
        finally
        {
            gate.Release();
        }

        Assert.Empty(await Catalog().PendingVaultIdsAsync(a.User.Id, a.Key, Ct));

        // Not pending anywhere, since nothing began: the retry re-keys the vault, it doesn't report a re-keying that never ran.
        var summary = await runner.RunAsync(id, a.User.Id, a.Key).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(13, summary.Documents);
        Assert.NotEqual(oldKey, await VaultKeyAsync(a.Key, id));
        Assert.Equal("# Plan\n\nalpha beta gamma, take 3\n", await Store(a).ReadAsync(P("/Arbeit/plan.md"), Ct));
    }

    [Fact]
    public async Task A_damaged_holder_of_the_new_pid_never_pushes_out_a_readable_note()
    {
        var a = await AccountAsync("Arbeit");
        var id = a.Id("Arbeit");
        Assert.True(await Store(a).TryCreateAsync(New("/Arbeit/plan.md", "the real one"), Ct));
        var original = (await Docs("notes").Find(d => d.Vault == id).SingleAsync(Ct)).Id;
        var forged = ObjectId.GenerateNewId();
        Catalog().RekeyFaultAt = async p =>
        {
            if (p != "after-start") return;
            // Written by someone who knows the new pid but not the key, with a revision that would win.
            var next = new UnlockedVault(id, "", await VaultKeyAsync(a.Key, id, SlotDocument.UserNext), Now, new VaultLocks());
            await Docs("notes").InsertOneAsync(
                new SealedDocument { Id = forged, Vault = id, PathId = next.PathId(P("/Arbeit/plan.md")), Revision = 99, Blob = RandomNumberGenerator.GetBytes(80) }, cancellationToken: Ct);
        };

        await Catalog().RekeyAsync(id, a.User.Id, a.Key, Ct);

        Assert.Equal(original, (await Docs("notes").Find(d => d.Vault == id).SingleAsync(Ct)).Id);
        Assert.Equal("the real one", await Store(a, NewCatalog()).ReadAsync(P("/Arbeit/plan.md"), Ct));
        Assert.Contains(_log.Lines, l => l.Contains($"Document {forged} of vault {id}", StringComparison.Ordinal) && l.Contains("opens under neither key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_hung_run_times_out_and_the_queue_goes_on()
    {
        var a = await AccountAsync("Eins", "Zwei");
        var (hung, next) = (a.Id("Eins"), a.Id("Zwei"));
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hanging = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Catalog().RekeyFaultAt = async p =>
        {
            if (p != "after-start" || Catalog().IsRekeying(next)) return;
            // Never returns while the test runs, and watches no token.
            hanging.TrySetResult();
            await hang.Task;
        };
        var runner = await RunnerAsync();
        runner.AttemptTimeout = TimeSpan.FromMinutes(1);
        try
        {
            var first = runner.RunAsync(hung, a.User.Id, a.Key);
            var second = runner.RunAsync(next, a.User.Id, a.Key);
            await hanging.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await Task.Delay(200, Ct);
            Assert.False(second.IsCompleted);

            _time.Advance(TimeSpan.FromMinutes(1));

            var failure = await Assert.ThrowsAsync<TimeoutException>(() => first.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            Assert.Contains(hung, failure.Message, StringComparison.Ordinal);
            await second.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.DoesNotContain(next, await Catalog().PendingVaultIdsAsync(a.User.Id, a.Key, Ct));
            Assert.False(runner.IsRunning(hung));
        }
        finally
        {
            hang.TrySetResult();
        }
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();

        public void Stop() => _stopping.Cancel();
    }
}

/// <summary>Re-keying as the server's clients meet it: the vault answers 503 while it waits, and an app that loses its
/// tokens reconnects and opens the vault under its new key.</summary>
[Collection("app")]
public sealed class RekeyApiTests(KumbuAppFactory app)
{
    private const string Callback = "https://claude.test/cb";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Api => app.PublicApi;

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    private static string Address() => $"198.51.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    private async Task<ModelContextProtocol.Client.McpClient> McpAsync(string connectionId, string access)
    {
        var transport = new ModelContextProtocol.Client.HttpClientTransport(
            new ModelContextProtocol.Client.HttpClientTransportOptions
            {
                Endpoint = new Uri(OAuth.OAuthFlow.ResourceOf(connectionId)),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {access}", ["X-Forwarded-For"] = Address() },
            },
            Api.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        return await ModelContextProtocol.Client.McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private static async Task<string> CatAsync(ModelContextProtocol.Client.McpClient client, string path)
    {
        var result = await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = $"cat {path}" }, cancellationToken: Ct);
        return Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(result.Content[0]).Text;
    }

    private async Task<string> AccessAsync(Kumbu.Server.Tests.Api.RestClient owner, string connectionId)
    {
        var flow = await OAuth.OAuthFlow.StartAsync(Api, owner, connectionId, await OAuth.OAuthFlow.RegisterAsync(Api, Callback), Callback);
        await flow.AllowAsync();
        using var response = await flow.TokenAsync();
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return (await Kumbu.Server.Tests.Api.RestClient.JsonAsync(response))["access_token"]!.GetValue<string>();
    }

    private Task<(string Key, ApiKeyInfo Info)> CreateKeyAsync(Kumbu.Server.Tests.Api.RestClient owner, string vaultId, string name) =>
        Api.Services.GetRequiredService<ApiKeys>().CreateAsync(owner.User, owner.UserKey, DateTime.UtcNow, name, new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Read }, Ct);

    private async Task<string> ConnectionAsync(Kumbu.Server.Tests.Api.RestClient owner)
    {
        using var created = await owner.SendAsync(HttpMethod.Post, "/api/connections", new { name = "claude", vaults = new Dictionary<string, string> { ["Arbeit"] = "write" } });
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        return (await Kumbu.Server.Tests.Api.RestClient.JsonAsync(created))["id"]!.GetValue<string>();
    }

    /// <summary>A run of the vault that stops after its start, as a crash there would: the vault is left in its two-key period.</summary>
    private async Task StopAfterStartAsync(string vaultId, Kumbu.Server.Tests.Api.RestClient owner)
    {
        Catalog.RekeyFaultAt = p => p == "after-start" ? throw new InvalidOperationException("crash after the start") : Task.CompletedTask;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, Ct));
        }
        finally
        {
            Catalog.RekeyFaultAt = null;
        }
    }

    /// <summary>Runs <paramref name="during"/> the first time an issuing reaches <paramref name="point"/>.</summary>
    private void OnceAt(string point, Func<Task> during)
    {
        var done = false;
        Catalog.IssueFaultAt = async p =>
        {
            if (p != point || done) return;
            done = true;
            await during();
        };
    }

    private Task<long> SlotsOnAsync(string vaultId, string kind) =>
        Catalog.Slots.CountDocumentsAsync(s => s.Vault == vaultId && s.Kind == kind, cancellationToken: Ct);

    private async Task<string> RefusedConsentAsync(Kumbu.Server.Tests.Api.RestClient owner, string connectionId)
    {
        var flow = await OAuth.OAuthFlow.StartAsync(Api, owner, connectionId, await OAuth.OAuthFlow.RegisterAsync(Api, Callback), Callback);
        using var response = await flow.DecideAsync(allow: true);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        return await Kumbu.Server.Tests.Api.RestClient.ErrorAsync(response);
    }

    /// <summary>After the vault was re-keyed: a new API key and a new consent open it, under its new key.</summary>
    private async Task AssertIssuedUnderTheNewKeyAsync(Kumbu.Server.Tests.Api.RestClient owner, string vaultId, string connectionId, string keyName)
    {
        var (key, _) = await CreateKeyAsync(owner, vaultId, keyName);
        using (var read = await owner.GetAsync(owner.Url("plan.md"), bearer: key)) Assert.Equal(System.Net.HttpStatusCode.OK, read.StatusCode);
        var access = await AccessAsync(owner, connectionId);
        await using var client = await McpAsync(connectionId, access);
        Assert.Contains("# Plan", await CatAsync(client, "/Arbeit/plan.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_or_code_prepared_before_the_start_and_committed_after_fails()
    {
        var owner = await Kumbu.Server.Tests.Api.RestClient.NewAsync(Api);
        await owner.CreateAsync("plan.md", "# Plan\n");
        var vaultId = (await Catalog.FindForUserAsync(owner.User.Id, owner.UserKey, "Arbeit", Ct))!;
        var connectionId = await ConnectionAsync(owner);
        try
        {
            // The key's slots wrap the vault key; the run's start commits before the key's transaction.
            OnceAt("after-wrap", () => StopAfterStartAsync(vaultId, owner));
            var key = await Assert.ThrowsAsync<VaultBusyException>(() => CreateKeyAsync(owner, vaultId, "agent"));
            Assert.Equal(VaultBusyException.JustReencrypted, key.Message);
            Assert.Equal(0, await SlotsOnAsync(vaultId, SlotDocument.ApiKey));
            // Prepared during the period, it is refused before anything is wrapped.
            Assert.Equal(VaultBusyException.BeingReencrypted, (await Assert.ThrowsAsync<VaultBusyException>(() => CreateKeyAsync(owner, vaultId, "agent"))).Message);
            await Catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, Ct);

            // The same for a code: the consent's slots wrap the vault key, then a run starts.
            OnceAt("after-wrap", () => StopAfterStartAsync(vaultId, owner));
            Assert.Equal(VaultBusyException.JustReencrypted, await RefusedConsentAsync(owner, connectionId));
            Assert.Equal(0, await SlotsOnAsync(vaultId, SlotDocument.Connection));
            Catalog.IssueFaultAt = null;
            Assert.Equal(VaultBusyException.BeingReencrypted, await RefusedConsentAsync(owner, connectionId));
            // The consent page met the pending vault and started the runner: the resume is its.
            await Api.Services.GetRequiredService<RekeyRunner>().RunAsync(vaultId, owner.User.Id, owner.UserKey).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        finally
        {
            Catalog.IssueFaultAt = null;
        }

        await AssertIssuedUnderTheNewKeyAsync(owner, vaultId, connectionId, "agent");
    }

    [Fact]
    public async Task A_key_or_code_prepared_before_the_finish_and_committed_after_fails()
    {
        var owner = await Kumbu.Server.Tests.Api.RestClient.NewAsync(Api);
        await owner.CreateAsync("plan.md", "# Plan\n");
        var vaultId = (await Catalog.FindForUserAsync(owner.User.Id, owner.UserKey, "Arbeit", Ct))!;
        var connectionId = await ConnectionAsync(owner);
        try
        {
            // A whole run, start to finish, between the wrapping and the transaction: the vault is out of its
            // period again, but its user slot now opens to another key than the one wrapped.
            OnceAt("after-wrap", () => Catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, Ct));
            Assert.Equal(VaultBusyException.JustReencrypted, (await Assert.ThrowsAsync<VaultBusyException>(() => CreateKeyAsync(owner, vaultId, "agent"))).Message);
            Assert.Equal(0, await SlotsOnAsync(vaultId, SlotDocument.ApiKey));

            OnceAt("after-wrap", () => Catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, Ct));
            Assert.Equal(VaultBusyException.JustReencrypted, await RefusedConsentAsync(owner, connectionId));
            Assert.Equal(0, await SlotsOnAsync(vaultId, SlotDocument.Connection));
        }
        finally
        {
            Catalog.IssueFaultAt = null;
        }

        await AssertIssuedUnderTheNewKeyAsync(owner, vaultId, connectionId, "agent");
    }

    [Fact]
    public async Task A_key_or_code_issued_across_a_rekey_wraps_the_new_key_or_fails()
    {
        var owner = await Kumbu.Server.Tests.Api.RestClient.NewAsync(Api);
        await owner.CreateAsync("plan.md", "# Plan\n");
        var vaultId = (await Catalog.FindForUserAsync(owner.User.Id, owner.UserKey, "Arbeit", Ct))!;
        var connectionId = await ConnectionAsync(owner);
        try
        {
            // The code's transaction has read the connection, the user and its vaults, and the run's start commits
            // only now: its checks would pass on what it read, but its write to the connection conflicts with the
            // start's, and the retry sees the vault in its period.
            OnceAt("before-check", () => StopAfterStartAsync(vaultId, owner));
            Assert.Equal(VaultBusyException.JustReencrypted, await RefusedConsentAsync(owner, connectionId));
            Assert.Equal(0, await SlotsOnAsync(vaultId, SlotDocument.Connection));
            await Catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, Ct);

            // The key's transaction has written the user document when a run starts: the start's write to it
            // conflicts until the key commits, and then revokes the key with the vault.
            Task<RekeySummary>? run = null;
            OnceAt("before-check", async () =>
            {
                run = Task.Run(() => Catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, Ct), Ct);
                await Task.Delay(500, Ct);
                Assert.False(run.IsCompleted);
            });
            var (key, info) = await CreateKeyAsync(owner, vaultId, "agent");
            var summary = await run!.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal([info.Id], summary.RevokedApiKeys);
            Assert.Null(await Api.Services.GetRequiredService<ApiKeys>().ResolveAsync(key, Ct));
            Assert.Equal(0, await SlotsOnAsync(vaultId, SlotDocument.ApiKey));
        }
        finally
        {
            Catalog.IssueFaultAt = null;
        }

        await AssertIssuedUnderTheNewKeyAsync(owner, vaultId, connectionId, "agent 2");
    }

    [Fact]
    public async Task A_consent_refused_for_a_vault_being_reencrypted_keeps_its_request()
    {
        var owner = await Kumbu.Server.Tests.Api.RestClient.NewAsync(Api);
        await owner.CreateAsync("plan.md", "# Plan\n");
        var vaultId = (await Catalog.FindForUserAsync(owner.User.Id, owner.UserKey, "Arbeit", Ct))!;
        var connectionId = await ConnectionAsync(owner);
        var runner = Api.Services.GetRequiredService<RekeyRunner>();
        // The first run stops after its start; the runner's resume, which the decision starts, waits there until let go.
        var first = true;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Catalog.RekeyFaultAt = async p =>
        {
            if (p != "after-start") return;
            if (first)
            {
                first = false;
                throw new InvalidOperationException("crash after the start");
            }

            await hold.Task;
        };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, Ct));
            var flow = await OAuth.OAuthFlow.StartAsync(Api, owner, connectionId, await OAuth.OAuthFlow.RegisterAsync(Api, Callback), Callback);

            using (var busy = await flow.DecideAsync(allow: true))
            {
                Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, busy.StatusCode);
                Assert.Equal("5", busy.Headers.GetValues("Retry-After").Single());
                Assert.Equal(VaultBusyException.BeingReencrypted, await Kumbu.Server.Tests.Api.RestClient.ErrorAsync(busy));
            }

            using (var still = await flow.GetConsentAsync()) Assert.Equal(System.Net.HttpStatusCode.OK, still.StatusCode);
            Assert.Equal(0, await SlotsOnAsync(vaultId, SlotDocument.Connection));
            hold.SetResult();
            await runner.RunAsync(vaultId, owner.User.Id, owner.UserKey).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Catalog.RekeyFaultAt = null;

            // The same request, allowed once the vault is done.
            await flow.AllowAsync();
            using (var tokens = await flow.TokenAsync()) Assert.Equal(System.Net.HttpStatusCode.OK, tokens.StatusCode);

            // Refused inside the transaction (a whole run between the wrapping and the commit): the request goes back too.
            var again = await OAuth.OAuthFlow.StartAsync(Api, owner, connectionId, await OAuth.OAuthFlow.RegisterAsync(Api, Callback), Callback);
            OnceAt("after-wrap", () => Catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, Ct));
            using (var busy = await again.DecideAsync(allow: true))
            {
                Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, busy.StatusCode);
                Assert.Equal(VaultBusyException.JustReencrypted, await Kumbu.Server.Tests.Api.RestClient.ErrorAsync(busy));
            }

            await again.AllowAsync();
            using (var tokens = await again.TokenAsync()) Assert.Equal(System.Net.HttpStatusCode.OK, tokens.StatusCode);
        }
        finally
        {
            hold.TrySetResult();
            Catalog.RekeyFaultAt = null;
            Catalog.IssueFaultAt = null;
        }
    }

    [Fact]
    public async Task A_stale_vault_key_of_a_bearer_answers_503()
    {
        var owner = await Kumbu.Server.Tests.Api.RestClient.NewAsync(Api);
        await owner.CreateAsync("plan.md", "# Plan\n");
        var vaultId = (await Catalog.FindForUserAsync(owner.User.Id, owner.UserKey, "Arbeit", Ct))!;
        var connectionId = await ConnectionAsync(owner);
        var access = await AccessAsync(owner, connectionId);
        var key = await owner.KeyAsync("agent", KeyRight.Read);
        // A backup of the key's and the token's records and slots, restored after the re-keying: they still wrap the old key.
        var db = app.Database;
        var saved = new List<(string Collection, BsonDocument Doc)>();
        foreach (var (collection, filter) in new (string, BsonDocument)[]
        {
            (ApiKeys.CollectionName, new BsonDocument("user", owner.User.Id)),
            (Kumbu.Server.Accounts.Connections.CollectionName, new BsonDocument("_id", connectionId)),
            (ConnectionRevocation.TokensCollection, new BsonDocument("connection", connectionId)),
            ("slots", new BsonDocument { ["vault"] = vaultId, ["kind"] = new BsonDocument("$ne", SlotDocument.User) }),
        })
        {
            saved.AddRange((await db.GetCollection<BsonDocument>(collection).Find(filter).ToListAsync(Ct)).Select(d => (collection, d)));
        }

        Assert.Contains(saved, d => d.Collection == "slots" && d.Doc["kind"] == SlotDocument.ApiKey);
        Assert.Contains(saved, d => d.Collection == "slots" && d.Doc["kind"] == SlotDocument.Connection);
        await Catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, Ct);
        foreach (var (collection, doc) in saved)
        {
            await db.GetCollection<BsonDocument>(collection).ReplaceOneAsync(new BsonDocument("_id", doc["_id"]), doc, new ReplaceOptions { IsUpsert = true }, Ct);
        }

        using (var api = await owner.GetAsync(owner.Url("plan.md"), bearer: key))
        {
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, api.StatusCode);
            Assert.Equal("5", api.Headers.GetValues("Retry-After").Single());
            Assert.Equal(VaultBusyException.JustReencrypted, await Kumbu.Server.Tests.Api.RestClient.ErrorAsync(api));
        }

        using var client = Api.CreateClient();
        using (var mcp = new HttpRequestMessage(HttpMethod.Post, "/mcp"))
        {
            mcp.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            mcp.Headers.Add("X-Forwarded-For", Address());
            mcp.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var answer = await client.SendAsync(mcp, Ct);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, answer.StatusCode);
            Assert.Equal("5", answer.Headers.GetValues("Retry-After").Single());
        }

        using (var mcp = new HttpRequestMessage(HttpMethod.Post, new Uri(OAuth.OAuthFlow.ResourceOf(connectionId)).AbsolutePath))
        {
            mcp.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
            mcp.Headers.Add("X-Forwarded-For", Address());
            mcp.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var answer = await client.SendAsync(mcp, Ct);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, answer.StatusCode);
            Assert.Equal("5", answer.Headers.GetValues("Retry-After").Single());
        }
    }

    [Fact]
    public async Task A_reconnect_after_a_rekey_opens_the_vault_with_the_new_key()
    {
        var owner = await Kumbu.Server.Tests.Api.RestClient.NewAsync(Api);
        await owner.CreateAsync("plan.md", "# Plan\n\nbefore\n");
        using var created = await owner.SendAsync(HttpMethod.Post, "/api/connections", new { name = "claude", vaults = new Dictionary<string, string> { ["Arbeit"] = "write" } });
        var connectionId = (await Kumbu.Server.Tests.Api.RestClient.JsonAsync(created))["id"]!.GetValue<string>();
        var access = await AccessAsync(owner, connectionId);
        await using (var client = await McpAsync(connectionId, access)) Assert.Contains("before", await CatAsync(client, "/Arbeit/plan.md"), StringComparison.Ordinal);
        var key = await owner.KeyAsync("agent", KeyRight.Read);
        var vaultId = (await Catalog.FindForUserAsync(owner.User.Id, owner.UserKey, "Arbeit", Ct))!;

        var summary = await Api.Services.GetRequiredService<RekeyRunner>().RunAsync(vaultId, owner.User.Id, owner.UserKey).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal([connectionId], summary.DisconnectedConnections);
        Assert.Single(summary.RevokedApiKeys);
        using (var listed = await owner.GetAsync("/api/connections"))
        {
            var connection = (await Kumbu.Server.Tests.Api.RestClient.JsonAsync(listed)).AsArray().Single(c => c!["id"]!.GetValue<string>() == connectionId)!;
            Assert.Equal(("needs_reconnect", ConnectionReasons.Rekeyed), (connection["state"]!.GetValue<string>(), connection["reason"]!.GetValue<string>()));
        }

        await Assert.ThrowsAnyAsync<Exception>(() => McpAsync(connectionId, access));
        using (var withKey = await owner.GetAsync(owner.Url("plan.md"), bearer: key)) Assert.Equal(System.Net.HttpStatusCode.Unauthorized, withKey.StatusCode);

        // The reconnect wraps the new vault key for the app's new tokens.
        var again = await AccessAsync(owner, connectionId);
        await using (var client = await McpAsync(connectionId, again)) Assert.Contains("before", await CatAsync(client, "/Arbeit/plan.md"), StringComparison.Ordinal);
        Assert.Equal("# Plan\n\nbefore\n", (await owner.ReadAsync("plan.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_vault_being_reencrypted_answers_503_and_stays_listed()
    {
        var owner = await Kumbu.Server.Tests.Api.RestClient.NewAsync(Api);
        await owner.CreateAsync("plan.md", "# Plan\n");
        var vaultId = (await Catalog.FindForUserAsync(owner.User.Id, owner.UserKey, "Arbeit", Ct))!;
        var runner = Api.Services.GetRequiredService<RekeyRunner>();
        // The first run stops after its start, as a crash would. Every later one (the runner's, which the requests
        // below start) waits after its start until the test lets it go: the vault is in its two-key period throughout.
        var first = true;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Catalog.RekeyFaultAt = async p =>
        {
            if (p != "after-start") return;
            if (first)
            {
                first = false;
                throw new InvalidOperationException("crash after the start");
            }

            await hold.Task;
        };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog.RekeyAsync(vaultId, owner.User.Id, owner.UserKey, Ct));

            using (var vaults = await owner.GetAsync("/api/vaults"))
            {
                Assert.Equal(System.Net.HttpStatusCode.OK, vaults.StatusCode);
                var arbeit = (await Kumbu.Server.Tests.Api.RestClient.JsonAsync(vaults)).AsArray().Single(v => v!["name"]!.GetValue<string>() == "Arbeit")!;
                Assert.Equal(0, arbeit["notes"]!.GetValue<int>());
            }

            foreach (var path in new[] { owner.Url("plan.md"), $"/api/vaults/Arbeit/tree", $"/api/vaults/Arbeit/checkpoints", owner.Url("plan.md", "history") })
            {
                using var busy = await owner.GetAsync(path);
                Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, busy.StatusCode);
                Assert.Equal("5", busy.Headers.GetValues("Retry-After").Single());
                Assert.Equal(VaultBusyException.BeingReencrypted, await Kumbu.Server.Tests.Api.RestClient.ErrorAsync(busy));
            }

            using var write = await owner.SendAsync(HttpMethod.Put, owner.Url("new.md"), new { content = "new" });
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, write.StatusCode);

            // The requests started the runner; its run waits after the start. The owner's request waits for that run.
            var run = runner.RunAsync(vaultId, owner.User.Id, owner.UserKey);
            Assert.False(run.IsCompleted);
            hold.SetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        finally
        {
            hold.TrySetResult();
            Catalog.RekeyFaultAt = null;
        }

        Assert.Equal("# Plan\n", (await owner.ReadAsync("plan.md"))["content"]!.GetValue<string>());
    }
}
