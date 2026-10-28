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

/// <summary>Deleting a vault, one's own account, or any user by an admin: everything of it, nothing else, in one go.</summary>
[Collection("mongo")]
public sealed class DeletionTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";

    /// <summary>Every collection that holds a vault's documents under their "vault" field.</summary>
    private static readonly string[] ContentCollections = ["notes", "trash", "moves", "embeddings", "checkpoints", "checkpoint_texts", "attachments", "attachment_trash", "blobs"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero));
    private readonly CapturedLog _log = new();
    private VaultCatalog? _catalog;

    /// <summary>One catalog, as one server process has it: vaults in memory are shared between the calls.</summary>
    private VaultCatalog Catalog() => _catalog ??= new(_db, Options.Create(new KumbuOptions()), _time, LoggerFactory.Create(b => b.AddProvider(_log)).CreateLogger<VaultCatalog>());

    private ApiKeys Keys() => new(_db, Catalog(), _time, NullLogger<ApiKeys>.Instance);

    private UserCatalog Users() =>
        new(_db, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), Keys(), new Connections(_db, Catalog(), _time, NullLogger<Connections>.Instance), _time, NullLogger<UserCatalog>.Instance);

    private Sessions Sessions() => new(_db, _time);

    private Admin Admin() => new(_db, Catalog(), Sessions(), Keys(), new Connections(_db, Catalog(), _time, NullLogger<Connections>.Instance), _time, NullLogger<Admin>.Instance);

    private string? _listPath;

    private DeletionList List() =>
        new(Options.Create(new KumbuOptions { Deletions = new DeletionsOptions { Path = _listPath } }), LoggerFactory.Create(b => b.AddProvider(_log)).CreateLogger<DeletionList>());

    private Deletion Deletion() => new(_db, Catalog(), Users(), List(), Restored(), _time, LoggerFactory.Create(b => b.AddProvider(_log)).CreateLogger<Deletion>());

    private RestoredDeletions Restored() => new(_db, List());

    private string TempListPath() => _listPath = Path.Combine(Path.GetTempPath(), "kumbu-deletions-" + Guid.NewGuid().ToString("N"));

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
    public async Task Finding_the_vault_to_delete_unlocks_none_of_the_users_vaults()
    {
        var (lea, key, _) = await SignedInAsync("lea");
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");
        var work = await FilledVaultAsync(lea, key, "Arbeit");
        _time.Advance(TimeSpan.FromMinutes(20));
        Catalog().Sweep();

        Assert.NotNull(await Deletion().DeleteVaultAsync(lea, key, "Tagebuch", Password, "Tagebuch", Ct));

        Assert.Equal(0, Catalog().OpenCount);
        await AssertGoneAsync(diary.Id);
        await AssertIntactAsync(work.Id);
    }

    [Fact]
    public async Task An_api_key_left_without_a_vault_is_revoked_with_the_deletion()
    {
        var (lea, key, _) = await SignedInAsync("lea");
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");
        var work = await FilledVaultAsync(lea, key, "Arbeit");
        var (only, onlyInfo) = await Keys().CreateAsync(lea, key, Now, "diary only", new Dictionary<string, KeyRight> { [diary.Id] = KeyRight.Write }, Ct);
        var (both, bothInfo) = await Keys().CreateAsync(
            lea, key, Now, "both", new Dictionary<string, KeyRight> { [diary.Id] = KeyRight.Read, [work.Id] = KeyRight.Read }, Ct);

        await Deletion().DeleteVaultAsync(lea, key, "Tagebuch", Password, "Tagebuch", Ct);

        Assert.Null(await Keys().ResolveAsync(only, Ct));
        Assert.NotNull(await Keys().ResolveAsync(both, Ct));
        var listed = await Keys().ListAsync(lea, key, Ct);
        Assert.NotNull(listed.Single(k => k.Id == onlyInfo.Id).RevokedAt);
        Assert.Null(listed.Single(k => k.Id == bothInfo.Id).RevokedAt);
        // Revoked, its name is free again.
        await Keys().CreateAsync(lea, key, Now, "diary only", new Dictionary<string, KeyRight> { [work.Id] = KeyRight.Read }, Ct);
        Assert.Contains("API key " + onlyInfo.Id + " has no vault left and is revoked", string.Join('\n', _log.Lines), StringComparison.Ordinal);
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
    public async Task A_deletion_during_a_slow_embedding_leaves_no_embedding_behind()
    {
        var (lea, key, _) = await SignedInAsync("lea");
        var stale = await FilledVaultAsync(lea, key, "Tagebuch");
        // Locked, so the deletion doesn't wait for this object's index gate: the indexing runs alongside it.
        _time.Advance(TimeSpan.FromMinutes(20));
        Catalog().Sweep();
        // An embedder that holds every embedding until the test lets it go, and says when one has begun.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var embedder = new Search.FakeEmbedder
        {
            BeforeEmbed = async _ =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(Ct);
            },
        };
        using var index = new Kumbu.Server.Search.SearchIndex(Catalog(), Kumbu.Server.Search.EmbeddingProviders.Of(embedder), NullLogger<Kumbu.Server.Search.SearchIndex>.Instance);

        // Past the first tombstone check and into the embedding when the deletion comes.
        var indexing = index.IndexAsync(stale, P("/Tagebuch/plan.md"), Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Deletion().DeleteVaultAsync(lea, key, "Tagebuch", Password, "Tagebuch", Ct);
        release.SetResult();
        await indexing;

        Assert.Equal(0, await CountAsync("embeddings", "vault", stale.Id));
        await AssertGoneAsync(stale.Id);
    }

    /// <summary>A vault of lea's on an external provider that hangs, and a long note in it being embedded, one chunk
    /// per call: the provider is in its first call.</summary>
    private async Task<(User Lea, byte[] Key, UnlockedVault Vault, Search.FakeEmbedder Remote, Task Indexing, Kumbu.Server.Search.SearchIndex Index)> HangingEmbeddingAsync(bool ignoreCancellation = false)
    {
        var (lea, key, _) = await SignedInAsync("lea");
        var vault = await FilledVaultAsync(lea, key, "Tagebuch");
        var remote = new Search.FakeEmbedder(model: "remote-1", dimensions: 8) { MaxBatch = 1, Hang = true, IgnoreCancellation = ignoreCancellation };
        var index = new Kumbu.Server.Search.SearchIndex(
            Catalog(),
            Kumbu.Server.Search.EmbeddingProviders.Of(new Search.FakeEmbedder(), new Kumbu.Server.Search.EmbeddingProvider("remote", "Remote", null, remote)),
            NullLogger<Kumbu.Server.Search.SearchIndex>.Instance);
        await Catalog().SetEmbeddingProviderAsync(vault, "remote", Ct);
        var path = P("/Tagebuch/long.md");
        var long_ = string.Join("\n\n", Enumerable.Range(1, 3).Select(i => $"# Teil {i}\n\n{string.Concat(Enumerable.Repeat($"Abschnitt {i} mit eigenem Text. ", 60))}"));
        var store = new EncryptedNoteStore(Catalog(), new UserCredential(lea.Id, key));
        Assert.True(await store.TryCreateAsync(new StoredNote(path, long_, 1, lea.Username, Now, []), Ct));
        var indexing = index.IndexAsync(vault, path, Ct);
        await remote.Hanging.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        return (lea, key, vault, remote, indexing, index);
    }

    [Fact]
    public async Task Deleting_a_vault_whose_provider_hangs_cancels_the_call_and_sends_no_more_batches()
    {
        var (lea, key, vault, remote, indexing, index) = await HangingEmbeddingAsync();
        using var _ = index;

        await Deletion().DeleteVaultAsync(lea, key, "Tagebuch", Password, "Tagebuch", Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await indexing.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Single(remote.Seen);
        Assert.Equal(0, await CountAsync("embeddings", "vault", vault.Id));
        await AssertGoneAsync(vault.Id);
    }

    [Fact]
    public async Task While_a_deletion_waits_for_a_hanging_provider_other_vaults_answer_at_once()
    {
        var (lea, key, vault, remote, indexing, index) = await HangingEmbeddingAsync(ignoreCancellation: true);
        using var _ = index;
        var (bob, bobKey, _) = await SignedInAsync("bob");
        await FilledVaultAsync(bob, bobKey, "Projekte");

        // The provider doesn't even honour the cancellation: the deletion has to wait for the vault's index gate.
        var deletion = Deletion().DeleteVaultAsync(lea, key, "Tagebuch", Password, "Tagebuch", Ct);
        await Task.Delay(200, Ct);

        Assert.False(deletion.IsCompleted);
        Assert.True(Catalog().IsDeleted(vault.Id));
        Assert.Single(await Catalog().OpenForUserAsync(bob.Id, bobKey, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct));
        Assert.Empty(await Catalog().OpenForUserAsync(lea.Id, key, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct));

        // Its answer comes in after all: the batches after it don't go out, and nothing is stored.
        remote.Release.SetResult();
        await indexing.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await deletion.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Single(remote.Seen);
        Assert.Equal(0, await CountAsync("embeddings", "vault", vault.Id));
        await AssertGoneAsync(vault.Id);
    }

    [Fact]
    public async Task Choosing_a_provider_for_a_vault_deleted_meanwhile_says_it_was_deleted()
    {
        var (lea, key, _) = await SignedInAsync("lea");
        var vault = await FilledVaultAsync(lea, key, "Tagebuch");
        // Locked, as a request that read the vault just before the deletion holds it: nobody marks this object deleted.
        _time.Advance(TimeSpan.FromMinutes(20));
        Catalog().Sweep();
        await Deletion().DeleteVaultAsync(lea, key, "Tagebuch", Password, "Tagebuch", Ct);

        await Assert.ThrowsAsync<VaultDeletedException>(() => Catalog().SetEmbeddingProviderAsync(vault, "remote", Ct));
        // Without the tombstone too (the vault went in another process): its document is gone.
        var other = new VaultCatalog(_db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance);
        await Assert.ThrowsAsync<VaultDeletedException>(() => other.SetEmbeddingProviderAsync(vault, "remote", Ct));
    }

    [Fact]
    public async Task An_older_vault_object_waiting_in_the_index_queue_brings_no_embedding_back()
    {
        var (lea, key, _) = await SignedInAsync("lea");
        var stale = await FilledVaultAsync(lea, key, "Tagebuch");
        _time.Advance(TimeSpan.FromMinutes(20));
        Catalog().Sweep();
        using var index = new Kumbu.Server.Search.SearchIndex(Catalog(), Kumbu.Server.Search.EmbeddingProviders.Of(new Search.FakeEmbedder()), NullLogger<Kumbu.Server.Search.SearchIndex>.Instance);
        index.Enqueue(stale, [P("/Tagebuch/plan.md")]);

        // Locked, the vault is in no working set: only the object in the queue is left, and nobody marks it deleted.
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
        var (anna, annaKey, _) = await SignedInAsync("anna", Roles.Admin);
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
    public async Task After_the_account_is_gone_no_vault_or_session_can_be_made_for_it()
    {
        var (lea, key, _) = await SignedInAsync("lea");
        Assert.True(await Deletion().DeleteOwnAccountAsync(lea, key, Password, "lea", Ct));

        // A request whose session was checked just before the deletion, arriving after it.
        await Assert.ThrowsAsync<CredentialsChangedException>(() => Catalog().CreateForUserAsync("Spät", lea.Id, key, Ct));
        await Assert.ThrowsAsync<CredentialsChangedException>(() => Sessions().StartAsync(lea, key, Now, Ct));

        Assert.Equal(0, await CountAsync("slots", "owner", lea.Id));
        Assert.Equal(0, await Raw("vaults").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct));
        Assert.Equal(0, await CountAsync("user_slots", "user", lea.Id));
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
    public async Task An_admin_deletes_a_user_only_with_their_password_and_the_username_typed()
    {
        var (anna, annaKey, _) = await SignedInAsync("anna", Roles.Admin);
        var (bob, bobKey, _) = await SignedInAsync("bob");
        var (chris, _, _) = await SignedInAsync("chris", Roles.PrimaryAdmin);
        var (lea, key, _) = await SignedInAsync("lea");
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");

        await Assert.ThrowsAsync<NotPermittedException>(() => Deletion().DeleteUserAsync(bob, bobKey, Password, lea.Id, "lea", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Deletion().DeleteUserAsync(anna, annaKey, Password, lea.Id, "le", Ct));
        var wrong = await Assert.ThrowsAsync<ArgumentException>(() => Deletion().DeleteUserAsync(anna, annaKey, "not anna's password at all", lea.Id, "lea", Ct));
        Assert.StartsWith("Your password is wrong.", wrong.Message, StringComparison.Ordinal);
        // Anna's password, but the target's user key: it must open the admin's own.
        await Assert.ThrowsAsync<ArgumentException>(() => Deletion().DeleteUserAsync(anna, key, Password, lea.Id, "lea", Ct));
        await Assert.ThrowsAsync<NotPermittedException>(() => Deletion().DeleteUserAsync(anna, annaKey, Password, chris.Id, "chris", Ct));
        Assert.Null(await Deletion().DeleteUserAsync(anna, annaKey, Password, "no-such-user", "x", Ct));
        await AssertIntactAsync(diary.Id);

        Assert.NotNull(await Deletion().DeleteUserAsync(anna, annaKey, Password, lea.Id, "LEA", Ct));

        Assert.Equal(0, await CountAsync("users", "_id", lea.Id));
        Assert.Equal(0, await CountAsync("user_slots", "user", lea.Id));
        await AssertGoneAsync(diary.Id);
        Assert.Null(await Deletion().DeleteUserAsync(anna, annaKey, Password, lea.Id, "lea", Ct));
    }

    [Fact]
    public async Task A_deactivated_user_can_still_be_deleted()
    {
        var (anna, annaKey, _) = await SignedInAsync("anna", Roles.Admin);
        var (lea, key, _) = await SignedInAsync("lea");
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");
        Assert.True(await Admin().SetActiveAsync(anna, lea.Id, false, Ct));

        Assert.NotNull(await Deletion().DeleteUserAsync(anna, annaKey, Password, lea.Id, "lea", Ct));

        Assert.Equal(0, await CountAsync("users", "_id", lea.Id));
        await AssertGoneAsync(diary.Id);
    }

    [Fact]
    public async Task An_admin_deletes_an_active_user_in_one_step()
    {
        var (anna, annaKey, _) = await SignedInAsync("anna", Roles.Admin);
        var (lea, key, _) = await SignedInAsync("lea", Roles.Admin);
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");
        await Keys().CreateAsync(lea, key, Now, "agent", new Dictionary<string, KeyRight> { [diary.Id] = KeyRight.Write }, Ct);

        var result = await Deletion().DeleteUserAsync(anna, annaKey, Password, lea.Id, "lea", Ct);

        Assert.NotNull(result);
        Assert.Equal(lea.Id, result.Id);
        Assert.Equal("lea", result.Username);
        Assert.Equal(lea.Display, result.Display);
        Assert.Equal("anna", result.DeletedBy);
        Assert.Equal(Now, result.DeletedAt);
        Assert.Equal(1, result.Summary.Vaults);
        Assert.Equal(1, result.Summary.Notes);
        Assert.Equal(1, result.Summary.ApiKeys);
        Assert.Equal(0, await CountAsync("users", "_id", lea.Id));
        await AssertGoneAsync(diary.Id);
    }

    [Fact]
    public async Task Deleting_a_vault_removes_attachments_and_blobs_and_the_receipt_counts_them()
    {
        var (anna, annaKey, _) = await SignedInAsync("anna", Roles.Admin);
        var (lea, key, _) = await SignedInAsync("lea");
        var diary = await Catalog().CreateForUserAsync("Tagebuch", lea.Id, key, Ct);
        var store = new EncryptedNoteStore(Catalog(), new UserCredential(lea.Id, key));
        var batch = new NoteBatch();
        foreach (var (path, bytes) in new[] { ("/Tagebuch/a.png", new byte[] { 1, 2, 3 }), ("/Tagebuch/b.png", [1, 2, 3]), ("/Tagebuch/c.pdf", [4, 5]) })
        {
            var state = new Kumbu.Core.Attachments.AttachmentState("image/png", bytes.Length, 1, 1, VersionToken.Of(bytes), await store.PutBlobAsync("Tagebuch", bytes, Ct));
            batch.AttachmentCreates.Add(new Kumbu.Core.Attachments.StoredAttachment(P(path), state, 1, lea.Username, Now, [], null));
        }

        Assert.Empty(await store.TryCommitAsync(batch, lea.Username, Now, Ct));
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentTrashes = { batch.AttachmentCreates[2] } }, lea.Username, Now, Ct));
        Assert.Equal(2, await CountAsync("blobs", "vault", diary.Id));

        var result = await Deletion().DeleteUserAsync(anna, annaKey, Password, lea.Id, "lea", Ct);

        Assert.NotNull(result);
        Assert.Equal(2, result.Summary.Attachments);
        await AssertGoneAsync(diary.Id);
    }

    [Fact]
    public async Task An_admin_cant_delete_themselves_here()
    {
        var (anna, annaKey, _) = await SignedInAsync("anna", Roles.Admin);
        var diary = await FilledVaultAsync(anna, annaKey, "Tagebuch");

        var refused = await Assert.ThrowsAsync<NotPermittedException>(() => Deletion().DeleteUserAsync(anna, annaKey, Password, anna.Id, "anna", Ct));

        Assert.Equal("Delete your own account in Settings.", refused.Message);
        Assert.Equal(1, await CountAsync("users", "_id", anna.Id));
        await AssertIntactAsync(diary.Id);
    }

    [Fact]
    public async Task Deleting_an_active_user_ends_their_session_and_writes()
    {
        var (anna, annaKey, _) = await SignedInAsync("anna", Roles.Admin);
        var (lea, key, session) = await SignedInAsync("lea");
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");
        // A request of lea's that opened her vault before the deletion and writes after it.
        var store = new EncryptedNoteStore(Catalog(), new UserCredential(lea.Id, key));
        Assert.Contains("Tagebuch", await store.ListVaultsAsync(Ct));

        Assert.NotNull(await Deletion().DeleteUserAsync(anna, annaKey, Password, lea.Id, "lea", Ct));

        Assert.Null(await Sessions().ResolveAsync(session, Ct));
        await Assert.ThrowsAsync<VaultDeletedException>(() => store.TryCreateAsync(new StoredNote(P("/Tagebuch/late.md"), "late", 1, "lea", Now, []), Ct));
        await Assert.ThrowsAsync<CredentialsChangedException>(() => Catalog().CreateForUserAsync("Spät", lea.Id, key, Ct));
        await AssertGoneAsync(diary.Id);
    }

    [Fact]
    public async Task Both_deletions_list_the_id()
    {
        var dir = TempListPath();
        try
        {
            var (anna, annaKey, _) = await SignedInAsync("anna", Roles.Admin);
            var (lea, _, _) = await SignedInAsync("lea");
            var (bob, bobKey, _) = await SignedInAsync("bob");

            var deleted = await Deletion().DeleteUserAsync(anna, annaKey, Password, lea.Id, "lea", Ct);
            Assert.True(await Deletion().DeleteOwnAccountAsync(bob, bobKey, Password, "bob", Ct));

            Assert.True(deleted!.Listed);
            Assert.Equal(new HashSet<string> { lea.Id, bob.Id }, await List().ReadAsync(Ct));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task An_unwritable_list_never_blocks_a_deletion()
    {
        // The path is a file, not a directory: nothing can be written below it.
        var file = Path.GetTempFileName();
        _listPath = file;
        try
        {
            var (anna, annaKey, _) = await SignedInAsync("anna", Roles.Admin);
            var (lea, key, _) = await SignedInAsync("lea");
            var diary = await FilledVaultAsync(lea, key, "Tagebuch");

            var deleted = await Deletion().DeleteUserAsync(anna, annaKey, Password, lea.Id, "lea", Ct);

            Assert.NotNull(deleted);
            Assert.False(deleted.Listed);
            Assert.Equal(0, await CountAsync("users", "_id", lea.Id));
            await AssertGoneAsync(diary.Id);
            Assert.Contains(_log.Lines, l => l.StartsWith("Error", StringComparison.Ordinal) && l.Contains(lea.Id, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_restored_user_is_found_and_deleted_again_but_never_the_admin_themselves()
    {
        var dir = TempListPath();
        try
        {
            var (anna, annaKey, _) = await SignedInAsync("anna", Roles.Admin);
            var (lea, key, _) = await SignedInAsync("lea");
            var (bob, _, _) = await SignedInAsync("bob");
            await FilledVaultAsync(lea, key, "Tagebuch");
            var leaDoc = await Raw("users").Find(Builders<BsonDocument>.Filter.Eq("_id", lea.Id)).FirstAsync(Ct);
            Assert.NotNull(await Deletion().DeleteUserAsync(anna, annaKey, Password, lea.Id, "lea", Ct));
            Assert.Empty(await Restored().FindAsync(Ct));
            // A backup restore brings lea back, and with her an id the list names that the admin holds now (anna's
            // own, listed by hand here; a restore after anna deleted herself would do the same).
            await Raw("users").InsertOneAsync(leaDoc, cancellationToken: Ct);
            Assert.True(await List().AddAsync(anna.Id, Ct));

            Assert.Equal(["anna", "lea"], (await Restored().FindAsync(Ct)).Select(u => u.Username).Order());
            // Anna doesn't see herself: she can't delete herself here, so she isn't offered it.
            Assert.Equal(["lea"], (await Restored().ListAsync(anna, Ct)).Select(u => u.Username));
            await Assert.ThrowsAsync<ArgumentException>(() => Deletion().RepeatAsync(anna, annaKey, "not anna's password at all", Ct));
            var again = await Deletion().RepeatAsync(anna, annaKey, Password, Ct);

            var deleted = Assert.Single(again);
            Assert.Equal(lea.Id, deleted.Id);
            Assert.Equal("anna", deleted.DeletedBy);
            Assert.True(deleted.Listed);
            Assert.Equal(0, await CountAsync("users", "_id", lea.Id));
            Assert.Equal(1, await CountAsync("users", "_id", anna.Id));
            Assert.Equal(1, await CountAsync("users", "_id", bob.Id));
            Assert.Equal(["anna"], (await Restored().FindAsync(Ct)).Select(u => u.Username));
            Assert.Empty(await Restored().ListAsync(anna, Ct));
            // Nothing new on the list: lea's id is there once, as before.
            Assert.Single(await File.ReadAllLinesAsync(Path.Combine(dir, "deleted-users"), Ct), l => l == lea.Id);
            Assert.Contains(_log.Lines, l => l.Contains($"anna deleted user {lea.Id} again after a restore", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task At_start_the_log_warns_about_restored_users_by_id_only()
    {
        var dir = TempListPath();
        try
        {
            var (lea, _, _) = await SignedInAsync("lea");
            Assert.True(await List().AddAsync(lea.Id, Ct));
            using var check = new RestoredDeletionsCheck(Restored(), LoggerFactory.Create(b => b.AddProvider(_log)).CreateLogger<RestoredDeletionsCheck>());

            await check.StartAsync(Ct);
            await check.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), Ct);

            var warning = Assert.Single(_log.Lines, l => l.Contains("deleted users are back after a restore", StringComparison.Ordinal));
            Assert.StartsWith("Warning", warning, StringComparison.Ordinal);
            Assert.Contains("1 deleted users are back after a restore; an admin can delete them again on the users page", warning, StringComparison.Ordinal);
            Assert.Contains(lea.Id, warning, StringComparison.Ordinal);
            Assert.DoesNotContain("lea", warning.Replace(lea.Id, "", StringComparison.Ordinal).Replace("RestoredDeletions", "", StringComparison.Ordinal), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Only_an_admin_repeats_a_deletion()
    {
        var (bob, bobKey, _) = await SignedInAsync("bob");

        await Assert.ThrowsAsync<NotPermittedException>(() => Deletion().RepeatAsync(bob, bobKey, Password, Ct));
        await Assert.ThrowsAsync<NotPermittedException>(() => Restored().ListAsync(bob, Ct));
    }

    [Fact]
    public async Task Without_a_list_the_receipt_says_it_is_not_listed()
    {
        var (anna, annaKey, _) = await SignedInAsync("anna", Roles.Admin);
        var (lea, _, _) = await SignedInAsync("lea");

        var deleted = await Deletion().DeleteUserAsync(anna, annaKey, Password, lea.Id, "lea", Ct);

        Assert.False(deleted!.Listed);
    }

    [Fact]
    public async Task Deleting_a_user_erases_the_note_and_link_of_the_invitation_they_joined_with()
    {
        var (anna, annaKey, _) = await SignedInAsync("anna", Roles.Admin);
        var invitations = new Invitations(_db, new ServerSecret(_db), Users(), _time, NullLogger<Invitations>.Instance);
        var (code, joinedWith) = await invitations.CreateAsync(anna, Roles.User, "Lea Müller, lea@example.org", null, Ct);
        var (_, other) = await invitations.CreateAsync(anna, Roles.User, "for Max", null, Ct);
        var (lea, _) = await invitations.RedeemAsync(code, "lea", Password, Ct);

        var deleted = await Deletion().DeleteUserAsync(anna, annaKey, Password, lea.Id, "lea", Ct);

        Assert.Equal(1, deleted!.Summary.Invitations);
        var record = (await invitations.ListAsync(anna, Ct)).Single(i => i.Id == joinedWith.Id);
        Assert.Null(record.Note);
        Assert.Null(record.UsedBy);
        Assert.NotNull(record.UsedAt);
        Assert.Equal(InvitationState.Used, record.State);
        Assert.Equal(0, await CountAsync("invitations", "usedBy", lea.Id));
        // Another invitation, not theirs, keeps its note.
        Assert.Equal("for Max", (await invitations.ListAsync(anna, Ct)).Single(i => i.Id == other.Id).Note);
    }

    [Fact]
    public async Task The_own_deletion_erases_the_invitation_they_joined_with_too()
    {
        var (anna, _, _) = await SignedInAsync("anna", Roles.Admin);
        var invitations = new Invitations(_db, new ServerSecret(_db), Users(), _time, NullLogger<Invitations>.Instance);
        var (code, joinedWith) = await invitations.CreateAsync(anna, Roles.User, "Lea Müller", null, Ct);
        await invitations.RedeemAsync(code, "lea", Password, Ct);
        var (signedIn, key, _) = (await Users().TrySignInAsync("lea", Password, Ct))!.Value;

        Assert.True(await Deletion().DeleteOwnAccountAsync(signedIn, key, Password, "lea", Ct));

        var record = (await invitations.ListAsync(anna, Ct)).Single(i => i.Id == joinedWith.Id);
        Assert.Null(record.Note);
        Assert.Null(record.UsedBy);
    }

    [Fact]
    public async Task The_log_names_ids_and_counts_never_a_vault_name_or_content()
    {
        var (anna, annaKey, _) = await SignedInAsync("anna", Roles.Admin);
        var (lea, key, _) = await SignedInAsync("lea");
        var (bob, bobKey, _) = await SignedInAsync("bob");
        var diary = await FilledVaultAsync(lea, key, "Tagebuch");
        await FilledVaultAsync(lea, key, "Arbeit");
        await FilledVaultAsync(bob, bobKey, "Projekte");

        await Deletion().DeleteVaultAsync(lea, key, "Tagebuch", Password, "Tagebuch", Ct);
        await Deletion().DeleteOwnAccountAsync(bob, bobKey, Password, "bob", Ct);
        await Deletion().DeleteUserAsync(anna, annaKey, Password, lea.Id, "lea", Ct);

        var log = string.Join("\n", _log.Lines);
        Assert.Contains(diary.Id, log, StringComparison.Ordinal);
        Assert.Contains("deleted their account", log, StringComparison.Ordinal);
        Assert.Contains($"anna deleted user {lea.Id}", log, StringComparison.Ordinal);
        // The lines of the account deletions name the erased people by id only, not by their usernames.
        Assert.All(_log.Lines.Where(l => l.Contains("deleted user", StringComparison.Ordinal) || l.Contains("deleted their account", StringComparison.Ordinal)),
            l => Assert.DoesNotMatch(@"\b(lea|bob)\b", l));
        foreach (var secret in new[] { "Tagebuch", "Arbeit", "Projekte", "Acme", "plan.md", Password })
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }
    }
}
