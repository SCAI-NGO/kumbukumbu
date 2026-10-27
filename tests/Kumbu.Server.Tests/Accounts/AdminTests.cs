// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Kumbu.Core.Notes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>The admin's tools: the user list (metadata only, never content), deactivation and roles.</summary>
[Collection("mongo")]
public sealed class AdminTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));

    private UserCatalog Users() =>
        new(_db, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), Keys(), new Connections(_db, Catalog(), _time, NullLogger<Connections>.Instance), _time, NullLogger<UserCatalog>.Instance);

    private VaultCatalog Catalog() => new(_db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance);

    private Sessions Sessions() => new(_db, _time);

    private ApiKeys Keys() => new(_db, Catalog(), _time, NullLogger<ApiKeys>.Instance);

    private Admin Admin() => new(_db, Catalog(), Sessions(), Keys(), new Connections(_db, Catalog(), _time, NullLogger<Connections>.Instance), _time, NullLogger<Admin>.Instance);

    private async Task<User> UserAsync(string username, string role)
    {
        var (user, _) = await Users().CreateAsync(username, Password, role, Ct);
        return user;
    }

    /// <summary>A user signed in: the user, their user key and a session secret.</summary>
    private async Task<(User User, byte[] Key, string Session)> SignedInAsync(string username, string role = Roles.User)
    {
        await UserAsync(username, role);
        var (user, key, readAt) = (await Users().TrySignInAsync(username, Password, Ct))!.Value;
        return (user, key, await Sessions().StartAsync(user, key, readAt, Ct));
    }

    [Fact]
    public async Task The_listing_shows_metadata_vault_counts_and_bytes_but_no_vault_names_or_content()
    {
        var admin = await UserAsync("anna", Roles.Admin);
        var (lea, key, _) = await SignedInAsync("lea");
        var catalog = Catalog();
        await catalog.CreateForUserAsync("Geheimprojekt", lea.Id, key, Ct);
        await catalog.CreateForUserAsync("Tagebuch", lea.Id, key, Ct);
        var store = new EncryptedNoteStore(catalog, new UserCredential(lea.Id, key));
        Assert.True(await store.TryCreateAsync(
            new StoredNote(P("/Geheimprojekt/Plan.md"), "Wir kaufen die Firma Acme.", 1, "lea", _time.GetUtcNow().UtcDateTime, []), Ct));
        // A dev-token vault belongs to nobody and counts for nobody.
        await catalog.CreateAsync("Dev", Token, null, Ct);

        var users = await Admin().ListUsersAsync(admin, Ct);

        Assert.Equal(["anna", "lea"], users.Select(u => u.Username).Order());
        var row = users.Single(u => u.Username == "lea");
        Assert.Equal(Roles.User, row.Role);
        Assert.True(row.Active);
        Assert.Equal(lea.CreatedAt, row.CreatedAt);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, row.LastSignInAt);
        Assert.Equal(2, row.Vaults);
        Assert.True(row.BytesStored > 0);
        var anna = users.Single(u => u.Username == "anna");
        Assert.Equal(0, anna.Vaults);
        Assert.Equal(0, anna.BytesStored);
        Assert.Null(anna.LastSignInAt);

        var json = JsonSerializer.Serialize(users);
        Assert.DoesNotContain("Geheimprojekt", json);
        Assert.DoesNotContain("Tagebuch", json);
        Assert.DoesNotContain("Plan", json);
        Assert.DoesNotContain("Acme", json);
    }

    [Fact]
    public async Task Bytes_stored_grow_with_what_a_user_writes()
    {
        var admin = await UserAsync("anna", Roles.Admin);
        var (lea, key, _) = await SignedInAsync("lea");
        var catalog = Catalog();
        await catalog.CreateForUserAsync("Notes", lea.Id, key, Ct);
        var before = (await Admin().ListUsersAsync(admin, Ct)).Single(u => u.Username == "lea").BytesStored;

        var store = new EncryptedNoteStore(catalog, new UserCredential(lea.Id, key));
        await store.TryCreateAsync(new StoredNote(P("/Notes/a.md"), new string('x', 10_000), 1, "lea", _time.GetUtcNow().UtcDateTime, []), Ct);

        var after = (await Admin().ListUsersAsync(admin, Ct)).Single(u => u.Username == "lea").BytesStored;
        Assert.True(after - before >= 10_000, $"{before} → {after}");
    }

    [Fact]
    public async Task Only_an_active_admin_may_use_the_tools()
    {
        var (plain, _, _) = await SignedInAsync("bob");
        var other = await UserAsync("lea", Roles.User);

        await Assert.ThrowsAsync<NotPermittedException>(() => Admin().ListUsersAsync(plain, Ct));
        await Assert.ThrowsAsync<NotPermittedException>(() => Admin().SetActiveAsync(plain, other.Id, false, Ct));
        await Assert.ThrowsAsync<NotPermittedException>(() => Admin().SetRoleAsync(plain, other.Id, Roles.Admin, Ct));

        var admin = await UserAsync("anna", Roles.Admin);
        var primary = await UserAsync("chris", Roles.PrimaryAdmin);
        Assert.True(await Admin().SetActiveAsync(primary, admin.Id, false, Ct));
        // Deactivated, the stale User object opens nothing: the actor is read again from the database.
        await Assert.ThrowsAsync<NotPermittedException>(() => Admin().ListUsersAsync(admin, Ct));
    }

    [Fact]
    public async Task Deactivating_ends_every_session_and_revokes_every_key_and_reactivating_revives_neither()
    {
        var admin = await UserAsync("anna", Roles.Admin);
        var (lea, key, session) = await SignedInAsync("lea");
        var otherSession = await Sessions().StartAsync(lea, key, _time.GetUtcNow().UtcDateTime, Ct);
        var vault = await Catalog().CreateForUserAsync("Notes", lea.Id, key, Ct);
        var keys = Keys();
        var (laptop, _) = await keys.CreateAsync(lea, key, _time.GetUtcNow().UtcDateTime, "laptop", new Dictionary<string, KeyRight> { [vault.Id] = KeyRight.Write }, Ct);
        var (phone, _) = await keys.CreateAsync(lea, key, _time.GetUtcNow().UtcDateTime, "phone", new Dictionary<string, KeyRight> { [vault.Id] = KeyRight.Read }, Ct);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await Admin().SetActiveAsync(admin, lea.Id, false, Ct));

        Assert.Null(await Sessions().ResolveAsync(session, Ct));
        Assert.Null(await Sessions().ResolveAsync(otherSession, Ct));
        Assert.Null(await keys.ResolveAsync(laptop, Ct));
        Assert.Null(await keys.ResolveAsync(phone, Ct));
        Assert.Equal(0, await _db.GetCollection<UserSlotDocument>("user_slots").CountDocumentsAsync(s => s.User == lea.Id && s.Kind == UserSlotDocument.Session, cancellationToken: Ct));
        Assert.Equal(0, await _db.GetCollection<SlotDocument>("slots").CountDocumentsAsync(s => s.Kind == SlotDocument.ApiKey, cancellationToken: Ct));
        Assert.Null(await Users().TrySignInAsync("lea", Password, Ct));
        Assert.False((await Admin().ListUsersAsync(admin, Ct)).Single(u => u.Username == "lea").Active);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await Admin().SetActiveAsync(admin, lea.Id, true, Ct));

        Assert.Null(await Sessions().ResolveAsync(session, Ct));
        Assert.Null(await keys.ResolveAsync(laptop, Ct));
        Assert.Null(await keys.ResolveAsync(phone, Ct));
        Assert.All(await keys.ListAsync(lea, key, Ct), k => Assert.NotNull(k.RevokedAt));
        // Signing in again works, and the revoked names are free for new keys.
        _time.Advance(TimeSpan.FromSeconds(1));
        var (_, freshKey, readAt) = (await Users().TrySignInAsync("lea", Password, Ct))!.Value;
        Assert.NotNull(await Sessions().ResolveAsync(await Sessions().StartAsync(lea, freshKey, readAt, Ct), Ct));
        await keys.CreateAsync(lea, freshKey, readAt, "laptop", new Dictionary<string, KeyRight> { [vault.Id] = KeyRight.Write }, Ct);
    }

    [Fact]
    public async Task Deactivation_stamps_credentials_changed_so_a_session_slot_left_behind_is_refused_after_reactivation()
    {
        var admin = await UserAsync("anna", Roles.Admin);
        var (lea, key, session) = await SignedInAsync("lea");
        var slots = _db.GetCollection<BsonDocument>("user_slots");
        var kept = await slots.Find(new BsonDocument("kind", UserSlotDocument.Session)).ToListAsync(Ct);
        _time.Advance(TimeSpan.FromSeconds(1));

        await Admin().SetActiveAsync(admin, lea.Id, false, Ct);
        await slots.InsertManyAsync(kept, cancellationToken: Ct);
        await Admin().SetActiveAsync(admin, lea.Id, true, Ct);

        Assert.Null(await Sessions().ResolveAsync(session, Ct));
    }

    [Fact]
    public async Task Reactivating_an_active_user_changes_nothing_and_keeps_their_keys_and_sessions()
    {
        var admin = await UserAsync("anna", Roles.Admin);
        var (lea, key, session) = await SignedInAsync("lea");
        var vault = await Catalog().CreateForUserAsync("Notes", lea.Id, key, Ct);
        var (laptop, _) = await Keys().CreateAsync(lea, key, _time.GetUtcNow().UtcDateTime, "laptop", new Dictionary<string, KeyRight> { [vault.Id] = KeyRight.Write }, Ct);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await Admin().SetActiveAsync(admin, lea.Id, true, Ct));

        Assert.NotNull(await Keys().ResolveAsync(laptop, Ct));
        Assert.NotNull(await Sessions().ResolveAsync(session, Ct));
    }

    [Fact]
    public async Task Deactivating_an_inactive_user_again_succeeds_and_changes_nothing()
    {
        var admin = await UserAsync("anna", Roles.Admin);
        var lea = await UserAsync("lea", Roles.User);

        Assert.True(await Admin().SetActiveAsync(admin, lea.Id, false, Ct));
        Assert.True(await Admin().SetActiveAsync(admin, lea.Id, false, Ct));

        Assert.False((await Admin().ListUsersAsync(admin, Ct)).Single(u => u.Username == "lea").Active);
    }

    [Fact]
    public async Task A_key_creation_landing_after_a_deactivation_is_refused()
    {
        // The race: a key-creation request whose session resolved just before the deactivation reaches the
        // database after it. Its transaction checks the user again (active, credentials unchanged since the sign-in).
        var admin = await UserAsync("anna", Roles.Admin);
        var (lea, key, _) = await SignedInAsync("lea");
        var readAt = _time.GetUtcNow().UtcDateTime;
        var vault = await Catalog().CreateForUserAsync("Notes", lea.Id, key, Ct);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Admin().SetActiveAsync(admin, lea.Id, false, Ct);

        await Assert.ThrowsAsync<CredentialsChangedException>(
            () => Keys().CreateAsync(lea, key, readAt, "late", new Dictionary<string, KeyRight> { [vault.Id] = KeyRight.Write }, Ct));

        // Reactivating doesn't let the old sign-in make one either: the deactivation stamped the credentials.
        _time.Advance(TimeSpan.FromSeconds(1));
        await Admin().SetActiveAsync(admin, lea.Id, true, Ct);
        await Assert.ThrowsAsync<CredentialsChangedException>(
            () => Keys().CreateAsync(lea, key, readAt, "late", new Dictionary<string, KeyRight> { [vault.Id] = KeyRight.Write }, Ct));
        Assert.Equal(0, await _db.GetCollection<ApiKeyDocument>("api_keys").CountDocumentsAsync(k => k.User == lea.Id, cancellationToken: Ct));
        Assert.Equal(0, await _db.GetCollection<SlotDocument>("slots").CountDocumentsAsync(s => s.Kind == SlotDocument.ApiKey, cancellationToken: Ct));
    }

    [Fact]
    public async Task The_primary_admin_cant_be_deactivated_or_demoted_and_nobody_is_made_primary_admin()
    {
        var admin = await UserAsync("anna", Roles.Admin);
        var primary = await UserAsync("chris", Roles.PrimaryAdmin);
        var lea = await UserAsync("lea", Roles.User);

        await Assert.ThrowsAsync<NotPermittedException>(() => Admin().SetActiveAsync(admin, primary.Id, false, Ct));
        await Assert.ThrowsAsync<NotPermittedException>(() => Admin().SetActiveAsync(primary, primary.Id, false, Ct));
        await Assert.ThrowsAsync<NotPermittedException>(() => Admin().SetRoleAsync(admin, primary.Id, Roles.User, Ct));
        await Assert.ThrowsAsync<NotPermittedException>(() => Admin().SetRoleAsync(primary, primary.Id, Roles.Admin, Ct));
        await Assert.ThrowsAsync<NotPermittedException>(() => Admin().SetRoleAsync(primary, lea.Id, Roles.PrimaryAdmin, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Admin().SetRoleAsync(primary, lea.Id, "root", Ct));

        var rows = await Admin().ListUsersAsync(admin, Ct);
        Assert.Equal(Roles.PrimaryAdmin, rows.Single(u => u.Username == "chris").Role);
        Assert.True(rows.Single(u => u.Username == "chris").Active);
        Assert.Equal(Roles.User, rows.Single(u => u.Username == "lea").Role);
    }

    [Fact]
    public async Task An_admin_cant_deactivate_themselves()
    {
        var admin = await UserAsync("anna", Roles.Admin);

        await Assert.ThrowsAsync<NotPermittedException>(() => Admin().SetActiveAsync(admin, admin.Id, false, Ct));
    }

    [Fact]
    public async Task An_admin_promotes_and_demotes_between_user_and_admin()
    {
        var admin = await UserAsync("anna", Roles.Admin);
        var lea = await UserAsync("lea", Roles.User);

        Assert.True(await Admin().SetRoleAsync(admin, lea.Id, Roles.Admin, Ct));
        Assert.Equal(Roles.Admin, (await Admin().ListUsersAsync(admin, Ct)).Single(u => u.Username == "lea").Role);
        Assert.True(await Admin().SetRoleAsync(admin, lea.Id, Roles.User, Ct));
        Assert.Equal(Roles.User, (await Admin().ListUsersAsync(admin, Ct)).Single(u => u.Username == "lea").Role);
        Assert.False(await Admin().SetRoleAsync(admin, "no-such-user", Roles.User, Ct));
        Assert.False(await Admin().SetActiveAsync(admin, "no-such-user", false, Ct));
    }
}
