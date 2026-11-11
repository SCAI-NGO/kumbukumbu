// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Publishing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>
/// The AI link (AI link spec §3.2–§3.5): a reader with the cookie makes it; an AI's fetch tool, without cookies, reads
/// the copy as Markdown and its images under it; every end answers the one plain-text 404; the reader's limits and
/// headers; nothing of the token in a log.
/// </summary>
[Collection("app")]
public sealed class AiLinkApiTests(KumbuAppFactory app)
{
    private const string Plan = "/Work/Beds/Plan.md";

    private const string PlainText = "text/plain; charset=utf-8";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static readonly WriterRef BySession = WriterRef.ForOwner(WriterRef.Session, null);

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    /// <summary>The app on <paramref name="time"/>, which starts at the real time: else MongoDB's TTL monitor deletes the
    /// documents mid-test.</summary>
    private WebApplicationFactory<Program> Timed(FakeTimeProvider time) => Api.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(time);
    }));

    private static FakeTimeProvider Now() => new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    /// <summary>An AI's fetch tool: no cookies, no session, from <see cref="Address"/>.</summary>
    private sealed class Fetcher(WebApplicationFactory<Program> api, string address) : IDisposable
    {
        public string Address { get; set; } = address;

        private readonly HttpClient _client = api.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? origin = null)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Add("X-Forwarded-For", Address);
            if (origin is not null) request.Headers.Add("Origin", origin);
            return await _client.SendAsync(request, Ct);
        }

        public Task<HttpResponseMessage> GetAsync(string url) => SendAsync(HttpMethod.Get, url);

        public void Dispose() => _client.Dispose();
    }

    /// <summary><c>POST /api/s/ai-links</c> with the reader's cookie jar and <paramref name="copy"/>.</summary>
    private static async Task<HttpResponseMessage> MakeAiLinkAsync(
        WebApplicationFactory<Program> api, ReaderApiTests.Reader reader, string? copy, string? origin = RestClient.Origin)
    {
        using var client = api.CreateDefaultClient(new Uri(KumbuAppFactory.PublicUrl), new CookieContainerHandler(reader.Jar));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ReaderApi.CookiePath}/ai-links");
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (copy is not null) request.Headers.Add(ReaderApi.CopyHeader, copy);
        request.Headers.Add("X-Forwarded-For", reader.Address);
        var response = await client.SendAsync(request, Ct);
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }

    private sealed record Linked(ReaderApiTests.Published Published, ReaderApiTests.Reader Reader, string Link, string Token, DateTime ExpiresAt) : IDisposable
    {
        public string Path => Link[KumbuAppFactory.PublicUrl.Length..];

        public void Dispose() => Reader.Dispose();
    }

    /// <summary>A published note (<see cref="ReaderApiTests.PublishedAsync"/>), read by a reader at <paramref name="address"/>,
    /// who makes an AI link.</summary>
    private static async Task<Linked> LinkedAsync(WebApplicationFactory<Program> api, string address, ReaderApiTests.Published? published = null)
    {
        published ??= await ReaderApiTests.PublishedAsync(api);
        var reader = new ReaderApiTests.Reader(api, address);
        using (var read = await reader.PostAsync(published.Formatted)) Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var made = await MakeAiLinkAsync(api, reader, reader.Copy);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var body = await RestClient.JsonAsync(made);
        var link = body["link"]!.GetValue<string>();
        return new Linked(published, reader, link, link[(link.LastIndexOf('/') + 1)..], body["expiresAt"]!.GetValue<DateTime>().ToUniversalTime());
    }

    private static async Task AssertGoneAsync(HttpResponseMessage response, string? what = null)
    {
        Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{what} answered {(int)response.StatusCode}");
        Assert.Equal(PlainText, response.Content.Headers.ContentType?.ToString());
        Assert.Equal(AiLinkApi.NotFoundSentence, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<string> AssertMarkdownAsync(HttpResponseMessage response, string? what = null)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{what} answered {(int)response.StatusCode}");
        Assert.Equal("text/markdown; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Dig on Monday.", text, StringComparison.Ordinal);
        return text;
    }

    private async Task SetActiveAsync(RestClient user, bool active)
    {
        var admin = await RestClient.NewAsync(Api, vault: "Admin");
        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == admin.User.Id, Builders<UserDocument>.Update.Set(u => u.Role, Roles.Admin), cancellationToken: Ct);
        Assert.True(await Api.Services.GetRequiredService<Admin>().SetActiveAsync(admin.User, user.User.Id, active, Ct));
    }

    private async Task<(EncryptedNoteStore Notes, VaultAccess Access)> OpenAsync(RestClient alice)
    {
        var notes = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(alice.User.Id, alice.UserKey));
        return (notes, (await notes.UnlockedVaultsAsync(Ct)).Single(a => a.Name == "Work"));
    }

    private IMongoCollection<BsonDocument> AiLinks => app.Database.GetCollection<BsonDocument>("ai_links");

    /// <summary>Uses the token up in the database, as its ceiling's requests would.</summary>
    private async Task UseUpAsync(string token) =>
        Assert.Equal(1, (await AiLinks.UpdateOneAsync(
            new BsonDocument("lookup", AiLinkToken.Lookup(token)), new BsonDocument("$set", new BsonDocument("count", 1000)), cancellationToken: Ct)).ModifiedCount);

    [Fact]
    public async Task A_reader_makes_a_link_an_AI_reads_as_Markdown()
    {
        using var linked = await LinkedAsync(Api, "198.18.1.1");
        using var ai = new Fetcher(Api, "198.18.1.2");

        using var response = await ai.GetAsync(linked.Link);

        var text = await AssertMarkdownAsync(response);
        Assert.StartsWith("# Plan for the beds\n\nA published note, snapshot of ", text, StringComparison.Ordinal);
        Assert.Matches(@"snapshot of \d{4}-\d\d-\d\d \d\d:\d\d UTC; links to other notes are plain text\.\n\n", text);
        Assert.DoesNotContain("#todo", text, StringComparison.Ordinal);
        Assert.Contains($"\n![flow.jpg]({KumbuAppFactory.PublicUrl}/s/t/{linked.Token}/files/0) (image/jpeg)", text, StringComparison.Ordinal);
        Assert.Matches("^" + KumbuAppFactory.PublicUrl.Replace(".", "\\.", StringComparison.Ordinal) + "/s/t/[0-9A-HJKMNP-TV-Z]{32}$", linked.Link);
        Assert.Equal(DateTime.UtcNow + AiLinkToken.Lifetime, linked.ExpiresAt, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Its_image_URLs_serve_the_pages_images_and_nothing_else()
    {
        using var linked = await LinkedAsync(Api, "198.18.2.1");
        using var ai = new Fetcher(Api, "198.18.2.2");
        using var page = await linked.Reader.FileAsync(0);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        using var file = await ai.GetAsync($"{linked.Link}/files/0");

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(page.Content.Headers.ContentType!.MediaType, file.Content.Headers.ContentType!.MediaType);
        Assert.Equal(await page.Content.ReadAsByteArrayAsync(Ct), await file.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(Security.FileContentPolicy, Assert.Single(file.Headers.GetValues("Content-Security-Policy")));
        Assert.Null(file.Content.Headers.ContentDisposition);
        foreach (var bad in new[] { "1", "-1", "x", "99999999999" })
        {
            using var response = await ai.GetAsync($"{linked.Link}/files/{bad}");
            await AssertGoneAsync(response, bad);
        }
    }

    [Fact]
    public async Task Every_end_answers_the_one_404()
    {
        var time = Now();
        await using var api = Timed(time);
        using var ai = new Fetcher(api, "198.18.3.1");
        var octet = 1;
        async Task<HttpResponseMessage> FetchAsync(string url)
        {
            ai.Address = $"198.18.3.{octet++}";
            return await ai.GetAsync(url);
        }

        var answers = new List<(string What, HttpResponseMessage Response)>();

        using var ceiling = await LinkedAsync(api, "198.18.3.200");
        var allowed = AiLinkToken.Ceiling(1);
        for (var i = 1; i <= allowed; i++)
        {
            using var read = await FetchAsync(ceiling.Link);
            await AssertMarkdownAsync(read, $"request {i}");
        }

        answers.Add(("past the ceiling", await FetchAsync(ceiling.Link)));

        using var owner = await LinkedAsync(api, "198.18.3.201");
        using (var read = await FetchAsync(owner.Link)) await AssertMarkdownAsync(read);
        await SetActiveAsync(owner.Published.Alice, false);
        answers.Add(("owner deactivated", await FetchAsync(owner.Link)));
        await SetActiveAsync(owner.Published.Alice, true);
        using (var read = await FetchAsync(owner.Link)) await AssertMarkdownAsync(read, "owner reactivated");

        using var withdrawn = await LinkedAsync(api, "198.18.3.202");
        using (var read = await FetchAsync(withdrawn.Link)) await AssertMarkdownAsync(read);
        var (notes, access) = await OpenAsync(withdrawn.Published.Alice);
        await api.Services.GetRequiredService<Publications>().WithdrawAsync(access, notes, P(Plan), withdrawn.Published.Alice.Username, BySession, Ct);
        answers.Add(("withdrawn", await FetchAsync(withdrawn.Link)));
        using (var image = await FetchAsync($"{withdrawn.Link}/files/0")) await AssertGoneAsync(image, "withdrawn image");

        using var vault = await LinkedAsync(api, "198.18.3.203");
        using (var read = await FetchAsync(vault.Link)) await AssertMarkdownAsync(read);
        var alice = vault.Published.Alice;
        Assert.NotNull(await api.Services.GetRequiredService<Deletion>().DeleteVaultAsync(alice.User, alice.UserKey, "Work", RestClient.Password, "Work", null, Ct));
        answers.Add(("vault deleted", await FetchAsync(vault.Link)));

        answers.Add(("malformed", await FetchAsync("/s/t/not-a-token")));
        answers.Add(("unknown", await FetchAsync($"/s/t/{AiLinkToken.New()}")));

        // Last, so the clock moved on spoils nothing before.
        using var hour = await LinkedAsync(api, "198.18.3.204");
        using (var read = await FetchAsync(hour.Link)) await AssertMarkdownAsync(read);
        time.Advance(AiLinkToken.Lifetime);
        answers.Add(("after the hour", await FetchAsync(hour.Link)));

        var expected = await ReaderApiTests.FingerprintAsync(answers[0].Response);
        foreach (var (what, response) in answers)
        {
            await AssertGoneAsync(response, what);
            Assert.True(expected == await ReaderApiTests.FingerprintAsync(response), $"{what} answers otherwise");
            response.Dispose();
        }
    }

    [Fact]
    public async Task An_update_serves_the_new_copy()
    {
        using var linked = await LinkedAsync(Api, "198.18.4.200");
        using var ai = new Fetcher(Api, "198.18.4.1");
        using (var read = await ai.GetAsync(linked.Link)) await AssertMarkdownAsync(read);
        var alice = linked.Published.Alice;
        await alice.CreateFileAsync("Beds/flow-2.jpg", Attachments.ImageMetadataTests.JpegWithGps(6));
        var version = (await alice.ReadAsync("Beds/Plan.md"))["version"]!.GetValue<string>();
        using (var put = await alice.SendAsync(HttpMethod.Put, alice.Url("Beds/Plan.md"), new { content = "# Plan for the beds\n\nDig on Monday. Then on Tuesday.\n![[flow.jpg]]\n![[flow-2.jpg]]", version }))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        var (notes, access) = await OpenAsync(alice);
        await Api.Services.GetRequiredService<Publications>().UpdateAsync(access, notes, P(Plan), alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct);

        // One request so far; the ceiling follows the copy's two images.
        for (var i = 2; i <= AiLinkToken.Ceiling(2); i++)
        {
            ai.Address = $"198.18.4.{i}";
            using var read = await ai.GetAsync(linked.Link);
            var text = await AssertMarkdownAsync(read, $"request {i}");
            if (i == 2)
            {
                Assert.Contains("Dig on Monday. Then on Tuesday.", text, StringComparison.Ordinal);
                Assert.Contains($"/s/t/{linked.Token}/files/1) (image/jpeg)", text, StringComparison.Ordinal);
            }
        }

        using var file = await ai.GetAsync($"{linked.Link}/files/1");
        await AssertGoneAsync(file, "past the new ceiling");
    }

    [Fact]
    public async Task Publishing_off_pauses_and_on_again_resumes()
    {
        using var linked = await LinkedAsync(Api, "198.18.5.1");
        using var ai = new Fetcher(Api, "198.18.5.2");
        using (var read = await ai.GetAsync(linked.Link)) await AssertMarkdownAsync(read);
        await using var off = Api.WithWebHostBuilder(b => b.UseSetting("Kumbu:Publish:Enabled", "false"));
        using var offAi = new Fetcher(off, "198.18.5.3");

        using (var paused = await offAi.GetAsync(linked.Link))
        {
            Assert.Equal(HttpStatusCode.NotFound, paused.StatusCode);
            Assert.Equal(PlainText, paused.Content.Headers.ContentType?.ToString());
            Assert.Equal(ReaderApi.OffSentence, await paused.Content.ReadAsStringAsync(Ct));
        }

        using (var image = await offAi.GetAsync($"{linked.Link}/files/0")) Assert.Equal(ReaderApi.OffSentence, await image.Content.ReadAsStringAsync(Ct));
        using (var making = await MakeAiLinkAsync(off, linked.Reader, linked.Reader.Copy))
        {
            Assert.Equal(HttpStatusCode.NotFound, making.StatusCode);
            var body = await RestClient.JsonAsync(making);
            Assert.Equal(ReaderApi.OffSentence, body["error"]!.GetValue<string>());
            Assert.Equal(ReaderApi.OffType, body["type"]!.GetValue<string>());
        }

        using var resumed = await ai.GetAsync(linked.Link);
        await AssertMarkdownAsync(resumed, "on again");
    }

    [Fact]
    public async Task Unknown_tokens_never_block_a_network()
    {
        var published = await ReaderApiTests.PublishedAsync(Api);
        await using var api = ReaderApiTests.Limited(Api);
        using var expired = await LinkedAsync(api, "198.18.6.200", published);
        using var usedUp = await LinkedAsync(api, "198.18.6.201", published);
        using var ai = new Fetcher(api, "198.18.6.1");
        foreach (var linked in new[] { expired, usedUp })
        {
            using var read = await ai.GetAsync(linked.Link);
            await AssertMarkdownAsync(read);
        }

        Assert.Equal(1, (await AiLinks.UpdateOneAsync(
            new BsonDocument("lookup", AiLinkToken.Lookup(expired.Token)),
            new BsonDocument("$set", new BsonDocument("expiresAt", DateTime.UtcNow - TimeSpan.FromSeconds(1))), cancellationToken: Ct)).ModifiedCount);
        await UseUpAsync(usedUp.Token);

        // More than the network's wrong codes, each from an address of its own, so no address's reads run out.
        var dead = new List<string>();
        for (var i = 0; i < 10; i++) dead.Add(i % 3 == 0 ? expired.Link : i % 3 == 1 ? usedUp.Link : $"/s/t/{AiLinkToken.New()}");
        for (var i = 0; i < dead.Count; i++)
        {
            ai.Address = $"198.18.6.{10 + i}";
            using var refused = await ai.GetAsync(dead[i]);
            await AssertGoneAsync(refused, dead[i]);
        }

        using var reader = new ReaderApiTests.Reader(api, "198.18.6.50");
        using var read2 = await reader.PostAsync(published.Formatted);
        Assert.Equal(HttpStatusCode.OK, read2.StatusCode);
    }

    [Fact]
    public async Task No_cookie_another_cookie_or_a_wrong_copy_makes_no_link()
    {
        var x = await ReaderApiTests.PublishedAsync(Api);
        var y = await ReaderApiTests.PublishedAsync(Api);
        using var reader = new ReaderApiTests.Reader(Api, "198.18.7.1");
        (await reader.PostAsync(x.Formatted)).Dispose();
        using (var made = await MakeAiLinkAsync(Api, reader, x.Copy)) Assert.Equal(HttpStatusCode.OK, made.StatusCode);

        using var stranger = new ReaderApiTests.Reader(Api, "198.18.7.2");
        using (var none = await MakeAiLinkAsync(Api, stranger, x.Copy)) await AssertReaderNotFoundAsync(none);

        // The cookie is Y's now: X's copy id makes nothing, and the cookie stays.
        (await reader.PostAsync(y.Formatted)).Dispose();
        using (var other = await MakeAiLinkAsync(Api, reader, x.Copy))
        {
            await AssertReaderNotFoundAsync(other);
            Assert.False(other.Headers.Contains("Set-Cookie"));
        }

        Assert.NotNull(reader.ReaderCookie);
        foreach (var copy in new[] { null, "none", y.Copy.ToUpperInvariant() })
        {
            using var wrong = await MakeAiLinkAsync(Api, reader, copy);
            await AssertReaderNotFoundAsync(wrong);
        }

        using (var forged = await MakeAiLinkAsync(Api, reader, y.Copy, origin: "https://evil.example.org")) Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        using var right = await MakeAiLinkAsync(Api, reader, y.Copy);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    private static async Task AssertReaderNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await RestClient.JsonAsync(response);
        Assert.Equal(ReaderApi.NotFoundSentence, body["error"]!.GetValue<string>());
        Assert.Null(body["link"]);
    }

    [Fact]
    public async Task A_share_code_in_the_path_and_a_token_as_a_code_open_nothing()
    {
        using var linked = await LinkedAsync(Api, "198.18.8.1");
        using var ai = new Fetcher(Api, "198.18.8.2");
        using (var read = await ai.GetAsync(linked.Link)) await AssertMarkdownAsync(read);

        foreach (var code in new[] { linked.Published.Code, linked.Published.Formatted })
        {
            using var refused = await ai.GetAsync($"/s/t/{code}");
            await AssertGoneAsync(refused, code);
        }

        using var reader = new ReaderApiTests.Reader(Api, "198.18.8.3");
        using var asCode = await reader.PostAsync(linked.Token);
        Assert.Equal(HttpStatusCode.NotFound, asCode.StatusCode);
        Assert.Equal(ReaderApi.NotFoundSentence, (await RestClient.JsonAsync(asCode))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Its_answers_carry_the_readers_headers()
    {
        using var linked = await LinkedAsync(Api, "198.18.9.1");
        using var ai = new Fetcher(Api, "198.18.9.2");

        foreach (var url in new[] { linked.Link, $"{linked.Link}/files/0", $"/s/t/{AiLinkToken.New()}" })
        {
            using var response = await ai.SendAsync(HttpMethod.Get, url, origin: "https://chat.example.org");
            Assert.True(response.StatusCode == (url.Contains(linked.Token, StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.NotFound), $"{url} answered {(int)response.StatusCode}");
            Assert.Equal("noindex, nofollow", string.Join(", ", response.Headers.GetValues("X-Robots-Tag")));
            Assert.Equal("no-referrer", string.Join(", ", response.Headers.GetValues("Referrer-Policy")));
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.Equal("nosniff", string.Join(", ", response.Headers.GetValues("X-Content-Type-Options")));
            Assert.False(response.Headers.Contains("Set-Cookie"), url);
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"), url);
        }
    }

    [Fact]
    public async Task GET_HEAD_and_POST_on_a_valid_and_an_unknown_token()
    {
        // A web app of its own, so a fall-through to its shell would show.
        var webRoot = Directory.CreateTempSubdirectory("kumbu-wwwroot-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>Kumbu test shell</title>", Ct);
            await using var api = Api.WithWebHostBuilder(b => b.UseWebRoot(webRoot));
            using var linked = await LinkedAsync(api, "198.18.10.200");
            using var ai = new Fetcher(api, "198.18.10.1");
            using var get = await ai.GetAsync(linked.Link);
            await AssertMarkdownAsync(get);
            var length = (await get.Content.ReadAsByteArrayAsync(Ct)).Length;

            // The GET counted one; HEADs use up the rest.
            for (var i = 2; i <= AiLinkToken.Ceiling(1); i++)
            {
                ai.Address = $"198.18.10.{i}";
                using var head = await ai.SendAsync(HttpMethod.Head, linked.Link);
                Assert.True(head.StatusCode == HttpStatusCode.OK, $"HEAD {i} answered {(int)head.StatusCode}");
                Assert.Equal("text/markdown; charset=utf-8", head.Content.Headers.ContentType?.ToString());
                Assert.Equal(length, head.Content.Headers.ContentLength);
                Assert.Empty(await head.Content.ReadAsByteArrayAsync(Ct));
            }

            ai.Address = "198.18.10.100";
            using (var spent = await ai.GetAsync(linked.Link)) await AssertGoneAsync(spent, "GET past HEADs");
            using (var spentHead = await ai.SendAsync(HttpMethod.Head, linked.Link))
            {
                Assert.Equal(HttpStatusCode.NotFound, spentHead.StatusCode);
                Assert.Equal(PlainText, spentHead.Content.Headers.ContentType?.ToString());
            }

            using var fresh = await LinkedAsync(api, "198.18.10.201");
            ai.Address = "198.18.10.101";
            using (var read = await ai.GetAsync(fresh.Link)) await AssertMarkdownAsync(read);
            var unknown = $"/s/t/{AiLinkToken.New()}";
            var refusals = new (HttpMethod Method, string Url)[]
            {
                (HttpMethod.Post, fresh.Link), (HttpMethod.Post, unknown), (HttpMethod.Get, unknown), (HttpMethod.Head, unknown),
                (HttpMethod.Post, $"{fresh.Link}/files/0"), (HttpMethod.Put, fresh.Link), (HttpMethod.Delete, fresh.Link),
                (HttpMethod.Get, "/s/t"), (HttpMethod.Get, "/s/t/"), (HttpMethod.Get, "/s/t/x/y/z"), (HttpMethod.Head, "/s/t/x/y/z"),
                (HttpMethod.Get, $"{fresh.Link}/files"), (HttpMethod.Get, $"{fresh.Link}/other"),
            };
            foreach (var (method, url) in refusals)
            {
                using var response = await ai.SendAsync(method, url, origin: RestClient.Origin);
                Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {url} answered {(int)response.StatusCode}");
                Assert.Equal(PlainText, response.Content.Headers.ContentType?.ToString());
                var body = await response.Content.ReadAsStringAsync(Ct);
                Assert.DoesNotContain("<!doctype", body, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(method == HttpMethod.Head ? "" : AiLinkApi.NotFoundSentence, body);
            }

            // None of those counted: the fresh link reads on.
            using var still = await ai.GetAsync(fresh.Link);
            await AssertMarkdownAsync(still, "after the refusals");
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Fetches_count_against_the_reader_limits()
    {
        await using var api = ReaderApiTests.Limited(Api);
        using var linked = await LinkedAsync(api, "198.18.11.1");
        using var ai = new Fetcher(api, "198.18.11.2");
        for (var i = 0; i < ReaderApiTests.ReadsPerAddress; i++)
        {
            using var read = await ai.GetAsync(linked.Link);
            await AssertMarkdownAsync(read, $"read {i + 1}");
        }

        using var limited = await ai.GetAsync(linked.Link);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(PlainText, limited.Content.Headers.ContentType?.ToString());
        Assert.Equal(AiLinkApi.BusySentence, await limited.Content.ReadAsStringAsync(Ct));

        // Files have an allowance of their own, as on the reader page.
        for (var i = 0; i < ReaderApiTests.FilesPerAddress; i++)
        {
            using var file = await ai.GetAsync($"{linked.Link}/files/0");
            Assert.True(file.StatusCode == HttpStatusCode.OK, $"file {i + 1} answered {(int)file.StatusCode}");
        }

        using var files = await ai.GetAsync($"{linked.Link}/files/0");
        Assert.Equal(HttpStatusCode.TooManyRequests, files.StatusCode);
        Assert.NotNull(files.Headers.RetryAfter);
    }

    [Fact]
    public async Task A_blocked_network_is_refused_at_the_AI_link_too()
    {
        await using var api = ReaderApiTests.Limited(Api);
        using var linked = await LinkedAsync(api, "198.18.13.1");
        using var ai = new Fetcher(api, "198.18.12.1");
        using (var read = await ai.GetAsync(linked.Link)) await AssertMarkdownAsync(read);
        using var guesser = new ReaderApiTests.Reader(api, "198.18.12.2");
        for (var i = 0; i <= ReaderApiTests.FailuresPerNetwork; i++)
        {
            guesser.Address = $"198.18.12.{10 + i}";
            (await guesser.PostAsync(ShareCode.New())).Dispose();
        }

        ai.Address = "198.18.12.50";
        using (var blocked = await ai.GetAsync(linked.Link)) await AssertGoneAsync(blocked, "blocked network");

        ai.Address = "198.18.14.1";
        using var elsewhere = await ai.GetAsync(linked.Link);
        await AssertMarkdownAsync(elsewhere, "another network");
    }

    [Fact]
    public async Task The_token_reaches_no_log()
    {
        var log = new CapturedLog();
        var time = Now();
        await using var api = ReaderApiTests.Limited(Timed(time), log);
        using var linked = await LinkedAsync(api, "198.18.15.1");
        using var spent = await LinkedAsync(api, "198.18.15.2", linked.Published);
        using var ai = new Fetcher(api, "198.18.15.3");
        var unknown = AiLinkToken.New();
        var statuses = new List<int>();
        async Task SendAsync(HttpMethod method, string url)
        {
            using var response = await ai.SendAsync(method, url);
            statuses.Add((int)response.StatusCode);
        }

        await SendAsync(HttpMethod.Get, linked.Link);
        await SendAsync(HttpMethod.Head, linked.Link);
        await SendAsync(HttpMethod.Get, $"{linked.Link}/files/0");
        await SendAsync(HttpMethod.Get, $"{linked.Link}/files/7");
        await SendAsync(HttpMethod.Get, $"/s/t/{unknown}");
        await SendAsync(HttpMethod.Post, linked.Link);
        await UseUpAsync(spent.Token);
        await SendAsync(HttpMethod.Get, spent.Link);
        // The address's reads used up: a 429.
        await SendAsync(HttpMethod.Get, linked.Link);
        ai.Address = "198.18.15.4";
        time.Advance(AiLinkToken.Lifetime);
        await SendAsync(HttpMethod.Get, linked.Link);

        Assert.Equal([200, 200, 200, 404, 404, 404, 404, 429, 404], statuses);
        var tokens = new[] { linked.Token, spent.Token, unknown };
        var secrets = tokens
            .Concat(tokens.Select(t => Convert.ToHexStringLower(AiLinkToken.Lookup(t))))
            .Concat(tokens.Select(t => Convert.ToHexString(AiLinkToken.Lookup(t))))
            .Concat(tokens.Select(t => Convert.ToBase64String(AiLinkToken.Lookup(t))))
            .Concat([AiLinkApi.Path + "/", linked.Published.Code, linked.Published.Formatted, linked.Published.Copy]);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(log.Lines, l => l.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Markdown_leaves_out_an_image_not_in_the_copy_and_keeps_code()
    {
        var copy = new PublishedCopy(
            "Two\nlines",
            "See ![a](f/0 \"title\") here\r\n![b](f/3)\n```\n![c](f/0)\n```\n`![d](f/0)` and ![e](<f/0>) and ![f](f/x)\n",
            new DateTime(2026, 11, 11, 8, 30, 5, DateTimeKind.Utc),
            [new PublishedCopyFile(0, "image/png")]);

        var markdown = AiLinkApi.Markdown(copy, "https://kumbu.test/s/t/TOKEN/files");

        Assert.Equal(
            "# Two lines\n\nA published note, snapshot of 2026-11-11 08:30 UTC; links to other notes are plain text.\n\n"
            + "See ![a](https://kumbu.test/s/t/TOKEN/files/0 \"title\") (image/png) here\r\n\n```\n![c](f/0)\n```\n`![d](f/0)` and ![e](<f/0>) and ![f](f/x)\n",
            markdown);
    }
}
