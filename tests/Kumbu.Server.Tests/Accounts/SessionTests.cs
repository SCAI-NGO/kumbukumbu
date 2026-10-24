// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Text;
using Kumbu.Server.Accounts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Accounts;

[Collection("mongo")]
public sealed class SessionTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "tr0ub4dor and three more words";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));

    private IMongoCollection<UserSlotDocument> Slots => _db.GetCollection<UserSlotDocument>("user_slots");

    private UserCatalog Users() =>
        new(_db, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), _time, NullLogger<UserCatalog>.Instance);

    private Sessions Sessions() => new(_db, _time);

    /// <summary>A user, signed in: the user and their user key.</summary>
    private async Task<(User User, byte[] Key)> SignedInAsync(string username = "chris")
    {
        var users = Users();
        await users.CreateAsync(username, Password, Roles.User, Ct);
        return (await users.TrySignInAsync(username, Password, Ct))!.Value;
    }

    [Fact]
    public async Task A_started_session_resolves_to_the_user_and_their_user_key()
    {
        var (user, key) = await SignedInAsync();

        var secret = await Sessions().StartAsync(user, key, Ct);
        var resolved = await Sessions().ResolveAsync(secret, Ct);

        Assert.NotNull(resolved);
        Assert.Equal(user.Id, resolved.Value.User.Id);
        Assert.Equal(key, resolved.Value.UserKey);
        Assert.Equal(32, Base64Url.DecodeFromChars(secret).Length);
    }

    [Fact]
    public async Task Two_sessions_have_different_secrets_and_each_opens_only_itself()
    {
        var (user, key) = await SignedInAsync();
        var sessions = Sessions();

        var first = await sessions.StartAsync(user, key, Ct);
        var second = await sessions.StartAsync(user, key, Ct);
        await sessions.EndAsync(first, Ct);

        Assert.NotEqual(first, second);
        Assert.Null(await sessions.ResolveAsync(first, Ct));
        Assert.NotNull(await sessions.ResolveAsync(second, Ct));
    }

    [Fact]
    public async Task An_ended_session_resolves_to_nothing()
    {
        var (user, key) = await SignedInAsync();
        var sessions = Sessions();
        var secret = await sessions.StartAsync(user, key, Ct);

        await sessions.EndAsync(secret, Ct);

        Assert.Null(await sessions.ResolveAsync(secret, Ct));
        Assert.False(await Slots.Find(s => s.Kind == UserSlotDocument.Session).AnyAsync(Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64url!")]
    [InlineData("AAAA")] // valid Base64Url, but 3 bytes and no session
    public async Task A_malformed_or_unknown_secret_resolves_to_nothing(string secret)
    {
        var (user, key) = await SignedInAsync();
        await Sessions().StartAsync(user, key, Ct);

        Assert.Null(await Sessions().ResolveAsync(secret, Ct));
        Assert.Null(await Sessions().ResolveAsync(Base64Url.EncodeToString(new byte[32]), Ct));
    }

    [Fact]
    public async Task A_session_idle_for_31_minutes_is_refused_and_removed()
    {
        var (user, key) = await SignedInAsync();
        var sessions = Sessions();
        var secret = await sessions.StartAsync(user, key, Ct);

        _time.Advance(TimeSpan.FromMinutes(29));
        Assert.NotNull(await sessions.ResolveAsync(secret, Ct));
        _time.Advance(TimeSpan.FromMinutes(31));

        Assert.Null(await sessions.ResolveAsync(secret, Ct));
        Assert.False(await Slots.Find(s => s.Kind == UserSlotDocument.Session).AnyAsync(Ct));
    }

    [Fact]
    public async Task A_session_ends_after_12_hours_despite_activity()
    {
        var (user, key) = await SignedInAsync();
        var sessions = Sessions();
        var secret = await sessions.StartAsync(user, key, Ct);

        // Used every 20 minutes, so it never goes idle.
        for (var minutes = 20; minutes < 12 * 60; minutes += 20)
        {
            _time.Advance(TimeSpan.FromMinutes(20));
            Assert.NotNull(await sessions.ResolveAsync(secret, Ct));
        }

        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(await sessions.ResolveAsync(secret, Ct));
    }

    [Fact]
    public async Task The_slot_expires_after_12_hours_through_a_ttl_index()
    {
        var (user, key) = await SignedInAsync();
        await Sessions().StartAsync(user, key, Ct);

        var slot = await Slots.Find(s => s.Kind == UserSlotDocument.Session).SingleAsync(Ct);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddHours(12), slot.ExpiresAt);

        var indexes = await (await Slots.Indexes.ListAsync(Ct)).ToListAsync(Ct);
        var ttl = Assert.Single(indexes, i => i.Contains("expireAfterSeconds"));
        Assert.Equal(new BsonDocument("expiresAt", 1), ttl["key"].AsBsonDocument);
        Assert.Equal(0, ttl["expireAfterSeconds"].ToInt32());
    }

    [Fact]
    public async Task Last_used_is_written_at_most_once_a_minute()
    {
        var (user, key) = await SignedInAsync();
        var sessions = Sessions();
        var secret = await sessions.StartAsync(user, key, Ct);
        var started = _time.GetUtcNow().UtcDateTime;

        async Task<DateTime?> LastUsedAsync() => (await Slots.Find(s => s.Kind == UserSlotDocument.Session).SingleAsync(Ct)).LastUsedAt;

        _time.Advance(TimeSpan.FromSeconds(30));
        await sessions.ResolveAsync(secret, Ct);
        Assert.Equal(started, await LastUsedAsync());

        _time.Advance(TimeSpan.FromSeconds(31));
        await sessions.ResolveAsync(secret, Ct);
        Assert.Equal(started.AddSeconds(61), await LastUsedAsync());
    }

    [Fact]
    public async Task A_deactivated_users_session_is_refused()
    {
        var (user, key) = await SignedInAsync();
        var secret = await Sessions().StartAsync(user, key, Ct);

        await _db.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == user.Id, Builders<UserDocument>.Update.Set(u => u.Active, false), cancellationToken: Ct);

        Assert.Null(await Sessions().ResolveAsync(secret, Ct));
    }

    [Fact]
    public async Task Ending_all_sessions_can_keep_the_current_one()
    {
        var (user, key) = await SignedInAsync();
        var (other, otherKey) = await SignedInAsync("other");
        var sessions = Sessions();
        var current = await sessions.StartAsync(user, key, Ct);
        var elsewhere = await sessions.StartAsync(user, key, Ct);
        var someoneElses = await sessions.StartAsync(other, otherKey, Ct);

        await sessions.EndAllAsync(user.Id, current, Ct);

        Assert.NotNull(await sessions.ResolveAsync(current, Ct));
        Assert.Null(await sessions.ResolveAsync(elsewhere, Ct));
        Assert.NotNull(await sessions.ResolveAsync(someoneElses, Ct));

        await sessions.EndAllAsync(user.Id, null, Ct);

        Assert.Null(await sessions.ResolveAsync(current, Ct));
        Assert.NotNull(await sessions.ResolveAsync(someoneElses, Ct));
    }

    [Fact]
    public async Task A_session_survives_a_server_restart()
    {
        var (user, key) = await SignedInAsync();
        var secret = await Sessions().StartAsync(user, key, Ct);

        // A new process: new Sessions, new client, the same database.
        var restarted = new Sessions(new MongoClient(fixture.ConnectionString).GetDatabase(_db.DatabaseNamespace.DatabaseName), _time);

        Assert.Equal(key, (await restarted.ResolveAsync(secret, Ct))?.UserKey);
    }

    [Fact]
    public async Task Changing_the_password_ends_every_session_and_a_new_sign_in_works()
    {
        var (user, key) = await SignedInAsync();
        var secret = await Sessions().StartAsync(user, key, Ct);
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(await Users().ChangePasswordAsync(user.Id, key, Password, NewPassword, Ct));

        Assert.Null(await Sessions().ResolveAsync(secret, Ct));
        var again = (await Users().TrySignInAsync("chris", NewPassword, Ct))!.Value;
        Assert.NotNull(await Sessions().ResolveAsync(await Sessions().StartAsync(again.User, again.UserKey, Ct), Ct));
    }

    [Fact]
    public async Task A_session_slot_left_over_from_before_a_password_change_is_refused()
    {
        var (user, key) = await SignedInAsync();
        var secret = await Sessions().StartAsync(user, key, Ct);
        var leftOver = await Slots.Find(s => s.Kind == UserSlotDocument.Session).SingleAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(await Users().ChangePasswordAsync(user.Id, key, Password, NewPassword, Ct));
        // As if the server had crashed between the new password slot and the deletion of the sessions.
        await Slots.InsertOneAsync(leftOver, cancellationToken: Ct);

        Assert.Null(await Sessions().ResolveAsync(secret, Ct));
    }

    [Fact]
    public async Task A_session_slot_left_over_from_before_a_recovery_is_refused()
    {
        var users = Users();
        var (_, recoveryKey) = await users.CreateAsync("chris", Password, Roles.User, Ct);
        var (user, key) = (await users.TrySignInAsync("chris", Password, Ct))!.Value;
        var secret = await Sessions().StartAsync(user, key, Ct);
        var leftOver = await Slots.Find(s => s.Kind == UserSlotDocument.Session).SingleAsync(Ct);
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(await users.RecoverAsync("chris", recoveryKey, NewPassword, Ct));
        await Slots.InsertOneAsync(leftOver, cancellationToken: Ct);

        Assert.Null(await Sessions().ResolveAsync(secret, Ct));
    }

    [Fact]
    public async Task Neither_the_secret_nor_the_user_key_reaches_the_database()
    {
        var (user, key) = await SignedInAsync();
        var secret = await Sessions().StartAsync(user, key, Ct);

        var dump = string.Join('\n', (await _db.GetCollection<BsonDocument>("user_slots").Find(_ => true).ToListAsync(Ct)).Select(d => d.ToString()));

        var secretBytes = Base64Url.DecodeFromChars(secret);
        Assert.DoesNotContain(secret, dump);
        Assert.DoesNotContain(Convert.ToBase64String(secretBytes), dump);
        Assert.DoesNotContain(Convert.ToBase64String(key), dump);
    }
}
