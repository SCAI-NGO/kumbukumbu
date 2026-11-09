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
        Assert.Null(discreet.Until);
        Assert.Null(discreet.LateSave);
    }

    [Fact]
    public async Task A_timed_reveal_lasts_until_its_end_and_not_a_moment_longer()
    {
        var secret = await SessionAsync();
        var start = Now;

        var until = await Sessions().RevealAsync(secret, TimeSpan.FromMinutes(5), Ct);

        Assert.Equal(start.AddMinutes(5), until);
        var revealed = await DiscreetAsync(secret);
        Assert.True(revealed.Revealed);
        Assert.Equal(start.AddMinutes(5), revealed.Until);

        _time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.True((await DiscreetAsync(secret)).Revealed);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False((await DiscreetAsync(secret)).Revealed);
    }

    [Fact]
    public async Task A_reveal_for_ever_lasts_eleven_hours_until_hidden()
    {
        // Remembered, so the session outlives eleven idle hours (a normal one would end after 30 minutes).
        var secret = await SessionAsync(remember: true);

        Assert.Null(await Sessions().RevealAsync(secret, null, Ct));
        _time.Advance(TimeSpan.FromHours(11));

        var discreet = await DiscreetAsync(secret);
        Assert.True(discreet.Revealed);
        Assert.Null(discreet.Until);

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
        Assert.Null(discreet.Until);
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

        for (var i = 1; i < Kumbu.Server.Accounts.Sessions.PinEntryLimit; i++)
        {
            Assert.False((await Sessions().PinEnteredAsync(secret, Ct)).Ended);
        }

        Assert.NotNull(await Sessions().ResolveAsync(secret, Ct));
        Assert.Equal((true, userId), await Sessions().PinEnteredAsync(secret, Ct));
        Assert.Null(await Sessions().ResolveAsync(secret, Ct));
        Assert.Equal(10, Kumbu.Server.Accounts.Sessions.PinEntryLimit);
    }

    [Fact]
    public async Task A_reveal_does_not_reset_the_count_of_pin_entries()
    {
        var secret = await SessionAsync();
        for (var i = 1; i < Kumbu.Server.Accounts.Sessions.PinEntryLimit; i++) await Sessions().PinEnteredAsync(secret, Ct);

        await Sessions().RevealAsync(secret, TimeSpan.FromMinutes(5), Ct);

        Assert.True((await Sessions().PinEnteredAsync(secret, Ct)).Ended);
        Assert.Null(await Sessions().ResolveAsync(secret, Ct));
    }

    [Fact]
    public async Task Pin_entries_for_an_unknown_session_end_nothing()
    {
        Assert.Equal((false, null), await Sessions().PinEnteredAsync(new string('A', 43), Ct));
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

        Assert.Equal((user.Id, false), await Sessions().SocketStateAsync(subject, Ct));

        await Sessions().RevealAsync(secret, TimeSpan.FromMinutes(5), Ct);
        Assert.Equal((user.Id, true), await Sessions().SocketStateAsync(subject, Ct));

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal((user.Id, false), await Sessions().SocketStateAsync(subject, Ct));

        await Sessions().EndAsync(secret, Ct);
        Assert.Equal(((string?)null, false), await Sessions().SocketStateAsync(subject, Ct));
        Assert.Equal(((string?)null, false), await Sessions().SocketStateAsync("not hex", Ct));
    }
}
