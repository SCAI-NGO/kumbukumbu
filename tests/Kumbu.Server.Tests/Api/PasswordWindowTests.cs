// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Tests.OAuth;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Api;

/// <summary>The guard of "keep me signed in": with its normal window closed, a remembered session makes no new
/// credential, widens no rights and exports nothing until the password is given again.</summary>
[Collection("app")]
public sealed class PasswordWindowTests(KumbuAppFactory app)
{
    private const string Callback = "https://claude.test/cb";
    private const string SessionOnlySentence = "This needs a signed-in session: API keys, connected apps and the dev token can't do it.";

    private static readonly byte[] Pdf = "%PDF-1.4\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF\n"u8.ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The time of the timed host, starting at the real time in whole seconds (MongoDB keeps milliseconds).</summary>
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program> Timed() => app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    /// <summary>31 minutes without a request: a remembered session's window closes, the session stays.</summary>
    private void CloseWindow() => _time.Advance(TimeSpan.FromMinutes(31));

    private static async Task ConfirmAsync(RestClient user)
    {
        using var confirmed = await user.SendAsync(HttpMethod.Post, "/api/session/confirm", new { password = RestClient.Password });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
    }

    private static async Task<bool> WindowOpenAsync(RestClient user)
    {
        using var session = await user.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        return (await RestClient.JsonAsync(session))["passwordWindowOpen"]!.GetValue<bool>();
    }

