// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;
using Kumbu.Server.Welcome;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Welcome;

/// <summary>The vault "Welcome" a new account gets when it joins, and only then.</summary>
[Collection("app")]
public sealed class WelcomeVaultTests(KumbuAppFactory app)
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewUsername() => "u" + Guid.NewGuid().ToString("N")[..12];

    private static string NewIp() => $"198.21.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    /// <summary>A request as the web app would send it to <paramref name="api"/>, whose own origin is <paramref name="origin"/>.</summary>
    private static async Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> api, HttpMethod method, string path, object? body = null, string? session = null,
        string origin = KumbuAppFactory.PublicUrl)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (session is not null) request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={session}");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("X-Forwarded-For", NewIp());
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    private static string SessionOf(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").First(v => v.StartsWith(CallerMiddleware.SessionCookie + "=", StringComparison.Ordinal));
        return cookie[(CallerMiddleware.SessionCookie.Length + 1)..].Split(';')[0];
    }

    private static async Task<string> SignInAsync(WebApplicationFactory<Program> api, string username, string origin = KumbuAppFactory.PublicUrl)
    {
        using var response = await SendAsync(api, HttpMethod.Post, "/api/session", new { username, password = Password }, origin: origin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return SessionOf(response);
    }

    /// <summary>Joins <paramref name="api"/> by a fresh invitation; the new username and the join's session.</summary>
    private static async Task<(string Username, string Session)> JoinAsync(WebApplicationFactory<Program> api, string origin = KumbuAppFactory.PublicUrl)
    {
        var adminName = NewUsername();
        await api.Services.GetRequiredService<UserCatalog>().CreateAsync(adminName, Password, Roles.Admin, Ct);
        var admin = await SignInAsync(api, adminName, origin);
        using var invitation = await SendAsync(api, HttpMethod.Post, "/api/admin/invitations", new { role = "user" }, admin, origin);
        Assert.Equal(HttpStatusCode.Created, invitation.StatusCode);
        var code = (await JsonAsync(invitation))["code"]!.GetValue<string>();

        var username = NewUsername();
        using var join = await SendAsync(api, HttpMethod.Post, "/api/join", new { code, username, password = Password }, origin: origin);
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        return (username, SessionOf(join));
    }

    private static async Task<string[]> VaultNamesAsync(WebApplicationFactory<Program> api, string session)
    {
        using var response = await SendAsync(api, HttpMethod.Get, "/api/vaults", session: session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await JsonAsync(response)).AsArray().Select(v => v!["name"]!.GetValue<string>())];
    }

    private static async Task<JsonNode> NoteAsync(WebApplicationFactory<Program> api, string session, string name)
    {
        using var response = await SendAsync(api, HttpMethod.Get, $"/api/notes/{WelcomeVault.Name}/{Uri.EscapeDataString(name)}", session: session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    /// <summary>The app without <c>Kumbu:PublicUrl</c>, as a local or development setup runs, with a cheap Argon2.</summary>
    private WebApplicationFactory<Program> WithoutPublicUrl() => app.PublicApi.WithWebHostBuilder(b =>
    {
        b.UseSetting("Kumbu:PublicUrl", "");
        b.UseSetting("Kumbu:Proxy:Trusted", "false");
    });

    [Fact]
    public async Task Joining_creates_the_welcome_vault_with_every_text()
    {
        var (_, session) = await JoinAsync(app.PublicApi);

        Assert.Contains(WelcomeVault.Name, await VaultNamesAsync(app.PublicApi, session));
        using var tree = await SendAsync(app.PublicApi, HttpMethod.Get, $"/api/vaults/{WelcomeVault.Name}/tree", session: session);
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        var root = (await JsonAsync(tree))["root"]!;
        Assert.Empty(root["folders"]!.AsArray());
        Assert.Equal(WelcomeVault.Texts().Select(t => t.Name), root["notes"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()));

        var start = await NoteAsync(app.PublicApi, session, "Start here.md");
        var embedded = WelcomeVault.Texts().Single(t => t.Name == "Start here.md").Content;
        Assert.Equal(WelcomeVault.Fill(embedded, KumbuAppFactory.PublicUrl), start["content"]!.GetValue<string>());
        Assert.Equal("seed", start["lastWriter"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_server_address_is_filled_in()
    {
        var (_, session) = await JoinAsync(app.PublicApi);

        foreach (var (name, _) in WelcomeVault.Texts())
        {
            Assert.DoesNotContain("{{", (await NoteAsync(app.PublicApi, session, name))["content"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        Assert.Contains("https://kumbu.test/mcp", (await NoteAsync(app.PublicApi, session, "Connect your AI.md"))["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_public_url_the_address_is_a_readable_placeholder()
    {
        await using var local = WithoutPublicUrl();
        const string origin = "https://localhost";
        var (_, session) = await JoinAsync(local, origin);

        using var response = await SendAsync(local, HttpMethod.Get, $"/api/notes/{WelcomeVault.Name}/{Uri.EscapeDataString("Connect your AI.md")}", session: session, origin: origin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = (await JsonAsync(response))["content"]!.GetValue<string>();
        Assert.Contains("https://<your Kumbu² address>/mcp", content, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Fill_drops_a_trailing_slash_and_falls_back_to_the_placeholder_address()
    {
        Assert.Equal("at https://kumbu.example/mcp and https://kumbu.example", WelcomeVault.Fill("at {{server}}/mcp and {{server}}", "https://kumbu.example/"));
        Assert.Equal($"at {WelcomeVault.NoAddress}/mcp", WelcomeVault.Fill("at {{server}}/mcp", null));
        Assert.Equal($"at {WelcomeVault.NoAddress}/mcp", WelcomeVault.Fill("at {{server}}/mcp", ""));
    }

    [Fact]
    public async Task Signing_in_again_never_recreates_the_welcome_vault()
    {
        var (username, session) = await JoinAsync(app.PublicApi);
        using var deleted = await SendAsync(
            app.PublicApi, HttpMethod.Post, $"/api/vaults/{WelcomeVault.Name}/delete", new { password = Password, confirm = WelcomeVault.Name }, session);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var again = await SignInAsync(app.PublicApi, username);

        Assert.DoesNotContain(WelcomeVault.Name, await VaultNamesAsync(app.PublicApi, again));
    }

    [Fact]
    public async Task A_user_made_without_joining_gets_no_welcome_vault()
    {
        var client = await Api.RestClient.NewAsync(app.PublicApi);

        Assert.Equal(["Arbeit"], await VaultNamesAsync(app.PublicApi, client.Session));
    }

    [Fact]
    public async Task A_user_deactivated_before_the_creation_gets_a_warning_and_no_vault()
    {
        var log = new CapturedLog();
        await using var logged = app.PublicApi.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var username = NewUsername();
        var users = logged.Services.GetRequiredService<UserCatalog>();
        await users.CreateAsync(username, Password, Roles.User, Ct);
        var (user, userKey, readAt) = (await users.TrySignInAsync(username, Password, Ct))!.Value;
        await app.Database.GetCollection<UserDocument>("users")
            .UpdateOneAsync(u => u.Id == user.Id, Builders<UserDocument>.Update.Set(u => u.Active, false), cancellationToken: Ct);

        await logged.Services.GetRequiredService<WelcomeVault>().CreateAsync(user, userKey, readAt, Ct);

        var warnings = log.Lines.Where(l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("welcome vault", StringComparison.Ordinal)).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains($"Could not create the welcome vault for user {user.Id}: ", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(username, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("Kumbu²", warning, StringComparison.Ordinal);
        Assert.Equal(0, await app.Database.GetCollection<SlotDocument>("slots").CountDocumentsAsync(s => s.Owner == user.Id, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_vault_whose_notes_fail_is_removed_again()
    {
        // A note that can't be written (here: one exists already, so TryCreateAsync refuses it) must not leave a
        // "Welcome" that later looks complete; the vault itself is still there when the write fails.
        var log = new CapturedLog();
        await using var logged = app.PublicApi.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var username = NewUsername();
        var users = logged.Services.GetRequiredService<UserCatalog>();
        await users.CreateAsync(username, Password, Roles.User, Ct);
        var (user, userKey, readAt) = (await users.TrySignInAsync(username, Password, Ct))!.Value;
        var welcome = logged.Services.GetRequiredService<WelcomeVault>();
        var catalog = logged.Services.GetRequiredService<VaultCatalog>();
        var firstName = WelcomeVault.Texts()[0].Name;
        welcome.AfterVaultCreated = async (_, token) =>
        {
            var store = new EncryptedNoteStore(catalog, new UserCredential(user.Id, userKey));
            var path = Kumbu.Core.Paths.VaultPath.Root.Child(WelcomeVault.Name).Child(firstName);
            Assert.True(await store.TryCreateAsync(new Kumbu.Core.Notes.StoredNote(path, "in the way", 1, "test", DateTime.UtcNow, []), token));
        };

        await welcome.CreateAsync(user, userKey, readAt, Ct);

        Assert.Single(log.Lines, l => l.Contains($"Could not create the welcome vault for user {user.Id}: InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(await catalog.OpenForUserAsync(user.Id, userKey, Ct), v => v.Name == WelcomeVault.Name);
        Assert.Equal(0, await app.Database.GetCollection<SlotDocument>("slots").CountDocumentsAsync(s => s.Owner == user.Id, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_failing_welcome_vault_never_fails_the_join()
    {
        // MongoDB hangs while the vault is filled: the join still answers in time, with the recovery key and the
        // cookie, and the half-written vault is gone.
        var log = new CapturedLog();
        await using var logged = app.PublicApi.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var welcome = logged.Services.GetRequiredService<WelcomeVault>();
        welcome.Limit = TimeSpan.FromSeconds(1);
        welcome.AfterVaultCreated = (_, token) => Task.Delay(Timeout.Infinite, token);

        var adminName = NewUsername();
        await logged.Services.GetRequiredService<UserCatalog>().CreateAsync(adminName, Password, Roles.Admin, Ct);
        var admin = await SignInAsync(logged, adminName);
        using var invitation = await SendAsync(logged, HttpMethod.Post, "/api/admin/invitations", new { role = "user" }, admin);
        var code = (await JsonAsync(invitation))["code"]!.GetValue<string>();
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var join = await SendAsync(logged, HttpMethod.Post, "/api/join", new { code, username = NewUsername(), password = Password });
        started.Stop();

        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        Assert.False(string.IsNullOrEmpty((await JsonAsync(join))["recoveryKey"]!.GetValue<string>()));
        Assert.True(started.Elapsed < welcome.Limit + welcome.CleanupLimit, $"The join took {started.Elapsed}");
        Assert.Contains(log.Lines, l => l.Contains("Could not create the welcome vault for user ", StringComparison.Ordinal));
        Assert.DoesNotContain(WelcomeVault.Name, await VaultNamesAsync(logged, SessionOf(join)));
    }

    [Fact]
    public async Task A_cancellation_the_join_did_not_ask_for_never_fails_it()
    {
        // The join passes CancellationToken.None: an OperationCanceledException from below (a driver timeout, say) is a
        // failure like any other, not a cancellation, and must not hide the recovery key.
        var log = new CapturedLog();
        await using var logged = app.PublicApi.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var username = NewUsername();
        var users = logged.Services.GetRequiredService<UserCatalog>();
        await users.CreateAsync(username, Password, Roles.User, Ct);
        var (user, userKey, readAt) = (await users.TrySignInAsync(username, Password, Ct))!.Value;
        var welcome = logged.Services.GetRequiredService<WelcomeVault>();
        welcome.AfterVaultCreated = (_, _) => throw new OperationCanceledException();

        await welcome.CreateAsync(user, userKey, readAt, CancellationToken.None);

        Assert.Single(log.Lines, l => l.Contains($"Could not create the welcome vault for user {user.Id}: OperationCanceledException", StringComparison.Ordinal));
        Assert.Equal(0, await app.Database.GetCollection<SlotDocument>("slots").CountDocumentsAsync(s => s.Owner == user.Id, cancellationToken: Ct));
    }
}
