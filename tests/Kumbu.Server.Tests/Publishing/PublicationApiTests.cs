// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Publishing;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>The owner's publications in the REST API (publish spec §3.5, §3.11, §3.13): her session alone, the
/// password as an invitation's or the open window, the link only on her single-note read, a member the path alone.</summary>
[Collection("app")]
public sealed class PublicationApiTests(KumbuAppFactory app)
{
    private const string Questions = "/api/publications/Work/Routing/Questions.md";
    private const string Wrong = "not the password at all";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program> Timed() => Api.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    /// <summary>31 minutes without a request: a remembered session's window closes, the session stays.</summary>
    private void CloseWindow() => _time.Advance(TimeSpan.FromMinutes(31));

    private static async Task<RestClient> AliceAsync(WebApplicationFactory<Program> api, bool remember = false)
    {
        var alice = await RestClient.NewAsync(api, vault: "Work", remember: remember);
        await alice.CreateAsync("Routing/Questions.md", "# Questions on routing\n\nShould the chatbot move the user between screens?\n");
        return alice;
    }

    private static Task<HttpResponseMessage> PublishAsync(RestClient user, string url = Questions, string? password = null) =>
        user.SendAsync(HttpMethod.Post, url, password is null ? new { } : new { password });

    /// <summary>Publishes and returns the answer's JSON.</summary>
    private static async Task<JsonNode> PublishedAsync(RestClient user, string url = Questions)
    {
        using var published = await PublishAsync(user, url);
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        return await RestClient.JsonAsync(published);
    }

    private static string CodeOf(JsonNode published) => ShareCode.Parse(published["link"]!.GetValue<string>().Split('#')[1])!;

