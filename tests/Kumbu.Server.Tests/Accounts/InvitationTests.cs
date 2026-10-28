// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.Cli;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>Invitations: single-use links with an expiry and a role, stored only as a keyed hash; and the bootstrap of the primary admin.</summary>
[Collection("mongo")]
public sealed class InvitationTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";
    private const string BaseUrl = "https://kumbu.example.org";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));

    private IMongoCollection<BsonDocument> InvitationDocs => _db.GetCollection<BsonDocument>("invitations");

    private ApiKeys Keys() =>
        new(_db, new VaultCatalog(_db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance), _time, NullLogger<ApiKeys>.Instance);

    private UserCatalog Users() =>
        new(_db, new Argon2Kdf(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1)), Keys(), new Connections(_db, new VaultCatalog(_db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance), _time, NullLogger<Connections>.Instance), _time, NullLogger<UserCatalog>.Instance);

    /// <summary>A second call is a second server process: nothing is shared but the database.</summary>
    private Invitations Invitations() => new(_db, new ServerSecret(_db), Users(), _time, NullLogger<Invitations>.Instance);

    private BootstrapAdmin Bootstrap() => new(_db, Invitations());

    private async Task<User> AdminAsync(string username = "anna")
    {
        var (user, _) = await Users().CreateAsync(username, Password, Roles.Admin, Ct);
        return user;
    }

    [Fact]
    public async Task A_new_invitation_is_32_random_bytes_and_only_its_keyed_hash_is_stored()
    {
        var admin = await AdminAsync();

        var (code, info) = await Invitations().CreateAsync(admin, Roles.User, "for Lea", null, Ct);

        Assert.Equal(32, Base64Url.DecodeFromChars(code).Length);
        Assert.Equal(Roles.User, info.Role);
        Assert.Equal("for Lea", info.Note);
        Assert.Equal(admin.Id, info.CreatedBy);
        Assert.Equal(_time.GetUtcNow().UtcDateTime + Kumbu.Server.Accounts.Invitations.DefaultExpiry, info.ExpiresAt);
        Assert.Equal(TimeSpan.FromDays(7), Kumbu.Server.Accounts.Invitations.DefaultExpiry);
        Assert.True(info.Valid);

        var doc = Assert.Single(await InvitationDocs.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct));
        Assert.DoesNotContain(code, doc.ToJson());
        Assert.Equal(32, doc["codeHash"].AsByteArray.Length);
        Assert.Equal(info.Id, doc["_id"].AsString);
    }

    [Fact]
    public async Task The_hash_key_comes_from_a_server_secret_kept_in_the_database_and_is_the_same_in_a_second_process()
    {
        var admin = await AdminAsync();
        var (code, _) = await Invitations().CreateAsync(admin, Roles.User, null, null, Ct);

        // A second process reads the same secret: the code still matches.
        Assert.NotNull(await Invitations().PeekAsync(code, Ct));
        var secrets = await _db.GetCollection<BsonDocument>("server").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct);
        Assert.Equal(32, Assert.Single(secrets)["secret"].AsByteArray.Length);
    }

    [Fact]
    public async Task Only_an_active_admin_may_invite_and_only_as_user_or_admin()
    {
        var admin = await AdminAsync();
        var (plain, _) = await Users().CreateAsync("bob", Password, Roles.User, Ct);

        await Assert.ThrowsAsync<NotPermittedException>(() => Invitations().CreateAsync(plain, Roles.User, null, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Invitations().CreateAsync(admin, Roles.PrimaryAdmin, null, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Invitations().CreateAsync(admin, "root", null, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Invitations().CreateAsync(admin, Roles.User, null, TimeSpan.Zero, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Invitations().CreateAsync(admin, Roles.User, null, TimeSpan.FromDays(31), Ct));

        // The role is read again from the database: a stale User of a demoted admin invites nobody.
        await _db.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == admin.Id, Builders<UserDocument>.Update.Set(u => u.Role, Roles.User), cancellationToken: Ct);
        await Assert.ThrowsAsync<NotPermittedException>(() => Invitations().CreateAsync(admin, Roles.User, null, null, Ct));
    }

    [Fact]
    public async Task Redeeming_creates_an_account_with_the_invitations_role_and_marks_it_used()
    {
        var admin = await AdminAsync();
        var (code, info) = await Invitations().CreateAsync(admin, Roles.Admin, null, null, Ct);

        var (user, recoveryKey) = await Invitations().RedeemAsync(code, "Lea", Password, Ct);

        Assert.Equal("lea", user.Username);
        Assert.Equal(Roles.Admin, user.Role);
        Assert.StartsWith("kumbu-rk-", recoveryKey);
        Assert.NotNull(await Users().TrySignInAsync("lea", Password, Ct));
        var used = (await Invitations().PeekAsync(code, Ct))!;
        Assert.Equal(InvitationState.Used, used.State);
        Assert.False(used.Valid);
        Assert.Equal(user.Id, used.UsedBy);
        Assert.Equal(info.Id, used.Id);
    }

    [Fact]
    public async Task A_second_redeem_is_refused_and_creates_no_account()
    {
        var admin = await AdminAsync();
        var (code, _) = await Invitations().CreateAsync(admin, Roles.User, null, null, Ct);
        await Invitations().RedeemAsync(code, "lea", Password, Ct);

        var refused = await Assert.ThrowsAsync<InvitationUnusableException>(() => Invitations().RedeemAsync(code, "mallory", Password, Ct));

        Assert.Equal(InvitationState.Used, refused.State);
        Assert.Null(await Users().TrySignInAsync("mallory", Password, Ct));
    }

    [Fact]
    public async Task An_expired_invitation_is_refused()
    {
        var admin = await AdminAsync();
        var (code, _) = await Invitations().CreateAsync(admin, Roles.User, null, TimeSpan.FromDays(1), Ct);
        _time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(InvitationState.Expired, (await Invitations().PeekAsync(code, Ct))!.State);
        var refused = await Assert.ThrowsAsync<InvitationUnusableException>(() => Invitations().RedeemAsync(code, "lea", Password, Ct));
        Assert.Equal(InvitationState.Expired, refused.State);
        Assert.Null(await Users().TrySignInAsync("lea", Password, Ct));
    }

    [Fact]
    public async Task A_revoked_invitation_is_refused_and_a_used_one_cant_be_revoked()
    {
        var admin = await AdminAsync();
        var (code, info) = await Invitations().CreateAsync(admin, Roles.User, null, null, Ct);
        var (usedCode, usedInfo) = await Invitations().CreateAsync(admin, Roles.User, null, null, Ct);
        await Invitations().RedeemAsync(usedCode, "lea", Password, Ct);

        Assert.True(await Invitations().RevokeAsync(admin, info.Id, Ct));
        Assert.False(await Invitations().RevokeAsync(admin, usedInfo.Id, Ct));

        Assert.Equal(InvitationState.Revoked, (await Invitations().PeekAsync(code, Ct))!.State);
        var refused = await Assert.ThrowsAsync<InvitationUnusableException>(() => Invitations().RedeemAsync(code, "bob", Password, Ct));
        Assert.Equal(InvitationState.Revoked, refused.State);
        Assert.Equal(InvitationState.Used, (await Invitations().PeekAsync(usedCode, Ct))!.State);
    }

    [Fact]
    public async Task An_open_invitation_whose_creator_is_no_longer_an_admin_is_refused_and_shown_as_revoked()
    {
        // As if the creator was deleted, deactivated or demoted just as the invitation was made: the revocation of
        // their open invitations missed it, and the redeem catches it.
        var admin = await AdminAsync();
        var lea = await AdminAsync("lea");
        var (code, info) = await Invitations().CreateAsync(lea, Roles.Admin, null, null, Ct);
        await _db.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == lea.Id, Builders<UserDocument>.Update.Set(u => u.Active, false), cancellationToken: Ct);

        Assert.Equal(InvitationState.Revoked, (await Invitations().PeekAsync(code, Ct))!.State);
        Assert.Equal(InvitationState.Revoked, (await Invitations().ListAsync(admin, Ct)).Single(i => i.Id == info.Id).State);
        var refused = await Assert.ThrowsAsync<InvitationUnusableException>(() => Invitations().RedeemAsync(code, "mallory", Password, Ct));

        Assert.Equal(InvitationState.Revoked, refused.State);
        Assert.Null(await Users().TrySignInAsync("mallory", Password, Ct));
        var doc = await InvitationDocs.Find(new BsonDocument("_id", info.Id)).SingleAsync(Ct);
        Assert.False(doc.Contains("usedAt"));
        Assert.True(doc.Contains("revokedAt"));
        // Active again, it stays revoked.
        await _db.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == lea.Id, Builders<UserDocument>.Update.Set(u => u.Active, true), cancellationToken: Ct);
        Assert.Equal(InvitationState.Revoked, (await Invitations().PeekAsync(code, Ct))!.State);
    }

    [Fact]
    public async Task Only_an_admin_may_revoke_or_list_invitations()
    {
        var admin = await AdminAsync();
        var (plain, _) = await Users().CreateAsync("bob", Password, Roles.User, Ct);
        var (_, info) = await Invitations().CreateAsync(admin, Roles.User, "for Lea", null, Ct);

        await Assert.ThrowsAsync<NotPermittedException>(() => Invitations().RevokeAsync(plain, info.Id, Ct));
        await Assert.ThrowsAsync<NotPermittedException>(() => Invitations().ListAsync(plain, Ct));

        var listed = Assert.Single(await Invitations().ListAsync(admin, Ct));
        Assert.Equal(info.Id, listed.Id);
        Assert.Equal("for Lea", listed.Note);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-code")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    public async Task An_unknown_or_malformed_code_peeks_as_nothing_and_is_refused(string code)
    {
        Assert.Null(await Invitations().PeekAsync(code, Ct));
        var refused = await Assert.ThrowsAsync<InvitationUnusableException>(() => Invitations().RedeemAsync(code, "lea", Password, Ct));
        Assert.Null(refused.State);
    }

    [Fact]
    public async Task A_taken_username_doesnt_use_up_the_invitation()
    {
        var admin = await AdminAsync();
        await Users().CreateAsync("lea", Password, Roles.User, Ct);
        var (code, _) = await Invitations().CreateAsync(admin, Roles.User, null, null, Ct);

        await Assert.ThrowsAsync<UsernameTakenException>(() => Invitations().RedeemAsync(code, "Lea", Password, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Invitations().RedeemAsync(code, "lea2", "short", Ct));

        Assert.True((await Invitations().PeekAsync(code, Ct))!.Valid);
        var (user, _) = await Invitations().RedeemAsync(code, "lea2", Password, Ct);
        Assert.Equal("lea2", user.Username);
    }

    /// <summary>Throws on the "Created user" log line, i.e. right after the user is inserted: a fault there
    /// (a dropped connection, a cancelled request) must not hand the invitation out a second time.</summary>
    private sealed class FailAfterInsert : Microsoft.Extensions.Logging.ILogger<UserCatalog>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new IOException("connection lost");
    }

    [Fact]
    public async Task A_fault_after_the_account_exists_leaves_the_invitation_used()
    {
        var admin = await AdminAsync();
        var (code, _) = await Invitations().CreateAsync(admin, Roles.Admin, null, null, Ct);
        var failing = new Invitations(
            _db, new ServerSecret(_db), new UserCatalog(_db, new Argon2Kdf(new Argon2Parameters(1024, 1, 1)), Keys(), new Connections(_db, new VaultCatalog(_db, Options.Create(new KumbuOptions()), _time, NullLogger<VaultCatalog>.Instance), _time, NullLogger<Connections>.Instance), _time, new FailAfterInsert()), _time, NullLogger<Invitations>.Instance);

        await Assert.ThrowsAsync<IOException>(() => failing.RedeemAsync(code, "lea", Password, Ct));

        Assert.NotNull(await Users().TrySignInAsync("lea", Password, Ct));
        Assert.Equal(InvitationState.Used, (await Invitations().PeekAsync(code, Ct))!.State);
        await Assert.ThrowsAsync<InvitationUnusableException>(() => Invitations().RedeemAsync(code, "mallory", Password, Ct));
    }

    [Fact]
    public async Task Two_concurrent_redeems_of_one_code_create_exactly_one_account()
    {
        var admin = await AdminAsync();
        var (code, _) = await Invitations().CreateAsync(admin, Roles.User, null, null, Ct);

        var attempts = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try
            {
                await Invitations().RedeemAsync(code, $"racer{i}", Password, Ct);
                return true;
            }
            catch (InvitationUnusableException)
            {
                return false;
            }
        }, Ct)).ToList();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, r => r);
        var accounts = await _db.GetCollection<UserDocument>("users").CountDocumentsAsync(u => u.Username.StartsWith("racer"), cancellationToken: Ct);
        Assert.Equal(1, accounts);
    }

    [Fact]
    public async Task Bootstrap_prints_a_join_link_whose_first_redeem_creates_the_primary_admin()
    {
        var link = await Bootstrap().RunAsync(BaseUrl + "/", Ct);

        // A fragment: the browser never sends it, so the code is in no request line or access log.
        Assert.StartsWith(BaseUrl + "/join#", link);
        var code = link[(BaseUrl + "/join#").Length..];
        Assert.Equal(Roles.PrimaryAdmin, (await Invitations().PeekAsync(code, Ct))!.Role);

        var (user, _) = await Invitations().RedeemAsync(code, "chris", Password, Ct);
        Assert.Equal(Roles.PrimaryAdmin, user.Role);
    }

    [Fact]
    public async Task Bootstrap_is_refused_once_a_primary_admin_exists()
    {
        var link = await Bootstrap().RunAsync(BaseUrl, Ct);
        await Invitations().RedeemAsync(link.Split('#')[^1], "chris", Password, Ct);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Bootstrap().RunAsync(BaseUrl, Ct));
        Assert.Contains("primary admin", refused.Message);
    }

    [Fact]
    public async Task Running_bootstrap_again_before_the_link_is_used_revokes_the_earlier_link()
    {
        var first = (await Bootstrap().RunAsync(BaseUrl, Ct)).Split('#')[^1];
        var second = (await Bootstrap().RunAsync(BaseUrl, Ct)).Split('#')[^1];

        Assert.Equal(InvitationState.Revoked, (await Invitations().PeekAsync(first, Ct))!.State);
        Assert.True((await Invitations().PeekAsync(second, Ct))!.Valid);
    }

    [Fact]
    public async Task There_is_never_a_second_primary_admin_even_from_two_bootstrap_links()
    {
        // Two links open at once (two bootstraps racing past the revocation): the unique index decides.
        var first = (await Bootstrap().RunAsync(BaseUrl, Ct)).Split('#')[^1];
        await InvitationDocs.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Unset("revokedAt"), cancellationToken: Ct);
        var second = (await Bootstrap().RunAsync(BaseUrl, Ct)).Split('#')[^1];
        await InvitationDocs.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Unset("revokedAt"), cancellationToken: Ct);

        await Invitations().RedeemAsync(first, "chris", Password, Ct);
        // Redeeming revoked the other link too; undo that as well, to reach the index.
        await InvitationDocs.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Unset("revokedAt"), cancellationToken: Ct);

        await Assert.ThrowsAsync<PrimaryAdminExistsException>(() => Invitations().RedeemAsync(second, "eve", Password, Ct));
        Assert.Equal(1, await _db.GetCollection<UserDocument>("users").CountDocumentsAsync(u => u.Role == Roles.PrimaryAdmin, cancellationToken: Ct));
        Assert.Null(await Users().TrySignInAsync("eve", Password, Ct));
    }

    [Fact]
    public async Task Creating_the_primary_admin_revokes_every_other_open_bootstrap_link()
    {
        var first = (await Bootstrap().RunAsync(BaseUrl, Ct)).Split('#')[^1];
        await InvitationDocs.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Unset("revokedAt"), cancellationToken: Ct);
        var second = (await Bootstrap().RunAsync(BaseUrl, Ct)).Split('#')[^1];
        await InvitationDocs.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Unset("revokedAt"), cancellationToken: Ct);

        await Invitations().RedeemAsync(second, "chris", Password, Ct);

        Assert.Equal(InvitationState.Revoked, (await Invitations().PeekAsync(first, Ct))!.State);
        Assert.Equal(InvitationState.Used, (await Invitations().PeekAsync(second, Ct))!.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task The_command_fails_with_a_clear_message_without_a_public_url(string? publicUrl)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await BootstrapAdmin.ExecuteAsync(Bootstrap(), publicUrl, stdout, stderr, Ct);

        Assert.NotEqual(0, exit);
        Assert.Equal("", stdout.ToString());
        Assert.Contains("KUMBU_PUBLIC_URL", stderr.ToString());
    }

    [Fact]
    public async Task The_command_prints_only_the_link_to_stdout()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await BootstrapAdmin.ExecuteAsync(Bootstrap(), BaseUrl, stdout, stderr, Ct);

        Assert.Equal(0, exit);
        Assert.Matches(@"^https://kumbu\.example\.org/join#[A-Za-z0-9_-]{43}\r?\n$", stdout.ToString());
        Assert.DoesNotContain("/join#", stderr.ToString());
    }
}
