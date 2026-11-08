// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Skills;
using Kumbu.Server.Sync;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Skills;

/// <summary>A vault or an alias the user already has under the name Skills, in any spelling, makes room for the vault
/// Skills: renamed once, its grants kept, and a notice saying which until she dismisses it.</summary>
[Collection("app")]
public sealed class SkillsVaultClashTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// A user who had a vault of this name before the upgrade, with a note and a read key on it, not yet signed in.
    private async Task<(string Username, User User, byte[] Key, string ApiKey)> BeforeUpgradeAsync(string name)
    {
        var users = app.PublicApi.Services.GetRequiredService<UserCatalog>();
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, RestClient.Password, Roles.User, Ct);
        var (user, key, readAt) = (await users.TrySignInAsync(username, RestClient.Password, Ct))!.Value;
        var catalog = app.PublicApi.Services.GetRequiredService<VaultCatalog>();
        var vault = await catalog.CreateForUserAsync(name, user.Id, key, Ct, readAt);
        var store = new EncryptedNoteStore(catalog, new UserCredential(user.Id, key));
        Assert.True(await store.TryCreateAsync(new StoredNote(VaultPath.Root.Child(name).Child("cv.md"), "Alice's CV\n", 1, "alice", DateTime.UtcNow, [], "Alice's CV\n", Ref: WriterRef.ForOwner(WriterRef.Session, null)), Ct));
        var (apiKey, _) = await app.PublicApi.Services.GetRequiredService<ApiKeys>().CreateAsync(user, key, DateTime.UtcNow, "Claude Code", new Dictionary<string, KeyRight> { [vault.Id] = KeyRight.Read }, Ct);
        return (username, user, key, apiKey);
    }

    private async Task<JsonNode> SignInAsync(string username) => (await SignInWithSessionAsync(username)).View;

    private async Task<(JsonNode View, string Session)> SignInWithSessionAsync(string username)
    {
        using var answer = await RestClient.SendAsync(app.PublicApi, HttpMethod.Post, "/api/session", new { username, password = RestClient.Password }, session: null, bearer: null);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var cookie = answer.Headers.GetValues("Set-Cookie").First(v => v.StartsWith(CallerMiddleware.SessionCookie + "=", StringComparison.Ordinal));
        return (await RestClient.JsonAsync(answer), cookie[(CallerMiddleware.SessionCookie.Length + 1)..].Split(';')[0]);
    }

    private Task<DateTime?> SkillsAtAsync(string userId) =>
        app.Database.GetCollection<UserDocument>("users").Find(u => u.Id == userId).Project(u => u.SkillsAt).SingleAsync(Ct);

    [Fact]
    public async Task An_own_vault_named_Skills_is_renamed_and_keeps_its_grants()
    {
        var (username, user, key, apiKey) = await BeforeUpgradeAsync("Skills");
        var view = await SignInAsync(username);
        Assert.Equal("Skills (renamed)", view["skillsNotice"]!["renamed"]!.GetValue<string>());
        Assert.False(view["skillsNotice"]!["alias"]!.GetValue<bool>());
        var opened = await app.PublicApi.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.Id, key, Ct);
        Assert.Equal(VaultKinds.Skills, opened.Single(a => a.Name == "Skills").Vault.Kind);
        Assert.Null(opened.Single(a => a.Name == "Skills (renamed)").Vault.Kind);
        var alice = (await RestClient.NewAsync(app.PublicApi)).On(app.PublicApi);       // any client, for ShAsync on this factory
        Assert.Equal("Skills (renamed)/", (await alice.ShAsync("ls /", apiKey)).Text);
        Assert.Contains("Alice's CV", (await alice.ShAsync("cat \"/Skills (renamed)/cv.md\"", apiKey)).Text);
        Assert.True((await alice.ShAsync("ls /Skills", apiKey)).IsError);
    }

    [Fact]
    public async Task The_sockets_on_the_renamed_vault_are_told()
    {
        var (username, user, key, apiKey) = await BeforeUpgradeAsync("Skills");
        var vaultId = (await app.PublicApi.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.Id, key, Ct)).Single().Id;
        var anyone = await RestClient.NewAsync(app.PublicApi);
        using var socket = await SyncSockets.OpenAsync(app.PublicApi, anyone, apiKey);
        await HeldAsync(h => h.Subject.User == user.Id && h.VaultId == vaultId && h.Name == "Skills");

        await SignInAsync(username);

        var renamed = new JsonObject { ["type"] = "renamed", ["vault"] = "Skills (renamed)", ["from"] = "Skills" }.ToJsonString();
        Assert.Contains(renamed, await SyncSockets.DrainAsync(socket, TimeSpan.FromSeconds(1.5)));
    }

    [Theory, InlineData("SKILLS"), InlineData("skills"), InlineData(" Skills "), InlineData("SkIlLs")]
    public async Task A_vault_named_SKILLS_in_any_spelling_is_a_clash(string name)
    {
        var (username, user, key, _) = await BeforeUpgradeAsync(name);
        Assert.Equal("Skills (renamed)", (await SignInAsync(username))["skillsNotice"]!["renamed"]!.GetValue<string>());
        Assert.Contains(await app.PublicApi.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.Id, key, Ct), a => a.Name == "Skills" && a.Vault.Kind == VaultKinds.Skills);
    }

    [Fact]
    public async Task Skills_renamed_taken_too_becomes_renamed_2()
    {
        var (username, user, key, _) = await BeforeUpgradeAsync("Skills");
        var catalog = app.PublicApi.Services.GetRequiredService<VaultCatalog>();
        await catalog.CreateForUserAsync("skills (RENAMED)", user.Id, key, Ct);
        Assert.Equal("Skills (renamed 2)", (await SignInAsync(username))["skillsNotice"]!["renamed"]!.GetValue<string>());
        var names = (await catalog.OpenForUserAsync(user.Id, key, Ct)).Select(a => a.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Skills", "Skills (renamed 2)", "skills (RENAMED)"], names);
    }

    [Fact]
    public async Task A_member_alias_named_Skills_is_renamed()
    {
        var (alice, bob, membership) = await AliasNamedSkillsAsync();
        var catalog = app.PublicApi.Services.GetRequiredService<VaultCatalog>();
        var view = await SignInAsync(bob.Username);
        Assert.Equal("Skills (renamed)", view["skillsNotice"]!["renamed"]!.GetValue<string>());
        Assert.True(view["skillsNotice"]!["alias"]!.GetValue<bool>());
        var bobs = await catalog.OpenForUserAsync(bob.User.Id, bob.UserKey, Ct);
        Assert.Contains(bobs, a => a.Name == "Skills (renamed)" && a.Membership?.Id == membership);
        Assert.Contains(bobs, a => a.Name == "Skills" && a.Vault.Kind == VaultKinds.Skills);
        Assert.Contains(await catalog.OpenForUserAsync(alice.User.Id, alice.UserKey, Ct), a => a.Name == "Work");
    }

    [Fact]
    public async Task The_sockets_on_the_renamed_alias_name_it_so()
    {
        var (alice, bob, _) = await AliasNamedSkillsAsync();
        var work = await alice.VaultIdAsync();
        using var socket = await SyncSockets.OpenAsync(app.PublicApi, bob);
        await HeldAsync(h => h.Subject.User == bob.User.Id && h.VaultId == work && h.Name == "Skills");

        await SignInAsync(bob.Username);

        Assert.Equal("Skills (renamed)", Assert.Single(Hub.Held, h => h.Subject.User == bob.User.Id && h.VaultId == work).Name);
    }

    [Fact]
    public async Task The_rename_drops_open_pairing_codes()
    {
        var (username, user, key, _) = await BeforeUpgradeAsync("Skills");
        var catalog = app.PublicApi.Services.GetRequiredService<VaultCatalog>();
        var vault = (await catalog.OpenForUserAsync(user.Id, key, Ct)).Single();
        var secret = await app.PublicApi.Services.GetRequiredService<Sessions>().StartAsync(user, key, DateTime.UtcNow, Ct);
        var pairings = app.PublicApi.Services.GetRequiredService<Pairings>();
        var code = pairings.Create(user, (byte[])key.Clone(), DateTime.UtcNow, Sessions.SubjectOf(secret)!, vault.Id, vault.Name, null);
        Assert.True(pairings.IsOpen(code.Code));
        await SignInAsync(username);
        Assert.False(pairings.IsOpen(code.Code));
        Assert.Null(await pairings.RedeemAsync(code.Code, "Laptop", Ct));
    }

    [Fact]
    public async Task A_vault_being_rekeyed_is_left_for_next_time()
    {
        var (username, user, key, _) = await BeforeUpgradeAsync("Skills");
        var catalog = app.PublicApi.Services.GetRequiredService<VaultCatalog>();
        var old = (await catalog.OpenForUserAsync(user.Id, key, Ct)).Single().Id;
        var vaults = app.Database.GetCollection<VaultDocument>("vaults");
        await vaults.UpdateOneAsync(v => v.Id == old, Builders<VaultDocument>.Update.Set(v => v.RekeyStartedAt, DateTime.UtcNow), cancellationToken: Ct);
        // The sign-in would resume the run this mark stands for, which would hold the vault past the test's unmarking.
        var rekeying = app.PublicApi.Services.GetRequiredService<Rekeying>();
        rekeying.ResumeFault = () => throw new InvalidOperationException("left to the test");
        try
        {
            Assert.Null((await SignInAsync(username))["skillsNotice"]);
        }
        finally
        {
            rekeying.ResumeFault = null;
        }

        Assert.Null((await app.Database.GetCollection<UserDocument>("users").Find(u => u.Id == user.Id).SingleAsync(Ct)).SkillsAt);
        Assert.Equal("Skills", (await app.PublicApi.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.Id, key, Ct)).Single().Name);
        await vaults.UpdateOneAsync(v => v.Id == old, Builders<VaultDocument>.Update.Unset(v => v.RekeyStartedAt), cancellationToken: Ct);
        Assert.Equal("Skills (renamed)", (await SignInAsync(username))["skillsNotice"]!["renamed"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_alias_of_a_vault_being_rekeyed_is_left_for_next_time()
    {
        var (alice, bob, membership) = await AliasNamedSkillsAsync();
        var catalog = app.PublicApi.Services.GetRequiredService<VaultCatalog>();
        var work = await alice.VaultIdAsync();
        var vaults = app.Database.GetCollection<VaultDocument>("vaults");
        await vaults.UpdateOneAsync(v => v.Id == work, Builders<VaultDocument>.Update.Set(v => v.RekeyStartedAt, DateTime.UtcNow), cancellationToken: Ct);

        Assert.Null((await SignInAsync(bob.Username))["skillsNotice"]);
        var bobs = await catalog.OpenForUserAsync(bob.User.Id, bob.UserKey, Ct);
        Assert.Contains(bobs, a => a.Name == "Skills" && a.Membership?.Id == membership);
        Assert.DoesNotContain(bobs, a => a.Vault.Kind == VaultKinds.Skills);
        Assert.Null(await SkillsAtAsync(bob.User.Id));

        await vaults.UpdateOneAsync(v => v.Id == work, Builders<VaultDocument>.Update.Unset(v => v.RekeyStartedAt), cancellationToken: Ct);
        var view = await SignInAsync(bob.Username);
        Assert.Equal("Skills (renamed)", view["skillsNotice"]!["renamed"]!.GetValue<string>());
        Assert.True(view["skillsNotice"]!["alias"]!.GetValue<bool>());
        bobs = await catalog.OpenForUserAsync(bob.User.Id, bob.UserKey, Ct);
        Assert.Contains(bobs, a => a.Name == "Skills (renamed)" && a.Membership?.Id == membership);
        Assert.Contains(bobs, a => a.Name == "Skills" && a.Vault.Kind == VaultKinds.Skills);
        Assert.NotNull(await SkillsAtAsync(bob.User.Id));
    }

    [Fact]
    public async Task The_notice_is_shown_until_dismissed()
    {
        var (username, _, _, _) = await BeforeUpgradeAsync("Skills");
        var (_, session) = await SignInWithSessionAsync(username);
        using (var shown = await RestClient.SendAsync(app.PublicApi, HttpMethod.Get, "/api/session", null, session, bearer: null))
        {
            Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
            Assert.Equal("Skills (renamed)", (await RestClient.JsonAsync(shown))["skillsNotice"]!["renamed"]!.GetValue<string>());
        }

        using (var dismissed = await RestClient.SendAsync(app.PublicApi, HttpMethod.Delete, "/api/account/skills-notice", null, session, bearer: null))
        {
            Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        }

        using var after = await RestClient.SendAsync(app.PublicApi, HttpMethod.Get, "/api/session", null, session, bearer: null);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Null((await RestClient.JsonAsync(after))["skillsNotice"]);
        Assert.Null((await SignInAsync(username))["skillsNotice"]);
    }

    [Fact]
    public async Task A_rename_whose_answer_is_lost_after_it_landed_still_leaves_the_notice()
    {
        var (username, user, key, _) = await BeforeUpgradeAsync("Skills");
        var catalog = app.PublicApi.Services.GetRequiredService<VaultCatalog>();
        catalog.MetaFaultAt = point => point == "after-rename-commit" ? throw new InvalidOperationException("The answer was lost.") : Task.CompletedTask;
        try
        {
            Assert.Equal("Skills (renamed)", (await SignInAsync(username))["skillsNotice"]!["renamed"]!.GetValue<string>());
        }
        finally
        {
            catalog.MetaFaultAt = null;
        }

        Assert.Equal(["Skills (renamed)"], (await catalog.OpenForUserAsync(user.Id, key, Ct)).Select(a => a.Name));
        Assert.Null(await SkillsAtAsync(user.Id));
        // The next attempt meets no clash: it creates the vault Skills, and the notice stays.
        var view = await SignInAsync(username);
        Assert.Equal("Skills (renamed)", view["skillsNotice"]!["renamed"]!.GetValue<string>());
        Assert.False(view["skillsNotice"]!["alias"]!.GetValue<bool>());
        Assert.Contains(await catalog.OpenForUserAsync(user.Id, key, Ct), a => a.Name == "Skills" && a.Vault.Kind == VaultKinds.Skills);
    }

    [Fact]
    public async Task A_refused_retry_keeps_the_notice_of_the_attempt_before()
    {
        var (username, user, key, _) = await BeforeUpgradeAsync("Skills");
        var catalog = app.PublicApi.Services.GetRequiredService<VaultCatalog>();
        var old = (await catalog.OpenForUserAsync(user.Id, key, Ct)).Single().Id;
        // First attempt: the rename's outcome isn't known (here it didn't land), so its notice stays.
        catalog.MetaFaultAt = point => point == "inside-rename" ? throw new InvalidOperationException("The answer was lost.") : Task.CompletedTask;
        try
        {
            Assert.Equal("Skills (renamed)", (await SignInAsync(username))["skillsNotice"]!["renamed"]!.GetValue<string>());
        }
        finally
        {
            catalog.MetaFaultAt = null;
        }

        Assert.Equal(["Skills"], (await catalog.OpenForUserAsync(user.Id, key, Ct)).Select(a => a.Name));

        // Second attempt: refused while the vault is re-keyed; what the first left is kept, not dropped.
        var vaults = app.Database.GetCollection<VaultDocument>("vaults");
        await vaults.UpdateOneAsync(v => v.Id == old, Builders<VaultDocument>.Update.Set(v => v.RekeyStartedAt, DateTime.UtcNow), cancellationToken: Ct);
        var rekeying = app.PublicApi.Services.GetRequiredService<Rekeying>();
        rekeying.ResumeFault = () => throw new InvalidOperationException("left to the test");
        try
        {
            Assert.Equal("Skills (renamed)", (await SignInAsync(username))["skillsNotice"]!["renamed"]!.GetValue<string>());
        }
        finally
        {
            rekeying.ResumeFault = null;
        }

        await vaults.UpdateOneAsync(v => v.Id == old, Builders<VaultDocument>.Update.Unset(v => v.RekeyStartedAt), cancellationToken: Ct);
        Assert.Equal("Skills (renamed)", (await SignInAsync(username))["skillsNotice"]!["renamed"]!.GetValue<string>());
        Assert.Equal(["Skills", "Skills (renamed)"], (await catalog.OpenForUserAsync(user.Id, key, Ct)).Select(a => a.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_notice_that_doesnt_open_is_no_notice()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        // Sealed under another key, as after a rotation of her user key.
        var sealedElsewhere = VaultCrypto.Seal(VaultCrypto.Derive(VaultCrypto.NewKey(), "kumbu/v1/skills-notice", Encoding.UTF8.GetBytes(alice.User.Id)),
            "{\"NewName\":\"Skills (renamed)\",\"Alias\":false}"u8, $"skills-notice:{alice.User.Id}");
        await SetNoticeAsync(alice.User.Id, sealedElsewhere);
        await NoNoticeAsync(alice);
    }

    [Fact]
    public async Task A_notice_that_cant_be_read_is_no_notice_and_fails_nothing()
    {
        var log = new CapturedLog();
        await using var api = app.PublicApi.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var alice = await RestClient.NewAsync(api, vault: "Work");
        // It opens, but isn't a notice.
        var notJson = VaultCrypto.Seal(VaultCrypto.Derive(alice.UserKey, "kumbu/v1/skills-notice", Encoding.UTF8.GetBytes(alice.User.Id)), "Skills (renamed"u8, $"skills-notice:{alice.User.Id}");
        await SetNoticeAsync(alice.User.Id, notJson);
        await NoNoticeAsync(alice);
        Assert.Contains(log.Lines, l => l.Contains($"Could not read the notice of the vault Skills for user {alice.User.Id}: JsonException"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("renamed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Nothing_names_the_old_vault_in_the_clear()
    {
        var (username, user, _, _) = await BeforeUpgradeAsync("Skills");
        await SignInAsync(username);
        var raw = await app.Database.GetCollection<BsonDocument>("users").Find(new BsonDocument("_id", user.Id)).SingleAsync(Ct);
        Assert.DoesNotContain("renamed", raw.ToJson(), StringComparison.OrdinalIgnoreCase);
        // The JSON shows bytes as base64: the notice's own bytes too.
        Assert.DoesNotContain("renamed", System.Text.Encoding.UTF8.GetString(raw["skillsNotice"].AsByteArray), StringComparison.OrdinalIgnoreCase);
    }

    private SyncHub Hub => app.PublicApi.Services.GetRequiredService<SyncHub>();

    private async Task HeldAsync(Func<(Subject Subject, string VaultId, string Name, AccessLevel Level), bool> which)
    {
        var waiting = System.Diagnostics.Stopwatch.StartNew();
        while (!Hub.Held.Any(which))
        {
            Assert.True(waiting.Elapsed < TimeSpan.FromSeconds(10), "The hub doesn't hold the connection.");
            await Task.Delay(20, Ct);
        }
    }

    private Task SetNoticeAsync(string userId, byte[] notice) =>
        app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == userId, Builders<UserDocument>.Update.Set(u => u.SkillsNotice, notice), cancellationToken: Ct);

    /// <summary>The session and a new sign-in both answer, neither with a notice.</summary>
    private async Task NoNoticeAsync(RestClient user)
    {
        using (var session = await user.GetAsync("/api/session"))
        {
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
            Assert.Null((await RestClient.JsonAsync(session))["skillsNotice"]);
        }

        Assert.Null((await SignInAsync(user.Username))["skillsNotice"]);
    }

    /// <summary>Alice's Work shared with Bob, who named it Skills before the upgrade, so he has no vault Skills yet.</summary>
    private async Task<(RestClient Alice, RestClient Bob, string Membership)> AliasNamedSkillsAsync()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Private");
        var membership = await alice.ShareWithAsync(bob, alias: "Garden");
        var catalog = app.PublicApi.Services.GetRequiredService<VaultCatalog>();
        var bobsSkills = (await catalog.OpenForUserAsync(bob.User.Id, bob.UserKey, Ct)).Single(a => a.Vault.Kind == VaultKinds.Skills);
        await catalog.DeleteVaultsAsync([bobsSkills.Id], (_, _) => Task.CompletedTask, Ct);
        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == bob.User.Id, Builders<UserDocument>.Update.Unset(u => u.SkillsAt), cancellationToken: Ct);
        app.PublicApi.Services.GetRequiredService<SkillsVaults>().Forget(bob.User.Id);
        await catalog.RenameAliasAsync(bob.User.Id, bob.UserKey, membership, "Skills", Ct);
        return (alice, bob, membership);
    }
}