    private static async Task AssertPasswordRequiredAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await RestClient.JsonAsync(response);
        Assert.Equal("password-required", body["type"]?.GetValue<string>());
        Assert.Equal("This needs your password again: confirm it with POST /api/session/confirm.", body["error"]!.GetValue<string>());
    }

    private static object ReadArbeit => new { name = "agent", vaults = new Dictionary<string, string> { ["Arbeit"] = "read" } };

    private static Task<HttpResponseMessage> CreateKeyAsync(RestClient user) => user.SendAsync(HttpMethod.Post, "/api/keys", ReadArbeit);

    private static async Task<JsonArray> ListAsync(RestClient user, string path)
    {
        using var response = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response)).AsArray();
    }

    private static async Task<string> TicketAsync(RestClient user)
    {
        using var response = await user.SendAsync(HttpMethod.Post, "/api/export/ticket", new { password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response))["ticket"]!.GetValue<string>();
    }

    private static string WithTicket(string url, string ticket) => url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "ticket=" + Uri.EscapeDataString(ticket);

    /// <summary>Another user, made by the admin's hand in the catalogue: its id and username.</summary>
    private static async Task<(string Id, string Username)> OtherUserAsync(WebApplicationFactory<Program> api)
    {
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        var (user, _) = await api.Services.GetRequiredService<UserCatalog>().CreateAsync(username, RestClient.Password, Roles.User, Ct);
        return (user.Id, username);
    }

    private static async Task<string> ConnectionAsync(RestClient user)
    {
        using var created = await user.SendAsync(HttpMethod.Post, "/api/connections", ReadArbeit);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
    }

    private static IEnumerable<RouteEndpoint> Endpoints(WebApplicationFactory<Program> api) =>
        api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    private static string Name(RouteEndpoint e) => $"{e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods[0]} {e.RoutePattern.RawText}";

    private static readonly string[] Guarded =
    [
        "GET /api/export",
        "GET /api/vaults/{vault}/checkpoints/{name}/export",
        "GET /api/vaults/{vault}/export",
        "POST /api/connections/",
        "POST /api/keys",
        "POST /api/vaults/{vault}/pairing",
        "PUT /api/connections/{id}/vaults",
    ];

    private const string Reading = "reading: the session reads anyway";
    private const string CleaningUp = "revoking and signing out are never guarded";
    private const string OwnPassword = "asks for the password itself, which opens the window";
    private const string EndsTheSession = "asks for the password itself; ends the session";
    private const string GrantingInline = "guarded inline: only granting needs the window";

    /// <summary>Every session-only endpoint without the marker, and why. A new one fails the test until someone decides.</summary>
    private static readonly Dictionary<string, string> DeliberatelyOpen = new(StringComparer.Ordinal)
    {
        ["GET /api/session"] = Reading,
        ["GET /api/keys"] = Reading,
        ["GET /api/admin/users"] = Reading,
        ["GET /api/admin/deletions"] = Reading,
        ["GET /api/admin/invitations"] = Reading,
        ["GET /api/account/preferences"] = Reading,
        ["GET /api/connections/"] = Reading,
        ["GET /api/oauth/requests/{id}"] = Reading,
        ["GET /api/vaults/{vault}/access"] = Reading,
        ["GET /api/search/earlier"] = Reading,
        ["GET /api/changes"] = Reading,
        ["GET /api/vaults/{vault}/activity"] = Reading,
        ["DELETE /api/session"] = CleaningUp,
        ["DELETE /api/keys/{id}"] = CleaningUp,
        ["DELETE /api/admin/invitations/{id}"] = CleaningUp,
        ["DELETE /api/connections/{id}"] = CleaningUp,
        ["POST /api/sessions/end-others"] = CleaningUp,
        ["POST /api/password"] = EndsTheSession,
        ["POST /api/recovery-key"] = OwnPassword,
        ["POST /api/account/delete"] = EndsTheSession,
        ["POST /api/account/reencrypt"] = OwnPassword,
        ["POST /api/admin/users/{id}/delete"] = OwnPassword,
        ["POST /api/admin/deletions/repeat"] = OwnPassword,
        ["POST /api/vaults/{vault}/delete"] = OwnPassword,
        ["POST /api/vaults/{vault}/rekey"] = OwnPassword,
        ["POST /api/export/ticket"] = OwnPassword,
        ["POST /api/session/confirm"] = OwnPassword,
        ["PUT /api/account/preferences"] = "turning the download password off needs the password; on is safe",
        ["POST /api/vaults"] = "a new empty vault reaches nothing new",
        ["PUT /api/vaults/{vault}/embeddings"] = "deliberate exception, security-architecture.md",
        ["GET /api/files/{vault}/zip"] = "guarded inline: the ticket when the window is closed",
        ["POST /api/files/{vault}/zip"] = "guarded inline: the ticket when the window is closed",
        ["POST /api/oauth/requests/{id}/decision"] = GrantingInline,
        ["PATCH /api/admin/users/{id}"] = GrantingInline,
        ["POST /api/admin/invitations"] = GrantingInline,
    };

    [Fact]
    public void Exactly_these_endpoints_need_the_password_window()
    {
        var guarded = Endpoints(app.PublicApi).Where(e => e.Metadata.GetMetadata<PasswordWindowMetadata>() is not null).Select(Name).Order(StringComparer.Ordinal);

        Assert.Equal(Guarded.Order(StringComparer.Ordinal), guarded);
    }

    [Fact]
    public void Every_guarded_endpoint_is_session_only()
    {
        Assert.All(
            Endpoints(app.PublicApi).Where(e => e.Metadata.GetMetadata<PasswordWindowMetadata>() is not null),
            e => Assert.NotNull(e.Metadata.GetMetadata<SessionOnlyMetadata>()));
    }

    [Fact]
    public void Every_session_only_endpoint_is_guarded_or_deliberately_open()
    {
        var sessionOnly = Endpoints(app.PublicApi).Where(e => e.Metadata.GetMetadata<SessionOnlyMetadata>() is not null).ToList();

        Assert.Empty(sessionOnly.Where(e => e.Metadata.GetMetadata<PasswordWindowMetadata>() is null && !DeliberatelyOpen.ContainsKey(Name(e))).Select(Name));
        // And the list names no endpoint that is gone or guarded after all.
        Assert.Empty(DeliberatelyOpen.Keys.Except(sessionOnly.Where(e => e.Metadata.GetMetadata<PasswordWindowMetadata>() is null).Select(Name)));
    }

    public static TheoryData<string, bool> GuardedRequests()
    {
        var data = new TheoryData<string, bool>();
        foreach (var request in new[] { "keys", "connections", "connection-vaults", "decision", "pairing", "invitation", "activation", "vault-export", "export", "checkpoint-export" })
        {
            data.Add(request, false);
            data.Add(request, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(GuardedRequests))]
    public async Task A_guarded_request_needs_the_window(string request, bool open)
    {
        await using var api = Timed();
        var admin = request is "invitation" or "activation";
        var user = await RestClient.NewAsync(api, remember: true, role: admin ? Roles.Admin : Roles.User);

        // Made while the window is open, right after sign-in.
        string? connection = request is "connection-vaults" or "decision" ? await ConnectionAsync(user) : null;
        (string Id, string Username)? deactivated = null;
        if (request == "activation")
        {
            deactivated = await OtherUserAsync(api);
            using var off = await user.SendAsync(HttpMethod.Patch, $"/api/admin/users/{deactivated.Value.Id}", new { active = false });
            Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        }

        if (request == "checkpoint-export")
        {
            await user.CreateAsync("plan.md", "# Plan\n");
            using var made = await user.SendAsync(HttpMethod.Post, "/api/vaults/Arbeit/checkpoints", new { name = "cp" });
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        }

        CloseWindow();
        if (open) await ConfirmAsync(user);
        // An authorisation request lives 10 minutes: started after the window closed.
        var flow = request == "decision" ? await OAuthFlow.StartAsync(api, user, connection!, await OAuthFlow.RegisterAsync(api, Callback), Callback) : null;

        var url = request switch
        {
            "vault-export" => "/api/vaults/Arbeit/export",
            "export" => "/api/export?vault=Arbeit",
            "checkpoint-export" => "/api/vaults/Arbeit/checkpoints/cp/export",
            _ => null,
        };
        using var response = request switch
        {
            "keys" => await CreateKeyAsync(user),
            "connections" => await user.SendAsync(HttpMethod.Post, "/api/connections", ReadArbeit),
            "connection-vaults" => await user.SendAsync(HttpMethod.Put, $"/api/connections/{connection}/vaults", new { vaults = new Dictionary<string, string> { ["Arbeit"] = "write" } }),
            "decision" => await flow!.DecideAsync(allow: true),
            "pairing" => await user.SendAsync(HttpMethod.Post, "/api/vaults/Arbeit/pairing", new { }),
            "invitation" => await user.SendAsync(HttpMethod.Post, "/api/admin/invitations", new { role = Roles.User }),
            "activation" => await user.SendAsync(HttpMethod.Patch, $"/api/admin/users/{deactivated!.Value.Id}", new { active = true }),
            _ => await user.GetAsync(url!),
        };

        if (!open)
        {
            await AssertPasswordRequiredAsync(response);
            switch (request)
            {
                case "keys":
                    Assert.Empty(await ListAsync(user, "/api/keys"));
                    break;
                case "connections":
                    Assert.Empty(await ListAsync(user, "/api/connections"));
                    break;
                case "connection-vaults":
                    Assert.Equal("read", (await ListAsync(user, "/api/connections")).Single()!["vaults"]!["Arbeit"]!.GetValue<string>());
                    break;
                case "decision":
                    using (var still = await flow!.GetConsentAsync()) Assert.Equal(HttpStatusCode.OK, still.StatusCode);
                    break;
                case "invitation":
                    // Other tests' admins share the list.
                    Assert.DoesNotContain(await ListAsync(user, "/api/admin/invitations"), i => i!["createdBy"]!.GetValue<string>() == user.User.Id);
                    break;
                case "activation":
                    var listed = (await ListAsync(user, "/api/admin/users")).Single(u => u!["id"]!.GetValue<string>() == deactivated!.Value.Id)!;
                    Assert.False(listed["active"]!.GetValue<bool>());
                    break;
            }

            return;
        }

        switch (request)
        {
            case "keys" or "connections" or "pairing" or "invitation":
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                break;
            case "connection-vaults":
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                break;
            case "decision":
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var redirect = (await RestClient.JsonAsync(response))["redirect"]!.GetValue<string>();
                Assert.True(OAuthFlow.QueryOf(redirect).ContainsKey("code"));
                break;
            case "activation":
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                break;
            default:
                // An export still needs its ticket, whose password opens the window anyway.
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                using (var exported = await user.GetAsync(WithTicket(url!, await TicketAsync(user)))) Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
                break;
        }
    }

    [Fact]
    public async Task What_grants_nothing_or_brings_the_password_passes_with_the_window_closed()
    {
        await using var api = Timed();
        var user = await RestClient.NewAsync(api, remember: true, role: Roles.Admin);
        var connection = await ConnectionAsync(user);
        var (otherId, _) = await OtherUserAsync(api);
        var (promotedId, _) = await OtherUserAsync(api);
        var (demotedId, _) = await OtherUserAsync(api);
        using (var made = await user.SendAsync(HttpMethod.Patch, $"/api/admin/users/{demotedId}", new { role = Roles.Admin, password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.NoContent, made.StatusCode);
        }

        CloseWindow();

        var flow = await OAuthFlow.StartAsync(api, user, connection, await OAuthFlow.RegisterAsync(api, Callback), Callback);
        using (var denied = await flow.DecideAsync(allow: false)) Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        using (var gone = await flow.GetConsentAsync()) Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using (var off = await user.SendAsync(HttpMethod.Patch, $"/api/admin/users/{otherId}", new { active = false })) Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        using (var demoted = await user.SendAsync(HttpMethod.Patch, $"/api/admin/users/{demotedId}", new { role = Roles.User })) Assert.Equal(HttpStatusCode.NoContent, demoted.StatusCode);
        Assert.Equal(Roles.User, (await ListAsync(user, "/api/admin/users")).Single(u => u!["id"]!.GetValue<string>() == demotedId)!["role"]!.GetValue<string>());
        Assert.False(await WindowOpenAsync(user));

        using (var promoted = await user.SendAsync(HttpMethod.Patch, $"/api/admin/users/{promotedId}", new { role = Roles.Admin, password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.NoContent, promoted.StatusCode);
        }

        Assert.True(await WindowOpenAsync(user));
        CloseWindow();
        using (var invited = await user.SendAsync(HttpMethod.Post, "/api/admin/invitations", new { role = Roles.Admin, password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
        }

        Assert.True(await WindowOpenAsync(user));
    }

    [Fact]
    public async Task The_window_closed_by_12_hours_since_the_password_guards_too()
    {
        await using var api = Timed();
        var user = await RestClient.NewAsync(api, remember: true);

        // Used every 20 minutes: never idle, but 12 hours after the password the window closes.
        for (var minutes = 20; minutes <= 12 * 60; minutes += 20)
        {
            _time.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(minutes < 12 * 60, await WindowOpenAsync(user));
        }

        using (var refused = await CreateKeyAsync(user)) await AssertPasswordRequiredAsync(refused);
        await ConfirmAsync(user);
        using var created = await CreateKeyAsync(user);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task A_normal_session_is_never_asked()
    {
        await using var api = Timed();
        var user = await RestClient.NewAsync(api);
        _time.Advance(TimeSpan.FromMinutes(29));

        using var created = await CreateKeyAsync(user);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Null((await RestClient.JsonAsync(created))["type"]);
        Assert.True(await WindowOpenAsync(user));
    }

    [Fact]
    public async Task Api_keys_and_connections_still_get_403_on_guarded_endpoints()
    {
        var user = await RestClient.NewAsync(app.PublicApi);
        var key = await user.KeyAsync("agent", Kumbu.Server.Accounts.KeyRight.Write);

        using var refused = await user.SendAsync(HttpMethod.Post, "/api/keys", ReadArbeit, bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await RestClient.JsonAsync(refused);
        Assert.Equal(SessionOnlySentence, body["error"]!.GetValue<string>());
        Assert.Null(body["type"]);
    }

    [Fact]
    public async Task A_forged_origin_is_refused_before_the_window_is_asked()
    {
        await using var api = Timed();
        var user = await RestClient.NewAsync(api, remember: true);
        CloseWindow();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/keys")
        {
            Content = new StringContent(JsonSerializer.Serialize(ReadArbeit), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={user.Session}");
        request.Headers.Add("Origin", "https://evil.test");
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        using var forged = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Null((await RestClient.JsonAsync(forged))["type"]);
    }

    private static async Task TurnDownloadPasswordOffAsync(RestClient user)
    {
        using var off = await user.SendAsync(HttpMethod.Put, "/api/account/preferences", new { askPasswordForDownloads = false, password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
    }

    private static Task<HttpResponseMessage> PostZipAsync(RestClient user, string? ticket = null)
    {
        var fields = new List<KeyValuePair<string, string>> { new("path", "a.pdf") };
        if (ticket is not null) fields.Add(new("ticket", ticket));
        return user.SendContentAsync(HttpMethod.Post, "/api/files/Arbeit/zip", new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task The_files_zip_needs_the_ticket_while_the_window_is_closed()
    {
        await using var api = Timed();
        var user = await RestClient.NewAsync(api, remember: true);
        await user.CreateFileAsync("a.pdf", Pdf);
        await TurnDownloadPasswordOffAsync(user);
        const string zip = "/api/files/Arbeit/zip?path=a.pdf";

        CloseWindow();
        using (var refused = await user.GetAsync(zip))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("POST /api/export/ticket", await RestClient.ErrorAsync(refused), StringComparison.Ordinal);
        }

        using (var withTicket = await user.GetAsync(WithTicket(zip, await TicketAsync(user)))) Assert.Equal(HttpStatusCode.OK, withTicket.StatusCode);
        // The ticket's password opened the window.
        using (var after = await user.GetAsync(zip)) Assert.Equal(HttpStatusCode.OK, after.StatusCode);

        CloseWindow();
        using (var refused = await PostZipAsync(user))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("POST /api/export/ticket", await RestClient.ErrorAsync(refused), StringComparison.Ordinal);
        }

        using (var withTicket = await PostZipAsync(user, await TicketAsync(user))) Assert.Equal(HttpStatusCode.OK, withTicket.StatusCode);
        using (var after = await PostZipAsync(user)) Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task The_files_zip_without_the_preference_and_with_the_window_open_needs_no_ticket()
    {
        await using var api = Timed();
        var user = await RestClient.NewAsync(api, remember: true);
        await user.CreateFileAsync("a.pdf", Pdf);
        await TurnDownloadPasswordOffAsync(user);
        _time.Advance(TimeSpan.FromMinutes(29));

        using var zip = await user.GetAsync("/api/files/Arbeit/zip?path=a.pdf");
        using var posted = await PostZipAsync(user);

        Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
    }

    [Theory]
    [InlineData("ticket")]
    [InlineData("recovery-key")]
    [InlineData("reencrypt")]
    [InlineData("preferences")]
    [InlineData("rekey")]
    [InlineData("delete-vault")]
    [InlineData("delete-user")]
    [InlineData("repeat-deletions")]
    public async Task Every_password_check_opens_the_window(string action)
    {
        await using var api = Timed();
        var admin = action is "delete-user" or "repeat-deletions";
        var user = await RestClient.NewAsync(api, remember: true, role: admin ? Roles.Admin : Roles.User);
        if (action == "delete-vault")
        {
            using var second = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Alt" });
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        }

        var other = action == "delete-user" ? await OtherUserAsync(api) : default;
        CloseWindow();
        var password = RestClient.Password;

        using var response = action switch
        {
            "ticket" => await user.SendAsync(HttpMethod.Post, "/api/export/ticket", new { password }),
            "recovery-key" => await user.SendAsync(HttpMethod.Post, "/api/recovery-key", new { currentPassword = password }),
            "reencrypt" => await user.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password }),
            "preferences" => await user.SendAsync(HttpMethod.Put, "/api/account/preferences", new { askPasswordForDownloads = false, password }),
            "rekey" => await user.SendAsync(HttpMethod.Post, "/api/vaults/Arbeit/rekey", new { password }),
            "delete-vault" => await user.SendAsync(HttpMethod.Post, "/api/vaults/Alt/delete", new { password, confirm = "Alt" }),
            "delete-user" => await user.SendAsync(HttpMethod.Post, $"/api/admin/users/{other.Id}/delete", new { password, confirm = other.Username }),
            "repeat-deletions" => await user.SendAsync(HttpMethod.Post, "/api/admin/deletions/repeat", new { password }),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        Assert.True(response.IsSuccessStatusCode, $"{action}: {(int)response.StatusCode}");
        Assert.True(await WindowOpenAsync(user));
        // Re-encrypting goes on in the background, and a vault being re-keyed makes no key (503) meanwhile.
        if (action == "reencrypt") return;
        using var created = await CreateKeyAsync(user);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_on_a_ticket_leaves_the_window_closed()
    {
        await using var api = Timed();
        var user = await RestClient.NewAsync(api, remember: true);
        CloseWindow();

        using (var wrong = await user.SendAsync(HttpMethod.Post, "/api/export/ticket", new { password = "not the password at all" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        using var refused = await CreateKeyAsync(user);
        await AssertPasswordRequiredAsync(refused);
    }

    [Theory]
    [InlineData("delete-vault")]
    [InlineData("rekey")]
    public async Task A_wrong_password_on_a_vault_action_leaves_the_window_closed(string action)
    {
        await using var api = Timed();
        var user = await RestClient.NewAsync(api, remember: true);
        CloseWindow();
        var password = "not the password at all";

        using (var wrong = action == "rekey"
            ? await user.SendAsync(HttpMethod.Post, "/api/vaults/Arbeit/rekey", new { password })
            : await user.SendAsync(HttpMethod.Post, "/api/vaults/Arbeit/delete", new { password, confirm = "Arbeit" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        using var refused = await CreateKeyAsync(user);
        await AssertPasswordRequiredAsync(refused);
    }

    [Fact]
    public async Task Polling_the_board_moves_no_clock_of_a_remembered_session()
    {
        await using var api = Timed();
        var user = await RestClient.NewAsync(api, remember: true);
        using (var created = await user.SendAsync(HttpMethod.Post, "/api/vaults/Arbeit/board")) Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var slots = api.Services.GetRequiredService<IMongoDatabase>().GetCollection<UserSlotDocument>("user_slots");
        Task<UserSlotDocument> SlotAsync() => slots.Find(s => s.User == user.User.Id && s.Kind == UserSlotDocument.Session).SingleAsync(Ct);
        var before = (await SlotAsync()).LastUsedAt;

        // An open board tab asks every 5 seconds; every 5 minutes is enough to show that none of it counts as use.
        for (var minutes = 5; minutes <= 35; minutes += 5)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            using var board = await user.GetAsync("/api/vaults/Arbeit/board");
            Assert.Equal(HttpStatusCode.OK, board.StatusCode);
        }

        Assert.Equal(before, (await SlotAsync()).LastUsedAt);
        Assert.False(await WindowOpenAsync(user));
    }

    [Fact]
    public async Task Switching_embeddings_is_not_guarded()
    {
        await using var api = Timed();
        var user = await RestClient.NewAsync(api, remember: true);
        CloseWindow();

        using var switched = await user.SendAsync(HttpMethod.Put, "/api/vaults/Arbeit/embeddings", new { provider = "internal" });

        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
    }
}
