// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>Expired API keys lose their slots without being presented again: at start and every hour.</summary>
[Collection("mongo")]
public sealed class ExpiredKeySweepTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));

    private VaultCatalog Catalog() => new(_db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance);

    private ApiKeys Keys() => new(_db, Catalog(), _time, NullLogger<ApiKeys>.Instance);

    private UserCatalog Users() =>
        new(_db, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), Keys(), new Connections(_db, Catalog(), _time, NullLogger<Connections>.Instance), _time, NullLogger<UserCatalog>.Instance);

    private Task<long> SlotsAsync(string keyId) =>
        _db.GetCollection<BsonDocument>("slots").CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("apiKey", keyId), cancellationToken: Ct);

    private Task<BsonDocument> KeyDocAsync(string keyId) =>
        _db.GetCollection<BsonDocument>("api_keys").Find(Builders<BsonDocument>.Filter.Eq("_id", keyId)).SingleAsync(Ct);

    /// <summary>A user with two vaults, and a key on both that expires in 30 days, one in 90 and one that never does.</summary>
    private async Task<(ApiKeyInfo Month, ApiKeyInfo Quarter, ApiKeyInfo Never)> KeysAsync()
    {
        await Users().CreateAsync("lea", Password, Roles.User, Ct);
        var (user, key, readAt) = (await Users().TrySignInAsync("lea", Password, Ct))!.Value;
        var catalog = Catalog();
        var grant = new Dictionary<string, KeyRight>
        {
            [(await catalog.CreateForUserAsync("Notes", user.Id, key, Ct)).Id] = KeyRight.Write,
            [(await catalog.CreateForUserAsync("Work", user.Id, key, Ct)).Id] = KeyRight.Read,
        };
        var keys = Keys();
        _time.Advance(TimeSpan.FromSeconds(1));
        var (_, month) = await keys.CreateAsync(user, key, readAt, "month", grant, Ct, expiresInDays: 30);
        var (_, quarter) = await keys.CreateAsync(user, key, readAt, "quarter", grant, Ct, expiresInDays: 90);
        var (_, never) = await keys.CreateAsync(user, key, readAt, "never", grant, Ct);
        Assert.Equal(2, await SlotsAsync(month.Id));
        return (month, quarter, never);
    }

    [Fact]
    public async Task Expired_keys_lose_their_slots_on_every_vault_and_their_name_and_nothing_else_does()
    {
        var (month, quarter, never) = await KeysAsync();
        _time.Advance(TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));

        Assert.Equal(1, await Keys().RetireAllExpiredAsync(Ct));

        Assert.Equal(0, await SlotsAsync(month.Id));
        Assert.False((await KeyDocAsync(month.Id)).Contains("nameId"));
        // Expired, not revoked: the list still says what happened to it.
        Assert.False((await KeyDocAsync(month.Id)).Contains("revokedAt"));
        Assert.Equal(2, await SlotsAsync(quarter.Id));
        Assert.Equal(2, await SlotsAsync(never.Id));
        Assert.True((await KeyDocAsync(quarter.Id)).Contains("nameId"));
        Assert.Equal(0, await Keys().RetireAllExpiredAsync(Ct));
    }

    [Fact]
    public async Task The_sweep_runs_at_start_and_then_every_hour()
    {
        var (month, quarter, never) = await KeysAsync();
        _time.Advance(TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));
        using var sweep = new ExpiredKeySweep(Keys(), _time, NullLogger<ExpiredKeySweep>.Instance);

        await sweep.StartAsync(Ct);
        await WaitUntilAsync(async () => await SlotsAsync(month.Id) == 0);
        Assert.Equal(2, await SlotsAsync(quarter.Id));

        _time.Advance(TimeSpan.FromDays(60));
        _time.Advance(ExpiredKeySweep.Interval);
        await WaitUntilAsync(async () => await SlotsAsync(quarter.Id) == 0);
        Assert.Equal(2, await SlotsAsync(never.Id));
        await sweep.StopAsync(Ct);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> done)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await done()) return;
            await Task.Delay(25, Ct);
        }

        Assert.Fail("The sweep didn't run.");
    }
}
