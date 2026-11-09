// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>The reveal of discreet vaults on the web session's slot and the count of PIN entries (discreet vaults
/// design, Decisions 1 and 5).</summary>
[Collection("mongo")]
public sealed class RevealTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    // The real time, in whole seconds: MongoDB's TTL monitor sweeps slots made at a past fake time.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    private readonly Argon2Kdf _kdf = new(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1));
    private VaultCatalog? _catalog;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private VaultCatalog Catalog() => _catalog ??= new(_db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance);

    private UserKeyRotation Rotation() => new(_db, _kdf, _time, NullLogger<UserKeyRotation>.Instance);

    private UserCatalog Users() =>
        new(_db, _kdf, new ApiKeys(_db, Catalog(), _time, NullLogger<ApiKeys>.Instance), new Connections(_db, Catalog(), _time, NullLogger<Connections>.Instance),
            _time, NullLogger<UserCatalog>.Instance, Rotation());

    private Sessions Sessions() => new(_db, _time);

    private async Task<(User User, byte[] Key, DateTime ReadAt)> SignedInAsync()
    {
        var users = Users();
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, Password, Roles.User, Ct);
        return (await users.TrySignInAsync(username, Password, Ct))!.Value;
    }

    private async Task<string> SessionAsync(bool remember = false)
    {
        var (user, key, readAt) = await SignedInAsync();
        return await Sessions().StartAsync(user, key, readAt, Ct, remember);
    }

    private async Task<SessionDiscreet> DiscreetAsync(string secret) => (await Sessions().ResolveAsync(secret, Ct))!.Value.Discreet;

    [Fact]
    public async Task A_new_session_is_not_revealed()
    {
        var secret = await SessionAsync();

        var discreet = await DiscreetAsync(secret);

        Assert.False(discreet.Revealed);
        Assert.Null(discreet.RevealUntil);
        Assert.Null(discreet.ShownUntil);
        Assert.Null(discreet.LateSave);
    }

    [Fact]
    public async Task A_timed_reveal_lasts_until_its_end_and_not_a_moment_longer()
    {
        var secret = await SessionAsync();
        var start = Now;

        Assert.Equal((true, (DateTime?)start.AddMinutes(5)), await Sessions().RevealAsync(secret, TimeSpan.FromMinutes(5), Ct));

        var revealed = await DiscreetAsync(secret);
        Assert.True(revealed.Revealed);
        Assert.Equal(start.AddMinutes(5), revealed.RevealUntil);
        Assert.Equal(start.AddMinutes(5), revealed.ShownUntil);

        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.True((await DiscreetAsync(secret)).Revealed);

        _time.Advance(TimeSpan.FromSeconds(1));
        var expired = await DiscreetAsync(secret);
        Assert.False(expired.Revealed);
        Assert.Null(expired.ShownUntil);
        // Kept for the late save, which reads when the reveal ended.
        Assert.Equal(start.AddMinutes(5), expired.RevealUntil);
    }

    [Fact]
    public async Task A_reveal_answers_the_end_it_stored_to_the_millisecond()
    {
        var secret = await SessionAsync();
        _time.Advance(TimeSpan.FromTicks(1_234_567));

        var (revealed, until) = await Sessions().RevealAsync(secret, TimeSpan.FromMinutes(5), Ct);

        Assert.True(revealed);
        Assert.Equal(until, (await DiscreetAsync(secret)).RevealUntil);
    }

    [Fact]
    public async Task A_reveal_of_an_ended_session_answers_not_revealed()
    {
        var secret = await SessionAsync();
        await Sessions().EndAsync(secret, Ct);

        Assert.False((await Sessions().RevealAsync(secret, TimeSpan.FromMinutes(5), Ct)).Revealed);
        Assert.False((await Sessions().RevealAsync(secret, null, Ct)).Revealed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reveal_of_a_session_idle_too_long_answers_not_revealed(bool remember)
    {
        var secret = await SessionAsync(remember);
        _time.Advance((remember ? Kumbu.Server.Accounts.Sessions.RememberedIdleTimeout : Kumbu.Server.Accounts.Sessions.IdleTimeout) + TimeSpan.FromSeconds(1));

        Assert.False((await Sessions().RevealAsync(secret, null, Ct)).Revealed);
    }

    [Fact]
    public async Task A_reveal_for_ever_lasts_eleven_hours_until_hidden()
    {
        // Remembered, so the session outlives eleven idle hours (a normal one would end after 30 minutes).
        var secret = await SessionAsync(remember: true);

        Assert.Equal((true, (DateTime?)null), await Sessions().RevealAsync(secret, null, Ct));
        _time.Advance(TimeSpan.FromHours(11));

        var discreet = await DiscreetAsync(secret);
        Assert.True(discreet.Revealed);
        Assert.Null(discreet.RevealUntil);
        Assert.Null(discreet.ShownUntil);

        await Sessions().HideAsync(secret, Ct);
        Assert.False((await DiscreetAsync(secret)).Revealed);
    }

    [Fact]
    public async Task Hiding_ends_a_timed_reveal_at_once()
    {
        var secret = await SessionAsync();
        await Sessions().RevealAsync(secret, TimeSpan.FromMinutes(30), Ct);

        await Sessions().HideAsync(secret, Ct);

        var discreet = await DiscreetAsync(secret);
        Assert.False(discreet.Revealed);
        Assert.Null(discreet.RevealUntil);
    }

    [Fact]
    public async Task Ending_a_session_revealed_for_ever_shows_nothing_any_more()
    {
        var secret = await SessionAsync();
        var subject = Kumbu.Server.Accounts.Sessions.SubjectOf(secret)!;
        await Sessions().RevealAsync(secret, null, Ct);

        await Sessions().EndAsync(secret, Ct);

        Assert.Null(await Sessions().ResolveAsync(secret, Ct));
        Assert.Equal(((string?)null, false, (DateTime?)null), await Sessions().SocketStateAsync(subject, Ct));
    }

    [Fact]
    public async Task A_session_started_after_another_reveals_starts_hidden()
    {
        var (user, key, readAt) = await SignedInAsync();
        var first = await Sessions().StartAsync(user, key, readAt, Ct);
        await Sessions().RevealAsync(first, null, Ct);

        var second = await Sessions().StartAsync(user, key, readAt, Ct);

        Assert.True((await DiscreetAsync(first)).Revealed);
        Assert.False((await DiscreetAsync(second)).Revealed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_tenth_pin_entry_ends_the_session(bool remember)
    {
        var secret = await SessionAsync(remember);
        var userId = (await Sessions().ResolveAsync(secret, Ct))!.Value.User.Id;

        for (var i = 0; i < 9; i++)
        {
            Assert.False((await Sessions().PinEnteredAsync(secret, Ct)).Ended);
        }

        Assert.NotNull(await Sessions().ResolveAsync(secret, Ct));
        Assert.Equal((true, userId), await Sessions().PinEnteredAsync(secret, Ct));
        Assert.Null(await Sessions().ResolveAsync(secret, Ct));
    }

    [Fact]
    public async Task An_entry_after_the_tenth_reports_the_session_ended_too()
    {
        var secret = await SessionAsync();
        for (var i = 0; i < 10; i++) await Sessions().PinEnteredAsync(secret, Ct);

        Assert.Equal((true, (string?)null), await Sessions().PinEnteredAsync(secret, Ct));
    }

    [Fact]
    public async Task Of_concurrent_pin_entries_the_ones_from_the_tenth_on_end_the_session_and_one_names_its_user()
    {
        var secret = await SessionAsync();
        var userId = (await Sessions().ResolveAsync(secret, Ct))!.Value.User.Id;

        var results = await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => Task.Run(() => Sessions().PinEnteredAsync(secret, Ct), Ct)));

        Assert.Equal(Kumbu.Server.Accounts.Sessions.PinEntryLimit - 1, results.Count(r => !r.Ended));
        Assert.Single(results, r => r.Ended && r.UserId == userId);
        Assert.All(results, r => Assert.True(r.UserId is null || r.UserId == userId));
        Assert.Null(await Sessions().ResolveAsync(secret, Ct));
    }

    [Fact]
    public async Task A_reveal_does_not_reset_the_count_of_pin_entries()
    {
        var secret = await SessionAsync();
        for (var i = 0; i < 9; i++) await Sessions().PinEnteredAsync(secret, Ct);

        await Sessions().RevealAsync(secret, TimeSpan.FromMinutes(5), Ct);

        Assert.True((await Sessions().PinEnteredAsync(secret, Ct)).Ended);
        Assert.Null(await Sessions().ResolveAsync(secret, Ct));
    }

    [Fact]
    public async Task A_pin_entry_for_a_session_gone_reports_it_ended_and_names_nobody()
    {
        Assert.Equal((true, null), await Sessions().PinEnteredAsync(new string('A', 43), Ct));
    }

    [Fact]
    public async Task The_kept_session_of_a_rotation_stays_revealed()
    {
        var (user, key, readAt) = await SignedInAsync();
        var secret = await Sessions().StartAsync(user, key, readAt, Ct);
        await Sessions().RevealAsync(secret, TimeSpan.FromMinutes(10), Ct);

        Assert.NotNull(await Rotation().RotateAsync(user, key, Password, secret, Ct));

        Assert.True((await DiscreetAsync(secret)).Revealed);
    }

    [Fact]
    public async Task The_session_carries_the_sealed_list_of_the_user()
    {
        var (user, key, readAt) = await SignedInAsync();
        var secret = await Sessions().StartAsync(user, key, readAt, Ct);
        Assert.Null((await DiscreetAsync(secret)).SealedList);

        var sealedList = DiscreetVaults.SealList(user.Id, key, ["v1", "v2"]);
        await _db.GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == user.Id, Builders<UserDocument>.Update.Set(u => u.DiscreetVaults, sealedList), cancellationToken: Ct);

        Assert.Equal(sealedList, (await DiscreetAsync(secret)).SealedList);
    }

    [Fact]
    public async Task The_socket_state_follows_the_reveal()
    {
        var (user, key, readAt) = await SignedInAsync();
        var secret = await Sessions().StartAsync(user, key, readAt, Ct);
        var subject = Kumbu.Server.Accounts.Sessions.SubjectOf(secret)!;

        Assert.Equal((user.Id, false, (DateTime?)null), await Sessions().SocketStateAsync(subject, Ct));

        var (_, until) = await Sessions().RevealAsync(secret, TimeSpan.FromMinutes(5), Ct);
        Assert.NotNull(until);
        Assert.Equal((user.Id, true, until), await Sessions().SocketStateAsync(subject, Ct));

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal((user.Id, false, (DateTime?)null), await Sessions().SocketStateAsync(subject, Ct));

        await Sessions().RevealAsync(secret, null, Ct);
        Assert.Equal((user.Id, true, (DateTime?)null), await Sessions().SocketStateAsync(subject, Ct));

        await Sessions().EndAsync(secret, Ct);
        Assert.Equal(((string?)null, false, (DateTime?)null), await Sessions().SocketStateAsync(subject, Ct));
        Assert.Equal(((string?)null, false, (DateTime?)null), await Sessions().SocketStateAsync("not hex", Ct));
    }
}
