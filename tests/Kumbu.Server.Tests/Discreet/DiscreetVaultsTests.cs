// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>The sealed list of a user's discreet vaults and her PIN verifier (discreet vaults design, Decisions 2 and 4).</summary>
[Collection("mongo")]
public sealed class DiscreetVaultsTests(MongoFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly Argon2Kdf _kdf = new(new Argon2Parameters(MemoryKiB: 8, Iterations: 1, Parallelism: 1));

    private IMongoCollection<UserDocument> Users => _db.GetCollection<UserDocument>("users");

    private DiscreetVaults Discreet() => new(_db, _kdf, NullLogger<DiscreetVaults>.Instance);

    /// <summary>A user with a user key of her own, as the catalogue would store her.</summary>
    private async Task<(string UserId, byte[] UserKey)> NewUserAsync()
    {
        var id = "u" + Guid.NewGuid().ToString("N");
        var key = VaultCrypto.NewKey();
        await Users.InsertOneAsync(
            new UserDocument { Id = id, Username = id, Display = id, Role = Roles.User, Active = true, KeyId = UserCatalog.KeyId(key, id), CreatedAt = DateTime.UtcNow },
            cancellationToken: Ct);
        return (id, key);
    }

    private static byte[] ListKey(string userId, byte[] userKey) => VaultCrypto.Derive(userKey, "kumbu/v1/discreet-vaults", Encoding.UTF8.GetBytes(userId));

    private async Task<IReadOnlySet<string>?> StoredListAsync(string userId, byte[] userKey)
    {
        var stored = await Users.Find(u => u.Id == userId).Project(u => u.DiscreetVaults).FirstOrDefaultAsync(Ct);
        return DiscreetVaults.OpenList(userId, userKey, stored);
    }

    [Fact]
    public void A_sealed_list_opens_to_its_ids_and_only_for_its_user_and_key()
    {
        var key = VaultCrypto.NewKey();
        var sealedList = DiscreetVaults.SealList("alice", key, ["v2", "v1", "v2"]);

        Assert.Equal(["v1", "v2"], DiscreetVaults.OpenList("alice", key, sealedList)!.Order(StringComparer.Ordinal));
        Assert.Null(DiscreetVaults.OpenList("alice", VaultCrypto.NewKey(), sealedList));
        Assert.Null(DiscreetVaults.OpenList("bob", key, sealedList));
    }

    [Fact]
    public void No_list_opens_to_none_and_garbage_to_null()
    {
        var key = VaultCrypto.NewKey();

        Assert.Empty(DiscreetVaults.OpenList("alice", key, null, out var none)!);
        Assert.Null(none);
        Assert.Null(DiscreetVaults.OpenList("alice", key, [1, 2, 3], out var garbage));
        Assert.Equal("CryptographicException", garbage);
        Assert.Null(DiscreetVaults.OpenList("alice", key, VaultCrypto.Seal(ListKey("alice", key), "not json"u8, "discreet-vaults:alice"), out var notJson));
        Assert.Equal("JsonException", notJson);
        Assert.Null(DiscreetVaults.OpenList("alice", key, VaultCrypto.Seal(ListKey("alice", key), "[\"v1\",null]"u8, "discreet-vaults:alice"), out var nullId));
        Assert.Equal("JsonException", nullId);
    }

    [Fact]
    public async Task The_sealed_plaintext_is_the_sorted_distinct_json_array()
    {
        var key = VaultCrypto.NewKey();
        var sealedList = DiscreetVaults.SealList("alice", key, ["v2", "v1", "v2"]);

        Assert.Equal("[\"v1\",\"v2\"]", Encoding.UTF8.GetString(VaultCrypto.Open(ListKey("alice", key), sealedList, "discreet-vaults:alice")));

        var (user, userKey) = await NewUserAsync();
        await Discreet().SetAsync(user, userKey, "v2", true, ["v1", "v2"], Ct);
        await Discreet().SetAsync(user, userKey, "v1", true, ["v1", "v2"], Ct);
        var stored = await Users.Find(u => u.Id == user).Project(u => u.DiscreetVaults).FirstAsync(Ct);
        Assert.Equal("[\"v1\",\"v2\"]", Encoding.UTF8.GetString(VaultCrypto.Open(ListKey(user, userKey), stored!, $"discreet-vaults:{user}")));
    }

    [Fact]
    public async Task Marking_adds_and_removes_and_keeps_the_other_ids()
    {
        var (user, key) = await NewUserAsync();
        var discreet = Discreet();

        await discreet.SetAsync(user, key, "v1", true, ["v1", "v2", "v3"], Ct);
        await discreet.SetAsync(user, key, "v2", true, ["v1", "v2", "v3"], Ct);
        await discreet.SetAsync(user, key, "v2", true, ["v1", "v2", "v3"], Ct);
        Assert.Equal(["v1", "v2"], (await StoredListAsync(user, key))!.Order(StringComparer.Ordinal));

        await discreet.SetAsync(user, key, "v1", false, ["v1", "v2", "v3"], Ct);
        Assert.Equal(["v2"], (await StoredListAsync(user, key))!);

        // Unmarking a vault not on the list leaves the rest as it is.
        await discreet.SetAsync(user, key, "v3", false, ["v1", "v2", "v3"], Ct);
        Assert.Equal(["v2"], (await StoredListAsync(user, key))!);
    }

    [Fact]
    public async Task Concurrent_marks_of_different_vaults_all_land()
    {
        string[] vaults = ["v1", "v2"];
        for (var run = 0; run < 10; run++)
        {
            var (user, key) = await NewUserAsync();
            // Both read before either writes: each waits on its first attempt until the other has read too.
            var arrived = 0;
            var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DiscreetVaults Racing()
            {
                var discreet = Discreet();
                var first = true;
                discreet.BeforeWrite = async () =>
                {
                    if (!first) return;
                    first = false;
                    if (Interlocked.Increment(ref arrived) == vaults.Length) bothRead.SetResult();
                    await bothRead.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
                };
                return discreet;
            }

            var racers = vaults.Select(v => (Discreet: Racing(), Vault: v)).ToArray();
            await Task.WhenAll(racers.Select(r => Task.Run(() => r.Discreet.SetAsync(user, key, r.Vault, true, vaults, Ct), Ct)));

            Assert.Equal(vaults, (await StoredListAsync(user, key))!.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task A_mark_between_the_read_and_the_write_is_kept()
    {
        var (user, key) = await NewUserAsync();
        var discreet = Discreet();
        var other = Discreet();
        var once = 0;
        discreet.BeforeWrite = async () =>
        {
            if (Interlocked.Exchange(ref once, 1) == 0) await other.SetAsync(user, key, "v2", true, ["v1", "v2"], Ct);
        };

        await discreet.SetAsync(user, key, "v1", true, ["v1", "v2"], Ct);

        Assert.Equal(["v1", "v2"], (await StoredListAsync(user, key))!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_list_that_keeps_changing_gives_up_after_three_attempts()
    {
        var (user, key) = await NewUserAsync();
        var discreet = Discreet();
        var calls = 0;
        discreet.BeforeWrite = async () =>
        {
            calls++;
            await Users.UpdateOneAsync(
                u => u.Id == user,
                Builders<UserDocument>.Update.Set(u => u.DiscreetVaults, DiscreetVaults.SealList(user, key, [Guid.NewGuid().ToString("N")])),
                cancellationToken: Ct);
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => discreet.SetAsync(user, key, "v1", true, ["v1"], Ct));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_list_that_does_not_open_starts_from_every_vault()
    {
        var (user, key) = await NewUserAsync();
        await Users.UpdateOneAsync(u => u.Id == user, Builders<UserDocument>.Update.Set(u => u.DiscreetVaults, new byte[] { 9, 9, 9 }), cancellationToken: Ct);

        await Discreet().SetAsync(user, key, "v2", false, ["v1", "v2", "v3"], Ct);

        Assert.Equal(["v1", "v3"], (await StoredListAsync(user, key))!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_mark_under_a_user_key_no_longer_in_force_is_refused()
    {
        var (user, key) = await NewUserAsync();
        var discreet = Discreet();
        await discreet.SetAsync(user, key, "v1", true, ["v1", "v2"], Ct);
        var before = await Users.Find(u => u.Id == user).Project(u => u.DiscreetVaults).FirstAsync(Ct);

        await Assert.ThrowsAsync<CredentialsChangedException>(() => discreet.SetAsync(user, VaultCrypto.NewKey(), "v2", true, ["v1", "v2"], Ct));

        Assert.Equal(before, await Users.Find(u => u.Id == user).Project(u => u.DiscreetVaults).FirstAsync(Ct));
    }

    [Fact]
    public async Task A_key_rotated_between_the_read_and_the_write_refuses_the_mark()
    {
        var (user, key) = await NewUserAsync();
        var discreet = Discreet();
        var newKey = VaultCrypto.NewKey();
        discreet.BeforeWrite = () => Users.UpdateOneAsync(
            u => u.Id == user, Builders<UserDocument>.Update.Set(u => u.KeyId, UserCatalog.KeyId(newKey, user)), cancellationToken: Ct);

        await Assert.ThrowsAsync<CredentialsChangedException>(() => discreet.SetAsync(user, key, "v1", true, ["v1"], Ct));

        Assert.Null(await Users.Find(u => u.Id == user).Project(u => u.DiscreetVaults).FirstAsync(Ct));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234567")]
    [InlineData("12a4")]
    [InlineData("١٢٣٤")]
    [InlineData("1234\n")]
    [InlineData("")]
    public async Task A_pin_is_4_to_6_ascii_digits(string pin)
    {
        var (user, _) = await NewUserAsync();

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => Discreet().SetPinAsync(user, pin, Ct));

        Assert.Equal(DiscreetVaults.PinSentence, refused.Message);
        Assert.False(DiscreetVaults.IsPin(pin));
        Assert.Null(await Users.Find(u => u.Id == user).Project(u => u.DiscreetPin).FirstAsync(Ct));
    }

    [Fact]
    public void Four_to_six_digits_are_a_pin()
    {
        Assert.True(DiscreetVaults.IsPin("1234"));
        Assert.True(DiscreetVaults.IsPin("12345"));
        Assert.True(DiscreetVaults.IsPin("000000"));
        Assert.False(DiscreetVaults.IsPin(null));
    }

    [Fact]
    public async Task Checking_a_pin_reveals_only_for_the_right_one_after_exactly_one_derivation()
    {
        var (withPin, _) = await NewUserAsync();
        var (withoutPin, _) = await NewUserAsync();
        var discreet = Discreet();
        await discreet.SetPinAsync(withPin, "4711", Ct);

        async Task<PinMatch> CheckAsync(string user, string pin)
        {
            var before = _kdf.Runs;
            var match = await discreet.CheckPinAsync(user, pin, Ct);
            Assert.Equal(before + 1, _kdf.Runs);
            return match;
        }

        Assert.Equal(PinMatch.Reveal, await CheckAsync(withPin, "4711"));
        Assert.Equal(PinMatch.None, await CheckAsync(withPin, "4712"));
        Assert.Equal(PinMatch.None, await CheckAsync(withoutPin, "4711"));
        Assert.Equal(PinMatch.None, await CheckAsync(withPin, "47a1"));
        Assert.Equal(PinMatch.None, await CheckAsync("nobody", "4711"));
    }

    [Fact]
    public async Task Setting_a_pin_for_a_user_who_is_gone_is_refused()
    {
        await Assert.ThrowsAsync<CredentialsChangedException>(() => Discreet().SetPinAsync("nobody", "4711", Ct));
    }

    [Fact]
    public async Task A_verifier_with_fields_of_a_later_version_still_reads()
    {
        var (user, _) = await NewUserAsync();
        var discreet = Discreet();
        await discreet.SetPinAsync(user, "4711", Ct);
        await _db.GetCollection<BsonDocument>("users").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", user), Builders<BsonDocument>.Update.Set("discreetPin.later", 1), cancellationToken: Ct);

        Assert.NotNull(await Users.Find(u => u.Id == user).FirstAsync(Ct));
        Assert.Equal(PinMatch.Reveal, await discreet.CheckPinAsync(user, "4711", Ct));
    }

    [Fact]
    public async Task A_new_pin_replaces_the_old_one()
    {
        var (user, _) = await NewUserAsync();
        var discreet = Discreet();
        await discreet.SetPinAsync(user, "4711", Ct);
        await discreet.SetPinAsync(user, "081542", Ct);

        Assert.Equal(PinMatch.None, await discreet.CheckPinAsync(user, "4711", Ct));
        Assert.Equal(PinMatch.Reveal, await discreet.CheckPinAsync(user, "081542", Ct));
    }

    [Fact]
    public async Task The_stored_verifier_holds_no_pin_in_clear()
    {
        var (user, _) = await NewUserAsync();
        await Discreet().SetPinAsync(user, "4711", Ct);

        var raw = await _db.GetCollection<BsonDocument>("users").Find(Builders<BsonDocument>.Filter.Eq("_id", user)).FirstAsync(Ct);
        var pin = raw["discreetPin"].AsBsonDocument;
        Assert.Equal(["salt", "verifier"], pin.Names.Order(StringComparer.Ordinal));
        Assert.Equal(Argon2Kdf.SaltBytes, pin["salt"].AsByteArray.Length);
        Assert.Equal(Argon2Kdf.OutputBytes, pin["verifier"].AsByteArray.Length);
        Assert.NotEqual(Encoding.UTF8.GetBytes("4711"), pin["verifier"].AsByteArray);
    }
}