    private static async Task AssertPasswordRequiredAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("password-required", (await RestClient.JsonAsync(response))["type"]?.GetValue<string>());
    }

    private static async Task<JsonNode> OkJsonAsync(RestClient user, string path)
    {
        using var response = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await RestClient.JsonAsync(response);
    }

    private static async Task<string> OkTextAsync(RestClient user, string path)
    {
        using var response = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static async Task<bool> WindowOpenAsync(RestClient user) => (await OkJsonAsync(user, "/api/session"))["passwordWindowOpen"]!.GetValue<bool>();

    private static async Task AssertStatusAsync(HttpStatusCode expected, Task<HttpResponseMessage> request)
    {
        using var response = await request;
        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<(HttpStatusCode Status, JsonNode Body)> AnswerAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return (response.StatusCode, await RestClient.JsonAsync(response));
    }

    [Fact]
    public async Task Publishing_without_a_password_needs_the_open_window()
    {
        await using var api = Timed();
        var alice = await AliceAsync(api, remember: true);
        CloseWindow();

        using (var refused = await PublishAsync(alice)) await AssertPasswordRequiredAsync(refused);
        var (wrongStatus, wrong) = await AnswerAsync(PublishAsync(alice, password: Wrong));
        Assert.Equal(HttpStatusCode.BadRequest, wrongStatus);
        Assert.Equal("The password is wrong.", wrong["error"]!.GetValue<string>());
        await AssertStatusAsync(HttpStatusCode.NotFound, alice.GetAsync(Questions));

        using var published = await PublishAsync(alice, password: RestClient.Password);

        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        var body = await RestClient.JsonAsync(published);
        Assert.StartsWith(KumbuAppFactory.PublicUrl + "/s#", body["link"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotNull(ShareCode.Parse(body["link"]!.GetValue<string>().Split('#')[1]));
        Assert.Empty(body["plain"]!.AsArray());
        Assert.Equal(_time.GetUtcNow().UtcDateTime, body["snapshotAt"]!.GetValue<DateTime>());
    }

    [Fact]
    public async Task Within_the_window_no_password_is_asked()
    {
        await using var api = Timed();
        var remembered = await AliceAsync(api, remember: true);
        var normal = await AliceAsync(api);
        _time.Advance(TimeSpan.FromMinutes(29));

        await PublishedAsync(remembered);
        await PublishedAsync(normal);
    }

    [Fact]
    public async Task A_password_given_opens_the_window()
    {
        await using var api = Timed();
        var alice = await AliceAsync(api, remember: true);
        CloseWindow();
        Assert.False(await WindowOpenAsync(alice));

        var (status, published) = await AnswerAsync(PublishAsync(alice, password: RestClient.Password));
        Assert.Equal(HttpStatusCode.Created, status);

        Assert.True(await WindowOpenAsync(alice));
        var (updatedStatus, updated) = await AnswerAsync(alice.SendAsync(HttpMethod.Put, Questions));
        Assert.Equal(HttpStatusCode.OK, updatedStatus);
        Assert.Equal(published["link"]!.GetValue<string>(), updated["link"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_needs_the_window_and_withdrawal_doesnt()
    {
        await using var api = Timed();
        var alice = await AliceAsync(api, remember: true);
        var code = CodeOf(await PublishedAsync(alice));
        CloseWindow();

        using (var refused = await alice.SendAsync(HttpMethod.Put, Questions)) await AssertPasswordRequiredAsync(refused);
        await AssertStatusAsync(HttpStatusCode.NoContent, alice.SendAsync(HttpMethod.Delete, Questions));

        var (status, gone) = await AnswerAsync(alice.GetAsync(Questions));
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("/Work/Routing/Questions.md is not published.", gone["error"]!.GetValue<string>());
        Assert.Null(await api.Services.GetRequiredService<PublicationStore>().OpenCopyAsync(code, Ct));
    }

    [Fact]
    public async Task The_owners_note_view_holds_the_link_and_a_members_doesnt()
    {
        var alice = await AliceAsync(Api);
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var published = await PublishedAsync(alice);
        var code = CodeOf(published);
        var snapshotAt = published["snapshotAt"]!.GetValue<string>();

        var own = await OkJsonAsync(alice, Questions);
        Assert.Equal("/Work/Routing/Questions.md", own["path"]!.GetValue<string>());
        Assert.Equal(published["link"]!.GetValue<string>(), own["link"]!.GetValue<string>());
        Assert.Equal(snapshotAt, own["snapshotAt"]!.GetValue<string>());
        Assert.False(own["older"]!.GetValue<bool>());
        Assert.Null(own["changedAt"]);
        Assert.False(string.IsNullOrEmpty(own["publishedBy"]!.GetValue<string>()));
        var listed = (await OkJsonAsync(alice, "/api/vaults/Work/publications"))["publications"]!.AsArray().Single()!;
        Assert.Equal("/Work/Routing/Questions.md", listed["path"]!.GetValue<string>());
        Assert.Equal(snapshotAt, listed["snapshotAt"]!.GetValue<string>());
        Assert.Null(listed["link"]);
        var overview = await OkTextAsync(alice, "/api/publications");
        Assert.Contains("/Work/Routing/Questions.md", overview, StringComparison.Ordinal);

        var bobsNote = await OkTextAsync(bob, "/api/publications/alices-work/Routing/Questions.md");
        var bobsList = await OkTextAsync(bob, "/api/vaults/alices-work/publications");

        var note = JsonNode.Parse(bobsNote)!;
        Assert.Equal("/alices-work/Routing/Questions.md", note["path"]!.GetValue<string>());
        Assert.All(new[] { "snapshotAt", "older", "changedAt", "publishedBy", "link" }, field => Assert.Null(note[field]));
        Assert.Equal("/alices-work/Routing/Questions.md", JsonNode.Parse(bobsList)!["publications"]!.AsArray().Single()!["path"]!.GetValue<string>());
        foreach (var text in new[] { bobsNote, bobsList, overview })
        {
            Assert.DoesNotContain(code, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ShareCode.Format(code), text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(snapshotAt, bobsNote, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshotAt, bobsList, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marks_are_the_owners()
    {
        var alice = await AliceAsync(Api);
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Maintain);

        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            var (status, body) = await AnswerAsync(bob.SendAsync(method, "/api/unpublishable/alices-work/Routing"));
            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Equal("You have no vault named 'alices-work'.", body["error"]!.GetValue<string>());
        }

        Assert.Empty((await OkJsonAsync(alice, "/api/vaults/Work/publications"))["unpublishable"]!.AsArray());
        await AssertStatusAsync(HttpStatusCode.NoContent, alice.SendAsync(HttpMethod.Put, "/api/unpublishable/Work/Routing"));

        var marks = (await OkJsonAsync(alice, "/api/vaults/Work/publications"))["unpublishable"]!.AsArray();
        Assert.Equal("/Work/Routing", Assert.Single(marks)!.GetValue<string>());
        Assert.Empty((await OkJsonAsync(bob, "/api/vaults/alices-work/publications"))["unpublishable"]!.AsArray());
        var (refused, why) = await AnswerAsync(PublishAsync(alice));
        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Equal("unpublishable", why["code"]!.GetValue<string>());

        await AssertStatusAsync(HttpStatusCode.NoContent, alice.SendAsync(HttpMethod.Delete, "/api/unpublishable/Work/Routing"));
        Assert.Empty((await OkJsonAsync(alice, "/api/vaults/Work/publications"))["unpublishable"]!.AsArray());
    }

    [Fact]
    public async Task Refusals_answer_with_their_status()
    {
        var alice = await AliceAsync(Api);
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Maintain);
        await PublishedAsync(alice);

        var (memberStatus, member) = await AnswerAsync(PublishAsync(bob, "/api/publications/alices-work/Routing/Ideas.md"));
        Assert.Equal(HttpStatusCode.Forbidden, memberStatus);
        Assert.Equal("not-owner", member["code"]!.GetValue<string>());
        var (memberWithdraws, _) = await AnswerAsync(bob.SendAsync(HttpMethod.Delete, "/api/publications/alices-work/Routing/Questions.md"));
        Assert.Equal(HttpStatusCode.Forbidden, memberWithdraws);

        var (missingStatus, missing) = await AnswerAsync(PublishAsync(alice, "/api/publications/Work/Missing.md"));
        Assert.Equal(HttpStatusCode.NotFound, missingStatus);
        Assert.Equal("/Work/Missing.md: no such note.", missing["error"]!.GetValue<string>());
        Assert.Null(missing["code"]);

        var (againStatus, again) = await AnswerAsync(PublishAsync(alice));
        Assert.Equal(HttpStatusCode.Conflict, againStatus);
        Assert.StartsWith("/Work/Routing/Questions.md is already published", again["error"]!.GetValue<string>(), StringComparison.Ordinal);

        await alice.CreateAsync("Ideas.md", "# Ideas\n");
        var (notPublished, _) = await AnswerAsync(alice.SendAsync(HttpMethod.Delete, "/api/publications/Work/Ideas.md"));
        Assert.Equal(HttpStatusCode.NotFound, notPublished);
        var (noVault, _) = await AnswerAsync(alice.GetAsync("/api/vaults/Elsewhere/publications"));
        Assert.Equal(HttpStatusCode.NotFound, noVault);
    }

    [Fact]
    public async Task Keys_cannot_use_the_publication_api()
    {
        var alice = await AliceAsync(Api);
        await PublishedAsync(alice);
        var key = await alice.KeyAsync("Claude", KeyRight.Write, mayPublish: true);

        foreach (var (method, url) in new[]
        {
            (HttpMethod.Get, "/api/publications"),
            (HttpMethod.Get, "/api/vaults/Work/publications"),
            (HttpMethod.Get, Questions),
            (HttpMethod.Post, Questions),
            (HttpMethod.Put, Questions),
            (HttpMethod.Delete, Questions),
            (HttpMethod.Put, "/api/unpublishable/Work/Routing"),
            (HttpMethod.Delete, "/api/unpublishable/Work/Routing"),
        })
        {
            var (status, body) = await AnswerAsync(alice.SendAsync(method, url, new { }, bearer: key));
            Assert.True(status == HttpStatusCode.Forbidden, $"{method} {url}: {(int)status}");
            Assert.Equal("This needs a signed-in session: API keys, connected apps and the dev token can't do it.", body["error"]!.GetValue<string>());
        }

        Assert.NotNull((await OkJsonAsync(alice, Questions))["link"]);
        Assert.Empty((await OkJsonAsync(alice, "/api/vaults/Work/publications"))["unpublishable"]!.AsArray());
    }

    /// <summary>A sign-in on <paramref name="api"/>: its answer's JSON.</summary>
    private static async Task<JsonNode> SignInAsync(WebApplicationFactory<Program> api, RestClient user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { username = user.Username, password = RestClient.Password }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", RestClient.Origin);
        request.Headers.Add("X-Forwarded-For", $"198.21.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await RestClient.JsonAsync(response);
    }

    [Fact]
    public async Task The_session_says_whether_publishing_is_on()
    {
        var alice = await AliceAsync(Api);
        await using var off = Api.WithWebHostBuilder(b => b.UseSetting("Kumbu:Publish:Enabled", "false"));

        Assert.True((await OkJsonAsync(alice, "/api/session"))["publishEnabled"]!.GetValue<bool>());
        Assert.True((await SignInAsync(Api, alice))["publishEnabled"]!.GetValue<bool>());
        Assert.False((await OkJsonAsync(alice.On(off), "/api/session"))["publishEnabled"]!.GetValue<bool>());
        Assert.False((await SignInAsync(off, alice))["publishEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Switched_off_withdrawing_and_marks_still_work()
    {
        var alice = await AliceAsync(Api);
        await alice.CreateAsync("Ideas.md", "# Ideas\n");
        var code = CodeOf(await PublishedAsync(alice));
        await using var off = Api.WithWebHostBuilder(b => b.UseSetting("Kumbu:Publish:Enabled", "false"));
        var offAlice = alice.On(off);

        foreach (var (method, url) in new[] { (HttpMethod.Post, "/api/publications/Work/Ideas.md"), (HttpMethod.Put, Questions) })
        {
            var (status, body) = await AnswerAsync(offAlice.SendAsync(method, url, new { }));
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("publishing-off", body["code"]!.GetValue<string>());
            Assert.Equal("Publishing is switched off on this server.", body["error"]!.GetValue<string>());
        }

        var store = Api.Services.GetRequiredService<PublicationStore>();
        Assert.NotNull(await store.OpenCopyAsync(code, Ct));
        await AssertStatusAsync(HttpStatusCode.NoContent, offAlice.SendAsync(HttpMethod.Delete, Questions));
        Assert.Null(await store.OpenCopyAsync(code, Ct));
        Assert.Empty((await OkJsonAsync(offAlice, "/api/vaults/Work/publications"))["publications"]!.AsArray());

        await AssertStatusAsync(HttpStatusCode.NoContent, offAlice.SendAsync(HttpMethod.Put, "/api/unpublishable/Work/Ideas.md"));
        Assert.Equal("/Work/Ideas.md", Assert.Single((await OkJsonAsync(offAlice, "/api/vaults/Work/publications"))["unpublishable"]!.AsArray())!.GetValue<string>());
        await AssertStatusAsync(HttpStatusCode.NoContent, offAlice.SendAsync(HttpMethod.Delete, "/api/unpublishable/Work/Ideas.md"));
        Assert.Empty((await OkJsonAsync(offAlice, "/api/vaults/Work/publications"))["unpublishable"]!.AsArray());
    }

    [Fact]
    public async Task An_empty_path_is_404()
    {
        var alice = await AliceAsync(Api);

        foreach (var (method, url) in new[]
        {
            (HttpMethod.Get, "/api/publications/Work/"),
            (HttpMethod.Get, "/api/publications/Work"),
            (HttpMethod.Post, "/api/publications/Work/"),
            (HttpMethod.Put, "/api/publications/Work/"),
            (HttpMethod.Delete, "/api/publications/Work/"),
            (HttpMethod.Put, "/api/unpublishable/Work/"),
            (HttpMethod.Put, "/api/unpublishable/Work"),
            (HttpMethod.Delete, "/api/unpublishable/Work/"),
        })
        {
            using var response = await alice.SendAsync(method, url, new { });
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {url}: {(int)response.StatusCode}");
        }

        Assert.Empty((await OkJsonAsync(alice, "/api/vaults/Work/publications"))["unpublishable"]!.AsArray());
    }

    [Fact]
    public async Task The_overview_lists_her_vaults_only()
    {
        var alice = await AliceAsync(Api);
        await AssertStatusAsync(HttpStatusCode.Created, alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Gartenarbeit" }));
        await AssertStatusAsync(HttpStatusCode.Created, alice.SendAsync(HttpMethod.Put, "/api/notes/Gartenarbeit/Beete.md", new { content = "# Beete\n" }));
        var carol = await RestClient.NewAsync(Api, vault: "Work");
        await carol.CreateAsync("Plan.md", "# Plan\n");
        await carol.ShareWithAsync(alice, "carols-work", MemberRole.Write);
        await PublishedAsync(alice);
        await PublishedAsync(alice, "/api/publications/Gartenarbeit/Beete.md");
        await PublishedAsync(carol, "/api/publications/Work/Plan.md");

        var overview = (await OkJsonAsync(alice, "/api/publications")).AsArray();

        Assert.Equal(["/Gartenarbeit/Beete.md", "/Work/Routing/Questions.md"], overview.Select(e => e!["path"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.All(overview, e =>
        {
            Assert.Null(e!["link"]);
            Assert.False(e["older"]!.GetValue<bool>());
            Assert.NotNull(e["snapshotAt"]);
            Assert.False(string.IsNullOrEmpty(e["publishedBy"]!.GetValue<string>()));
        });
        // She sees that Carol's note is published, in Carol's vault, and nothing more.
        Assert.Equal("/carols-work/Plan.md", (await OkJsonAsync(alice, "/api/vaults/carols-work/publications"))["publications"]!.AsArray().Single()!["path"]!.GetValue<string>());
    }
}
