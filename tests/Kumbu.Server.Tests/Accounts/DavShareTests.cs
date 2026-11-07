// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>WebDAV shares (WebDAV design §3.2, §4): a username and a password shown once, built like API keys.</summary>
[Collection("app")]
public sealed class DavShareTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => app.PublicApi.Services;

    private DavShares Shares => Services.GetRequiredService<DavShares>();

    /// <summary>The app's shares, on a clock of the test's own.</summary>
    private DavShares SharesAt(FakeTimeProvider time) => new(app.Database, Services.GetRequiredService<VaultCatalog>(), time, NullLogger<DavShares>.Instance);

    private IMongoCollection<BsonDocument> ShareDocs => app.Database.GetCollection<BsonDocument>("dav_shares");

    private IMongoCollection<BsonDocument> SlotDocs => app.Database.GetCollection<BsonDocument>("slots");

    private Task<long> SlotsAsync(string shareId) =>
        SlotDocs.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("kind", "dav") & Builders<BsonDocument>.Filter.Eq("share", shareId), cancellationToken: Ct);

    private Task<BsonDocument> ShareDocAsync(string shareId) => ShareDocs.Find(Builders<BsonDocument>.Filter.Eq("_id", shareId)).SingleAsync(Ct);

    private static DateTime JustRead => DateTime.UtcNow;

    private static async Task<(RestClient Owner, Dictionary<string, KeyRight> Grant)> OwnerAsync(RestClient owner, KeyRight right = KeyRight.Write) =>
        (owner, new Dictionary<string, KeyRight> { [await owner.VaultIdAsync()] = right });

    private async Task<(RestClient Owner, Dictionary<string, KeyRight> Grant)> OwnerAsync(KeyRight right = KeyRight.Write) =>
        await OwnerAsync(await RestClient.NewAsync(app.PublicApi, vault: "Work"), right);

    private Task<(string Username, string Password, DavShareInfo Info)> CreateAsync(
        RestClient owner, Dictionary<string, KeyRight> grant, string label = "Work PC", int? expiresInDays = null, DavShares? shares = null) =>
        (shares ?? Shares).CreateAsync(owner.User, owner.UserKey, JustRead, label, grant, Ct, expiresInDays);

    [Fact]
    public async Task Create_returns_a_username_and_a_52_character_password_once()
    {
        var (alice, grant) = await OwnerAsync();

        var (username, password, info) = await CreateAsync(alice, grant);

        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){12}$", password);
        Assert.Matches($"^{Regex.Escape(alice.Username)}\\.[0-9a-hjkmnp-tv-z]{{4}}$", username);
        Assert.Equal(username, info.Username);
        Assert.Equal($"{alice.Username}.{info.Id}", username);
        Assert.Equal("Work PC", info.Label);
        Assert.Equal(grant, info.Vaults);
        Assert.Null(info.ExpiresAt);
        var listed = Assert.Single(await Shares.ListAsync(alice.User, alice.UserKey, Ct));
        Assert.Equal(info.Id, listed.Id);
        Assert.Equal(username, listed.Username);
        Assert.Empty(listed.LostVaults);
        Assert.Equal(1, await SlotsAsync(info.Id));
    }

    [Fact]
    public async Task Resolve_accepts_the_password_in_any_case_with_or_without_hyphens_and_I_L_O()
    {
        var (alice, grant) = await OwnerAsync();
        var (username, password, info) = await CreateAsync(alice, grant);
        var bare = password.Replace("-", "", StringComparison.Ordinal);

        foreach (var presented in new[]
                 {
                     password,
                     password.ToLowerInvariant(),
                     bare,
                     bare.ToLowerInvariant(),
                     string.Join(' ', password.Split('-')),
                     // I and L read as 1, O as 0.
                     password.Replace('1', 'I').Replace('0', 'O'),
                     password.Replace('1', 'l').Replace('0', 'o'),
                 })
        {
            var resolved = await Shares.ResolveAsync(username, presented, Ct);
            Assert.Equal(info.Id, resolved.Share?.Id);
        }

        var upper = await Shares.ResolveAsync(username.ToUpperInvariant(), password, Ct);
        Assert.Equal(info.Id, upper.Share?.Id);
        Assert.Equal(alice.User.Id, upper.Share!.User.Id);
        Assert.Equal("Work PC", upper.Share.Label);
    }

    [Fact]
    public async Task Resolve_refuses_the_token_with_another_shares_username()
    {
        var (alice, grant) = await OwnerAsync();
        var (_, password, info) = await CreateAsync(alice, grant, "Laptop");
        var (other, _, _) = await CreateAsync(alice, grant, "Tablet");
        var (bob, bobGrant) = await OwnerAsync();
        var (bobs, _, _) = await CreateAsync(bob, bobGrant);

        // Refused like a wrong token (spec §3.2): counted, not known.
        foreach (var username in new[] { other, bobs, $"{bob.Username}.{info.Id}", info.Id, alice.Username, "" })
        {
            var resolved = await Shares.ResolveAsync(username, password, Ct);
            Assert.Null(resolved.Share);
            Assert.False(resolved.Known);
        }
    }

    [Fact]
    public async Task Resolve_refuses_a_revoked_share()
    {
        var (alice, grant) = await OwnerAsync();
        var (username, password, info) = await CreateAsync(alice, grant);

        Assert.True(await Shares.RevokeAsync(alice.User, info.Id, Ct));

        var resolved = await Shares.ResolveAsync(username, password, Ct);
        Assert.Null(resolved.Share);
        Assert.True(resolved.Known);
        Assert.Equal(0, await SlotsAsync(info.Id));
        Assert.NotNull(Assert.Single(await Shares.ListAsync(alice.User, alice.UserKey, Ct)).RevokedAt);
        Assert.False(await Shares.RevokeAsync(alice.User, info.Id, Ct));
    }

    [Fact]
    public async Task Another_user_cannot_revoke_a_share()
    {
        var (alice, grant) = await OwnerAsync();
        var (username, password, info) = await CreateAsync(alice, grant);
        var (bob, _) = await OwnerAsync();

        Assert.False(await Shares.RevokeAsync(bob.User, info.Id, Ct));
        Assert.NotNull((await Shares.ResolveAsync(username, password, Ct)).Share);
    }

    [Fact]
    public async Task Resolve_refuses_an_expired_share()
    {
        var (alice, grant) = await OwnerAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var shares = SharesAt(time);
        var (username, password, info) = await CreateAsync(alice, grant, expiresInDays: 30, shares: shares);
        var expiresAt = Assert.IsType<DateTime>(info.ExpiresAt);
        Assert.InRange(expiresAt - time.GetUtcNow().UtcDateTime, TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(30));

        time.Advance(TimeSpan.FromDays(30) - TimeSpan.FromMinutes(1));
        Assert.NotNull((await shares.ResolveAsync(username, password, Ct)).Share);
        time.Advance(TimeSpan.FromMinutes(1));

        var resolved = await shares.ResolveAsync(username, password, Ct);
        Assert.Null(resolved.Share);
        Assert.True(resolved.Known);
        // Its slots go the first time it is presented after its expiry; it stays listed as expired, not revoked.
        Assert.Equal(0, await SlotsAsync(info.Id));
        Assert.Null(Assert.Single(await shares.ListAsync(alice.User, alice.UserKey, Ct)).RevokedAt);
    }

    [Fact]
    public async Task An_expiry_moved_in_mongodb_makes_the_share_open_nothing()
    {
        var (alice, grant) = await OwnerAsync();
        var (username, password, info) = await CreateAsync(alice, grant, expiresInDays: 30);

        await ShareDocs.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", info.Id), Builders<BsonDocument>.Update.Unset("expiresAt"), cancellationToken: Ct);

        Assert.Null((await Shares.ResolveAsync(username, password, Ct)).Share);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(31)]
    public async Task An_expiry_other_than_30_90_or_365_days_is_refused(int days)
    {
        var (alice, grant) = await OwnerAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => CreateAsync(alice, grant, expiresInDays: days));
        Assert.Empty(await Shares.ListAsync(alice.User, alice.UserKey, Ct));
    }

    [Fact]
    public async Task A_share_without_vaults_or_with_a_vault_not_the_users_is_refused()
    {
        var (alice, _) = await OwnerAsync();
        var (_, bobsGrant) = await OwnerAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => CreateAsync(alice, []));
        await Assert.ThrowsAsync<ArgumentException>(() => CreateAsync(alice, bobsGrant));
        Assert.Empty(await Shares.ListAsync(alice.User, alice.UserKey, Ct));
    }

    /// <summary>Deactivated by an admin: the share is revoked with the keys, so reactivating revives nothing.</summary>
    [Fact]
    public async Task Resolve_refuses_a_deactivated_user()
    {
        var (alice, grant) = await OwnerAsync();
        var (username, password, info) = await CreateAsync(alice, grant);
        var admin = await RestClient.NewAsync(app.PublicApi, vault: "Admin", role: Roles.Admin);
        var accounts = Services.GetRequiredService<Admin>();

        Assert.True(await accounts.SetActiveAsync(admin.User, alice.User.Id, false, Ct));

        var resolved = await Shares.ResolveAsync(username, password, Ct);
        Assert.Null(resolved.Share);
        Assert.True(resolved.Known);
        Assert.True((await ShareDocAsync(info.Id)).Contains("revokedAt"));
        Assert.Equal(0, await SlotsAsync(info.Id));

        Assert.True(await accounts.SetActiveAsync(admin.User, alice.User.Id, true, Ct));
        Assert.Null((await Shares.ResolveAsync(username, password, Ct)).Share);
    }

    [Fact]
    public async Task Resolve_refuses_an_inactive_user()
    {
        var (alice, grant) = await OwnerAsync();
        var (username, password, _) = await CreateAsync(alice, grant);

        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == alice.User.Id, Builders<UserDocument>.Update.Set(u => u.Active, false), cancellationToken: Ct);

        var resolved = await Shares.ResolveAsync(username, password, Ct);
        Assert.Null(resolved.Share);
        Assert.True(resolved.Known);
    }

    [Fact]
    public async Task A_password_change_that_revokes_the_keys_revokes_the_shares()
    {
        var (alice, grant) = await OwnerAsync();
        var (username, password, info) = await CreateAsync(alice, grant);
        var (keptName, keptPassword, _) = await CreateAsync(alice, grant, "Kept");
        var users = Services.GetRequiredService<UserCatalog>();

        Assert.True(await users.ChangePasswordAsync(alice.User, alice.UserKey, RestClient.Password, "tr0ub4dor and three more words", revokeApiKeys: false, Ct));
        Assert.NotNull((await Shares.ResolveAsync(keptName, keptPassword, Ct)).Share);

        Assert.True(await users.ChangePasswordAsync(alice.User, alice.UserKey, "tr0ub4dor and three more words", RestClient.Password, revokeApiKeys: true, Ct));
        Assert.Null((await Shares.ResolveAsync(username, password, Ct)).Share);
        Assert.Null((await Shares.ResolveAsync(keptName, keptPassword, Ct)).Share);
        Assert.Equal(0, await SlotsAsync(info.Id));
    }

    [Fact]
    public async Task A_recovery_revokes_the_shares()
    {
        var users = Services.GetRequiredService<UserCatalog>();
        var name = "u" + Guid.NewGuid().ToString("N")[..12];
        var (_, recoveryKey) = await users.CreateAsync(name, RestClient.Password, Roles.User, Ct);
        var (user, userKey, readAt) = (await users.TrySignInAsync(name, RestClient.Password, Ct))!.Value;
        var vault = await Services.GetRequiredService<VaultCatalog>().CreateForUserAsync("Work", user.Id, userKey, Ct);
        var (username, password, _) = await Shares.CreateAsync(user, userKey, readAt, "Laptop", new Dictionary<string, KeyRight> { [vault.Id] = KeyRight.Read }, Ct);

        Assert.NotNull(await users.RecoverAsync(name, recoveryKey, "tr0ub4dor and three more words", Ct));

        Assert.Null((await Shares.ResolveAsync(username, password, Ct)).Share);
    }

    [Fact]
    public async Task Resolve_with_an_unknown_token_is_not_known()
    {
        var (alice, grant) = await OwnerAsync();
        var (username, _, _) = await CreateAsync(alice, grant);

        foreach (var presented in new[] { DavTokens.Format(DavTokens.New()), "nonsense", "", "kumbu_0123456789" })
        {
            var resolved = await Shares.ResolveAsync(username, presented, Ct);
            Assert.Null(resolved.Share);
            Assert.False(resolved.Known);
        }
    }

    [Fact]
    public async Task A_username_with_a_dot_resolves()
    {
        var users = Services.GetRequiredService<UserCatalog>();
        await users.CreateAsync("alice.smith", RestClient.Password, Roles.User, Ct);
        var (user, userKey, readAt) = (await users.TrySignInAsync("alice.smith", RestClient.Password, Ct))!.Value;
        var vault = await Services.GetRequiredService<VaultCatalog>().CreateForUserAsync("Work", user.Id, userKey, Ct);

        var (username, password, info) = await Shares.CreateAsync(user, userKey, readAt, "Laptop", new Dictionary<string, KeyRight> { [vault.Id] = KeyRight.Read }, Ct);

        Assert.Equal($"alice.smith.{info.Id}", username);
        Assert.Equal(info.Id, (await Shares.ResolveAsync(username, password, Ct)).Share?.Id);
        Assert.Null((await Shares.ResolveAsync($"alice.{info.Id}", password, Ct)).Share);
        Assert.Null((await Shares.ResolveAsync($"smith.{info.Id}", password, Ct)).Share);
    }

    [Fact]
    public async Task Nothing_of_the_token_is_stored()
    {
        var (alice, grant) = await OwnerAsync();
        var (_, password, info) = await CreateAsync(alice, grant);
        var normal = DavTokens.Normalise(password)!;
        var doc = await ShareDocAsync(info.Id);

        Assert.False(doc.Contains("prefix"));
        var stored = doc.ToJson() + Encoding.Latin1.GetString(doc.ToBson());
        foreach (var slot in await SlotDocs.Find(Builders<BsonDocument>.Filter.Eq("share", info.Id)).ToListAsync(Ct)) stored += slot.ToJson() + Encoding.Latin1.GetString(slot.ToBson());
        // No assertion message: it would print the token.
        Assert.False(stored.Contains(password, StringComparison.OrdinalIgnoreCase));
        for (var i = 0; i + 8 <= normal.Length; i++) Assert.False(stored.Contains(normal.Substring(i, 8), StringComparison.OrdinalIgnoreCase));
        Assert.False(stored.Contains("Work PC", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_label_is_unique_among_a_users_unrevoked_shares()
    {
        var (alice, grant) = await OwnerAsync();
        var (_, _, first) = await CreateAsync(alice, grant, "Laptop");

        await Assert.ThrowsAsync<DavShareLabelTakenException>(() => CreateAsync(alice, grant, " LAPTOP "));
        // Another user's label, and a key's name, are no matter.
        var (bob, bobGrant) = await OwnerAsync();
        await CreateAsync(bob, bobGrant, "Laptop");
        await Services.GetRequiredService<ApiKeys>().CreateAsync(alice.User, alice.UserKey, JustRead, "Tablet", grant, Ct);
        await CreateAsync(alice, grant, "Tablet");

        Assert.True(await Shares.RevokeAsync(alice.User, first.Id, Ct));
        var (_, _, again) = await CreateAsync(alice, grant, "Laptop");
        Assert.Equal(["Laptop", "Tablet", "Laptop"], (await Shares.ListAsync(alice.User, alice.UserKey, Ct)).Select(s => s.Label));
        Assert.NotEqual(first.Id, again.Id);
    }

    [Fact]
    public async Task An_expired_shares_label_is_free_again()
    {
        var (alice, grant) = await OwnerAsync();
        await CreateAsync(alice, grant, "Laptop", expiresInDays: 30, shares: SharesAt(new FakeTimeProvider(DateTimeOffset.UtcNow.AddDays(-31))));

        await CreateAsync(alice, grant, "Laptop");

        Assert.Equal(2, (await Shares.ListAsync(alice.User, alice.UserKey, Ct)).Count);
    }

    [Fact]
    public async Task A_share_id_already_taken_is_drawn_again()
    {
        var (alice, grant) = await OwnerAsync();
        var (_, _, first) = await CreateAsync(alice, grant, "Laptop");
        var shares = SharesAt(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var fresh = await FreshIdAsync();
        var draws = new Queue<string>([first.Id, first.Id, fresh]);
        shares.NextId = draws.Dequeue;

        var (username, password, second) = await CreateAsync(alice, grant, "Tablet", shares: shares);

        Assert.Equal(fresh, second.Id);
        Assert.Equal($"{alice.Username}.{fresh}", username);
        Assert.Equal(fresh, (await Shares.ResolveAsync(username, password, Ct)).Share?.Id);
        Assert.Equal(1, await SlotsAsync(first.Id));
        Assert.Equal(1, await SlotsAsync(fresh));
    }

    /// <summary>A share id no share has yet.</summary>
    private async Task<string> FreshIdAsync()
    {
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        while (true)
        {
            var id = string.Concat(Enumerable.Range(0, 4).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]));
            if (await ShareDocs.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", id), cancellationToken: Ct) == 0) return id;
        }
    }

    [Fact]
    public async Task Resolve_writes_last_used_at_most_once_a_minute()
    {
        var (alice, grant) = await OwnerAsync();
        var (username, password, info) = await CreateAsync(alice, grant);
        var byId = Builders<BsonDocument>.Filter.Eq("_id", info.Id);
        DateTime LastUsed(BsonDocument d) => d["lastUsedAt"].ToUniversalTime();

        Assert.False((await ShareDocAsync(info.Id)).Contains("lastUsedAt"));
        Assert.NotNull((await Shares.ResolveAsync(username, password, Ct)).Share);
        var first = LastUsed(await ShareDocAsync(info.Id));

        var recent = first.AddSeconds(-30);
        await ShareDocs.UpdateOneAsync(byId, Builders<BsonDocument>.Update.Set("lastUsedAt", recent), cancellationToken: Ct);
        Assert.NotNull((await Shares.ResolveAsync(username, password, Ct)).Share);
        Assert.Equal(recent, LastUsed(await ShareDocAsync(info.Id)));

        var stale = first.AddMinutes(-2);
        await ShareDocs.UpdateOneAsync(byId, Builders<BsonDocument>.Update.Set("lastUsedAt", stale), cancellationToken: Ct);
        Assert.NotNull((await Shares.ResolveAsync(username, password, Ct)).Share);
        Assert.True(LastUsed(await ShareDocAsync(info.Id)) >= first);
        Assert.Equal(LastUsed(await ShareDocAsync(info.Id)), Assert.Single(await Shares.ListAsync(alice.User, alice.UserKey, Ct)).LastUsedAt);
    }

    [Fact]
    public async Task A_share_can_be_created_while_dav_is_off()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await using var off = app.PublicApi.WithWebHostBuilder(b => b.UseSetting("Kumbu:Dav:Enabled", "false"));
        var offAlice = alice.On(off);

        using var created = await offAlice.SendAsync(HttpMethod.Post, "/api/dav-shares", new { label = "Laptop", vaults = new Dictionary<string, string> { ["Work"] = "write" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var listedOff = await offAlice.GetAsync("/api/dav-shares");
        var offList = await RestClient.JsonAsync(listedOff);
        Assert.False(offList["enabled"]!.GetValue<bool>());
        Assert.Single(offList["shares"]!.AsArray());
        using var listedOn = await alice.GetAsync("/api/dav-shares");
        Assert.True((await RestClient.JsonAsync(listedOn))["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Rotation_reseals_the_meta()
    {
        var (alice, grant) = await OwnerAsync();
        var (username, password, info) = await CreateAsync(alice, grant, "Laptop");
        var (_, _, revoked) = await CreateAsync(alice, grant, "Old");
        Assert.True(await Shares.RevokeAsync(alice.User, revoked.Id, Ct));

        var rotation = (await Services.GetRequiredService<UserKeyRotation>().RotateAsync(alice.User, alice.UserKey, RestClient.Password, null, Ct))!;
        var key = rotation.NewUserKey;

        var listed = await Shares.ListAsync(alice.User, key, Ct);
        Assert.Equal(["Laptop", "Old"], listed.Select(s => s.Label));
        Assert.Equal(grant, listed[0].Vaults);
        // The label's hash is under the new key: a taken label stays taken, a revoked one free.
        await Assert.ThrowsAsync<DavShareLabelTakenException>(() => Shares.CreateAsync(alice.User, key, DateTime.UtcNow, "laptop", grant, Ct));
        await Shares.CreateAsync(alice.User, key, DateTime.UtcNow, "Old", grant, Ct);
        // The share's slots never involved the user key.
        Assert.Equal(info.Id, (await Shares.ResolveAsync(username, password, Ct)).Share?.Id);
    }

    [Fact]
    public async Task A_share_made_between_preparing_and_applying_a_rotation_makes_it_fail()
    {
        var (alice, grant) = await OwnerAsync();
        var rotation = Services.GetRequiredService<UserKeyRotation>();

        var prepared = await rotation.PrepareAsync(alice.User, alice.UserKey, RestClient.Password, null, null, Ct);
        await CreateAsync(alice, grant);

        await Assert.ThrowsAsync<RotationConflictException>(() => rotation.CommitAsync(prepared, Ct));
    }

    [Fact]
    public async Task Account_deletion_removes_shares_and_dav_slots()
    {
        var (alice, grant) = await OwnerAsync();
        var (_, _, info) = await CreateAsync(alice, grant);
        var (_, _, revoked) = await CreateAsync(alice, grant, "Old");
        Assert.True(await Shares.RevokeAsync(alice.User, revoked.Id, Ct));
        // A slot left over, of a vault that is not the user's: it goes by share id.
        await SlotDocs.InsertOneAsync(
            new BsonDocument { ["vault"] = "v" + Guid.NewGuid().ToString("N"), ["kind"] = "dav", ["share"] = info.Id, ["wrapped"] = new BsonBinaryData([1]), ["createdAt"] = DateTime.UtcNow },
            cancellationToken: Ct);

        Assert.True(await Services.GetRequiredService<Deletion>().DeleteOwnAccountAsync(alice.User, alice.UserKey, RestClient.Password, alice.Username, Ct));

        Assert.Equal(0, await ShareDocs.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("user", alice.User.Id), cancellationToken: Ct));
        Assert.Equal(0, await SlotsAsync(info.Id));
    }

    [Fact]
    public async Task The_expiry_sweep_retires_expired_shares()
    {
        var (alice, grant) = await OwnerAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (_, _, month) = await CreateAsync(alice, grant, "Month", expiresInDays: 30, shares: SharesAt(time));
        var (_, _, never) = await CreateAsync(alice, grant, "Never", shares: SharesAt(time));
        time.Advance(TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));
        var keys = new ApiKeys(app.Database, Services.GetRequiredService<VaultCatalog>(), time, NullLogger<ApiKeys>.Instance);

        using var sweep = new ExpiredKeySweep(keys, SharesAt(time), time, NullLogger<ExpiredKeySweep>.Instance);
        await sweep.StartAsync(Ct);
        for (var i = 0; i < 200 && await SlotsAsync(month.Id) > 0; i++) await Task.Delay(25, Ct);
        await sweep.StopAsync(Ct);

        Assert.Equal(0, await SlotsAsync(month.Id));
        var doc = await ShareDocAsync(month.Id);
        Assert.False(doc.Contains("nameId"));
        Assert.False(doc.Contains("revokedAt"));
        Assert.Equal(1, await SlotsAsync(never.Id));
        Assert.True((await ShareDocAsync(never.Id)).Contains("nameId"));
    }

    [Fact]
    public async Task The_list_says_which_vaults_a_share_lost()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var work = await alice.VaultIdAsync();
        var diary = (await Services.GetRequiredService<VaultCatalog>().CreateForUserAsync("Diary", alice.User.Id, alice.UserKey, Ct)).Id;
        var (_, _, info) = await CreateAsync(alice, new Dictionary<string, KeyRight> { [work] = KeyRight.Write, [diary] = KeyRight.Read });

        await SlotDocs.DeleteOneAsync(
            Builders<BsonDocument>.Filter.Eq("kind", "dav") & Builders<BsonDocument>.Filter.Eq("share", info.Id) & Builders<BsonDocument>.Filter.Eq("vault", diary), Ct);

        var listed = Assert.Single(await Shares.ListAsync(alice.User, alice.UserKey, Ct));
        Assert.Equal([diary], listed.LostVaults);
        Assert.Equal(2, listed.Vaults.Count);
    }

    [Fact]
    public void Tokens_are_52_crockford_characters_and_normalise_as_pairing_codes()
    {
        var token = DavTokens.New();

        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{52}$", token);
        Assert.NotEqual(token, DavTokens.New());
        Assert.Equal(token, DavTokens.Normalise(DavTokens.Format(token)));
        Assert.Equal(token, DavTokens.Normalise(DavTokens.Format(token).ToLowerInvariant().Replace('1', 'l').Replace('0', 'o')));
        Assert.Null(DavTokens.Normalise(token[..51]));
        Assert.Null(DavTokens.Normalise(token + "0"));
        Assert.Null(DavTokens.Normalise("U" + token[1..]));
        Assert.Null(DavTokens.Normalise(""));
    }
}
