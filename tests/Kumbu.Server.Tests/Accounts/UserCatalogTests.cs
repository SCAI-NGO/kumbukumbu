// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Accounts;

[Collection("mongo")]
public sealed class UserCatalogTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "tr0ub4dor and three more words";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Cheap Argon2 parameters, so a test run doesn't spend seconds per sign-in.</summary>
    private static Argon2Kdf CheapKdf() => new(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1));

    private UserCatalog Catalog(IMongoDatabase? db = null, Argon2Kdf? kdf = null, TimeProvider? time = null) =>
        new(db ?? fixture.NewDatabase(), kdf ?? CheapKdf(), time ?? TimeProvider.System, NullLogger<UserCatalog>.Instance);

    [Fact]
    public void The_production_parameters_are_those_of_rfc_9106s_second_recommendation()
    {
        Assert.Equal(new Argon2Parameters(MemoryKiB: 64 * 1024, Iterations: 3, Parallelism: 4), Argon2Parameters.Production);
        Assert.Equal(32, Argon2Kdf.OutputBytes);
        Assert.Equal(16, Argon2Kdf.SaltBytes);
        Assert.Equal(Argon2Parameters.Production, new Argon2Kdf().Parameters);
    }

    [Fact]
    public async Task Create_then_sign_in_opens_the_same_user_key()
    {
        var users = Catalog();
        var (user, recoveryKey) = await users.CreateAsync("Chris", Password, Roles.PrimaryAdmin, Ct);

        var signedIn = await users.TrySignInAsync("Chris", Password, Ct);

        Assert.NotNull(signedIn);
        Assert.Equal(user.Id, signedIn.Value.User.Id);
        Assert.Equal("chris", signedIn.Value.User.Username);
        Assert.Equal("Chris", signedIn.Value.User.Display);
        Assert.Equal(Roles.PrimaryAdmin, signedIn.Value.User.Role);
        Assert.True(signedIn.Value.User.Active);
        Assert.Equal(32, signedIn.Value.UserKey.Length);
        Assert.StartsWith("kumbu-rk-", recoveryKey);
    }

    [Fact]
    public async Task A_wrong_password_or_an_unknown_username_gets_nothing_after_the_same_work()
    {
        var kdf = CheapKdf();
        var users = Catalog(kdf: kdf);
        await users.CreateAsync("chris", Password, Roles.User, Ct);

        var before = kdf.Runs;
        Assert.Null(await users.TrySignInAsync("chris", "not the password at all", Ct));
        Assert.Equal(before + 1, kdf.Runs);

        // Argon2 runs for an unknown username too, so timing doesn't tell which usernames exist.
        Assert.Null(await users.TrySignInAsync("nobody", Password, Ct));
        Assert.Equal(before + 2, kdf.Runs);
    }

    [Fact]
    public async Task Usernames_ignore_case_and_are_unique()
    {
        var users = Catalog();
        await users.CreateAsync("Chris", Password, Roles.User, Ct);

        Assert.NotNull(await users.TrySignInAsync("CHRIS", Password, Ct));
        await Assert.ThrowsAsync<UsernameTakenException>(() => users.CreateAsync("chris", NewPassword, Roles.User, Ct));
    }

    [Fact]
    public async Task Two_creates_racing_on_one_username_make_exactly_one_user()
    {
        var db = fixture.NewDatabase();
        await Catalog(db).TrySignInAsync("warm-up", Password, Ct); // creates the indexes before the race

        // Two processes, as two server replicas would be.
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
        {
            try
            {
                await Catalog(db).CreateAsync("Racer", i == 0 ? Password : NewPassword, Roles.User, Ct);
                return true;
            }
            catch (UsernameTakenException)
            {
                return false;
            }
        }));

        Assert.Single(results, won => won);
        Assert.Equal(1, await db.GetCollection<UserDocument>("users").CountDocumentsAsync(u => u.Username == "racer", cancellationToken: Ct));
        var users = Catalog(db);
        Assert.NotEqual(
            await users.TrySignInAsync("racer", Password, Ct) is null,
            await users.TrySignInAsync("racer", NewPassword, Ct) is null);
    }

    [Fact]
    public async Task Recovery_sets_a_new_password_and_ends_every_session()
    {
        var db = fixture.NewDatabase();
        var users = Catalog(db);
        var (user, recoveryKey) = await users.CreateAsync("chris", Password, Roles.User, Ct);
        var key = (await users.TrySignInAsync("chris", Password, Ct))!.Value.UserKey;
        var slots = db.GetCollection<UserSlotDocument>("user_slots");
        await slots.InsertOneAsync(new UserSlotDocument { User = user.Id, Kind = UserSlotDocument.Session, Wrapped = [1, 2, 3] }, cancellationToken: Ct);

        // Typed by hand: lower case, spaces instead of dashes.
        Assert.True(await users.RecoverAsync("Chris", recoveryKey.ToLowerInvariant().Replace('-', ' '), NewPassword, Ct));

        Assert.Null(await users.TrySignInAsync("chris", Password, Ct));
        Assert.Equal(key, (await users.TrySignInAsync("chris", NewPassword, Ct))!.Value.UserKey);
        Assert.False(await slots.Find(s => s.User == user.Id && s.Kind == UserSlotDocument.Session).AnyAsync(Ct));
        Assert.Equal(1, await slots.CountDocumentsAsync(s => s.User == user.Id && s.Kind == UserSlotDocument.Password, cancellationToken: Ct));
    }

    [Fact]
    public async Task Recovery_with_a_wrong_or_malformed_key_changes_nothing()
    {
        var users = Catalog();
        await users.CreateAsync("chris", Password, Roles.User, Ct);
        var (_, otherKey) = await users.CreateAsync("other", Password, Roles.User, Ct);

        Assert.False(await users.RecoverAsync("chris", otherKey, NewPassword, Ct));
        Assert.False(await users.RecoverAsync("chris", "kumbu-rk-not-a-key", NewPassword, Ct));
        Assert.False(await users.RecoverAsync("nobody", otherKey, NewPassword, Ct));
        Assert.NotNull(await users.TrySignInAsync("chris", Password, Ct));
    }

    [Fact]
    public async Task Changing_the_password_needs_the_old_one_and_keeps_the_user_key()
    {
        var users = Catalog();
        var (user, _) = await users.CreateAsync("chris", Password, Roles.User, Ct);
        var key = (await users.TrySignInAsync("chris", Password, Ct))!.Value.UserKey;

        Assert.False(await users.ChangePasswordAsync(user.Id, key, "not the password at all", NewPassword, Ct));
        Assert.True(await users.ChangePasswordAsync(user.Id, key, Password, NewPassword, Ct));

        Assert.Null(await users.TrySignInAsync("chris", Password, Ct));
        Assert.Equal(key, (await users.TrySignInAsync("chris", NewPassword, Ct))!.Value.UserKey);
    }

    [Fact]
    public async Task Regenerating_the_recovery_key_invalidates_the_old_one()
    {
        var users = Catalog();
        var (user, oldRecoveryKey) = await users.CreateAsync("chris", Password, Roles.User, Ct);
        var key = (await users.TrySignInAsync("chris", Password, Ct))!.Value.UserKey;

        var newRecoveryKey = await users.RegenerateRecoveryKeyAsync(user.Id, key, Ct);

        Assert.NotEqual(oldRecoveryKey, newRecoveryKey);
        Assert.False(await users.RecoverAsync("chris", oldRecoveryKey, NewPassword, Ct));
        Assert.True(await users.RecoverAsync("chris", newRecoveryKey, NewPassword, Ct));
        Assert.Equal(key, (await users.TrySignInAsync("chris", NewPassword, Ct))!.Value.UserKey);
    }

    [Fact]
    public async Task Regenerating_the_recovery_key_refuses_a_key_that_is_not_the_users()
    {
        var users = Catalog();
        var (user, recoveryKey) = await users.CreateAsync("chris", Password, Roles.User, Ct);

        await Assert.ThrowsAsync<ArgumentException>(() => users.RegenerateRecoveryKeyAsync(user.Id, new byte[32], Ct));
        Assert.True(await users.RecoverAsync("chris", recoveryKey, NewPassword, Ct));
    }

    [Fact]
    public async Task Weak_passwords_unknown_roles_and_odd_usernames_are_refused()
    {
        var users = Catalog();

        await Assert.ThrowsAsync<ArgumentException>(() => users.CreateAsync("chris", "unbelievable", Roles.User, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => users.CreateAsync("chris", Password, "superuser", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => users.CreateAsync("chris\n", Password, Roles.User, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => users.CreateAsync("", Password, Roles.User, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => users.CreateAsync("chris\uD800", Password, Roles.User, Ct));
        Assert.Null(await users.TrySignInAsync("chris\uD800", Password, Ct));
    }

    [Fact]
    public async Task A_password_typed_composed_or_decomposed_opens_the_same_account()
    {
        var users = Catalog();
        const string composed = "Passw\u00f6rter-sind-toll";
        const string decomposed = "Passwo\u0308rter-sind-toll";
        var (user, _) = await users.CreateAsync("chris", composed, Roles.User, Ct);

        Assert.Equal(user.Id, (await users.TrySignInAsync("chris", decomposed, Ct))?.User.Id);
        Assert.Equal(user.Id, (await users.TrySignInAsync("chris", composed, Ct))?.User.Id);
    }

    [Theory]
    [InlineData("\u0430dmin")] // Cyrillic a: looks like "admin"
    [InlineData("j\u00fcrgen")]
    [InlineData("chris smith")]
    public async Task Usernames_are_ascii_only(string username)
    {
        var e = await Assert.ThrowsAsync<ArgumentException>(() => Catalog().CreateAsync(username, Password, Roles.User, Ct));
        Assert.StartsWith("A username has 1 to 64 characters: letters a-z, digits, '.', '-' or '_'.", e.Message);
    }

    [Fact]
    public async Task A_user_has_at_most_one_password_and_one_recovery_slot_but_many_sessions()
    {
        var db = fixture.NewDatabase();
        var (user, _) = await Catalog(db).CreateAsync("chris", Password, Roles.User, Ct);
        var slots = db.GetCollection<UserSlotDocument>("user_slots");

        foreach (var kind in new[] { UserSlotDocument.Password, UserSlotDocument.Recovery })
        {
            var e = await Assert.ThrowsAsync<MongoWriteException>(() => slots.InsertOneAsync(new UserSlotDocument { User = user.Id, Kind = kind, Wrapped = [1] }, cancellationToken: Ct));
            Assert.Equal(ServerErrorCategory.DuplicateKey, e.WriteError.Category);
        }

        await slots.InsertOneAsync(new UserSlotDocument { User = user.Id, Kind = UserSlotDocument.Session, Wrapped = [1] }, cancellationToken: Ct);
        await slots.InsertOneAsync(new UserSlotDocument { User = user.Id, Kind = UserSlotDocument.Session, Wrapped = [2] }, cancellationToken: Ct);
    }

    [Fact]
    public async Task The_password_slots_salt_differs_per_user_even_for_the_same_password()
    {
        var db = fixture.NewDatabase();
        var users = Catalog(db);
        var (a, _) = await users.CreateAsync("anna", Password, Roles.User, Ct);
        var (b, _) = await users.CreateAsync("bert", Password, Roles.User, Ct);

        var slots = db.GetCollection<UserSlotDocument>("user_slots");
        var saltA = (await slots.Find(s => s.User == a.Id && s.Kind == UserSlotDocument.Password).SingleAsync(Ct)).Salt;
        var saltB = (await slots.Find(s => s.User == b.Id && s.Kind == UserSlotDocument.Password).SingleAsync(Ct)).Salt;

        Assert.Equal(16, saltA!.Length);
        Assert.NotEqual(saltA, saltB);
    }

    [Fact]
    public async Task Each_sign_in_moves_the_last_sign_in_to_the_previous_one()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        var users = Catalog(time: time);
        var (created, _) = await users.CreateAsync("chris", Password, Roles.User, Ct);
        Assert.Null(created.LastSignInAt);

        time.Advance(TimeSpan.FromHours(1));
        var first = (await users.TrySignInAsync("chris", Password, Ct))!.Value.User;
        time.Advance(TimeSpan.FromHours(1));
        var second = (await users.TrySignInAsync("chris", Password, Ct))!.Value.User;

        Assert.Equal(new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc), first.LastSignInAt);
        Assert.Null(first.PreviousSignInAt);
        Assert.Equal(new DateTime(2026, 10, 24, 11, 0, 0, DateTimeKind.Utc), second.LastSignInAt);
        Assert.Equal(first.LastSignInAt, second.PreviousSignInAt);
    }

    [Fact]
    public async Task Nothing_secret_reaches_the_database()
    {
        var db = fixture.NewDatabase();
        var users = Catalog(db);
        var (_, recoveryKey) = await users.CreateAsync("chris", Password, Roles.User, Ct);
        var key = (await users.TrySignInAsync("chris", Password, Ct))!.Value.UserKey;

        var dump = string.Join('\n', (await db.GetCollection<MongoDB.Bson.BsonDocument>("user_slots").Find(_ => true).ToListAsync(Ct))
            .Concat(await db.GetCollection<MongoDB.Bson.BsonDocument>("users").Find(_ => true).ToListAsync(Ct))
            .Select(d => d.ToString()));

        Assert.DoesNotContain(Password, dump);
        Assert.DoesNotContain(recoveryKey, dump);
        Assert.DoesNotContain(Convert.ToBase64String(key), dump);
    }
}
