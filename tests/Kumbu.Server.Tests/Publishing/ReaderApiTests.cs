// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Attachments;
using Kumbu.Server.Auth;
using Kumbu.Server.Publishing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Attachments;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>
/// The anonymous reader endpoint (publish spec §3.12, §3.13, §3.7 deactivation): the code opens the copy and sets a
/// reader cookie of its own; every failure answers alike; the switch and a deactivated owner hide the copy; limits as
/// pairing's; the reader headers on every answer; nothing about readers stored or logged.
/// </summary>
[Collection("app")]
public sealed class ReaderApiTests(KumbuAppFactory app)
{
    private const string Plan = "/Work/Beds/Plan.md";

    private const string PlanText = """
        # Plan for the beds #garden

        Dig on Monday. #todo
        ![[flow.jpg]]
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static readonly WriterRef BySession = WriterRef.ForOwner(WriterRef.Session, null);

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    /// <summary>Small limits: reads per address, wrong codes per network and server-wide.</summary>
    internal const int ReadsPerAddress = 4;
    internal const int FailuresPerNetwork = 3;
    internal const int FailuresPerServer = 6;

    private WebApplicationFactory<Program> Limited(CapturedLog? log = null) => Limited(Api, log);

    /// <summary><paramref name="api"/> with <see cref="ReadsPerAddress"/>, <see cref="FailuresPerNetwork"/> and
    /// <see cref="FailuresPerServer"/>; the caller disposes it.</summary>
    internal static WebApplicationFactory<Program> Limited(WebApplicationFactory<Program> api, CapturedLog? log = null) => api.WithWebHostBuilder(b =>
    {
        b.ConfigureServices(s =>
        {
            s.RemoveAll<AccountLimits>();
            s.AddSingleton(new AccountLimits(
                AccountLimits.RedeemFailuresPerNetwork, AccountLimits.RedeemFailuresPerServer,
                readerReadsPerAddress: ReadsPerAddress, readerFailuresPerNetwork: FailuresPerNetwork, readerFailuresPerServer: FailuresPerServer));
        });
        if (log is not null) b.ConfigureLogging(l => l.AddProvider(log));
    });

    /// <summary>A stranger's browser: a cookie jar over https, no session, from <see cref="Address"/>.</summary>
    internal sealed class Reader(WebApplicationFactory<Program> api, string address) : IDisposable
    {
        public CookieContainer Jar { get; } = new();

        public string Address { get; set; } = address;

        private HttpClient? _client;

        private HttpClient Client => _client ??= api.CreateDefaultClient(new Uri(KumbuAppFactory.PublicUrl), new CookieContainerHandler(Jar));

        public Task<HttpResponseMessage> PostAsync(string code, string? origin = RestClient.Origin) =>
            SendAsync(HttpMethod.Post, "/api/s", JsonSerializer.Serialize(new { code }), origin);

        public Task<HttpResponseMessage> GetAsync(string path = "/api/s") => SendAsync(HttpMethod.Get, path, null, null);

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json, string? origin)
        {
            using var request = new HttpRequestMessage(method, path);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (origin is not null) request.Headers.Add("Origin", origin);
            request.Headers.Add("X-Forwarded-For", Address);
            return await Client.SendAsync(request, Ct);
        }

        public Cookie? ReaderCookie => Jar.GetCookies(new Uri(new Uri(KumbuAppFactory.PublicUrl), ReaderApi.CookiePath)).FirstOrDefault(c => c.Name == ReaderApi.Cookie);

        public void Dispose() => _client?.Dispose();
    }

    internal sealed record Published(RestClient Alice, string Code, string Formatted);

    private Task<Published> PublishedAsync() => PublishedAsync(Api);

    /// <summary>Alice's <c>Work</c> with <see cref="Plan"/> embedding <c>Beds/flow.jpg</c> (a camera's JPEG), published
    /// through <see cref="Publications"/> (there is no REST route yet).</summary>
    internal static async Task<Published> PublishedAsync(WebApplicationFactory<Program> api)
    {
        var alice = await RestClient.NewAsync(api, vault: "Work");
        await alice.CreateFileAsync("Beds/flow.jpg", ImageMetadataTests.JpegWithGps(6));
        await alice.CreateAsync("Beds/Plan.md", PlanText);
        var (notes, access) = await OpenAsync(api, alice);
        var published = await api.Services.GetRequiredService<Publications>()
            .PublishAsync(access, notes, P(Plan), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct);
        var formatted = published.Link[(published.Link.IndexOf('#') + 1)..];
        return new Published(alice, ShareCode.Parse(formatted)!, formatted);
    }

    private static async Task<(EncryptedNoteStore Notes, VaultAccess Access)> OpenAsync(WebApplicationFactory<Program> api, RestClient alice)
    {
        var notes = new EncryptedNoteStore(api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(alice.User.Id, alice.UserKey));
        return (notes, (await notes.UnlockedVaultsAsync(Ct)).Single(a => a.Name == "Work"));
    }

    private async Task WithdrawAsync(RestClient alice)
    {
        var (notes, access) = await OpenAsync(Api, alice);
        await Api.Services.GetRequiredService<Publications>().WithdrawAsync(access, notes, P(Plan), alice.Username, BySession, Ct);
    }

    private async Task SetActiveAsync(RestClient user, bool active)
    {
        var admin = await RestClient.NewAsync(Api, vault: "Admin");
        await app.Database.GetCollection<UserDocument>("users").UpdateOneAsync(u => u.Id == admin.User.Id, Builders<UserDocument>.Update.Set(u => u.Role, Roles.Admin), cancellationToken: Ct);
        Assert.True(await Api.Services.GetRequiredService<Admin>().SetActiveAsync(admin.User, user.User.Id, active, Ct));
    }

    private static string WrongCode() => ShareCode.New();

    /// <summary>Status, every header but <c>Date</c>, and the body: what makes two answers alike.</summary>
    private static async Task<string> FingerprintAsync(HttpResponseMessage response)
    {
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => h.Key != "Date")
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => $"{h.Key}: {string.Join(" | ", h.Value)}");
        return $"{(int)response.StatusCode}\n{string.Join('\n', headers)}\n\n{await response.Content.ReadAsStringAsync(Ct)}";
    }

    private static string? SetCookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal)) : null;

    private static async Task AssertNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await RestClient.JsonAsync(response);
        Assert.Equal(ReaderApi.NotFoundSentence, body["error"]!.GetValue<string>());
        Assert.Null(body["type"]);
    }

    private static async Task AssertCopyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await RestClient.JsonAsync(response);
        Assert.Equal("Plan for the beds", body["title"]!.GetValue<string>());
        var markdown = body["markdown"]!.GetValue<string>();
        Assert.Contains("Dig on Monday.", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("#todo", markdown, StringComparison.Ordinal);
        Assert.NotNull(body["snapshotAt"]);
        var file = Assert.Single(body["files"]!.AsArray())!;
        Assert.Equal(0, file["index"]!.GetValue<int>());
        Assert.Equal("flow.jpg", file["name"]!.GetValue<string>());
        Assert.Equal("image/jpeg", file["mediaType"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_right_code_reads_the_copy_and_sets_the_reader_cookie()
    {
        var published = await PublishedAsync();
        using var reader = new Reader(Api, "203.0.113.101");

        using var response = await reader.PostAsync(published.Formatted.ToLowerInvariant().Replace('-', ' '));

        await AssertCopyAsync(response);
        Assert.Equal($"{ReaderApi.Cookie}={published.Code}; path=/api/s; secure; samesite=strict; httponly", SetCookie(response, ReaderApi.Cookie));
        Assert.Null(SetCookie(response, CallerMiddleware.SessionCookie));
        Assert.Single(response.Headers.GetValues("Set-Cookie"));
    }

    [Fact]
    public async Task A_reload_reads_with_the_cookie_alone()
    {
        var published = await PublishedAsync();
        using var reader = new Reader(Api, "203.0.113.102");
        using (var first = await reader.PostAsync(published.Formatted)) await AssertCopyAsync(first);
        Assert.NotNull(reader.ReaderCookie);

        using var reload = await reader.GetAsync();

        await AssertCopyAsync(reload);
        Assert.Null(SetCookie(reload, ReaderApi.Cookie));
        Assert.Empty(reader.Jar.GetCookies(new Uri(new Uri(KumbuAppFactory.PublicUrl), "/api/session")));
        Assert.Empty(reader.Jar.GetCookies(new Uri(new Uri(KumbuAppFactory.PublicUrl), "/")));
        // Sent by hand to /api/session, the reader cookie is no session.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/session");
        request.Headers.Add("Cookie", $"{ReaderApi.Cookie}={published.Code}");
        request.Headers.Add("X-Forwarded-For", reader.Address);
        using var plain = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });
        using var session = await plain.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    [Fact]
    public async Task A_wrong_and_a_withdrawn_code_answer_alike()
    {
        var withdrawn = await PublishedAsync();
        var deactivated = await PublishedAsync();
        using var reader = new Reader(Api, "203.0.113.103");
        using (var read = await reader.PostAsync(withdrawn.Formatted)) await AssertCopyAsync(read);
        await WithdrawAsync(withdrawn.Alice);
        await SetActiveAsync(deactivated.Alice, false);

        var answers = new List<(string What, HttpResponseMessage Response)>();
        // The withdrawn one's cookie first, while the jar still holds it.
        answers.Add(("withdrawn cookie", await reader.GetAsync()));
        Assert.Null(reader.ReaderCookie);
        answers.Add(("malformed", await reader.PostAsync("not-a-code")));
        answers.Add(("wrong", await reader.PostAsync(WrongCode())));
        answers.Add(("withdrawn", await reader.PostAsync(withdrawn.Formatted)));
        answers.Add(("deactivated owner", await reader.PostAsync(deactivated.Formatted)));
        answers.Add(("no cookie", await reader.GetAsync()));

        var expected = await FingerprintAsync(answers[0].Response);
        foreach (var (what, response) in answers)
        {
            await AssertNotFoundAsync(response);
            Assert.True(expected == await FingerprintAsync(response), $"{what} answers otherwise");
            Assert.StartsWith($"{ReaderApi.Cookie}=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/api/s", SetCookie(response, ReaderApi.Cookie));
            response.Dispose();
        }
    }

    [Fact]
    public async Task Deactivation_hides_and_reactivation_shows()
    {
        var published = await PublishedAsync();
        using var reader = new Reader(Api, "203.0.113.104");
        await SetActiveAsync(published.Alice, false);

        using (var hidden = await reader.PostAsync(published.Formatted)) await AssertNotFoundAsync(hidden);

        await SetActiveAsync(published.Alice, true);
        using var shown = await reader.PostAsync(published.Formatted);
        await AssertCopyAsync(shown);
    }

    [Fact]
    public async Task Switched_off_reads_nothing()
    {
        var published = await PublishedAsync();
        await using var off = Api.WithWebHostBuilder(b => b.UseSetting("Kumbu:Publish:Enabled", "false"));
        using var offReader = new Reader(off, "203.0.113.105");

        using (var refused = await offReader.PostAsync(published.Formatted))
        {
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            var body = await RestClient.JsonAsync(refused);
            Assert.Equal(ReaderApi.OffSentence, body["error"]!.GetValue<string>());
            Assert.Equal(ReaderApi.OffType, body["type"]!.GetValue<string>());
            Assert.Null(SetCookie(refused, ReaderApi.Cookie));
        }

        using var onReader = new Reader(Api, "203.0.113.105");
        using var read = await onReader.PostAsync(published.Formatted);
        await AssertCopyAsync(read);
    }

    [Fact]
    public async Task Wrong_codes_are_limited_per_network_and_server()
    {
        var published = await PublishedAsync();
        await using var api = Limited();
        using var reader = new Reader(api, "203.0.113.1");

        for (var i = 0; i < FailuresPerNetwork; i++)
        {
            reader.Address = $"203.0.113.{10 + i}";
            using var wrong = await reader.PostAsync(WrongCode());
            await AssertNotFoundAsync(wrong);
        }

        reader.Address = "203.0.113.20";
        using (var held = await reader.PostAsync(published.Formatted))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, held.StatusCode);
            Assert.NotNull(held.Headers.RetryAfter);
        }

        using var other = new Reader(api, "198.51.100.20");
        using (var read = await other.PostAsync(published.Formatted)) await AssertCopyAsync(read);

        // Wrong codes from a third network use up the server's allowance: then every network is held.
        for (var i = 0; i < FailuresPerServer - FailuresPerNetwork; i++)
        {
            other.Address = $"192.0.2.{10 + i}";
            using var wrong = await other.PostAsync(WrongCode());
            await AssertNotFoundAsync(wrong);
        }

        other.Address = "198.51.100.21";
        using var everyone = await other.PostAsync(published.Formatted);
        Assert.Equal(HttpStatusCode.TooManyRequests, everyone.StatusCode);
    }

    [Fact]
    public async Task Reads_are_limited_per_address()
    {
        var published = await PublishedAsync();
        await using var api = Limited();
        using var reader = new Reader(api, "203.0.113.30");
        using (var first = await reader.PostAsync(published.Formatted)) await AssertCopyAsync(first);
        for (var i = 1; i < ReadsPerAddress; i++)
        {
            using var again = await reader.GetAsync();
            await AssertCopyAsync(again);
        }

        using var limited = await reader.GetAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);

        reader.Address = "203.0.113.31";
        using var elsewhere = await reader.GetAsync();
        await AssertCopyAsync(elsewhere);
    }

    [Fact]
    public async Task Every_reader_answer_carries_its_headers()
    {
        var published = await PublishedAsync();
        await using var api = Limited();
        var answers = new List<(string What, HttpResponseMessage Response)>();
        var octet = 40;
        foreach (var endpoint in new[] { "POST /api/s", "GET /api/s", "GET /api/s/files/0" })
        {
            using var reader = new Reader(api, $"203.0.113.{octet++}");
            Task<HttpResponseMessage> Send() => endpoint switch
            {
                "POST /api/s" => reader.PostAsync(published.Formatted),
                "GET /api/s" => reader.GetAsync(),
                _ => reader.GetAsync("/api/s/files/0"),
            };

            // Without the cookie the GETs fail; the POST fails with a wrong code.
            answers.Add(($"{endpoint} 404", endpoint.StartsWith("POST", StringComparison.Ordinal) ? await reader.PostAsync(WrongCode()) : await Send()));
            if (!endpoint.StartsWith("POST", StringComparison.Ordinal)) (await reader.PostAsync(published.Formatted)).Dispose();
            answers.Add(($"{endpoint} 200", await Send()));
            for (var i = 0; i < ReadsPerAddress; i++) (await Send()).Dispose();
            answers.Add(($"{endpoint} 429", await Send()));
        }

        foreach (var (what, response) in answers)
        {
            var status = what[^3..];
            Assert.True(status == ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), $"{what} answered {(int)response.StatusCode}");
            Assert.Equal("noindex, nofollow", string.Join(", ", response.Headers.GetValues("X-Robots-Tag")));
            Assert.Equal("no-referrer", string.Join(", ", response.Headers.GetValues("Referrer-Policy")));
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_file_reads_with_the_cookie_and_its_media_type()
    {
        var published = await PublishedAsync();
        using var stranger = new Reader(Api, "203.0.113.106");
        using var noCookie = await stranger.GetAsync("/api/s/files/0");
        await AssertNotFoundAsync(noCookie);

        using var reader = new Reader(Api, "203.0.113.107");
        (await reader.PostAsync(published.Formatted)).Dispose();
        using var file = await reader.GetAsync("/api/s/files/0");

        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/jpeg", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal(ImageMetadata.Strip(ImageMetadataTests.JpegWithGps(6), "image/jpeg"), await file.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(Security.FileContentPolicy, Assert.Single(file.Headers.GetValues("Content-Security-Policy")));

        foreach (var bad in new[] { "/api/s/files/1", "/api/s/files/-1", "/api/s/files/x", "/api/s/files/99999999999" })
        {
            using var response = await reader.GetAsync(bad);
            await AssertNotFoundAsync(response);
            Assert.Equal(await noCookie.Content.ReadAsStringAsync(Ct), await response.Content.ReadAsStringAsync(Ct));
        }

        // A bad index isn't a guess: the cookie stays.
        Assert.NotNull(reader.ReaderCookie);
    }

    [Fact]
    public async Task A_session_beside_it_changes_nothing()
    {
        var published = await PublishedAsync();
        var alice = published.Alice;
        var slots = app.Database.GetCollection<BsonDocument>("user_slots");
        var before = (await slots.Find(Builders<BsonDocument>.Filter.Eq("user", alice.User.Id)).ToListAsync(Ct)).Select(d => d.ToJson()).Order().ToList();

        async Task<List<string>> AnswersAsync(Reader reader)
        {
            var bodies = new List<string>();
            foreach (var send in new Func<Task<HttpResponseMessage>>[]
            {
                () => reader.PostAsync(WrongCode()),
                () => reader.GetAsync(),
                () => reader.PostAsync(published.Formatted),
                () => reader.GetAsync(),
                () => reader.GetAsync("/api/s/files/0"),
            })
            {
                using var response = await send();
                bodies.Add($"{(int)response.StatusCode} {SetCookie(response, ReaderApi.Cookie)} {SetCookie(response, CallerMiddleware.SessionCookie)} {Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync(Ct))}");
            }

            return bodies;
        }

        using var plain = new Reader(Api, "203.0.113.108");
        using var withSession = new Reader(Api, "203.0.113.109");
        withSession.Jar.Add(new Uri(KumbuAppFactory.PublicUrl), new Cookie(CallerMiddleware.SessionCookie, alice.Session, "/"));

        var expected = await AnswersAsync(plain);
        var answers = await AnswersAsync(withSession);

        // Only snapshotAt could differ between two reads, and it doesn't: the same copy.
        Assert.Equal(expected, answers);
        var after = (await slots.Find(Builders<BsonDocument>.Filter.Eq("user", alice.User.Id)).ToListAsync(Ct)).Select(d => d.ToJson()).Order().ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Nothing_about_readers_is_stored()
    {
        var published = await PublishedAsync();
        await app.IndexedAsync();
        await Api.Services.GetRequiredService<Kumbu.Server.Search.SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);
        var db = app.Database;

        async Task<Dictionary<string, long>> CountsAsync()
        {
            var counts = new Dictionary<string, long>();
            foreach (var name in await (await db.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct))
            {
                counts[name] = await db.GetCollection<BsonDocument>(name).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);
            }

            return counts;
        }

        var lookup = ShareCode.Lookup(published.Code);
        Task<BsonDocument> CopyAsync() => db.GetCollection<BsonDocument>("publications").Find(Builders<BsonDocument>.Filter.Eq("lookup", lookup)).SingleAsync(Ct);
        var counts = await CountsAsync();
        var copy = (await CopyAsync()).ToBson();

        using var reader = new Reader(Api, "203.0.113.110");
        (await reader.PostAsync(published.Formatted)).Dispose();
        for (var i = 0; i < 8; i++) (await reader.GetAsync(i % 2 == 0 ? "/api/s" : "/api/s/files/0")).Dispose();
        (await reader.PostAsync(WrongCode())).Dispose();

        Assert.Equal(counts, await CountsAsync());
        Assert.Equal(copy, (await CopyAsync()).ToBson());
    }

    [Fact]
    public async Task A_cross_site_post_is_refused()
    {
        var published = await PublishedAsync();
        using var reader = new Reader(Api, "203.0.113.111");

        using var forged = await reader.PostAsync(published.Formatted, origin: "https://evil.example.org");

        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Null(SetCookie(forged, ReaderApi.Cookie));
        using var missing = await reader.PostAsync(published.Formatted, origin: null);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
    }

    [Fact]
    public async Task The_code_reaches_no_log()
    {
        var published = await PublishedAsync();
        var log = new CapturedLog();
        await using var api = Limited(log);
        using var reader = new Reader(api, "203.0.113.50");
        var wrong = new List<string>();

        (await reader.PostAsync(published.Formatted)).Dispose();
        (await reader.GetAsync()).Dispose();
        (await reader.GetAsync("/api/s/files/0")).Dispose();
        (await reader.GetAsync("/api/s/files/7")).Dispose();
        for (var i = 0; i <= FailuresPerNetwork; i++)
        {
            reader.Address = $"203.0.113.{51 + i}";
            var code = WrongCode();
            wrong.Add(code);
            (await reader.PostAsync(ShareCode.Format(code))).Dispose();
        }

        // The network's allowance used up, which is logged as a warning, and then held.
        Assert.Contains(log.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("share codes", StringComparison.Ordinal));
        var secrets = new[] { published.Code, published.Formatted, Convert.ToHexStringLower(ShareCode.Lookup(published.Code)), Convert.ToHexString(ShareCode.Lookup(published.Code)) }
            .Concat(wrong).Concat(wrong.Select(ShareCode.Format)).Concat(wrong.Select(c => Convert.ToHexStringLower(ShareCode.Lookup(c))));
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(log.Lines, l => l.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }
}
