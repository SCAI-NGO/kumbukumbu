// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Publishing;
using Kumbu.Server.Skills;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Dav;
using Kumbu.Server.Tests.Publishing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Skills;

/// <summary>The anonymous skill-link endpoint (skills design §3.12, Decision 6): the code opens the skill and sets a cookie
/// of its own; the answer names no owner, vault, path or code; the <c>.zip</c> is rooted at the folder's name, never the
/// manifest's; the reader's headers, limits and switch; and a <c>.zip</c> of one's own skill for a session.</summary>
[Collection("app")]
public sealed class SkillPageApiTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly WriterRef BySession = WriterRef.ForOwner(WriterRef.Session, null);

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private static async Task PutNoteAsync(RestClient user, string path, string content)
    {
        using var put = await user.SendAsync(HttpMethod.Put, "/api/notes" + path, new { content });
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
    }

    /// <summary>Alice's skill <c>write-as-alice</c> in her vault Skills, shared by link; the code as the link formats it.</summary>
    private async Task<(RestClient Alice, string Code)> SharedAsync(string manifest = "---\nname: write-as-alice\ndescription: Writes posts.\n---\n# Write\n")
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await PutNoteAsync(alice, "/Skills/write-as-alice/SKILL.md", manifest);
        await PutNoteAsync(alice, "/Skills/write-as-alice/examples/post.md", "# Post\n");
        var services = app.PublicApi.Services;
        var store = new EncryptedNoteStore(
            services.GetRequiredService<VaultCatalog>(), new UserCredential(alice.User.Id, alice.UserKey), null, null, null,
            services.GetRequiredService<Publications>(), services.GetRequiredService<StorageQuota>());
        var access = (await store.UnlockedVaultsAsync(Ct)).Single(a => a.Name == "Skills");
        var shared = await services.GetRequiredService<Publications>()
            .PublishSkillAsync(access, store, P("/Skills/write-as-alice"), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct);
        return (alice, shared.Link[(shared.Link.IndexOf('#') + 1)..]);
    }

    /// <summary>A stranger's request from <paramref name="ip"/> (by default one of its own): no session, no cookie jar.</summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body, IReadOnlyDictionary<string, string>? headers = null, string? ip = null, WebApplicationFactory<Program>? api = null)
    {
        using var client = (api ?? app.PublicApi).CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.Add("Origin", RestClient.Origin);
        request.Headers.Add("X-Forwarded-For", ip ?? DavClient.NewIp());
        var response = await client.SendAsync(request, Ct);
        await response.Content.LoadIntoBufferAsync(Ct);
        return response;
    }

    private Task<HttpResponseMessage> OpenAsync(string code, string? ip = null, WebApplicationFactory<Program>? api = null) =>
        SendAsync(HttpMethod.Post, "/api/k", new { code }, ip: ip, api: api);

    private static string CookieOf(HttpResponseMessage open) =>
        open.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(SkillPageApi.Cookie + "=", StringComparison.Ordinal)).Split(';')[0];

    private Task<HttpResponseMessage> ZipAsync(string? cookie, string? copy)
    {
        var headers = new Dictionary<string, string>();
        if (cookie is not null) headers["Cookie"] = cookie;
        if (copy is not null) headers[ReaderApi.CopyHeader] = copy;
        return SendAsync(HttpMethod.Get, "/api/k/zip", null, headers);
    }

    private static async Task AssertNotFoundAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await RestClient.JsonAsync(response);
        Assert.Equal(SkillPageApi.NotFoundSentence, body["error"]!.GetValue<string>());
        Assert.Null(body["type"]);
    }

    private static async Task AssertEntriesAsync(HttpResponseMessage zip)
    {
        Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        Assert.Equal("application/zip", zip.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment; filename=\"write-as-alice.zip\"; filename*=UTF-8''write-as-alice.zip", zip.Content.Headers.ContentDisposition!.ToString());
        using var archive = new ZipArchive(await zip.Content.ReadAsStreamAsync(Ct));
        Assert.Equal(["write-as-alice/SKILL.md", "write-as-alice/examples/post.md"], archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        using var post = new StreamReader(archive.GetEntry("write-as-alice/examples/post.md")!.Open());
        Assert.Equal("# Post\n", await post.ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task The_code_opens_the_skill_and_sets_its_cookie()
    {
        var (_, code) = await SharedAsync();
        using var open = await OpenAsync(code);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        var cookie = open.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("kumbu_skill=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("path=/api/k", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("httponly", cookie);
        Assert.DoesNotContain(open.Headers.GetValues("Set-Cookie"), c => c.StartsWith(ReaderApi.Cookie + "=", StringComparison.Ordinal));
        var body = await RestClient.JsonAsync(open);
        Assert.Equal("write-as-alice", body["name"]!.GetValue<string>());
        Assert.Equal("Writes posts.", body["description"]!.GetValue<string>());
        Assert.Contains("# Write", body["manifest"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(body["hasScripts"]!.GetValue<bool>());
        Assert.Equal(["SKILL.md", "examples/post.md"], body["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()).Order(StringComparer.Ordinal));

        // A reload reads with the cookie alone.
        using var again = await SendAsync(HttpMethod.Get, "/api/k", null, new Dictionary<string, string> { ["Cookie"] = CookieOf(open) });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("write-as-alice", (await RestClient.JsonAsync(again))["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_answer_names_no_owner_vault_path_or_code()
    {
        var (alice, code) = await SharedAsync();
        using var open = await OpenAsync(code);
        var body = await open.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(alice.Username, body);
        Assert.DoesNotContain("/Skills/", body);
        Assert.DoesNotContain(ShareCode.Parse(code)!, body);
        Assert.DoesNotContain(code, body);
    }

    [Fact]
    public async Task The_zip_holds_every_file_under_the_folders_name()
    {
        var (_, code) = await SharedAsync();
        using var open = await OpenAsync(code);
        var copy = (await RestClient.JsonAsync(open))["copy"]!.GetValue<string>();
        using var zip = await ZipAsync(CookieOf(open), copy);
        await AssertEntriesAsync(zip);
    }

    [Fact]
    public async Task A_manifest_name_cant_steer_the_zip()
    {
        var (_, code) = await SharedAsync("---\nname: ../evil\ndescription: Writes posts.\n---\n");
        using var open = await OpenAsync(code);
        var body = await RestClient.JsonAsync(open);
        Assert.Equal("write-as-alice", body["name"]!.GetValue<string>());
        using var zip = await ZipAsync(CookieOf(open), body["copy"]!.GetValue<string>());
        await AssertEntriesAsync(zip);
    }

    [Fact]
    public async Task The_zip_needs_the_copy_header()
    {
        var (_, code) = await SharedAsync();
        using var open = await OpenAsync(code);
        var cookie = CookieOf(open);

        using (var without = await ZipAsync(cookie, null)) await AssertNotFoundAsync(without);
        using (var other = await ZipAsync(cookie, ShareCode.CopyId(ShareCode.New()))) await AssertNotFoundAsync(other);
        using (var noCookie = await ZipAsync(null, (await RestClient.JsonAsync(open))["copy"]!.GetValue<string>())) await AssertNotFoundAsync(noCookie);
    }

    [Fact]
    public async Task A_wrong_code_is_404_and_counts()
    {
        var (_, code) = await SharedAsync();
        await using var api = ReaderApiTests.Limited(app.PublicApi);
        for (var i = 0; i < ReaderApiTests.FailuresPerNetwork; i++)
        {
            using var wrong = await OpenAsync(ShareCode.New(), $"203.0.113.{40 + i}", api);
            await AssertNotFoundAsync(wrong);
            Assert.StartsWith($"{SkillPageApi.Cookie}=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/api/k",
                wrong.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(SkillPageApi.Cookie + "=", StringComparison.Ordinal)));
        }

        using (var held = await OpenAsync(code, "203.0.113.50", api))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, held.StatusCode);
            Assert.NotNull(held.Headers.RetryAfter);
        }

        using var other = await OpenAsync(code, "198.51.100.50", api);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task The_page_and_api_get_the_readers_headers()
    {
        var (_, code) = await SharedAsync();
        using var page = await app.PublicApi.CreateClient().GetAsync("/k", Ct);
        using var reader = await app.PublicApi.CreateClient().GetAsync("/s", Ct);
        Assert.Equal(reader.Headers.GetValues("Content-Security-Policy"), page.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal(reader.Headers.CacheControl?.ToString(), page.Headers.CacheControl?.ToString());
        Assert.Equal(reader.Headers.GetValues("Referrer-Policy"), page.Headers.GetValues("Referrer-Policy"));
        using var api = await OpenAsync(code);
        Assert.Equal(Security.ReaderPagePolicy, api.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-referrer", api.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("noindex, nofollow", string.Join(", ", api.Headers.GetValues("X-Robots-Tag")));
    }

    [Fact]
    public async Task A_note_link_doesnt_open_here()
    {
        var note = await ReaderApiTests.PublishedAsync(app.PublicApi);
        using var open = await OpenAsync(note.Formatted);
        await AssertNotFoundAsync(open);
    }

    [Fact]
    public async Task A_skill_link_doesnt_open_the_reader()
    {
        var (_, code) = await SharedAsync();
        using var open = await SendAsync(HttpMethod.Post, "/api/s", new { code });
        Assert.Equal(HttpStatusCode.NotFound, open.StatusCode);
        Assert.Equal(ReaderApi.NotFoundSentence, await RestClient.ErrorAsync(open));
    }

    [Fact]
    public async Task Publishing_off_is_404_with_its_type()
    {
        var (_, code) = await SharedAsync();
        await using var off = app.PublicApi.WithWebHostBuilder(b => b.UseSetting("Kumbu:Publish:Enabled", "false"));
        using var refused = await OpenAsync(code, api: off);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        var body = await RestClient.JsonAsync(refused);
        Assert.Equal(SkillPageApi.OffSentence, body["error"]!.GetValue<string>());
        Assert.Equal(ReaderApi.OffType, body["type"]!.GetValue<string>());
        Assert.False(refused.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(SkillPageApi.Cookie + "=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Own_skill_zip_for_the_session()
    {
        var (alice, _) = await SharedAsync();
        // The password for downloads off: the ticket is SkillZipApiTests' matter.
        using (var off = await alice.SendAsync(HttpMethod.Put, "/api/account/preferences", new { askPasswordForDownloads = false, password = RestClient.Password })) Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        using var zip = await alice.GetAsync("/api/skills/zip?path=" + Uri.EscapeDataString("/Skills/write-as-alice"));
        await AssertEntriesAsync(zip);

        using var none = await alice.GetAsync("/api/skills/zip?path=" + Uri.EscapeDataString("/Skills/nothing-here"));
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        using (var off = await bob.SendAsync(HttpMethod.Put, "/api/account/preferences", new { askPasswordForDownloads = false, password = RestClient.Password })) Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        using var bobs = await bob.GetAsync("/api/skills/zip?path=" + Uri.EscapeDataString("/Skills/write-as-alice"));
        Assert.Equal(HttpStatusCode.NotFound, bobs.StatusCode);
    }

    [Fact]
    public async Task The_code_reaches_no_log()
    {
        var (_, formatted) = await SharedAsync();
        var code = ShareCode.Parse(formatted)!;
        var log = new CapturedLog();
        await using var api = ReaderApiTests.Limited(app.PublicApi, log);
        using var open = await OpenAsync(formatted, "203.0.113.60", api);
        var cookie = CookieOf(open);
        (await SendAsync(HttpMethod.Get, "/api/k", null, new Dictionary<string, string> { ["Cookie"] = cookie }, "203.0.113.60", api)).Dispose();
        (await SendAsync(HttpMethod.Get, "/api/k/zip", null, new Dictionary<string, string> { ["Cookie"] = cookie, [ReaderApi.CopyHeader] = ShareCode.CopyId(code) }, "203.0.113.60", api)).Dispose();
        var wrong = new List<string>();
        for (var i = 0; i <= ReaderApiTests.FailuresPerNetwork; i++)
        {
            var guess = ShareCode.New();
            wrong.Add(guess);
            (await OpenAsync(ShareCode.Format(guess), $"203.0.113.{61 + i}", api)).Dispose();
        }

        Assert.Contains(log.Lines, l => l.StartsWith("Warning", StringComparison.Ordinal) && l.Contains("share codes", StringComparison.Ordinal));
        var secrets = new[] { code, formatted, Convert.ToHexStringLower(ShareCode.Lookup(code)), Convert.ToBase64String(ShareCode.Lookup(code)), ShareCode.CopyId(code) }
            .Concat(wrong).Concat(wrong.Select(ShareCode.Format)).Concat(wrong.Select(c => Convert.ToHexStringLower(ShareCode.Lookup(c))));
        foreach (var secret in secrets) Assert.DoesNotContain(log.Lines, l => l.Contains(secret, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_file_name_falls_back_to_ascii()
    {
        Assert.Equal("attachment; filename=\"schreib-_ber.zip\"; filename*=UTF-8''schreib-%C3%BCber.zip", SkillZip.Disposition("schreib-über"));
        Assert.Equal("attachment; filename=\"a_b_c.zip\"; filename*=UTF-8''a%22b%20c.zip", SkillZip.Disposition("a\"b c"));
    }

    [Fact]
    public void The_zip_has_the_given_time_and_root()
    {
        var at = new DateTime(2026, 11, 8, 10, 30, 0, DateTimeKind.Utc);
        var bytes = SkillZip.Of("x", [("SKILL.md", Encoding.UTF8.GetBytes("---\n")), ("scripts/run.py", Encoding.UTF8.GetBytes("print(1)\n"))], at);
        using var archive = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(["x/SKILL.md", "x/scripts/run.py"], archive.Entries.Select(e => e.FullName));
        // A zip keeps the time of day without a zone.
        Assert.All(archive.Entries, e => Assert.Equal(at.ToString("s"), e.LastWriteTime.DateTime.ToString("s")));
    }

    // The stored paths are checked where they are written; the archive refuses anything that would unpack outside its root.
    [Theory]
    [InlineData("", "SKILL.md")]
    [InlineData(".", "SKILL.md")]
    [InlineData("..", "SKILL.md")]
    [InlineData("a/b", "SKILL.md")]
    [InlineData("a\\b", "SKILL.md")]
    [InlineData("x", "")]
    [InlineData("x", "/etc/passwd")]
    [InlineData("x", "../SKILL.md")]
    [InlineData("x", "scripts/../../y")]
    [InlineData("x", "scripts/./run.py")]
    [InlineData("x", "scripts//run.py")]
    [InlineData("x", "scripts/")]
    [InlineData("x", "scripts\\run.py")]
    public void The_zip_refuses_a_path_that_leaves_its_root(string folderName, string path) =>
        Assert.Throws<ArgumentException>(() => SkillZip.Of(folderName, [(path, [1])], DateTime.UtcNow));

    [Theory]
    [InlineData(new[] { "SKILL.md", "examples/post.md" }, false)]
    [InlineData(new[] { "SKILL.md", "scripts/README.md" }, true)]
    [InlineData(new[] { "SKILL.md", "tools/run.py" }, true)]
    [InlineData(new[] { "SKILL.md", "run.sh" }, true)]
    [InlineData(new[] { "SKILL.md", "lib/a.JS" }, true)]
    [InlineData(new[] { "SKILL.md", "lib/a.mjs" }, true)]
    [InlineData(new[] { "SKILL.md", "lib/a.ts" }, true)]
    [InlineData(new[] { "SKILL.md", "notes/scripts.md" }, false)]
    public void Scripts_are_a_folder_or_an_extension(string[] paths, bool expected) => Assert.Equal(expected, SkillPageApi.HasScripts(paths));
}
