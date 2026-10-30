// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.OAuth;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.OAuth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>The triggers of re-keying (rekeying design §4): a recovery, "Re-encrypt my vaults" in Settings, and one
/// vault on request; and what sign-in resumes.</summary>
[Collection("app")]
public sealed class RekeyingTests(KumbuAppFactory app)
{
    private const string NewPassword = "another horse, another staple";
    private const string Callback = "https://claude.test/cb";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    private IMongoCollection<BsonDocument> Raw(string collection) => app.Database.GetCollection<BsonDocument>(collection);

    private static string Address() => $"198.19.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    /// <summary>A new user with the vault "Arbeit" and one more for each name, each with a note.</summary>
    private async Task<(RestClient Owner, Dictionary<string, string> Vaults)> OwnerAsync(params string[] more)
    {
        var owner = await RestClient.NewAsync(Api);
        await owner.CreateAsync("plan.md", "# Plan\n");
        foreach (var name in more)
        {
            using var created = await owner.SendAsync(HttpMethod.Post, "/api/vaults", new { name });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var note = await owner.SendAsync(HttpMethod.Put, $"/api/notes/{name}/plan.md", new { content = "# Plan\n" });
            Assert.Equal(HttpStatusCode.Created, note.StatusCode);
        }

        var ids = (await Catalog.OpenForUserAsync(owner.User.Id, owner.UserKey, Ct)).ToDictionary(v => v.Name, v => v.Id);
        return (owner, ids);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object body, string? session = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent(body) };
        request.Headers.Add("Origin", KumbuAppFactory.PublicUrl);
        request.Headers.Add("X-Forwarded-For", Address());
        if (session is not null) request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={session}");
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }

    private static StringContent JsonContent(object body) => new(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string SessionOf(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").First(v => v.StartsWith(CallerMiddleware.SessionCookie + "=", StringComparison.Ordinal));
        return cookie[(CallerMiddleware.SessionCookie.Length + 1)..].Split(';')[0];
    }

    private async Task<byte[]> VaultKeyAsync(byte[] userKey, string vaultId)
    {
        var slot = await Catalog.Slots.Find(s => s.Vault == vaultId && s.Kind == SlotDocument.User).SingleAsync(Ct);
        return VaultCrypto.Open(VaultCrypto.Derive(userKey, "kumbu/v1/user-slot-kek", Encoding.UTF8.GetBytes(vaultId)), slot.Wrapped, $"slot:{vaultId}:{SlotDocument.User}");
    }

    private async Task<bool> MetaOpensAsync(byte[] vaultKey, string vaultId)
    {
        var meta = (await Raw("vaults").Find(new BsonDocument("_id", vaultId)).SingleAsync(Ct))["meta"].AsByteArray;
        try
        {
            VaultCrypto.Open(VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), meta, $"vault:{vaultId}");
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private async Task<string[]> RekeyPendingAsync(string userId)
    {
        var user = await Raw("users").Find(new BsonDocument("_id", userId)).SingleAsync(Ct);
        return [.. user.GetValue("rekeyPending", new BsonArray()).AsBsonArray.Select(v => v.AsString).Order(StringComparer.Ordinal)];
    }

    private Task<bool> StartedAsync(IEnumerable<string> vaultIds) =>
        Raw("vaults").Find(new BsonDocument { ["_id"] = new BsonDocument("$in", new BsonArray(vaultIds)), ["rekeyStartedAt"] = new BsonDocument("$exists", true) }).AnyAsync(Ct);

    /// <summary>Waits until no vault of the user is pending: none in <c>rekeyPending</c>, none in its two-key period.</summary>
    private async Task AllRekeyedAsync(string userId, IEnumerable<string> vaultIds, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while ((await RekeyPendingAsync(userId)).Length > 0 || await StartedAsync(vaultIds))
        {
            Assert.True(DateTime.UtcNow < until, "The vaults were not re-keyed in time.");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>Holds a run of any of <paramref name="vaultIds"/> just after its start until released: the background
    /// work is then visibly not done while a request answers.</summary>
    private sealed class Hold(VaultCatalog catalog) : IAsyncDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static Hold Of(VaultCatalog catalog, IEnumerable<string> vaultIds)
        {
            var hold = new Hold(catalog);
            var ids = vaultIds.ToList();
            catalog.RekeyFaultAt = async point =>
            {
                if (point != "after-start" || !ids.Any(catalog.IsRekeying)) return;
                hold.Reached.TrySetResult();
                await hold._release.Task;
            };
            return hold;
        }

        public void Release() => _release.TrySetResult();

        public ValueTask DisposeAsync()
        {
            catalog.RekeyFaultAt = null;
            Release();
            return ValueTask.CompletedTask;
        }
    }

    private async Task<string> SignInAsync(string username, string password)
    {
        using var response = await PostAsync("/api/session", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return SessionOf(response);
    }

    private async Task<JsonNode[]> VaultListAsync(string session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/vaults");
        request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={session}");
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await RestClient.JsonAsync(response)).AsArray().Select(v => v!)];
    }

    [Fact]
    public async Task A_recovery_answers_at_once_and_the_runner_rekeys_every_vault()
    {
        var (owner, vaults) = await OwnerAsync("Diary");
        var users = Api.Services.GetRequiredService<UserCatalog>();
        var recoveryKey = (await users.RegenerateRecoveryKeyAsync(owner.User.Id, owner.UserKey, RestClient.Password, Ct))!;
        var oldKeys = new Dictionary<string, byte[]>();
        foreach (var id in vaults.Values) oldKeys[id] = await VaultKeyAsync(owner.UserKey, id);

        string newRecoveryKey;
        await using (var hold = Hold.Of(Catalog, vaults.Values))
        {
            using var recovered = await PostAsync("/api/recover", new { username = owner.Username, recoveryKey, newPassword = NewPassword });
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            newRecoveryKey = (await RestClient.JsonAsync(recovered))["recoveryKey"]!.GetValue<string>();
            Assert.NotEqual(recoveryKey, newRecoveryKey);
            // The answer came while the runner is still held: stage 2 runs behind it.
            await hold.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.NotEmpty(await RekeyPendingAsync(owner.User.Id));
        }

        await AllRekeyedAsync(owner.User.Id, vaults.Values);
        var (_, userKey, _) = (await users.TrySignInAsync(owner.Username, NewPassword, Ct))!.Value;
        foreach (var (id, oldKey) in oldKeys)
        {
            Assert.False(await MetaOpensAsync(oldKey, id));
            Assert.True(await MetaOpensAsync(await VaultKeyAsync(userKey, id), id));
        }

        var listed = await VaultListAsync(await SignInAsync(owner.Username, NewPassword));
        Assert.Equal(["Arbeit", "Diary"], listed.Select(v => v["name"]!.GetValue<string>()));
        Assert.All(listed, v => Assert.False(v["pending"]!.GetValue<bool>()));
        Assert.All(listed, v => Assert.Equal(1, v["notes"]!.GetValue<int>()));

        // The new recovery key works, the old one doesn't.
        Assert.Null(await users.RecoverAsync(owner.Username, recoveryKey, NewPassword, Ct));
        Assert.NotNull(await users.RecoverAsync(owner.Username, newRecoveryKey, NewPassword, Ct));
    }

    [Fact]
    public async Task A_recovery_interrupted_between_vaults_resumes_after_sign_in()
    {
        var (owner, vaults) = await OwnerAsync("Diary", "Ideas");
        var users = Api.Services.GetRequiredService<UserCatalog>();
        var recoveryKey = await users.RegenerateRecoveryKeyAsync(owner.User.Id, owner.UserKey, RestClient.Password, Ct);

        // A recovery whose process died after its commit: nothing started the runner.
        var (_, user, newKey) = (await users.RecoverWithKeyAsync(owner.Username, recoveryKey!, NewPassword, Ct))!.Value;
        Assert.Equal(vaults.Values.Order(StringComparer.Ordinal), await RekeyPendingAsync(owner.User.Id));
        // "Arbeit" was finished, "Diary" stopped in its two-key period, "Ideas" never started.
        await Catalog.RekeyAsync(vaults["Arbeit"], user.Id, newKey, Ct);
        Catalog.RekeyFaultAt = p => p == "after-start" ? throw new InvalidOperationException("crash after the start") : Task.CompletedTask;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog.RekeyAsync(vaults["Diary"], user.Id, newKey, Ct));
        }
        finally
        {
            Catalog.RekeyFaultAt = null;
        }

        Assert.Equal(new[] { vaults["Diary"], vaults["Ideas"] }.Order(StringComparer.Ordinal), await RekeyPendingAsync(owner.User.Id));

        string session;
        await using (var hold = Hold.Of(Catalog, [vaults["Diary"], vaults["Ideas"]]))
        {
            // Sign-in answers while the runner is held: it never waits for stage 2.
            session = await SignInAsync(owner.Username, NewPassword);
            await hold.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.NotEmpty(await RekeyPendingAsync(owner.User.Id));
        }

        await AllRekeyedAsync(owner.User.Id, vaults.Values);
        foreach (var id in vaults.Values) Assert.True(await MetaOpensAsync(await VaultKeyAsync(newKey, id), id));
        var listed = await VaultListAsync(session);
        Assert.Equal(3, listed.Length);
        Assert.All(listed, v => Assert.False(v["pending"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task A_pending_rekeying_resumes_at_the_first_request_of_a_session()
    {
        var (owner, vaults) = await OwnerAsync("Diary");
        var users = Api.Services.GetRequiredService<UserCatalog>();
        var recoveryKey = await users.RegenerateRecoveryKeyAsync(owner.User.Id, owner.UserKey, RestClient.Password, Ct);
        // A recovery whose process died after its commit, and a session that outlived the process ("keep me signed
        // in"): no sign-in follows that would resume the vaults.
        var (_, user, newKey) = (await users.RecoverWithKeyAsync(owner.Username, recoveryKey!, NewPassword, Ct))!.Value;
        var (_, _, readAt) = (await users.TrySignInAsync(owner.Username, NewPassword, Ct))!.Value;
        var session = await Api.Services.GetRequiredService<Sessions>().StartAsync(user, newKey, readAt, Ct, remember: true);

        // A new process: the owner's earlier requests in this one don't count.
        await using var restarted = Api.WithWebHostBuilder(_ => { });
        var catalog = restarted.Services.GetRequiredService<VaultCatalog>();
        await using (var hold = Hold.Of(catalog, vaults.Values))
        {
            using var listed = await GetAsync(restarted, "/api/vaults", session);
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
            await hold.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }

        await AllRekeyedAsync(owner.User.Id, vaults.Values);
        foreach (var id in vaults.Values) Assert.True(await MetaOpensAsync(await VaultKeyAsync(newKey, id), id));

        // Pending again behind the server's back: a second request of the session doesn't resume it, once per process is all.
        await Raw("users").UpdateOneAsync(
            new BsonDocument("_id", user.Id), new BsonDocument("$set", new BsonDocument("rekeyPending", new BsonArray { vaults["Arbeit"] })), cancellationToken: Ct);
        using (var again = await GetAsync(restarted, "/api/vaults", session)) Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.Equal([vaults["Arbeit"]], await RekeyPendingAsync(user.Id));
        Assert.False(await StartedAsync(vaults.Values));

        // A sign-in still resumes it.
        await SignInAsync(owner.Username, NewPassword);
        await AllRekeyedAsync(owner.User.Id, vaults.Values);
    }

    [Fact]
    public async Task A_resume_that_failed_at_a_sessions_request_is_tried_again_at_the_next()
    {
        var (owner, vaults) = await OwnerAsync();
        var users = Api.Services.GetRequiredService<UserCatalog>();
        var recoveryKey = await users.RegenerateRecoveryKeyAsync(owner.User.Id, owner.UserKey, RestClient.Password, Ct);
        var (_, user, newKey) = (await users.RecoverWithKeyAsync(owner.Username, recoveryKey!, NewPassword, Ct))!.Value;
        var (_, _, readAt) = (await users.TrySignInAsync(owner.Username, NewPassword, Ct))!.Value;
        var session = await Api.Services.GetRequiredService<Sessions>().StartAsync(user, newKey, readAt, Ct, remember: true);

        await using var restarted = Api.WithWebHostBuilder(_ => { });
        var rekeying = restarted.Services.GetRequiredService<Rekeying>();
        var runs = 0;
        // The first resume fails, as a blip of the database would make it.
        rekeying.ResumeFault = () => Interlocked.Increment(ref runs) == 1 ? throw new InvalidOperationException("the database didn't answer") : Task.CompletedTask;
        try
        {
            using (var first = await GetAsync(restarted, "/api/vaults", session)) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            using var waited = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            waited.CancelAfter(TimeSpan.FromSeconds(30));
            while (Volatile.Read(ref runs) < 2)
            {
                using (var again = await GetAsync(restarted, "/api/vaults", session)) Assert.Equal(HttpStatusCode.OK, again.StatusCode);
                await Task.Delay(TimeSpan.FromMilliseconds(50), waited.Token);
            }

            await AllRekeyedAsync(owner.User.Id, vaults.Values);
            // Once it worked, the next requests leave it be.
            using (var later = await GetAsync(restarted, "/api/vaults", session)) Assert.Equal(HttpStatusCode.OK, later.StatusCode);
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
            Assert.Equal(2, Volatile.Read(ref runs));
        }
        finally
        {
            rekeying.ResumeFault = null;
        }
    }

    private async Task<HttpResponseMessage> GetAsync(WebApplicationFactory<Program> api, string path, string session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={session}");
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Reencrypt_answers_the_recovery_key_after_stage_1()
    {
        var (owner, vaults) = await OwnerAsync("Diary");
        var users = Api.Services.GetRequiredService<UserCatalog>();
        var oldRecoveryKey = await users.RegenerateRecoveryKeyAsync(owner.User.Id, owner.UserKey, RestClient.Password, Ct);

        using (var wrong = await owner.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password = "not the password at all" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Equal("The password is wrong.", await RestClient.ErrorAsync(wrong));
        }

        Assert.Equal(owner.UserKey, (await users.TrySignInAsync(owner.Username, RestClient.Password, Ct))!.Value.UserKey);
        Assert.Empty(await RekeyPendingAsync(owner.User.Id));

        string recoveryKey;
        await using (var hold = Hold.Of(Catalog, vaults.Values))
        {
            using var response = await owner.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password = RestClient.Password });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            recoveryKey = (await RestClient.JsonAsync(response))["recoveryKey"]!.GetValue<string>();

            // Stage 1 is done: the password opens a new user key; every vault is pending, for the runner.
            var (_, newKey, _) = (await users.TrySignInAsync(owner.Username, RestClient.Password, Ct))!.Value;
            Assert.NotEqual(owner.UserKey, newKey);
            Assert.Equal(vaults.Values.Order(StringComparer.Ordinal), await RekeyPendingAsync(owner.User.Id));
            // This session stays signed in.
            using (var session = await owner.GetAsync("/api/session")) Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        }

        await AllRekeyedAsync(owner.User.Id, vaults.Values);
        Assert.Null(await users.RecoverAsync(owner.Username, oldRecoveryKey!, NewPassword, Ct));
        Assert.NotNull(await users.RecoverAsync(owner.Username, recoveryKey, NewPassword, Ct));
    }

    [Fact]
    public async Task Reencrypt_revokes_every_api_key_and_connection_before_answering()
    {
        var (owner, vaults) = await OwnerAsync("Diary");
        var keys = Api.Services.GetRequiredService<ApiKeys>();
        var (arbeitKey, _) = await keys.CreateAsync(owner.User, owner.UserKey, DateTime.UtcNow, "arbeit", new Dictionary<string, KeyRight> { [vaults["Arbeit"]] = KeyRight.Read }, Ct);
        var (diaryKey, _) = await keys.CreateAsync(owner.User, owner.UserKey, DateTime.UtcNow, "diary", new Dictionary<string, KeyRight> { [vaults["Diary"]] = KeyRight.Read }, Ct);
        using var created = await owner.SendAsync(HttpMethod.Post, "/api/connections", new { name = "claude", vaults = new Dictionary<string, string> { ["Diary"] = "write" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var connectionId = (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
        var flow = await OAuthFlow.StartAsync(Api, owner, connectionId, await OAuthFlow.RegisterAsync(Api, Callback), Callback);
        await flow.AllowAsync();
        using var token = await flow.TokenAsync();
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        var access = (await RestClient.JsonAsync(token))["access_token"]!.GetValue<string>();
        var tokens = Api.Services.GetRequiredService<OAuthTokens>();
        Assert.NotNull((await tokens.ResolveAccessAsync(access, connectionId, OAuthFlow.ResourceOf(connectionId), Ct)).Connection);

        // Only one vault can be reached by the runner while it is held; the other's key must be revoked all the same.
        await using (var hold = Hold.Of(Catalog, vaults.Values))
        {
            using var response = await owner.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password = RestClient.Password });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.Null(await keys.ResolveAsync(arbeitKey, Ct));
            Assert.Null(await keys.ResolveAsync(diaryKey, Ct));
            Assert.Null((await tokens.ResolveAccessAsync(access, connectionId, OAuthFlow.ResourceOf(connectionId), Ct)).Connection);
            var connection = await Raw("connections").Find(new BsonDocument("_id", connectionId)).SingleAsync(Ct);
            Assert.Equal(ConnectionReasons.Rekeyed, connection["tokensRevokedReason"].AsString);
            Assert.Equal(2, await Raw("api_keys").CountDocumentsAsync(new BsonDocument { ["user"] = owner.User.Id, ["revokedAt"] = new BsonDocument("$ne", BsonNull.Value) }, cancellationToken: Ct));
        }

        await AllRekeyedAsync(owner.User.Id, vaults.Values);
    }

    [Fact]
    public async Task Rekeying_one_vault_waits_and_needs_the_password_first()
    {
        var (owner, vaults) = await OwnerAsync("Diary");
        var id = vaults["Arbeit"];
        var keys = Api.Services.GetRequiredService<ApiKeys>();
        var (arbeitKey, _) = await keys.CreateAsync(owner.User, owner.UserKey, DateTime.UtcNow, "arbeit", new Dictionary<string, KeyRight> { [id] = KeyRight.Read }, Ct);
        var (diaryKey, _) = await keys.CreateAsync(owner.User, owner.UserKey, DateTime.UtcNow, "diary", new Dictionary<string, KeyRight> { [vaults["Diary"]] = KeyRight.Read }, Ct);
        using (var created = await owner.SendAsync(HttpMethod.Post, "/api/connections", new { name = "claude", vaults = new Dictionary<string, string> { ["Arbeit"] = "write" } }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        // Wrong password and an unknown name: the password answers first, as for a deletion.
        using (var wrong = await owner.SendAsync(HttpMethod.Post, "/api/vaults/Nowhere/rekey", new { password = "not the password at all" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Equal("The password is wrong.", await RestClient.ErrorAsync(wrong));
        }

        using (var missing = await owner.SendAsync(HttpMethod.Post, "/api/vaults/Nowhere/rekey", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using (var byKey = await owner.SendAsync(HttpMethod.Post, "/api/vaults/Arbeit/rekey", new { password = RestClient.Password }, bearer: diaryKey))
        {
            Assert.Equal(HttpStatusCode.Forbidden, byKey.StatusCode);
        }

        var oldKey = await VaultKeyAsync(owner.UserKey, id);
        using var response = await owner.SendAsync(HttpMethod.Post, "/api/vaults/Arbeit/rekey", new { password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await RestClient.JsonAsync(response);
        Assert.Equal(1, view["revokedApiKeys"]!.GetValue<int>());
        Assert.Equal(1, view["disconnectedApps"]!.GetValue<int>());

        // The answer waited for the run: the vault is done by now, under a new key.
        Assert.False(await StartedAsync([id]));
        Assert.Empty(await RekeyPendingAsync(owner.User.Id));
        var newKey = await VaultKeyAsync(owner.UserKey, id);
        Assert.NotEqual(oldKey, newKey);
        Assert.False(await MetaOpensAsync(oldKey, id));
        Assert.Contains("# Plan", (await owner.ReadAsync("plan.md"))["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(await keys.ResolveAsync(arbeitKey, Ct));
        Assert.NotNull(await keys.ResolveAsync(diaryKey, Ct));
    }

    [Fact]
    public async Task Reencrypt_answers_the_recovery_key_even_when_the_revocation_fails()
    {
        var (owner, vaults) = await OwnerAsync();
        var keys = Api.Services.GetRequiredService<ApiKeys>();
        var (key, _) = await keys.CreateAsync(owner.User, owner.UserKey, DateTime.UtcNow, "arbeit", new Dictionary<string, KeyRight> { [vaults["Arbeit"]] = KeyRight.Read }, Ct);
        var rekeying = Api.Services.GetRequiredService<Rekeying>();
        rekeying.RevokeFault = () => throw new InvalidOperationException("revocation failed");
        string recoveryKey;
        try
        {
            using var response = await owner.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password = RestClient.Password });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            recoveryKey = (await RestClient.JsonAsync(response))["recoveryKey"]!.GetValue<string>();
        }
        finally
        {
            rekeying.RevokeFault = null;
        }

        // Stage 2 still runs, and revokes the key with its vault.
        await AllRekeyedAsync(owner.User.Id, vaults.Values);
        Assert.Null(await keys.ResolveAsync(key, Ct));
        Assert.NotNull(await Api.Services.GetRequiredService<UserCatalog>().RecoverAsync(owner.Username, recoveryKey, NewPassword, Ct));
    }

    [Fact]
    public async Task A_vault_in_its_two_key_period_is_rekeyed_through_the_endpoint()
    {
        var (owner, vaults) = await OwnerAsync();
        var id = vaults["Arbeit"];
        var oldKey = await VaultKeyAsync(owner.UserKey, id);
        Catalog.RekeyFaultAt = p => p == "after-start" ? throw new InvalidOperationException("crash after the start") : Task.CompletedTask;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog.RekeyAsync(id, owner.User.Id, owner.UserKey, Ct));
        }
        finally
        {
            Catalog.RekeyFaultAt = null;
        }

        Assert.True(await StartedAsync([id]));
        Assert.Equal(id, await Catalog.FindForUserAsync(owner.User.Id, owner.UserKey, "Arbeit", Ct));

        using var response = await owner.SendAsync(HttpMethod.Post, "/api/vaults/Arbeit/rekey", new { password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await StartedAsync([id]));
        Assert.Empty(await RekeyPendingAsync(owner.User.Id));
        Assert.False(await MetaOpensAsync(oldKey, id));
        Assert.Contains("# Plan", (await owner.ReadAsync("plan.md"))["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_listing_vaults_while_a_finish_commits_gets_them()
    {
        var (owner, vaults) = await OwnerAsync();
        var id = vaults["Arbeit"];
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Catalog.RekeyFaultAt = async point =>
        {
            if (point != "inside-finish" || !Catalog.IsRekeying(id)) return;
            reached.TrySetResult();
            await release.Task;
        };
        try
        {
            var run = Catalog.RekeyAsync(id, owner.User.Id, owner.UserKey, Ct);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            // The listing reads the user slot (still K: the finish hasn't committed) and then waits for the finish.
            var listing = owner.GetAsync("/api/vaults");
            await Task.Delay(500, Ct);
            Assert.False(listing.IsCompleted);
            release.TrySetResult();
            await run;

            using var response = await listing;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var listed = (await RestClient.JsonAsync(response)).AsArray();
            Assert.Equal(["Arbeit"], listed.Select(v => v!["name"]!.GetValue<string>()));
            Assert.All(listed, v => Assert.False(v!["pending"]!.GetValue<bool>()));
        }
        finally
        {
            Catalog.RekeyFaultAt = null;
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Access_lists_what_will_lose_access()
    {
        var (owner, vaults) = await OwnerAsync("Diary");
        var keys = Api.Services.GetRequiredService<ApiKeys>();
        Dictionary<string, KeyRight> On(params string[] names) => names.ToDictionary(n => vaults[n], _ => KeyRight.Read);
        await keys.CreateAsync(owner.User, owner.UserKey, DateTime.UtcNow, "laptop", On("Arbeit"), Ct);
        await keys.CreateAsync(owner.User, owner.UserKey, DateTime.UtcNow, "both", On("Arbeit", "Diary"), Ct);
        await keys.CreateAsync(owner.User, owner.UserKey, DateTime.UtcNow, "diary only", On("Diary"), Ct);
        var (_, revoked) = await keys.CreateAsync(owner.User, owner.UserKey, DateTime.UtcNow, "old", On("Arbeit"), Ct);
        Assert.True(await keys.RevokeAsync(owner.User, revoked.Id, Ct));
        // Expired, not swept yet: its slots are still there, but it has no access to lose.
        var (_, expired) = await keys.CreateAsync(owner.User, owner.UserKey, DateTime.UtcNow, "expired", On("Arbeit"), Ct, 30);
        await Raw("api_keys").UpdateOneAsync(
            new BsonDocument("_id", expired.Id), new BsonDocument("$set", new BsonDocument("expiresAt", DateTime.UtcNow.AddMinutes(-1))), cancellationToken: Ct);
        foreach (var (name, vault) in new[] { ("claude", "Arbeit"), ("chatgpt", "Diary"), ("gone", "Arbeit") })
        {
            using var created = await owner.SendAsync(HttpMethod.Post, "/api/connections", new { name, vaults = new Dictionary<string, string> { [vault] = "read" } });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            if (name != "gone") continue;
            using var removed = await owner.SendAsync(HttpMethod.Delete, $"/api/connections/{(await RestClient.JsonAsync(created))["id"]!.GetValue<string>()}");
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var response = await owner.GetAsync("/api/vaults/Arbeit/access");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await RestClient.JsonAsync(response);
        Assert.Equal(["both", "laptop"], view["apiKeys"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(["claude"], view["connections"]!.AsArray().Select(v => v!.GetValue<string>()));

        using (var missing = await owner.GetAsync("/api/vaults/Nowhere/access")) Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var (key, _) = await keys.CreateAsync(owner.User, owner.UserKey, DateTime.UtcNow, "agent", On("Arbeit"), Ct);
        using (var byKey = await owner.GetAsync("/api/vaults/Arbeit/access", bearer: key)) Assert.Equal(HttpStatusCode.Forbidden, byKey.StatusCode);
    }
}
