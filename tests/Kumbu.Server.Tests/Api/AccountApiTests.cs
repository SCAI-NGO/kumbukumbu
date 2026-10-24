// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kumbu.Server.Tests.Api;

/// <summary>The account REST API: sign-in, join, recovery, password, API keys and the admin's tools.</summary>
[Collection("app")]
public sealed class AccountApiTests(KumbuAppFactory app)
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "another horse, another staple";
    private const string Origin = KumbuAppFactory.PublicUrl;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>An address of this test's own, so rate limits of other tests don't reach it.</summary>
    private readonly string _ip = $"198.18.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    private HttpClient Client(WebApplicationFactory<Program>? factory = null) =>
        (factory ?? Api).CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body = null, string? session = null, string? origin = Origin, string? ip = null, string? bearer = null,
        HttpClient? http = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (session is not null) request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={session}");
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Forwarded-For", ip ?? _ip);
        if (http is not null) return await http.SendAsync(request, Ct);
        using var client = Client();
        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    private static string? SetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(CallerMiddleware.SessionCookie + "=", StringComparison.Ordinal))
            : null;

    /// <summary>The session secret a response set in the cookie.</summary>
    private static string SessionOf(HttpResponseMessage response)
    {
        var cookie = SetCookie(response) ?? throw new InvalidOperationException("No session cookie was set.");
        var value = cookie[(CallerMiddleware.SessionCookie.Length + 1)..].Split(';')[0];
        Assert.NotEmpty(value);
        return value;
    }

    private static string NewUsername() => "u" + Guid.NewGuid().ToString("N")[..12];

    private async Task<string> NewUserAsync(string role = Roles.User)
    {
        var username = NewUsername();
        await Api.Services.GetRequiredService<UserCatalog>().CreateAsync(username, Password, role, Ct);
        return username;
    }

    private async Task<string> SignInAsync(string username, string password = Password)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/session", new { username, password }, ip: NewIp());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return SessionOf(response);
    }

    private Task<HttpResponseMessage> PeekAsync(string code, HttpClient? http = null) =>
        SendAsync(HttpMethod.Post, "/api/invitations/peek", new { code }, http: http);

    private async Task<string> InvitationCodeAsync(string role = "user")
    {
        var admin = await SignInAsync(await NewUserAsync(Roles.Admin));
        using var created = await SendAsync(HttpMethod.Post, "/api/admin/invitations", new { role }, session: admin);
        return (await JsonAsync(created))["code"]!.GetValue<string>();
    }

    private sealed class CapturedLog : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
        }
    }

    private static string NewIp() => $"198.19.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    [Fact]
    public async Task The_join_flow_creates_the_account_signs_it_in_and_shows_the_recovery_key_once()
    {
        var admin = await SignInAsync(await NewUserAsync(Roles.Admin));
        using var created = await SendAsync(HttpMethod.Post, "/api/admin/invitations", new { role = "user", note = "for Lea" }, session: admin);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var invitation = await JsonAsync(created);
        var code = invitation["code"]!.GetValue<string>();
        Assert.Equal($"{Origin}/join#{code}", invitation["link"]!.GetValue<string>());

        // The join page sees role, expiry and validity, nothing about who made it or for whom.
        using var peek = await PeekAsync(code);
        Assert.Equal(HttpStatusCode.OK, peek.StatusCode);
        var peeked = (await JsonAsync(peek)).AsObject();
        Assert.Equal(["expiresAt", "role", "valid"], peeked.Select(p => p.Key).Order());
        Assert.Equal("user", peeked["role"]!.GetValue<string>());
        Assert.True(peeked["valid"]!.GetValue<bool>());

        var username = NewUsername();
        using var join = await SendAsync(HttpMethod.Post, "/api/join", new { code, username, password = Password });

        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        var joined = await JsonAsync(join);
        Assert.Equal(username, joined["user"]!["username"]!.GetValue<string>());
        Assert.Equal("user", joined["user"]!["role"]!.GetValue<string>());
        var recoveryKey = joined["recoveryKey"]!.GetValue<string>();
        Assert.True(RecoveryKey.TryParse(recoveryKey, out _));

        using var me = await SendAsync(HttpMethod.Get, "/api/session", session: SessionOf(join));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var meJson = await JsonAsync(me);
        Assert.Equal(username, meJson["user"]!["username"]!.GetValue<string>());
        Assert.DoesNotContain(recoveryKey, meJson.ToJsonString(), StringComparison.Ordinal);

        using var used = await PeekAsync(code);
        var usedJson = await JsonAsync(used);
        Assert.False(usedJson["valid"]!.GetValue<bool>());
        Assert.Equal("used", usedJson["reason"]!.GetValue<string>());

        using var again = await SendAsync(HttpMethod.Post, "/api/join", new { code, username = NewUsername(), password = Password });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("This invitation has been used already.", (await JsonAsync(again))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task No_invitation_code_reaches_the_log()
    {
        var log = new CapturedLog();
        await using var logged = Api.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        using var http = Client(logged);
        var code = await InvitationCodeAsync();

        using var peek = await PeekAsync(code, http);
        using var join = await SendAsync(HttpMethod.Post, "/api/join", new { code, username = NewUsername(), password = Password }, http: http);
        // A code in a path (an old-style link pasted somewhere) must not be logged either.
        using var inPath = await SendAsync(HttpMethod.Get, $"/api/invitations/{code}", http: http);

        Assert.Equal(HttpStatusCode.OK, join.StatusCode);
        Assert.Contains(log.Lines, l => l.Contains("was used by", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains(code, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_invitation_code_is_never_part_of_an_api_path()
    {
        var code = await InvitationCodeAsync();

        using var old = await SendAsync(HttpMethod.Get, $"/api/invitations/{code}");

        Assert.NotEqual(HttpStatusCode.OK, old.StatusCode);
    }

    [Fact]
    public async Task An_unknown_invitation_code_is_404()
    {
        using var response = await PeekAsync(new string('A', 43));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull((await JsonAsync(response))["error"]);
    }

    [Fact]
    public async Task A_taken_username_at_join_is_409_and_the_invitation_stays_usable()
    {
        var taken = await NewUserAsync();
        var admin = await SignInAsync(await NewUserAsync(Roles.Admin));
        using var created = await SendAsync(HttpMethod.Post, "/api/admin/invitations", new { role = "user" }, session: admin);
        var code = (await JsonAsync(created))["code"]!.GetValue<string>();

        using var join = await SendAsync(HttpMethod.Post, "/api/join", new { code, username = taken, password = Password });

        Assert.Equal(HttpStatusCode.Conflict, join.StatusCode);
        Assert.Null(SetCookie(join));
        using var peek = await PeekAsync(code);
        Assert.True((await JsonAsync(peek))["valid"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Signing_in_sets_a_strict_session_cookie_and_signing_out_ends_the_session()
    {
        var username = await NewUserAsync();

        using var signIn = await SendAsync(HttpMethod.Post, "/api/session", new { username, password = Password });

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal(username, (await JsonAsync(signIn))["user"]!["username"]!.GetValue<string>());
        var cookie = SetCookie(signIn)!.ToLowerInvariant();
        Assert.Contains("httponly", cookie, StringComparison.Ordinal);
        Assert.Contains("secure", cookie, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/", cookie, StringComparison.Ordinal);
        var session = SessionOf(signIn);

        using var me = await SendAsync(HttpMethod.Get, "/api/session", session: session);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        using var signOut = await SendAsync(HttpMethod.Delete, "/api/session", session: session);
        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        Assert.Contains("expires=", SetCookie(signOut)!, StringComparison.OrdinalIgnoreCase);

        using var after = await SendAsync(HttpMethod.Get, "/api/session", session: session);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task The_session_shows_the_previous_sign_in()
    {
        var username = await NewUserAsync();
        await SignInAsync(username);
        var second = await SignInAsync(username);

        using var me = await SendAsync(HttpMethod.Get, "/api/session", session: second);

        Assert.NotNull((await JsonAsync(me))["previousSignInAt"]?.GetValue<DateTime>());
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_user_get_the_same_answer()
    {
        var username = await NewUserAsync();

        using var wrong = await SendAsync(HttpMethod.Post, "/api/session", new { username, password = "not the password at all" });
        using var unknown = await SendAsync(HttpMethod.Post, "/api/session", new { username = NewUsername(), password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(Ct), await unknown.Content.ReadAsStringAsync(Ct));
        Assert.Null(SetCookie(wrong));
        Assert.Null(SetCookie(unknown));
    }

    [Fact]
    public async Task Sign_in_takes_five_tries_per_minute_per_username_from_any_address()
    {
        var username = await NewUserAsync();
        for (var i = 0; i < 5; i++)
        {
            using var wrong = await SendAsync(HttpMethod.Post, "/api/session", new { username, password = "wrong password " + i }, ip: NewIp());
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // Even the right password, from yet another address: the username has used up its tries.
        using var limited = await SendAsync(HttpMethod.Post, "/api/session", new { username, password = Password }, ip: NewIp());

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.NotNull((await JsonAsync(limited))["error"]);
        Assert.Null(SetCookie(limited));
    }

    [Fact]
    public async Task Sign_in_takes_twenty_tries_per_minute_per_address()
    {
        for (var i = 0; i < 20; i++)
        {
            using var wrong = await SendAsync(HttpMethod.Post, "/api/session", new { username = NewUsername(), password = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var limited = await SendAsync(HttpMethod.Post, "/api/session", new { username = NewUsername(), password = Password });
        using var elsewhere = await SendAsync(HttpMethod.Post, "/api/session", new { username = NewUsername(), password = Password }, ip: NewIp());

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, elsewhere.StatusCode);
    }

    [Theory]
    [InlineData("/api/join")]
    [InlineData("/api/recover")]
    public async Task Join_and_recovery_take_five_tries_per_minute_per_address(string path)
    {
        object body = path == "/api/join"
            ? new { code = new string('A', 43), username = NewUsername(), password = Password }
            : new { username = NewUsername(), recoveryKey = "not-a-recovery-key", newPassword = NewPassword };
        for (var i = 0; i < 5; i++)
        {
            using var refused = await SendAsync(HttpMethod.Post, path, body);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, refused.StatusCode);
        }

        using var limited = await SendAsync(HttpMethod.Post, path, body);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task A_state_changing_request_with_a_foreign_origin_is_403()
    {
        var session = await SignInAsync(await NewUserAsync());

        using var foreign = await SendAsync(HttpMethod.Post, "/api/sessions/end-others", session: session, origin: "https://evil.example");
        using var signIn = await SendAsync(HttpMethod.Post, "/api/session", new { username = NewUsername(), password = Password }, origin: "https://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.NotNull((await JsonAsync(foreign))["error"]);
        Assert.Equal(HttpStatusCode.Forbidden, signIn.StatusCode);
    }

    [Fact]
    public async Task A_state_changing_request_with_a_session_and_no_origin_is_403()
    {
        var session = await SignInAsync(await NewUserAsync());

        using var missing = await SendAsync(HttpMethod.Delete, "/api/session", session: session, origin: null);
        using var stillThere = await SendAsync(HttpMethod.Get, "/api/session", session: session, origin: null);

        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
    }

    [Fact]
    public async Task Without_a_public_url_the_origin_must_be_the_requests_own()
    {
        // The plain app: no PublicUrl, and its own Argon2 parameters, so the user is created through it.
        using var http = Client(app);
        var username = NewUsername();
        await app.Services.GetRequiredService<UserCatalog>().CreateAsync(username, Password, Roles.User, Ct);

        using var foreign = await SendAsync(HttpMethod.Post, "/api/session", new { username, password = Password }, origin: Origin, http: http);
        using var own = await SendAsync(HttpMethod.Post, "/api/session", new { username, password = Password }, origin: "https://localhost", http: http);

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task A_bearer_caller_needs_no_origin_but_is_refused_account_endpoints_even_as_an_admins_key()
    {
        var username = await NewUserAsync(Roles.Admin);
        var (user, userKey, _) = (await Api.Services.GetRequiredService<UserCatalog>().TrySignInAsync(username, Password, Ct))!.Value;
        var vault = await Api.Services.GetRequiredService<VaultCatalog>().CreateForUserAsync("Notizen", user.Id, userKey, Ct);
        var (key, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(user, userKey, "laptop", new Dictionary<string, KeyRight> { [vault.Id] = KeyRight.Write }, Ct);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/api/admin/users"), (HttpMethod.Get, "/api/keys"), (HttpMethod.Post, "/api/keys"),
                     (HttpMethod.Post, "/api/recovery-key"), (HttpMethod.Get, "/api/session"),
                 })
        {
            using var response = await SendAsync(method, path, method == HttpMethod.Post ? new { } : null, origin: null, bearer: key);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("signed-in session", (await JsonAsync(response))["error"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        using var dev = await SendAsync(HttpMethod.Get, "/api/keys", origin: null, bearer: KumbuAppFactory.Token);
        Assert.Equal(HttpStatusCode.Forbidden, dev.StatusCode);
    }

    [Fact]
    public async Task A_non_admin_gets_403_on_every_admin_endpoint()
    {
        var session = await SignInAsync(await NewUserAsync());

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, "/api/admin/users", null),
                     (HttpMethod.Patch, "/api/admin/users/someone", new { active = false }),
                     (HttpMethod.Get, "/api/admin/invitations", null),
                     (HttpMethod.Post, "/api/admin/invitations", new { role = "user" }),
                     (HttpMethod.Delete, "/api/admin/invitations/something", null),
                 })
        {
            using var response = await SendAsync(method, path, body, session: session);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path}: {response.StatusCode}");
            Assert.NotNull((await JsonAsync(response))["error"]);
        }
    }

    [Fact]
    public async Task The_admin_lists_users_with_their_last_sign_in_and_can_deactivate_and_change_roles()
    {
        var admin = await SignInAsync(await NewUserAsync(Roles.Admin));
        var username = await NewUserAsync();
        var userSession = await SignInAsync(username);

        using var list = await SendAsync(HttpMethod.Get, "/api/admin/users", session: admin);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var row = (await JsonAsync(list)).AsArray().Single(u => u!["username"]!.GetValue<string>() == username)!.AsObject();
        Assert.Equal(
            ["active", "bytesStored", "createdAt", "display", "id", "lastSignInAt", "role", "username", "vaults"],
            row.Select(p => p.Key).Order());
        Assert.NotNull(row["lastSignInAt"]?.GetValue<DateTime>());
        var id = row["id"]!.GetValue<string>();

        using var role = await SendAsync(HttpMethod.Patch, $"/api/admin/users/{id}", new { role = "admin" }, session: admin);
        Assert.Equal(HttpStatusCode.NoContent, role.StatusCode);
        using var deactivate = await SendAsync(HttpMethod.Patch, $"/api/admin/users/{id}", new { active = false }, session: admin);
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        using var gone = await SendAsync(HttpMethod.Get, "/api/session", session: userSession);
        Assert.Equal(HttpStatusCode.Unauthorized, gone.StatusCode);
        using var after = await SendAsync(HttpMethod.Get, "/api/admin/users", session: admin);
        var changed = (await JsonAsync(after)).AsArray().Single(u => u!["id"]!.GetValue<string>() == id)!;
        Assert.False(changed["active"]!.GetValue<bool>());
        Assert.Equal("admin", changed["role"]!.GetValue<string>());

        using var missing = await SendAsync(HttpMethod.Patch, "/api/admin/users/nobody", new { active = true }, session: admin);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var primary = await SendAsync(HttpMethod.Patch, $"/api/admin/users/{id}", new { role = "primary_admin" }, session: admin);
        Assert.Equal(HttpStatusCode.Forbidden, primary.StatusCode);
    }

    [Fact]
    public async Task A_refused_admin_change_changes_nothing()
    {
        var adminName = await NewUserAsync(Roles.Admin);
        var admin = await SignInAsync(adminName);
        using var list = await SendAsync(HttpMethod.Get, "/api/admin/users", session: admin);
        var self = (await JsonAsync(list)).AsArray().Single(u => u!["username"]!.GetValue<string>() == adminName)!["id"]!.GetValue<string>();

        using var refused = await SendAsync(HttpMethod.Patch, $"/api/admin/users/{self}", new { role = "user", active = false }, session: admin);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var after = await SendAsync(HttpMethod.Get, "/api/admin/users", session: admin);
        var row = (await JsonAsync(after)).AsArray().Single(u => u!["id"]!.GetValue<string>() == self)!;
        Assert.Equal("admin", row["role"]!.GetValue<string>());
        Assert.True(row["active"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_admin_lists_and_revokes_invitations()
    {
        var admin = await SignInAsync(await NewUserAsync(Roles.Admin));
        using var created = await SendAsync(HttpMethod.Post, "/api/admin/invitations", new { role = "admin", note = "for Lea", expiresInDays = 2 }, session: admin);
        var json = await JsonAsync(created);
        var code = json["code"]!.GetValue<string>();
        var id = json["invitation"]!["id"]!.GetValue<string>();

        using var list = await SendAsync(HttpMethod.Get, "/api/admin/invitations", session: admin);
        var listed = (await JsonAsync(list)).AsArray().Single(i => i!["id"]!.GetValue<string>() == id)!;
        Assert.Equal("for Lea", listed["note"]!.GetValue<string>());
        Assert.Equal("open", listed["state"]!.GetValue<string>());
        Assert.DoesNotContain(code, (await list.Content.ReadAsStringAsync(Ct)), StringComparison.Ordinal);

        using var revoke = await SendAsync(HttpMethod.Delete, $"/api/admin/invitations/{id}", session: admin);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var again = await SendAsync(HttpMethod.Delete, $"/api/admin/invitations/{id}", session: admin);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        using var peek = await PeekAsync(code);
        Assert.Equal("revoked", (await JsonAsync(peek))["reason"]!.GetValue<string>());

        using var tooLong = await SendAsync(HttpMethod.Post, "/api/admin/invitations", new { role = "user", expiresInDays = 31 }, session: admin);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_signs_out_everywhere_and_the_new_password_signs_in()
    {
        var username = await NewUserAsync();
        var session = await SignInAsync(username);
        var other = await SignInAsync(username);

        using var wrongOld = await SendAsync(HttpMethod.Post, "/api/password", new { oldPassword = "not the password at all", newPassword = NewPassword }, session: session);
        Assert.Equal(HttpStatusCode.BadRequest, wrongOld.StatusCode);
        using var weak = await SendAsync(HttpMethod.Post, "/api/password", new { oldPassword = Password, newPassword = "short" }, session: session);
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.DoesNotContain("Parameter", (await JsonAsync(weak))["error"]!.GetValue<string>(), StringComparison.Ordinal);

        using var changed = await SendAsync(HttpMethod.Post, "/api/password", new { oldPassword = Password, newPassword = NewPassword }, session: session);

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.True((await JsonAsync(changed))["signedOut"]!.GetValue<bool>());
        Assert.Contains("expires=", SetCookie(changed)!, StringComparison.OrdinalIgnoreCase);
        foreach (var old in new[] { session, other })
        {
            using var me = await SendAsync(HttpMethod.Get, "/api/session", session: old);
            Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        }

        await SignInAsync(username, NewPassword);
    }

    [Fact]
    public async Task The_recovery_key_sets_a_new_password_and_a_regenerated_one_replaces_it()
    {
        var admin = await SignInAsync(await NewUserAsync(Roles.Admin));
        using var created = await SendAsync(HttpMethod.Post, "/api/admin/invitations", new { role = "user" }, session: admin);
        var code = (await JsonAsync(created))["code"]!.GetValue<string>();
        var username = NewUsername();
        using var join = await SendAsync(HttpMethod.Post, "/api/join", new { code, username, password = Password });
        var firstKey = (await JsonAsync(join))["recoveryKey"]!.GetValue<string>();

        using var regenerated = await SendAsync(HttpMethod.Post, "/api/recovery-key", session: SessionOf(join));
        Assert.Equal(HttpStatusCode.OK, regenerated.StatusCode);
        var secondKey = (await JsonAsync(regenerated))["recoveryKey"]!.GetValue<string>();
        Assert.NotEqual(firstKey, secondKey);

        using var old = await SendAsync(HttpMethod.Post, "/api/recover", new { username, recoveryKey = firstKey, newPassword = NewPassword }, ip: NewIp());
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var recovered = await SendAsync(HttpMethod.Post, "/api/recover", new { username, recoveryKey = secondKey, newPassword = NewPassword }, ip: NewIp());
        Assert.Equal(HttpStatusCode.NoContent, recovered.StatusCode);

        using var oldSession = await SendAsync(HttpMethod.Get, "/api/session", session: SessionOf(join));
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        await SignInAsync(username, NewPassword);
    }

    [Fact]
    public async Task Ending_the_other_sessions_keeps_this_one()
    {
        var username = await NewUserAsync();
        var mine = await SignInAsync(username);
        var other = await SignInAsync(username);

        using var end = await SendAsync(HttpMethod.Post, "/api/sessions/end-others", session: mine);

        Assert.Equal(HttpStatusCode.NoContent, end.StatusCode);
        using var me = await SendAsync(HttpMethod.Get, "/api/session", session: mine);
        using var them = await SendAsync(HttpMethod.Get, "/api/session", session: other);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, them.StatusCode);
    }

    [Fact]
    public async Task Api_keys_are_created_for_vaults_by_name_shown_once_listed_and_revoked()
    {
        var username = await NewUserAsync();
        var (user, userKey, _) = (await Api.Services.GetRequiredService<UserCatalog>().TrySignInAsync(username, Password, Ct))!.Value;
        await Api.Services.GetRequiredService<VaultCatalog>().CreateForUserAsync("Arbeit", user.Id, userKey, Ct);
        var session = await SignInAsync(username);

        using var created = await SendAsync(HttpMethod.Post, "/api/keys", new { name = "laptop", vaults = new Dictionary<string, string> { ["Arbeit"] = "read" } }, session: session);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await JsonAsync(created);
        var key = json["key"]!.GetValue<string>();
        Assert.StartsWith(ApiKeys.KeyPrefix, key, StringComparison.Ordinal);
        Assert.Equal("read", json["apiKey"]!["vaults"]!["Arbeit"]!.GetValue<string>());
        var id = json["apiKey"]!["id"]!.GetValue<string>();

        using var list = await SendAsync(HttpMethod.Get, "/api/keys", session: session);
        var listed = Assert.Single((await JsonAsync(list)).AsArray())!;
        Assert.Equal("laptop", listed["name"]!.GetValue<string>());
        Assert.Equal(key.Substring(ApiKeys.KeyPrefix.Length, listed["prefix"]!.GetValue<string>().Length), listed["prefix"]!.GetValue<string>());
        Assert.DoesNotContain(key, await list.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var taken = await SendAsync(HttpMethod.Post, "/api/keys", new { name = "laptop", vaults = new Dictionary<string, string> { ["Arbeit"] = "write" } }, session: session);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        using var notMine = await SendAsync(HttpMethod.Post, "/api/keys", new { name = "phone", vaults = new Dictionary<string, string> { ["Fremd"] = "read" } }, session: session);
        Assert.Equal(HttpStatusCode.BadRequest, notMine.StatusCode);
        using var badRight = await SendAsync(HttpMethod.Post, "/api/keys", new { name = "phone", vaults = new Dictionary<string, string> { ["Arbeit"] = "admin" } }, session: session);
        Assert.Equal(HttpStatusCode.BadRequest, badRight.StatusCode);

        using var revoke = await SendAsync(HttpMethod.Delete, $"/api/keys/{id}", session: session);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var again = await SendAsync(HttpMethod.Delete, $"/api/keys/{id}", session: session);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        using var after = await SendAsync(HttpMethod.Get, "/api/keys", session: session);
        Assert.NotNull(Assert.Single((await JsonAsync(after)).AsArray())!["revokedAt"]?.GetValue<DateTime>());
        using var refused = await SendAsync(HttpMethod.Get, "/api/keys", origin: null, bearer: key);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task A_body_that_is_not_the_expected_json_is_a_400_with_an_error()
    {
        using var http = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session") { Content = new StringContent("{not json", Encoding.UTF8, "application/json") };
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("X-Forwarded-For", _ip);
        using var malformed = await http.SendAsync(request, Ct);
        using var incomplete = await SendAsync(HttpMethod.Post, "/api/session", new { username = "someone" });

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.NotNull((await JsonAsync(malformed))["error"]);
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        Assert.NotNull((await JsonAsync(incomplete))["error"]);
    }

    [Theory]
    [InlineData("/api/session")]
    [InlineData("/mcp")]
    [InlineData("/healthz")]
    [InlineData("/somewhere-static")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path, origin: null);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(
            "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'",
            response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.StartsWith("max-age=", response.Headers.GetValues("Strict-Transport-Security").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_responses_are_never_cached_and_others_are_left_alone()
    {
        using var api = await SendAsync(HttpMethod.Get, "/api/session", origin: null);
        using var health = await SendAsync(HttpMethod.Get, "/healthz", origin: null);

        Assert.True(api.Headers.CacheControl?.NoStore);
        Assert.Null(health.Headers.CacheControl);
    }

    [Fact]
    public async Task Without_an_https_public_url_there_is_no_hsts()
    {
        using var http = Client(app);

        using var response = await http.GetAsync("/healthz", Ct);

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
    }

    [Fact]
    public async Task The_openapi_document_is_anonymous_and_lists_every_account_endpoint()
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/openapi.json", origin: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = (await JsonAsync(response))["paths"]!.AsObject();
        foreach (var (method, path) in new[]
                 {
                     ("post", "/api/session"), ("get", "/api/session"), ("delete", "/api/session"),
                     ("post", "/api/invitations/peek"), ("post", "/api/join"), ("post", "/api/recover"),
                     ("post", "/api/password"), ("post", "/api/recovery-key"), ("post", "/api/sessions/end-others"),
                     ("get", "/api/keys"), ("post", "/api/keys"), ("delete", "/api/keys/{id}"),
                     ("get", "/api/admin/users"), ("patch", "/api/admin/users/{id}"),
                     ("get", "/api/admin/invitations"), ("post", "/api/admin/invitations"), ("delete", "/api/admin/invitations/{id}"),
                 })
        {
            Assert.True(paths[path]?[method] is not null, $"{method} {path} is missing");
        }

        // Typed responses, so the web app can generate a client.
        Assert.NotNull(paths["/api/session"]!["post"]!["responses"]!["200"]!["content"]?["application/json"]?["schema"]);
        Assert.NotNull(paths["/api/session"]!["post"]!["responses"]!["401"]);
        Assert.DoesNotContain("reason", (await JsonAsync(response))["components"]!["schemas"]!["InvitationPeekView"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));
    }
}
