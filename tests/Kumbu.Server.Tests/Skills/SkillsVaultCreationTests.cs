// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Skills;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Skills;

/// <summary>Every user gets the vault Skills, at join, at sign-in and on <c>GET /api/session</c>: once, best effort, and
/// without any existing key gaining it.</summary>
[Collection("app")]
public sealed class SkillsVaultCreationTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMongoDatabase Db => app.Database;

    private async Task<IReadOnlyList<VaultAccess>> MarkedAsync(RestClient user) =>
        [.. (await app.PublicApi.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Where(a => a.Vault.Kind == VaultKinds.Skills)];

    [Fact]
    public async Task Sign_in_creates_the_vault_Skills()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var vaults = (await RestClient.JsonAsync(await alice.GetAsync("/api/vaults"))).AsArray();
        Assert.Contains(vaults, v => v!["name"]!.GetValue<string>() == "Skills" && v["kind"]?.GetValue<string>() == "skills");
        Assert.Single(await MarkedAsync(alice));
    }

    [Fact]
    public async Task Signing_in_again_keeps_one_vault_Skills()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await alice.SignInAgainAsync();
        app.PublicApi.Services.GetRequiredService<SkillsVaults>().Forget(alice.User.Id);
        await alice.SignInAgainAsync();
        Assert.Single(await MarkedAsync(alice));
    }

    [Fact]
    public async Task Two_sign_ins_at_once_make_one_vault_Skills()
    {
        var users = app.PublicApi.Services.GetRequiredService<UserCatalog>();
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, RestClient.Password, Roles.User, Ct);
        var body = new { username, password = RestClient.Password };
        var answers = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => RestClient.SendAsync(app.PublicApi, HttpMethod.Post, "/api/session", body, session: null, bearer: null)));
        Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
        var (user, key, _) = (await users.TrySignInAsync(username, RestClient.Password, Ct))!.Value;
        var marked = (await app.PublicApi.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.Id, key, Ct)).Count(a => a.Vault.Kind == VaultKinds.Skills);
        Assert.Equal(1, marked);
    }

    [Fact]
    public async Task Calls_at_once_for_one_user_create_once_and_none_fails()
    {
        var log = new CapturedLog();
        await using var api = app.PublicApi.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var (user, key, readAt) = await SignedInAsync(api);
        var skills = api.Services.GetRequiredService<SkillsVaults>();
        var calls = 0;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        skills.BeforeCreate = async token =>
        {
            // The first to get here gives the others a moment to come in beside it, as they would without the per-user gate.
            if (Interlocked.Increment(ref calls) > 1) second.TrySetResult();
            else await second.Task.WaitAsync(TimeSpan.FromMilliseconds(250), token).ContinueWith(_ => { }, TaskScheduler.Default);
        };

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => skills.EnsureAsync(user, key, readAt, Ct), Ct)));

        Assert.All(results, Assert.True);
        Assert.Equal(1, calls);
        Assert.Equal(1, (await api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.Id, key, Ct)).Count(a => a.Vault.Kind == VaultKinds.Skills));
        Assert.DoesNotContain(log.Lines, l => l.Contains("Could not create the vault Skills"));
    }

    [Fact]
    public async Task Another_process_that_wins_the_name_counts_as_done()
    {
        var log = new CapturedLog();
        await using var api = app.PublicApi.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var (user, key, readAt) = await SignedInAsync(api);
        var skills = api.Services.GetRequiredService<SkillsVaults>();
        var catalog = api.Services.GetRequiredService<VaultCatalog>();
        // Between this process's look and its create, another one creates the vault: the create meets the taken name.
        skills.BeforeCreate = async token => await catalog.CreateForUserAsync(SkillsVaults.Name, user.Id, key, token, readAt, kind: VaultKinds.Skills);

        Assert.True(await skills.EnsureAsync(user, key, readAt, Ct));
        Assert.Equal(1, (await catalog.OpenForUserAsync(user.Id, key, Ct)).Count(a => a.Vault.Kind == VaultKinds.Skills));
        Assert.DoesNotContain(log.Lines, l => l.Contains("Could not create the vault Skills"));
    }

    [Fact]
    public async Task Join_creates_Welcome_and_Skills()
    {
        var adminName = "u" + Guid.NewGuid().ToString("N")[..12];
        await app.PublicApi.Services.GetRequiredService<UserCatalog>().CreateAsync(adminName, RestClient.Password, Roles.Admin, Ct);
        using var signIn = await SendAsync(HttpMethod.Post, "/api/session", new { username = adminName, password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        using var invitation = await SendAsync(HttpMethod.Post, "/api/admin/invitations", new { role = "user" }, SessionOf(signIn));
        Assert.Equal(HttpStatusCode.Created, invitation.StatusCode);
        var code = (await RestClient.JsonAsync(invitation))["code"]!.GetValue<string>();

        using var join = await SendAsync(HttpMethod.Post, "/api/join", new { code, username = "u" + Guid.NewGuid().ToString("N")[..12], password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        using var list = await SendAsync(HttpMethod.Get, "/api/vaults", session: SessionOf(join));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var names = (await RestClient.JsonAsync(list)).AsArray().Select(v => v!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Welcome", names);
        Assert.Contains("Skills", names);
    }

    [Fact]
    public async Task A_kept_session_gets_it_on_GET_session()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work", remember: true);
        var catalog = app.PublicApi.Services.GetRequiredService<VaultCatalog>();
        await catalog.DeleteVaultsAsync([(await MarkedAsync(alice)).Single().Id], (_, _) => Task.CompletedTask, Ct);
        await Db.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == alice.User.Id, Builders<UserDocument>.Update.Unset(u => u.SkillsAt), cancellationToken: Ct);
        app.PublicApi.Services.GetRequiredService<SkillsVaults>().Forget(alice.User.Id);
        using var session = await alice.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Single(await MarkedAsync(alice));
    }

    [Fact]
    public async Task Sign_in_never_fails_when_creating_Skills_fails()
    {
        var log = new CapturedLog();
        await using var api = app.PublicApi.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        api.Services.GetRequiredService<SkillsVaults>().BeforeCreate = _ => throw new InvalidOperationException("Skills");
        var alice = await RestClient.NewAsync(api, vault: "Work");                       // asserts 200 itself
        Assert.Empty(await MarkedAsync(alice.On(app.PublicApi)));
        Assert.Contains(log.Lines, l => l.Contains($"Could not create the vault Skills for user {alice.User.Id}: InvalidOperationException"));
        Assert.DoesNotContain(log.Lines, l => l.Contains("Work"));
        api.Services.GetRequiredService<SkillsVaults>().BeforeCreate = null;
        await alice.SignInAgainAsync();
        Assert.Single(await MarkedAsync(alice.On(app.PublicApi)));
    }

    [Fact]
    public async Task No_existing_key_gains_the_vault_Skills()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        var key = await alice.KeyAsync("Claude Code", KeyRight.Write);
        Assert.Equal("Work/", (await alice.ShAsync("ls /", key)).Text);
        Assert.True((await alice.ShAsync("ls /Skills", key)).IsError);
    }

    [Fact]
    public async Task A_key_and_a_connection_made_before_the_vault_Skills_dont_gain_it()
    {
        // A user from before the vault Skills: a vault, a key and a connection, and no sign-in yet.
        var (user, userKey, readAt) = await SignedInAsync(app.PublicApi);
        var catalog = app.PublicApi.Services.GetRequiredService<VaultCatalog>();
        var work = await catalog.CreateForUserAsync("Work", user.Id, userKey, Ct, readAt);
        var rights = new Dictionary<string, KeyRight> { [work.Id] = KeyRight.Write };
        var (key, _) = await app.PublicApi.Services.GetRequiredService<ApiKeys>().CreateAsync(user, userKey, readAt, "Claude Code", rights, Ct);
        await app.PublicApi.Services.GetRequiredService<Connections>().CreateAsync(user, userKey, readAt, "Claude", rights, Ct);
        Assert.DoesNotContain(await catalog.OpenForUserAsync(user.Id, userKey, Ct), a => a.Vault.Kind == VaultKinds.Skills);

        using var signIn = await SendAsync(HttpMethod.Post, "/api/session", new { username = user.Username, password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        var skills = (await catalog.OpenForUserAsync(user.Id, userKey, Ct)).Single(a => a.Vault.Kind == VaultKinds.Skills);
        // Its key is wrapped for the user key alone: no slot of the key, the connection or anything else.
        Assert.Equal([SlotDocument.User], (await catalog.Slots.Find(s => s.Vault == skills.Id).ToListAsync(Ct)).Select(s => s.Kind));
        var alice = RestClient.Existing(app.PublicApi, user, userKey, SessionOf(signIn), "Work");
        Assert.Equal("Work/", (await alice.ShAsync("ls /", key)).Text);
        Assert.True((await alice.ShAsync("ls /Skills", key)).IsError);
    }

    /// <summary>A new user and her user key, signed in through the catalog only, so nothing has made her vault Skills yet.</summary>
    private static async Task<(User User, byte[] Key, DateTime ReadAt)> SignedInAsync(WebApplicationFactory<Program> api)
    {
        var users = api.Services.GetRequiredService<UserCatalog>();
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await users.CreateAsync(username, RestClient.Password, Roles.User, Ct);
        return (await users.TrySignInAsync(username, RestClient.Password, Ct))!.Value;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? session = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (session is not null) request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={session}");
        request.Headers.Add("Origin", RestClient.Origin);
        request.Headers.Add("X-Forwarded-For", $"198.22.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        using var client = app.PublicApi.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }

    private static string SessionOf(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").First(v => v.StartsWith(CallerMiddleware.SessionCookie + "=", StringComparison.Ordinal));
        return cookie[(CallerMiddleware.SessionCookie.Length + 1)..].Split(';')[0];
    }
}
