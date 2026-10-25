// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>Deleting a vault, one's own account, or a deactivated user: everything of it, nothing else, in one go.</summary>
[Collection("mongo")]
public sealed class DeletionTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";

    /// <summary>Every collection that holds a vault's documents under their "vault" field.</summary>
    private static readonly string[] ContentCollections = ["notes", "trash", "moves", "embeddings", "checkpoints", "checkpoint_texts"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
    private readonly CapturedLog _log = new();
    private VaultCatalog? _catalog;

    /// <summary>One catalog, as one server process has it: vaults in memory are shared between the calls.</summary>
    private VaultCatalog Catalog() => _catalog ??= new(_db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance);

    private ApiKeys Keys() => new(_db, Catalog(), _time, NullLogger<ApiKeys>.Instance);

    private UserCatalog Users() =>
        new(_db, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), Keys(), _time, NullLogger<UserCatalog>.Instance);

    private Sessions Sessions() => new(_db, _time);

    private Admin Admin() => new(_db, Catalog(), Sessions(), Keys(), _time, NullLogger<Admin>.Instance);

    private Deletion Deletion() => new(_db, Catalog(), Users(), LoggerFactory.Create(b => b.AddProvider(_log)).CreateLogger<Deletion>());

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private IMongoCollection<BsonDocument> Raw(string name) => _db.GetCollection<BsonDocument>(name);

    private async Task<long> CountAsync(string collection, string field, string value) =>
        await Raw(collection).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq(field, value), cancellationToken: Ct);

    /// <summary>A user signed in with a session: the user, their user key and the session secret.</summary>
    private async Task<(User User, byte[] Key, string Session)> SignedInAsync(string username, string role = Roles.User)
    {
        await Users().CreateAsync(username, Password, role, Ct);
        var (user, key, readAt) = (await Users().TrySignInAsync(username, Password, Ct))!.Value;
        return (user, key, await Sessions().StartAsync(user, key, readAt, Ct));
    }

    /// <summary>A vault of the user with a note in it, and a document in every other content collection.</summary>
    private async Task<UnlockedVault> FilledVaultAsync(User user, byte[] key, string name)
    {
        var vault = await Catalog().CreateForUserAsync(name, user.Id, key, Ct);
        var store = new EncryptedNoteStore(Catalog(), new UserCredential(user.Id, key));
        Assert.True(await store.TryCreateAsync(new StoredNote(P($"/{name}/plan.md"), "Geheim: wir kaufen Acme.", 1, user.Username, Now, []), Ct));
        foreach (var collection in ContentCollections.Skip(1))
        {
            await Raw(collection).InsertOneAsync(new BsonDocument { ["vault"] = vault.Id, ["pid"] = new BsonBinaryData([1, 2, 3]), ["blob"] = new BsonBinaryData([4]) }, cancellationToken: Ct);
        }

        return vault;
    }

    private async Task AssertGoneAsync(string vaultId)
    {
        Assert.Equal(0, await CountAsync("vaults", "_id", vaultId));
        Assert.Equal(0, await CountAsync("slots", "vault", vaultId));
        foreach (var collection in ContentCollections) Assert.Equal(0, await CountAsync(collection, "vault", vaultId));
    }

    private async Task AssertIntactAsync(string vaultId)
    {
        Assert.Equal(1, await CountAsync("vaults", "_id", vaultId));
        Assert.True(await CountAsync("slots", "vault", vaultId) > 0);
        foreach (var collection in ContentCollections) Assert.True(await CountAsync(collection, "vault", vaultId) > 0, collection);
    }

    [Fact]
    public async Task Deleting_a_vault_removes_it_with_every_document_and_slot_and_nothing_else()
    {
        var (lea, key, _) = await SignedInAsync("lea");
        var (bob, bobKey, _) = await SignedInAsync("bob");
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");
        var work = await FilledVaultAsync(lea, key, "Arbeit");
        var bobs = await FilledVaultAsync(bob, bobKey, "Tagebuch");
        var (apiKey, info) = await Keys().CreateAsync(
            lea, key, Now, "agent", new Dictionary<string, KeyRight> { [diary.Id] = KeyRight.Write, [work.Id] = KeyRight.Read }, Ct);

        var summary = await Deletion().DeleteVaultAsync(lea, key, "Tagebuch", Password, "Tagebuch", Ct);

        Assert.NotNull(summary);
        await AssertGoneAsync(diary.Id);
        await AssertIntactAsync(work.Id);
        await AssertIntactAsync(bobs.Id);
        Assert.Equal(["Arbeit"], (await Catalog().OpenForUserAsync(lea.Id, key, Ct)).Select(v => v.Name));
        // The key keeps its other vault.
        Assert.NotNull(await Keys().ResolveAsync(apiKey, Ct));
        Assert.Equal([work.Id], (await Catalog().OpenForApiKeyAsync(info.Id, apiKey, Ct)).Select(v => v.Vault.Id));
    }

    [Fact]
    public async Task A_vault_is_deleted_only_with_its_name_typed_exactly_and_only_by_its_owner()
    {
        var (lea, key, _) = await SignedInAsync("lea");
        var (bob, bobKey, _) = await SignedInAsync("bob");
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");

        await Assert.ThrowsAsync<ArgumentException>(() => Deletion().DeleteVaultAsync(lea, key, "Tagebuch", Password, "tagebuch", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Deletion().DeleteVaultAsync(lea, key, "Tagebuch", Password, "", Ct));
        Assert.Null(await Deletion().DeleteVaultAsync(lea, key, "tagebuch", Password, "tagebuch", Ct));
        Assert.Null(await Deletion().DeleteVaultAsync(bob, bobKey, "Tagebuch", Password, "Tagebuch", Ct));
        var wrong = await Assert.ThrowsAsync<ArgumentException>(() => Deletion().DeleteVaultAsync(lea, key, "Tagebuch", "wrong password, long enough", "Tagebuch", Ct));
        Assert.StartsWith("The password is wrong.", wrong.Message, StringComparison.Ordinal);
        // The right password, but for another user key: it must open exactly the caller's.
        await Assert.ThrowsAsync<ArgumentException>(() => Deletion().DeleteVaultAsync(lea, bobKey, "Tagebuch", Password, "Tagebuch", Ct));

        await AssertIntactAsync(diary.Id);
    }

    [Fact]
    public async Task A_write_that_reaches_a_vault_after_its_deletion_writes_nothing()
    {
        var (lea, key, _) = await SignedInAsync("lea");
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");
        // A request that opened the vault before the deletion and writes after it.
        var store = new EncryptedNoteStore(Catalog(), new UserCredential(lea.Id, key));
        Assert.Contains("Tagebuch", await store.ListVaultsAsync(Ct));

        await Deletion().DeleteVaultAsync(lea, key, "Tagebuch", Password, "Tagebuch", Ct);

        await Assert.ThrowsAsync<VaultDeletedException>(() => store.TryCreateAsync(new StoredNote(P("/Tagebuch/late.md"), "late", 1, "lea", Now, []), Ct));
        await AssertGoneAsync(diary.Id);
    }

    [Fact]
    public async Task A_load_that_read_the_slot_before_the_commit_does_not_bring_the_vault_back()
    {
        var (lea, key, _) = await SignedInAsync("lea");
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");
        // Locked, so the next request has to load it from MongoDB again.
        _time.Advance(TimeSpan.FromMinutes(20));
        Catalog().Sweep();
        Assert.Equal(0, Catalog().OpenCount);

        Task<IReadOnlyList<UnlockedVault>>? load = null;
        await Catalog().DeleteVaultsAsync(
            [diary.Id],
            async (_, _) =>
            {
                // A request while the deletion is uncommitted: it still finds the slot and the vault document.
                load ??= Catalog().OpenForUserAsync(lea.Id, key, Ct);
                await Task.Delay(300, Ct);
            },
            Ct);

        Assert.Empty(await load!);
        Assert.Equal(0, Catalog().OpenCount);
        Assert.True(Catalog().IsDeleted(diary.Id));
        await AssertGoneAsync(diary.Id);
    }

    [Fact]
    public async Task An_older_vault_object_waiting_in_the_index_queue_brings_no_embedding_back()
    {
        var (lea, key, _) = await SignedInAsync("lea");
        var stale = await FilledVaultAsync(lea, key, "Tagebuch");
        _time.Advance(TimeSpan.FromMinutes(20));
        Catalog().Sweep();
        using var index = new Kumbu.Server.Search.SearchIndex(Catalog(), new Search.FakeEmbedder(), NullLogger<Kumbu.Server.Search.SearchIndex>.Instance);
        index.Enqueue(stale, [P("/Tagebuch/plan.md")]);

        // The deletion unlocks a new object of the vault to find it; the one in the queue is older.
        await Deletion().DeleteVaultAsync(lea, key, "Tagebuch", Password, "Tagebuch", Ct);
        Assert.False(stale.Deleted);
        await index.IndexAsync(stale, P("/Tagebuch/plan.md"), Ct);

        Assert.Equal(0, await CountAsync("embeddings", "vault", stale.Id));
        await AssertGoneAsync(stale.Id);
        var store = new EncryptedNoteStore(Catalog(), new UserCredential(lea.Id, key));
        Assert.Empty(await store.ListVaultsAsync(Ct));
    }

    [Fact]
    public async Task Deleting_ones_account_removes_the_user_their_slots_keys_and_vaults_and_keeps_others_and_invitations()
    {
        var (anna, _, _) = await SignedInAsync("anna", Roles.Admin);
        var (lea, key, session) = await SignedInAsync("lea", Roles.Admin);
        var (bob, bobKey, _) = await SignedInAsync("bob");
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");
        var work = await FilledVaultAsync(lea, key, "Arbeit");
        var bobs = await FilledVaultAsync(bob, bobKey, "Tagebuch");
        var (apiKey, _) = await Keys().CreateAsync(lea, key, Now, "agent", new Dictionary<string, KeyRight> { [diary.Id] = KeyRight.Write }, Ct);
        var (bobsKey, _) = await Keys().CreateAsync(bob, bobKey, Now, "agent", new Dictionary<string, KeyRight> { [bobs.Id] = KeyRight.Write }, Ct);
        var invitations = new Invitations(_db, new ServerSecret(_db), Users(), _time, NullLogger<Invitations>.Instance);
        await invitations.CreateAsync(lea, Roles.User, "for Max", null, Ct);

        Assert.True(await Deletion().DeleteOwnAccountAsync(lea, key, Password, "Lea", Ct));

        Assert.Equal(0, await CountAsync("users", "_id", lea.Id));
        Assert.Equal(0, await CountAsync("user_slots", "user", lea.Id));
        Assert.Equal(0, await CountAsync("api_keys", "user", lea.Id));
        await AssertGoneAsync(diary.Id);
        await AssertGoneAsync(work.Id);
        Assert.Null(await Sessions().ResolveAsync(session, Ct));
        Assert.Null(await Keys().ResolveAsync(apiKey, Ct));
        Assert.Null(await Users().TrySignInAsync("lea", Password, Ct));

        await AssertIntactAsync(bobs.Id);
        Assert.NotNull(await Keys().ResolveAsync(bobsKey, Ct));
        Assert.NotNull(await Users().TrySignInAsync("bob", Password, Ct));
        Assert.Equal(1, await CountAsync("invitations", "createdBy", lea.Id));
        Assert.Equal(["anna", "bob"], (await Admin().ListUsersAsync(anna, Ct)).Select(u => u.Username).Order());
    }

    [Fact]
    public async Task Deleting_ones_account_needs_the_password_and_the_username_typed_and_never_the_primary_admin()
    {
        var (lea, key, _) = await SignedInAsync("lea");
        var (chris, chrisKey, _) = await SignedInAsync("chris", Roles.PrimaryAdmin);
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");

        Assert.False(await Deletion().DeleteOwnAccountAsync(lea, key, "wrong password, long enough", "lea", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Deletion().DeleteOwnAccountAsync(lea, key, Password, "bob", Ct));
        await Assert.ThrowsAsync<NotPermittedException>(() => Deletion().DeleteOwnAccountAsync(chris, chrisKey, Password, "chris", Ct));
        // Another user's key: the password is lea's, but it must open exactly this user key.
        Assert.False(await Deletion().DeleteOwnAccountAsync(lea, chrisKey, Password, "lea", Ct));

        Assert.Equal(1, await CountAsync("users", "_id", lea.Id));
        Assert.Equal(1, await CountAsync("users", "_id", chris.Id));
        await AssertIntactAsync(diary.Id);
    }

    [Fact]
    public async Task An_admin_deletes_only_a_deactivated_user_with_the_username_typed()
    {
        var (anna, _, _) = await SignedInAsync("anna", Roles.Admin);
        var (bob, _, _) = await SignedInAsync("bob");
        var (chris, _, _) = await SignedInAsync("chris", Roles.PrimaryAdmin);
        var (lea, key, _) = await SignedInAsync("lea");
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");

        await Assert.ThrowsAsync<NotPermittedException>(() => Deletion().DeleteUserAsync(anna, lea.Id, "lea", Ct));
        Assert.True(await Admin().SetActiveAsync(anna, lea.Id, false, Ct));
        await Assert.ThrowsAsync<NotPermittedException>(() => Deletion().DeleteUserAsync(bob, lea.Id, "lea", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Deletion().DeleteUserAsync(anna, lea.Id, "le", Ct));
        await Assert.ThrowsAsync<NotPermittedException>(() => Deletion().DeleteUserAsync(anna, chris.Id, "chris", Ct));
        Assert.False(await Deletion().DeleteUserAsync(anna, "no-such-user", "x", Ct));
        await AssertIntactAsync(diary.Id);

        Assert.True(await Deletion().DeleteUserAsync(anna, lea.Id, "LEA", Ct));

        Assert.Equal(0, await CountAsync("users", "_id", lea.Id));
        Assert.Equal(0, await CountAsync("user_slots", "user", lea.Id));
        await AssertGoneAsync(diary.Id);
        Assert.False(await Deletion().DeleteUserAsync(anna, lea.Id, "lea", Ct));
    }

    [Fact]
    public async Task The_log_names_ids_and_counts_never_a_vault_name_or_content()
    {
        var (anna, _, _) = await SignedInAsync("anna", Roles.Admin);
        var (lea, key, _) = await SignedInAsync("lea");
        var (bob, bobKey, _) = await SignedInAsync("bob");
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");
        await FilledVaultAsync(lea, key, "Arbeit");
        await FilledVaultAsync(bob, bobKey, "Projekte");

        await Deletion().DeleteVaultAsync(lea, key, "Tagebuch", Password, "Tagebuch", Ct);
        await Deletion().DeleteOwnAccountAsync(bob, bobKey, Password, "bob", Ct);
        await Admin().SetActiveAsync(anna, lea.Id, false, Ct);
        await Deletion().DeleteUserAsync(anna, lea.Id, "lea", Ct);

        var log = string.Join("\n", _log.Lines);
        Assert.Contains(diary.Id, log, StringComparison.Ordinal);
        Assert.Contains("deleted their account", log, StringComparison.Ordinal);
        Assert.Contains("anna deleted user lea", log, StringComparison.Ordinal);
        foreach (var secret in new[] { "Tagebuch", "Arbeit", "Projekte", "Acme", "plan.md", Password })
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }
    }
}
