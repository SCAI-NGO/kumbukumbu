// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kumbu.Server.Accounts;
using Kumbu.Server.Publishing;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Attachments;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>
/// Publish spec §6 end to end, over HTTP and MCP alone: Alice publishes, a stranger reads, a link preview sees a
/// generic page, an edit makes the copy older until she updates it, her agent without "May publish" and Bob the member
/// meet their limits, and a move into an unpublishable folder withdraws the publication.
/// </summary>
[Collection("app")]
public sealed partial class EndToEndTests : IAsyncDisposable
{
    private const string Questions = "/Work/Routing/Questions.md";
    private const string Url = "/api/publications/Work/Routing/Questions.md";
    private const string GenericPage = "<!doctype html><title>Kumbu² reader</title><div id=\"root\"></div>";

    /// <summary>Spec §6's note, with <c>flow.png</c> as <c>flow.jpg</c>.</summary>
    private const string Note = """
        ---
        status: draft
        ---
        # Questions on routing #routing

        1. Should the chatbot move the user between screens? See [[Routing master]].
        2. Is there a list of routes? [Router docs](https://example.org/router)
        %% ask about deep links later %%
        ![[flow.jpg]]

        """;

    private const string NewLine = "3. Who owns the router?";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _webRoot = Directory.CreateTempSubdirectory("kumbu-wwwroot-").FullName;
    private readonly CapturedLog _log = new();
    private readonly WebApplicationFactory<Program> _api;

    public EndToEndTests(KumbuAppFactory app)
    {
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>Kumbu test shell</title>");
        File.WriteAllText(Path.Combine(_webRoot, "s.html"), GenericPage);
        _api = app.PublicApi.WithWebHostBuilder(b => b.UseWebRoot(_webRoot).ConfigureLogging(l => l.AddProvider(_log)));
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        Directory.Delete(_webRoot, recursive: true);
    }

    [GeneratedRegex(@"^/Work/Routing/Questions\.md  \d{4}-\d\d-\d\d \d\d:\d\d UTC  older: no$")]
    private static partial Regex ListedLine();

    [GeneratedRegex("[0-9A-HJKMNP-TV-Z]{32}|([0-9A-HJKMNP-TV-Z]{4}-){7}[0-9A-HJKMNP-TV-Z]{4}")]
    private static partial Regex AnyCode();

