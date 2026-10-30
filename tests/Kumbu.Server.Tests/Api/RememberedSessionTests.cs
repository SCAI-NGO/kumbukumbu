// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Api;

/// <summary>"Keep me signed in" at the API: the remembered sign-in, its cookie, the normal window inside it and
/// <c>POST /api/session/confirm</c>, which opens the window again.</summary>
[Collection("app")]
public sealed class RememberedSessionTests(KumbuAppFactory app)
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "another horse, another staple";
    private const string Origin = KumbuAppFactory.PublicUrl;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The time of the timed host, starting at the real time in whole seconds (MongoDB keeps milliseconds).</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    /// <summary>An address of this test's own, so rate limits of other tests don't reach it.</summary>
    private readonly string _ip = $"198.18.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    private WebApplicationFactory<Program> Timed() => app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static string NewIp() => $"198.18.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";

    private static string NewUsername() => "u" + Guid.NewGuid().ToString("N")[..12];

    private async Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> api, HttpMethod method, string path, object? body = null, string? session = null, string? bearer = null, string? ip = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (session is not null) request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={session}");
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("X-Forwarded-For", ip ?? _ip);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

    private static async Task<string?> ErrorAsync(HttpResponseMessage response) => (await JsonAsync(response))["error"]?.GetValue<string>();

    private static string? SetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(CallerMiddleware.SessionCookie + "=", StringComparison.Ordinal))
            : null;

    private static async Task<string> NewUserAsync(WebApplicationFactory<Program> api, string role = Roles.User)
    {
        var username = NewUsername();
        await api.Services.GetRequiredService<UserCatalog>().CreateAsync(username, Password, role, Ct);
        return username;
    }

    /// <summary>Signs in and returns the session's secret and the whole <c>Set-Cookie</c> line.</summary>
    private async Task<(string Secret, string Cookie)> SignInAsync(
        WebApplicationFactory<Program> api, string username, bool remember, string password = Password)
    {
        using var response = await SendAsync(api, HttpMethod.Post, "/api/session", new { username, password, remember }, ip: NewIp());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await JsonAsync(response))["passwordWindowOpen"]!.GetValue<bool>());
        var cookie = SetCookie(response) ?? throw new InvalidOperationException("No session cookie was set.");
        return (cookie[(CallerMiddleware.SessionCookie.Length + 1)..].Split(';')[0], cookie);
    }

    private async Task<string> RememberedAsync(WebApplicationFactory<Program> api, string username) => (await SignInAsync(api, username, remember: true)).Secret;

    /// <summary><c>GET /api/session</c>: null if it isn't a 200.</summary>
    private async Task<JsonNode?> SessionViewAsync(WebApplicationFactory<Program> api, string session)
    {
        using var response = await SendAsync(api, HttpMethod.Get, "/api/session", session: session);
        return response.StatusCode == HttpStatusCode.OK ? await JsonAsync(response) : null;
    }

    private async Task<bool> WindowOpenAsync(WebApplicationFactory<Program> api, string session) =>
        (await SessionViewAsync(api, session))!["passwordWindowOpen"]!.GetValue<bool>();

    private Task<HttpResponseMessage> ConfirmAsync(WebApplicationFactory<Program> api, string session, string password = Password) =>
        SendAsync(api, HttpMethod.Post, "/api/session/confirm", new { password }, session: session);

    [Fact]
    public async Task An_unticked_sign_in_sets_a_12_hour_cookie_and_a_normal_session()
    {
        await using var api = Timed();
        var (session, cookie) = await SignInAsync(api, await NewUserAsync(api), remember: false);

        Assert.Contains("max-age=43200", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.True(await WindowOpenAsync(api, session));
        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(await SessionViewAsync(api, session));
    }

    [Fact]
    public async Task A_remembered_sign_in_sets_a_30_day_cookie()
    {
        var api = app.PublicApi;
        var (_, cookie) = await SignInAsync(api, await NewUserAsync(api), remember: true);

        Assert.Contains("max-age=2592000", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_remembered_session_still_reads_and_writes_after_its_window_closed()
    {
        await using var api = Timed();
        var session = await RememberedAsync(api, await NewUserAsync(api));
        using (var vault = await SendAsync(api, HttpMethod.Post, "/api/vaults", new { name = "Notizen" }, session: session)) Assert.Equal(HttpStatusCode.Created, vault.StatusCode);
        using (var note = await SendAsync(api, HttpMethod.Put, "/api/notes/Notizen/plan.md", new { content = "# Plan\n" }, session: session))
        {
            Assert.Equal(HttpStatusCode.Created, note.StatusCode);
        }

        string keyId;
        using (var key = await SendAsync(api, HttpMethod.Post, "/api/keys", new { name = "agent", vaults = new Dictionary<string, string> { ["Notizen"] = "read" } }, session: session))
        {
            Assert.Equal(HttpStatusCode.Created, key.StatusCode);
            keyId = (await JsonAsync(key))["apiKey"]!["id"]!.GetValue<string>();
        }

        _time.Advance(TimeSpan.FromMinutes(31));

        Assert.False(await WindowOpenAsync(api, session));
        string version;
        using (var read = await SendAsync(api, HttpMethod.Get, "/api/notes/Notizen/plan.md", session: session))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            version = (await JsonAsync(read))["version"]!.GetValue<string>();
        }

        using (var written = await SendAsync(api, HttpMethod.Put, "/api/notes/Notizen/plan.md", new { content = "# Plan\n\nMore.\n", version }, session: session))
        {
            Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        }

        using (var tree = await SendAsync(api, HttpMethod.Get, "/api/vaults/Notizen/tree", session: session)) Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        using (var revoked = await SendAsync(api, HttpMethod.Delete, $"/api/keys/{keyId}", session: session)) Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
    }

    [Fact]
    public async Task Confirming_the_password_opens_the_window_without_a_new_cookie()
    {
        await using var api = Timed();
        var session = await RememberedAsync(api, await NewUserAsync(api));
        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.False(await WindowOpenAsync(api, session));

        using var confirmed = await ConfirmAsync(api, session);

        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.False(confirmed.Headers.Contains("Set-Cookie"));
        Assert.True(await WindowOpenAsync(api, session));
    }

    [Fact]
    public async Task A_wrong_password_leaves_the_window_closed()
    {
        await using var api = Timed();
        var session = await RememberedAsync(api, await NewUserAsync(api));
        _time.Advance(TimeSpan.FromMinutes(31));

        using var wrong = await ConfirmAsync(api, session, NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("The password is wrong.", await ErrorAsync(wrong));
        Assert.False(await WindowOpenAsync(api, session));
    }

    [Fact]
    public async Task Confirmations_are_rate_limited_with_the_other_password_checks()
    {
        await using var api = Timed();
        var session = await RememberedAsync(api, await NewUserAsync(api));
        _time.Advance(TimeSpan.FromMinutes(31));

        for (var i = 0; i < 5; i++)
        {
            using var wrong = await ConfirmAsync(api, session, NewPassword);
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        using var limited = await ConfirmAsync(api, session, NewPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        using var right = await ConfirmAsync(api, session);
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
        Assert.False(await WindowOpenAsync(api, session));
    }

    [Fact]
    public async Task A_confirmation_needs_a_session()
    {
        var api = app.PublicApi;
        var session = await RememberedAsync(api, await NewUserAsync(api));
        using (var vault = await SendAsync(api, HttpMethod.Post, "/api/vaults", new { name = "Notizen" }, session: session)) Assert.Equal(HttpStatusCode.Created, vault.StatusCode);
        string key;
        using (var created = await SendAsync(api, HttpMethod.Post, "/api/keys", new { name = "agent", vaults = new Dictionary<string, string> { ["Notizen"] = "read" } }, session: session))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            key = (await JsonAsync(created))["key"]!.GetValue<string>();
        }

        using var withKey = await SendAsync(api, HttpMethod.Post, "/api/session/confirm", new { password = Password }, bearer: key);
        using var without = await SendAsync(api, HttpMethod.Post, "/api/session/confirm", new { password = Password });

        Assert.Equal(HttpStatusCode.Forbidden, withKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
    }

    [Fact]
    public async Task Confirming_in_the_session_kept_by_a_reencryption_works()
    {
        await using var api = Timed();
        var session = await RememberedAsync(api, await NewUserAsync(api));
        using (var reencrypted = await SendAsync(api, HttpMethod.Post, "/api/account/reencrypt", new { password = Password }, session: session))
        {
            Assert.Equal(HttpStatusCode.OK, reencrypted.StatusCode);
        }

        _time.Advance(TimeSpan.FromMinutes(31));
        Assert.False(await WindowOpenAsync(api, session));

        using var confirmed = await ConfirmAsync(api, session);

        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.True(await WindowOpenAsync(api, session));
    }

    [Fact]
    public async Task A_remembered_sign_in_and_a_confirmation_are_logged_by_user_id()
    {
        var log = new CapturedLog();
        await using var api = Timed().WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var username = await NewUserAsync(api);
        var session = await RememberedAsync(api, username);
        var userId = (await SessionViewAsync(api, session))!["user"]!["id"]!.GetValue<string>();
        _time.Advance(TimeSpan.FromMinutes(31));
        using (var confirmed = await ConfirmAsync(api, session)) Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);

        var lines = log.Lines.Where(l => l.Contains(typeof(Kumbu.Server.Api.AccountApi).FullName!, StringComparison.Ordinal)).ToList();

        Assert.Equal(2, lines.Count);
        Assert.Single(lines, l => l.Contains("kept signed in", StringComparison.Ordinal));
        Assert.Single(lines, l => l.Contains("confirmed their password", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.Contains(userId, l, StringComparison.Ordinal));
        Assert.All(lines, l => Assert.DoesNotContain(username, l, StringComparison.Ordinal));
        Assert.All(lines, l => Assert.DoesNotContain(Password, l, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sign-out")]
    [InlineData("end-others")]
    [InlineData("password")]
    [InlineData("recovery")]
    [InlineData("deactivation")]
    [InlineData("account-delete")]
    public async Task Every_way_of_ending_sessions_ends_a_remembered_one(string way)
    {
        var api = app.PublicApi;
        var username = await NewUserAsync(api);
        var session = await RememberedAsync(api, username);
        var userId = (await SessionViewAsync(api, session))!["user"]!["id"]!.GetValue<string>();

        switch (way)
        {
            case "sign-out":
                using (var r = await SendAsync(api, HttpMethod.Delete, "/api/session", session: session)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
                break;
            case "end-others":
                var other = (await SignInAsync(api, username, remember: false)).Secret;
                using (var r = await SendAsync(api, HttpMethod.Post, "/api/sessions/end-others", session: other)) Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
                break;
            case "password":
                using (var r = await SendAsync(api, HttpMethod.Post, "/api/password", new { oldPassword = Password, newPassword = NewPassword }, session: session))
                {
                    Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                }

                break;
            case "recovery":
                var users = api.Services.GetRequiredService<UserCatalog>();
                var (_, key, _) = (await users.TrySignInAsync(username, Password, Ct))!.Value;
                var recoveryKey = await users.RegenerateRecoveryKeyAsync(userId, key, Password, Ct);
                using (var r = await SendAsync(api, HttpMethod.Post, "/api/recover", new { username, recoveryKey, newPassword = NewPassword }, ip: NewIp()))
                {
                    Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                }

                break;
            case "deactivation":
                var admin = (await SignInAsync(api, await NewUserAsync(api, Roles.Admin), remember: false)).Secret;
                using (var r = await SendAsync(api, HttpMethod.Patch, $"/api/admin/users/{userId}", new { active = false }, session: admin))
                {
                    Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
                }

                break;
            case "account-delete":
                using (var r = await SendAsync(api, HttpMethod.Post, "/api/account/delete", new { password = Password, confirm = username }, session: session))
                {
                    Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(way));
        }

        Assert.Null(await SessionViewAsync(api, session));
    }

    [Fact]
    public async Task The_session_view_counts_since_the_previous_visit()
    {
        await using var api = Timed();
        var username = await NewUserAsync(api);
        var signedInAt = Now;
        string session;
        using (var signIn = await SendAsync(api, HttpMethod.Post, "/api/session", new { username, password = Password, remember = true }, ip: NewIp()))
        {
            Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
            // A first sign-in has no previous one.
            Assert.Null((await JsonAsync(signIn))["previousSignInAt"]);
            session = SetCookie(signIn)![(CallerMiddleware.SessionCookie.Length + 1)..].Split(';')[0];
        }

        // The first request after 30 minutes without one begins a visit: the banner counts from the sign-in.
        _time.Advance(TimeSpan.FromMinutes(45));
        Assert.Equal(signedInAt, (await SessionViewAsync(api, session))!["previousSignInAt"]!.GetValue<DateTime>());
        using (var confirmed = await ConfirmAsync(api, session)) Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(signedInAt, (await SessionViewAsync(api, session))!["previousSignInAt"]!.GetValue<DateTime>());
    }

    [Fact]
    public async Task A_confirmation_as_the_first_request_of_a_visit_moves_the_visit_once()
    {
        await using var api = Timed();
        var username = await NewUserAsync(api);
        var session = await RememberedAsync(api, username);
        _time.Advance(TimeSpan.FromMinutes(45));
        var firstVisit = Now;
        Assert.NotNull(await SessionViewAsync(api, session));

        // The confirmation is the first request after the gap: as a request it begins the visit, and confirming moves
        // nothing more, so the previous visit is the first one, not the confirmation's own time.
        _time.Advance(TimeSpan.FromMinutes(45));
        var secondVisit = Now;
        using (var confirmed = await ConfirmAsync(api, session)) Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(firstVisit, (await SessionViewAsync(api, session))!["previousSignInAt"]!.GetValue<DateTime>());
        var user = await app.Database.GetCollection<UserDocument>("users").Find(u => u.Username == username).SingleAsync(Ct);
        Assert.Equal((secondVisit, firstVisit), (user.LastSignInAt, user.PreviousSignInAt));
    }
}
