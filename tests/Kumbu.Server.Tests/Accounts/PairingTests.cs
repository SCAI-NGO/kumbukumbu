// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>Pairing codes (sync spec §4): a short code, once, for ten minutes, exchanged for a device key on one vault.</summary>
[Collection("mongo")]
public sealed class PairingTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
    private readonly Argon2Kdf _kdf = new(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1));
    private VaultCatalog? _catalog;
    private ApiKeys? _keys;
    private Pairings? _pairings;

    private VaultCatalog Catalog() => _catalog ??= new(_db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance);

    private ApiKeys Keys() => _keys ??= new(_db, Catalog(), _time, NullLogger<ApiKeys>.Instance);

    private Connections Connections() => new(_db, Catalog(), _time, NullLogger<Connections>.Instance);

    private UserKeyRotation Rotation() => new(_db, _kdf, _time, NullLogger<UserKeyRotation>.Instance);

    private UserCatalog Users() => new(_db, _kdf, Keys(), Connections(), _time, NullLogger<UserCatalog>.Instance, Rotation());

    private Pairings Pairings() => _pairings ??= new(Keys(), _time, NullLogger<Pairings>.Instance);

    private sealed record Account(User User, byte[] Key, DateTime ReadAt, string VaultId);

    private async Task<Account> AccountAsync(string vault = "Work")
    {
        var users = Users();
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, Password, Roles.User, Ct);
        // Signed in a moment after the account was made: a key made from this session postdates every credential change.
        _time.Advance(TimeSpan.FromSeconds(1));
        var (user, key, readAt) = (await users.TrySignInAsync(username, Password, Ct))!.Value;
        _time.Advance(TimeSpan.FromSeconds(1));
        var id = (await Catalog().CreateForUserAsync(vault, user.Id, key, Ct)).Id;
        return new Account(user, key, readAt, id);
    }

    private PairingCode Code(Account a, int? expiresInDays = null, string vault = "Work") =>
        Pairings().Create(a.User, (byte[])a.Key.Clone(), a.ReadAt, a.VaultId, vault, expiresInDays);

    [Fact]
    public async Task Codes_use_crockford_without_I_L_O_U()
    {
        var a = await AccountAsync();
        for (var i = 0; i < 1000; i++)
        {
            var code = Code(a).Code;
            Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}$", code);
            Assert.All(code.Replace("-", ""), c => Assert.Contains(c, Crockford));
        }
    }

    [Theory]
    [InlineData("k7qm 4xda", "K7QM4XDA")]
    [InlineData("K7QM-4XDA", "K7QM4XDA")]
    [InlineData("o0Il-1LIo", "00111110")]
    public void Normalise_reads_lower_case_dashes_spaces_and_lookalikes(string entered, string normal) =>
        Assert.Equal(normal, Kumbu.Server.Accounts.Pairings.Normalise(entered));

    [Fact]
    public void Format_groups_four_and_four() => Assert.Equal("K7QM-4XDA", Kumbu.Server.Accounts.Pairings.Format("K7QM4XDA"));

    [Fact]
    public async Task A_new_code_replaces_the_open_one()
    {
        var a = await AccountAsync();
        var first = Code(a);
        var second = Code(a);

        Assert.Null(await Pairings().RedeemAsync(first.Code, "Laptop", Ct));
        Assert.NotNull(await Pairings().RedeemAsync(second.Code, "Laptop", Ct));
    }

    [Fact]
    public async Task A_code_expires_after_ten_minutes()
    {
        var a = await AccountAsync();
        var code = Code(a);
        Assert.Equal(_time.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(10), code.ExpiresAt);

        _time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Assert.Null(await Pairings().RedeemAsync(code.Code, "Laptop", Ct));
        Assert.Empty(await Keys().ListAsync(a.User, a.Key, Ct));
    }

    [Fact]
    public async Task A_code_redeemed_twice_at_once_makes_one_key()
    {
        var a = await AccountAsync();
        var code = Code(a);

        var both = await Task.WhenAll(
            Task.Run(() => Pairings().RedeemAsync(code.Code, "Laptop", Ct), Ct),
            Task.Run(() => Pairings().RedeemAsync(code.Code.ToLowerInvariant(), "Phone", Ct), Ct));

        Assert.Single(both, r => r is not null);
        Assert.Single(await Keys().ListAsync(a.User, a.Key, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123456789012345678901234567890123")]
    [InlineData("a (b")]
    [InlineData("(Laptop)")]
    [InlineData("Lap)top")]
    [InlineData("x​y")]
    [InlineData("x\ny")]
    public async Task A_bad_device_name_keeps_the_code(string device)
    {
        var a = await AccountAsync();
        var code = Code(a);

        await Assert.ThrowsAsync<ArgumentException>(() => Pairings().RedeemAsync(code.Code, device, Ct));

        Assert.NotNull(await Pairings().RedeemAsync(code.Code, "Laptop", Ct));
    }

    [Fact]
    public async Task The_key_is_named_after_vault_and_device_and_numbered_when_taken()
    {
        var a = await AccountAsync();

        var first = await Pairings().RedeemAsync(Code(a).Code, "Laptop", Ct);
        var second = await Pairings().RedeemAsync(Code(a).Code, "Laptop", Ct);

        Assert.Equal("Work – Obsidian Laptop", first!.KeyName);
        Assert.Equal("Work – Obsidian Laptop 2", second!.KeyName);
        Assert.Equal("Work", first.Vault);
        Assert.Matches("^kumbu_[0-9A-Za-z]{43}$", first.Key);
    }

    [Fact]
    public async Task The_key_writes_its_one_vault_only_and_is_a_device()
    {
        var a = await AccountAsync();
        await Catalog().CreateForUserAsync("Diary", a.User.Id, a.Key, Ct);

        var redeemed = await Pairings().RedeemAsync(Code(a).Code, "Laptop", Ct);

        var info = Assert.Single(await Keys().ListAsync(a.User, a.Key, Ct));
        Assert.Equal(ApiKeyKind.Device, info.Kind);
        Assert.Equal(new Dictionary<string, KeyRight> { [a.VaultId] = KeyRight.Maintain }, info.Vaults);
        var resolved = await Keys().ResolveAsync(redeemed!.Key, Ct);
        Assert.Equal(info.Id, resolved!.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(365)]
    public async Task Expiry_is_as_chosen_and_never_by_default(int? days)
    {
        var a = await AccountAsync();

        await Pairings().RedeemAsync(Code(a, days).Code, "Laptop", Ct);

        var info = Assert.Single(await Keys().ListAsync(a.User, a.Key, Ct));
        if (days is null) Assert.Null(info.ExpiresAt);
        else Assert.Equal(_time.GetUtcNow().UtcDateTime.AddDays(days.Value), info.ExpiresAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task An_expiry_other_than_30_90_or_365_days_is_refused()
    {
        var a = await AccountAsync();

        Assert.Throws<ArgumentException>(() => Code(a, 7));
    }

    [Fact]
    public async Task A_rotated_user_key_invalidates_the_code()
    {
        var a = await AccountAsync();
        var code = Code(a);
        _time.Advance(TimeSpan.FromSeconds(1));

        await Rotation().RotateAsync(a.User, a.Key, Password, null, Ct);

        Assert.Null(await Pairings().RedeemAsync(code.Code, "Laptop", Ct));
        var (_, newKey, _) = (await Users().TrySignInAsync(a.User.Username, Password, Ct))!.Value;
        Assert.Empty(await Keys().ListAsync(a.User, newKey, Ct));
    }

    [Fact]
    public async Task An_expired_record_is_dropped_and_its_key_zeroed_without_another_pairing()
    {
        var a = await AccountAsync();
        var copy = (byte[])a.Key.Clone();
        Pairings().Create(a.User, copy, a.ReadAt, a.VaultId, "Work", null);

        _time.Advance(Kumbu.Server.Accounts.Pairings.Lifetime + TimeSpan.FromMinutes(1));

        Assert.Equal(0, Pairings().Count);
        Assert.All(copy, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Dropping_a_users_codes_zeroes_their_keys_and_leaves_others_open()
    {
        var a = await AccountAsync();
        var b = await AccountAsync();
        var copy = (byte[])a.Key.Clone();
        var mine = Pairings().Create(a.User, copy, a.ReadAt, a.VaultId, "Work", null);
        var theirs = Code(b);

        Pairings().DropAll(a.User.Id);

        Assert.False(Pairings().IsOpen(mine.Code));
        Assert.All(copy, x => Assert.Equal(0, x));
        Assert.True(Pairings().IsOpen(theirs.Code));
        Assert.Null(await Pairings().RedeemAsync(mine.Code, "Laptop", Ct));
    }

    [Fact]
    public async Task A_long_vault_name_pairs_with_a_long_device_name()
    {
        var vault = new string('V', 60);
        var a = await AccountAsync(vault);
        var device = new string('D', 32);

        var first = await Pairings().RedeemAsync(Pairings().Create(a.User, (byte[])a.Key.Clone(), a.ReadAt, a.VaultId, vault, null).Code, device, Ct);
        var second = await Pairings().RedeemAsync(Pairings().Create(a.User, (byte[])a.Key.Clone(), a.ReadAt, a.VaultId, vault, null).Code, device, Ct);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.InRange(first.KeyName.Length, 1, 64);
        Assert.EndsWith($"… – Obsidian {device}", first.KeyName, StringComparison.Ordinal);
        Assert.Equal(first.KeyName + " 2", second!.KeyName);
        Assert.Equal(vault, first.Vault);
    }

    [Theory]
    [InlineData("Work (old)", "Work [old] – Obsidian Laptop")]
    [InlineData("Wo\u200Brk", "Work – Obsidian Laptop")]
    public async Task A_vault_name_a_key_name_cant_hold_is_made_fit(string vault, string keyName)
    {
        var a = await AccountAsync(vault);

        var redeemed = await Pairings().RedeemAsync(Code(a, vault: vault).Code, "Laptop", Ct);

        Assert.Equal(keyName, redeemed!.KeyName);
        Assert.Equal(vault, redeemed.Vault);
    }

    [Fact]
    public async Task Codes_made_at_once_for_one_vault_leave_one_open()
    {
        var a = await AccountAsync();

        var codes = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => Code(a), Ct)));

        Assert.Equal(1, Pairings().Count);
        Assert.Single(codes, c => Pairings().IsOpen(c.Code));
    }

    [Fact]
    public async Task A_vault_being_rekeyed_keeps_the_code_for_a_retry()
    {
        var a = await AccountAsync();
        var code = Code(a);
        Catalog().RekeyFaultAt = p => p == "after-start" ? throw new InvalidOperationException("crash after the start") : Task.CompletedTask;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().RekeyAsync(a.VaultId, a.User.Id, a.Key, Ct));
        }
        finally
        {
            Catalog().RekeyFaultAt = null;
        }

        await Assert.ThrowsAsync<VaultBusyException>(() => Pairings().RedeemAsync(code.Code, "Laptop", Ct));
        await Catalog().RekeyAsync(a.VaultId, a.User.Id, a.Key, Ct);

        Assert.NotNull(await Pairings().RedeemAsync(code.Code, "Laptop", Ct));
    }

    [Theory]
    [InlineData("(Laptop)")]
    [InlineData("Lap)top")]
    public async Task A_device_name_with_parentheses_is_refused_whether_or_not_the_code_exists(string device)
    {
        var a = await AccountAsync();
        var code = Code(a);

        await Assert.ThrowsAsync<ArgumentException>(() => Pairings().RedeemAsync("ZZZZ-ZZZZ", device, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Pairings().RedeemAsync(code.Code, device, Ct));
        Assert.True(Pairings().IsOpen(code.Code));
    }

    /// <summary>
    /// No code-existence oracle (security §6, issue #47): the key-name check runs only for an open code, so a device name
    /// that passes the device rules must pass it too, and one that fails them must fail before the lookup. Either way an
    /// open code and an unknown one get the same kind of answer.
    /// </summary>
    [Theory]
    [InlineData("Laptop")]
    [InlineData("  Laptop  ")]
    [InlineData("dev")]
    [InlineData("seed")]
    [InlineData("Obsidian")]
    [InlineData("Lap (top")]
    [InlineData("\u0301Laptop")]
    [InlineData("e\u0301")]
    [InlineData("Phone \U0001F4F1")]
    [InlineData("DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD")]
    [InlineData("–")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Lap\u200Btop")]
    [InlineData("Lap\ntop")]
    [InlineData("(Laptop)")]
    [InlineData("DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD")]
    public async Task A_device_name_gets_the_same_answer_for_an_open_and_an_unknown_code(string device)
    {
        var a = await AccountAsync(new string('V', 60));
        var code = Code(a, vault: new string('V', 60));

        var unknown = await Answer(() => Pairings().RedeemAsync("ZZZZ-ZZZZ", device, Ct));
        var open = await Answer(() => Pairings().RedeemAsync(code.Code, device, Ct));

        if (unknown is "null") Assert.Equal("key", open);
        else Assert.Equal(unknown, open);

        static async Task<string> Answer(Func<Task<RedeemedKey?>> redeem)
        {
            try
            {
                return await redeem() is null ? "null" : "key";
            }
            catch (ArgumentException e)
            {
                return e.Message;
            }
        }
    }

    [Fact]
    public async Task A_wrong_code_is_null()
    {
        var a = await AccountAsync();
        Code(a);

        Assert.Null(await Pairings().RedeemAsync("ZZZZ-ZZZZ", "Laptop", Ct));
        Assert.Null(await Pairings().RedeemAsync("not a code at all", "Laptop", Ct));
    }
}