    private static async Task<JsonNode> OkJsonAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"answered {(int)response.StatusCode}");
        return await RestClient.JsonAsync(response);
    }

    private static async Task<string> TextAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"answered {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static async Task<string> NotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(ReaderApi.NotFoundSentence, JsonNode.Parse(body)!["error"]!.GetValue<string>());
        return body;
    }

    private static async Task WriteAsync(RestClient user, string url, string content)
    {
        using var read = await user.GetAsync(url);
        var version = (await RestClient.JsonAsync(read))["version"]!.GetValue<string>();
        using var written = await user.SendAsync(HttpMethod.Put, url, new { content, version });
        Assert.True(written.IsSuccessStatusCode, $"the write answered {(int)written.StatusCode}");
    }

    private static async Task MoveAsync(RestClient user, string from, string to)
    {
        using var moved = await user.SendAsync(HttpMethod.Post, "/api/move", new { from, to });
        Assert.True(moved.StatusCode == HttpStatusCode.OK, $"the move answered {(int)moved.StatusCode}");
    }

    private void AssertNoCodeLogged(string code)
    {
        foreach (var secret in new[] { code, ShareCode.Format(code), Convert.ToHexStringLower(ShareCode.Lookup(code)), ShareCode.CopyId(code) })
        {
            Assert.False(_log.Lines.Any(l => l.Contains(secret, StringComparison.OrdinalIgnoreCase)), "the log holds the code");
        }
    }

    [Fact]
    public async Task Spec_section_6()
    {
        // Setup: Alice's Work, Private marked, the note and its camera JPEG; Bob a write member; "Claude" without the right.
        var alice = await RestClient.NewAsync(_api, vault: "Work");
        await alice.CreateAsync("Private/Plan.md", "# Plan\n");
        using (var marked = await alice.SendAsync(HttpMethod.Put, "/api/unpublishable/Work/Private")) Assert.Equal(HttpStatusCode.NoContent, marked.StatusCode);
        await alice.CreateFileAsync("Routing/flow.jpg", ImageMetadataTests.JpegWithGps(1));
        await alice.CreateAsync("Routing/Questions.md", Note);
        var bob = await RestClient.NewAsync(_api, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "Work", MemberRole.Write);
        var (bobKey, _) = await _api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [await alice.VaultIdAsync()] = KeyRight.Write }, Ct);
        var claude = await alice.KeyAsync("Claude", KeyRight.Write);

        // 1. Alice publishes with her password.
        JsonNode published;
        using (var response = await alice.SendAsync(HttpMethod.Post, Url, new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            published = await RestClient.JsonAsync(response);
        }

        var link = published["link"]!.GetValue<string>();
        Assert.True(link.StartsWith(KumbuAppFactory.PublicUrl + "/s#", StringComparison.Ordinal), "the link points at the reader page");
        var fragment = link[(link.IndexOf('#') + 1)..];
        Assert.True(fragment.Length == 39, "the fragment has 39 characters");
        Assert.True(fragment.Replace("-", "", StringComparison.Ordinal).Length == 32, "the code has 32 characters");
        var code = ShareCode.Parse(fragment);
        Assert.True(code is not null, "the fragment is no code");
        var snapshotAt = published["snapshotAt"]!.GetValue<DateTime>();

        // 2. The reader posts the fragment's code.
        using var reader = new ReaderApiTests.Reader(_api, "203.0.113.140");
        string first;
        using (var response = await reader.PostAsync(fragment)) first = await TextAsync(response);
        var copy = JsonNode.Parse(first)!;
        Assert.Equal("Questions on routing", copy["title"]!.GetValue<string>());
        Assert.Equal(snapshotAt, copy["snapshotAt"]!.GetValue<DateTime>());
        var markdown = copy["markdown"]!.GetValue<string>();
        Assert.Contains("1. Should the chatbot move the user between screens? See Routing master.", markdown, StringComparison.Ordinal);
        Assert.Contains("2. Is there a list of routes? [Router docs](https://example.org/router)", markdown, StringComparison.Ordinal);
        foreach (var gone in new[] { "[[", "#routing", "status: draft", "deep links", "Work" })
        {
            Assert.False(markdown.Contains(gone, StringComparison.Ordinal), $"the copy holds {gone}");
        }

        Assert.False(first.Contains(alice.Username, StringComparison.OrdinalIgnoreCase), "the reader's JSON names Alice");
        Assert.Equal(0, Assert.Single(copy["files"]!.AsArray())!["index"]!.GetValue<int>());
        using (var file = await reader.FileAsync(0))
        {
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            var bytes = await file.Content.ReadAsByteArrayAsync(Ct);
            Assert.True(bytes.Length > 0, "the image is empty");
            Assert.True(bytes.AsSpan().IndexOf("GPS48N"u8) < 0, "the image keeps its GPS data");
        }

        AssertNoCodeLogged(code);

        // 3. A link preview: the page without the fragment.
        using (var plain = _api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(KumbuAppFactory.PublicUrl) }))
        using (var page = await plain.GetAsync("/s", Ct))
        {
            var html = await TextAsync(page);
            Assert.Equal(GenericPage, html);
            foreach (var word in new[] { "Questions", "routing", "chatbot", "Router", "flow" })
            {
                Assert.DoesNotContain(word, html, StringComparison.OrdinalIgnoreCase);
            }
        }

        // 4. Alice writes a line: her copy is older, the reader's isn't touched. She updates: the same code reads it.
        await WriteAsync(alice, alice.Url("Routing/Questions.md"), Note + NewLine + "\n");
        Assert.True((await OkJsonAsync(alice.GetAsync(Url)))["older"]!.GetValue<bool>(), "the copy isn't older");
        using (var reload = await reader.GetAsync()) Assert.True(first == await TextAsync(reload), "the reader's copy changed");

        await Task.Delay(5, Ct);
        using (var updated = await alice.SendAsync(HttpMethod.Put, Url)) Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        string second;
        using (var response = await reader.PostAsync(fragment)) second = await TextAsync(response);
        var renewed = JsonNode.Parse(second)!;
        Assert.Contains(NewLine, renewed["markdown"]!.GetValue<string>(), StringComparison.Ordinal);
        var updatedAt = renewed["snapshotAt"]!.GetValue<DateTime>();
        Assert.True(updatedAt > snapshotAt, "the snapshot time didn't move on");
        using (var reload = await reader.GetAsync()) Assert.True(second == await TextAsync(reload), "the reader's copy changed");

        // 5. "Claude", without "May publish".
        var withdraw = await alice.ShAsync($"publish rm {Questions}", claude);
        Assert.True(withdraw.IsError, "the key withdrew");
        Assert.True(withdraw.Text.StartsWith("publish rm: this key may not publish in Work", StringComparison.Ordinal), "the refusal doesn't name the missing right");
        var removal = await alice.ShAsync($"rm {Questions}", claude);
        Assert.True(removal.IsError, "the key removed the note");
        Assert.Contains($"{Questions} is published", removal.Text, StringComparison.Ordinal);
        Assert.Contains("may not publish", removal.Text, StringComparison.Ordinal);
        var listed = await alice.ShAsync("publish ls /Work", claude);
        Assert.False(listed.IsError, "publish ls failed");
        Assert.True(ListedLine().IsMatch(listed.Text), "publish ls gives otherwise than one line");
        Assert.True(listed.Text.Contains($"  {updatedAt:yyyy-MM-dd HH:mm} UTC  ", StringComparison.Ordinal), "publish ls shows another snapshot time");
        Assert.False(AnyCode().IsMatch(listed.Text) || listed.Text.Contains("/s#", StringComparison.Ordinal), "publish ls shows the code");
        using (var reload = await reader.GetAsync()) Assert.True(second == await TextAsync(reload), "the reader's copy changed");

        // 6. Bob, a write member: the path alone; his edit doesn't reach the copy.
        var bobsView = await OkJsonAsync(bob.GetAsync(Url));
        Assert.Equal(Questions, bobsView["path"]!.GetValue<string>());
        Assert.All(new[] { "snapshotAt", "older", "changedAt", "publishedBy", "link" }, field => Assert.True(bobsView[field] is null, $"a member sees {field}"));
        Assert.True(await bob.ShAsync("publish ls /Work", bobKey) == (false, Questions), "a member's publish ls gives more than the path");
        await WriteAsync(bob, "/api/notes/Work/Routing/Questions.md", Note + "Bob's line.\n");
        using (var reload = await reader.GetAsync()) Assert.True(second == await TextAsync(reload), "the reader's copy changed");

        // 7. Alice moves the note: within Work the link reads; into Private it is withdrawn.
        await MoveAsync(alice, Questions, "/Work/Done/Questions.md");
        using (var response = await reader.PostAsync(fragment)) Assert.True(second == await TextAsync(response), "the reader's copy changed");

        await MoveAsync(alice, "/Work/Done/Questions.md", "/Work/Private/Questions.md");
        string withdrawnGet, withdrawnPost, wrong;
        using (var response = await reader.GetAsync()) withdrawnGet = await NotFoundAsync(response);
        using (var response = await reader.PostAsync(fragment)) withdrawnPost = await NotFoundAsync(response);
        using (var stranger = new ReaderApiTests.Reader(_api, "203.0.113.141"))
        using (var response = await stranger.PostAsync(ShareCode.New())) wrong = await NotFoundAsync(response);
        Assert.True(wrong == withdrawnGet, "the withdrawn cookie answers otherwise than a wrong code");
        Assert.True(wrong == withdrawnPost, "the withdrawn code answers otherwise than a wrong code");

        var events = (await OkJsonAsync(alice.GetAsync("/api/vaults/Work/activity")))["events"]!.AsArray();
        Assert.Contains(
            "The publication of /Work/Done/Questions.md was withdrawn: you made it unpublishable.",
            events.Select(e => e!["text"]!.GetValue<string>()));
        string[] publicationKinds = [VaultEvents.NotePublished, VaultEvents.PublicationUpdated, VaultEvents.PublicationWithdrawn];
        foreach (var feed in new[] { (await OkJsonAsync(bob.GetAsync("/api/vault-events"))).AsArray(), (await OkJsonAsync(bob.GetAsync("/api/vaults/Work/activity")))["events"]!.AsArray() })
        {
            Assert.DoesNotContain(feed, e => publicationKinds.Contains(e!["kind"]!.GetValue<string>()) || e["text"]!.GetValue<string>().Contains("publi", StringComparison.OrdinalIgnoreCase));
        }

        AssertNoCodeLogged(code);
    }
}
