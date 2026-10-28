// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using System.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Api;

/// <summary><c>GET /api/vaults/{vault}/export</c> and <c>GET /api/export?vault=…</c>: vaults as ZIP folders of their notes
/// and files; <c>GET /api/files/{vault}/zip</c>: several files of a vault as a ZIP.</summary>
[Collection("app")]
public sealed class ExportApiTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static string Date(DateTime at) => at.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The file name as the server's UTC date gives it, before or after the request (a run across midnight).</summary>
    private static void AssertDated(string expected, string? actual, DateTime before)
    {
        Assert.Contains(actual, new[] { expected.Replace("{date}", Date(before)), expected.Replace("{date}", Date(DateTime.UtcNow)) });
    }

    private static async Task<ZipArchive> ZipAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        return new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
    }

    /// <summary>An export as the web app makes it: a ticket for the password first, then the download with it.</summary>
    private static async Task<HttpResponseMessage> ExportAsync(RestClient user, string url) =>
        await user.GetAsync(url + (url.Contains('?') ? "&" : "?") + "ticket=" + Uri.EscapeDataString(await TicketAsync(user)));

    private static async Task<string> TicketAsync(RestClient user, string password = RestClient.Password)
    {
        using var response = await user.SendAsync(HttpMethod.Post, "/api/export/ticket", new { password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response))["ticket"]!.GetValue<string>();
    }

    [Fact]
    public async Task Without_a_ticket_an_export_is_403_and_names_the_way()
    {
        var user = await RestClient.NewAsync(Api);

        using var one = await user.GetAsync($"/api/vaults/{user.Vault}/export");
        using var several = await user.GetAsync($"/api/export?vault={user.Vault}&ticket=kumbu_ext_nonsense");

        Assert.Equal(HttpStatusCode.Forbidden, one.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, several.StatusCode);
        Assert.Contains("POST /api/export/ticket", await RestClient.ErrorAsync(one), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_password_gets_no_ticket()
    {
        var user = await RestClient.NewAsync(Api);

        using var response = await user.SendAsync(HttpMethod.Post, "/api/export/ticket", new { password = "not the password at all" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The password is wrong.", await RestClient.ErrorAsync(response));
    }

    [Fact]
    public async Task A_ticket_works_once_for_its_own_user()
    {
        var user = await RestClient.NewAsync(Api);
        var other = await RestClient.NewAsync(Api, vault: "Fremd" + Guid.NewGuid().ToString("N")[..6]);
        var ticket = await TicketAsync(user);

        using var foreign = await other.GetAsync($"/api/vaults/{other.Vault}/export?ticket={ticket}");
        using var first = await user.GetAsync($"/api/vaults/{user.Vault}/export?ticket={ticket}");
        using var again = await user.GetAsync($"/api/vaults/{user.Vault}/export?ticket={ticket}");

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
    }

    [Fact]
    public async Task A_new_ticket_replaces_the_open_one_and_a_ticket_expires()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var tickets = new ExportTickets(time);

        var first = tickets.Issue("u1").Ticket;
        var second = tickets.Issue("u1").Ticket;
        var third = tickets.Issue("u1").Ticket;
        time.Advance(ExportTickets.Lifetime + TimeSpan.FromSeconds(1));

        Assert.False(tickets.Take("u1", first));
        Assert.False(tickets.Take("u1", second));
        Assert.False(tickets.Take("u1", third));
        Assert.True(tickets.Take("u2", tickets.Issue("u2").Ticket));
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task A_vault_exports_as_one_folder_of_its_notes()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateAsync("a.md", "# A\n\nü and ²\n");
        await user.CreateAsync("p/b.md", "b\n");

        var before = DateTime.UtcNow;
        using var response = await ExportAsync(user, $"/api/vaults/{user.Vault}/export");

        using var zip = await ZipAsync(response);
        Assert.Equal(["Arbeit/", "Arbeit/a.md", "Arbeit/p/b.md"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal("# A\n\nü and ²\n", Read(zip.GetEntry("Arbeit/a.md")!));
        Assert.Equal("b\n", Read(zip.GetEntry("Arbeit/p/b.md")!));
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        AssertDated("Arbeit {date}.zip", disposition.FileNameStar, before);
        AssertDated("Arbeit {date}.zip", disposition.FileName!.Trim('"'), before);
    }

    [Fact]
    public async Task Entries_carry_the_last_write_time()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        await user.CreateAsync("p/b.md", "b\n");

        // Without a time zone the entries carry UTC (ZIP time has 2-second precision).
        using var response = await ExportAsync(user, $"/api/vaults/{user.Vault}/export");

        var bytes = await BytesAsync(response);
        foreach (var path in new[] { "a.md", "p/b.md" })
        {
            var written = (await user.ReadAsync(path))["lastWriteAt"]!.GetValue<DateTime>().ToUniversalTime();
            Assert.InRange((DosTime(bytes, $"Arbeit/{path}") - written).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Entry_times_are_the_readers_clock_in_its_time_zone()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        var written = (await user.ReadAsync("a.md"))["lastWriteAt"]!.GetValue<DateTime>().ToUniversalTime();
        var vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

        using var inVienna = await ExportAsync(user, $"/api/vaults/{user.Vault}/export?tz=Europe%2FVienna");
        using var unknown = await ExportAsync(user, $"/api/vaults/{user.Vault}/export?tz=Mars%2FOlympus");
        using var several = await ExportAsync(user, $"/api/export?vault={user.Vault}&tz=Europe%2FVienna");

        // Read from the header bytes, not through ZipArchive, which would apply the test process's own zone.
        var expected = TimeZoneInfo.ConvertTimeFromUtc(written, vienna);
        Assert.InRange((DosTime(await BytesAsync(inVienna), "Arbeit/a.md") - expected).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.InRange((DosTime(await BytesAsync(several), "Arbeit/a.md") - expected).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.InRange((DosTime(await BytesAsync(unknown), "Arbeit/a.md") - written).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_missing_or_unknown_zone_is_utc()
    {
        Assert.Same(TimeZoneInfo.Utc, ExportApi.Zone(null));
        Assert.Same(TimeZoneInfo.Utc, ExportApi.Zone(""));
        Assert.Same(TimeZoneInfo.Utc, ExportApi.Zone("Mars/Olympus"));
        Assert.Equal(TimeSpan.FromHours(2), ExportApi.Zone("Europe/Vienna").GetUtcOffset(new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(TimeSpan.FromHours(1), ExportApi.Zone("Europe/Vienna").GetUtcOffset(new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc)));
    }

    private static async Task<byte[]> BytesAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    /// <summary>The MS-DOS date and time of the local file header named <paramref name="name"/>, as written.</summary>
    private static DateTime DosTime(byte[] zip, string name)
    {
        var at = LocalHeader(zip, name);
        var time = zip[at + 10] | (zip[at + 11] << 8);
        var date = zip[at + 12] | (zip[at + 13] << 8);
        return new DateTime(1980 + (date >> 9), (date >> 5) & 0xF, date & 0x1F, time >> 11, (time >> 5) & 0x3F, (time & 0x1F) * 2);
    }

    [Fact]
    public async Task Non_ascii_names_survive_the_zip()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateAsync("Bücher/Ü².md", "u\n");

        using var response = await ExportAsync(user, $"/api/vaults/{user.Vault}/export");

        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.Contains("Arbeit/Bücher/Ü².md", zip.Entries.Select(e => e.FullName));
        Assert.Equal("u\n", Read(zip.GetEntry("Arbeit/Bücher/Ü².md")!));
        // The language encoding flag (bit 11) on its local header: Windows and macOS unpackers read the name as UTF-8.
        Assert.True((LocalHeaderFlags(bytes, "Arbeit/Bücher/Ü².md") & 0x800) != 0);
    }

    /// <summary>The general-purpose flags of the local file header whose name is <paramref name="name"/> in UTF-8.</summary>
    private static int LocalHeaderFlags(byte[] zip, string name)
    {
        var at = LocalHeader(zip, name);
        return zip[at + 6] | (zip[at + 7] << 8);
    }

    /// <summary>The offset of the local file header whose name is <paramref name="name"/> in UTF-8.</summary>
    private static int LocalHeader(byte[] zip, string name)
    {
        var wanted = Encoding.UTF8.GetBytes(name);
        for (var i = 0; i + 30 <= zip.Length; i++)
        {
            if (zip[i] != 0x50 || zip[i + 1] != 0x4B || zip[i + 2] != 0x03 || zip[i + 3] != 0x04) continue;
            var length = zip[i + 26] | (zip[i + 27] << 8);
            if (i + 30 + length <= zip.Length && zip.AsSpan(i + 30, length).SequenceEqual(wanted)) return i;
        }

        throw new InvalidOperationException($"No local header for {name}.");
    }

    [Fact]
    public async Task An_empty_vault_exports_its_folder()
    {
        var user = await RestClient.NewAsync(Api);

        using var response = await ExportAsync(user, $"/api/vaults/{user.Vault}/export");

        using var zip = await ZipAsync(response);
        Assert.Equal(["Arbeit/"], zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public async Task Several_vaults_export_side_by_side()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        using var created = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Ideen" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var written = await user.SendAsync(HttpMethod.Put, "/api/notes/Ideen/x/c.md", new { content = "c\n" });
        Assert.Equal(HttpStatusCode.Created, written.StatusCode);

        var before = DateTime.UtcNow;
        using var response = await ExportAsync(user, "/api/export?vault=Arbeit&vault=Ideen");

        using var zip = await ZipAsync(response);
        Assert.Equal(["Arbeit/", "Arbeit/a.md", "Ideen/", "Ideen/x/c.md"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal("c\n", Read(zip.GetEntry("Ideen/x/c.md")!));
        var disposition = response.Content.Headers.ContentDisposition!;
        AssertDated("Kumbu export {date}.zip", disposition.FileName!.Trim('"'), before);
        AssertDated("Kumbu² export {date}.zip", disposition.FileNameStar, before);
    }

    [Fact]
    public async Task Export_is_for_sessions_only()
    {
        var user = await RestClient.NewAsync(Api);
        var key = await user.KeyAsync("laptop", KeyRight.Write);

        using var one = await user.GetAsync($"/api/vaults/{user.Vault}/export", bearer: key);
        using var several = await user.GetAsync($"/api/export?vault={user.Vault}", bearer: key);

        using var dev = await user.GetAsync($"/api/export?vault={user.Vault}", bearer: KumbuAppFactory.Token);

        Assert.Equal(HttpStatusCode.Forbidden, one.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, several.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, dev.StatusCode);
        Assert.Contains("signed-in session", await RestClient.ErrorAsync(several), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Another_users_vault_is_not_found()
    {
        var user = await RestClient.NewAsync(Api);
        var other = await RestClient.NewAsync(Api, vault: "Fremd" + Guid.NewGuid().ToString("N")[..6]);
        await other.CreateAsync("geheim.md", "secret\n");

        using var one = await ExportAsync(user, $"/api/vaults/{other.Vault}/export");
        using var several = await ExportAsync(user, $"/api/export?vault=Arbeit&vault={other.Vault}");

        Assert.Equal(HttpStatusCode.NotFound, one.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, several.StatusCode);
        Assert.Equal("application/json", several.Content.Headers.ContentType?.MediaType);
        Assert.Contains(other.Vault, await RestClient.ErrorAsync(several));
    }

    [Fact]
    public async Task Export_needs_a_vault()
    {
        var user = await RestClient.NewAsync(Api);

        using var none = await ExportAsync(user, "/api/export");
        using var twice = await ExportAsync(user, "/api/export?vault=Arbeit&vault=Arbeit");
        // "Bücher" precomposed and decomposed: one vault after NFC.
        using var spelt = await ExportAsync(user, "/api/export?vault=B%C3%BCcher&vault=Bu%CC%88cher");

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, spelt.StatusCode);
    }

    [Fact]
    public void Zip_names_follow_the_vaults_and_the_date()
    {
        var at = new DateTime(2026, 10, 26, 23, 30, 0, DateTimeKind.Utc);

        Assert.Equal("Work 2026-10-26.zip", ExportApi.ZipName(["Work"], at));
        Assert.Equal("Kumbu² export 2026-10-26.zip", ExportApi.ZipName(["Work", "Ideas"], at));
        Assert.Equal("Kumbu export 2026-10-26.zip", ExportApi.AsciiName("Kumbu² export 2026-10-26.zip"));
        Assert.Equal("Bucher 2026-10-26.zip", ExportApi.AsciiName("Bücher 2026-10-26.zip"));
    }

    private static async Task MakeCheckpointAsync(RestClient user, string name)
    {
        using var made = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{user.Vault}/checkpoints", new { name });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
    }

    [Fact]
    public async Task A_checkpoint_exports_as_its_vault_folder_of_its_notes_then()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateAsync("a.md", "then\n");
        await user.CreateAsync("Bücher/Ü².md", "u\n");
        await MakeCheckpointAsync(user, "cp");
        var version = (await user.ReadAsync("a.md"))["version"]!.GetValue<string>();
        using var rewritten = await user.SendAsync(HttpMethod.Put, user.Url("a.md"), new { content = "now\n", version });
        Assert.Equal(HttpStatusCode.OK, rewritten.StatusCode);
        await user.CreateAsync("c.md", "c\n");

        var before = DateTime.UtcNow;
        using var response = await ExportAsync(user, $"/api/vaults/{user.Vault}/checkpoints/cp/export");

        using var zip = await ZipAsync(response);
        Assert.Equal(["Arbeit/", "Arbeit/Bücher/Ü².md", "Arbeit/a.md"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal("then\n", Read(zip.GetEntry("Arbeit/a.md")!));
        AssertDated("Arbeit checkpoint cp {date}.zip", response.Content.Headers.ContentDisposition!.FileNameStar, before);
    }

    [Fact]
    public async Task A_folder_checkpoint_exports_with_the_vault_folder_on_top()
    {
        var user = await RestClient.NewAsync(Api);
        var write = await user.KeyAsync("w" + Guid.NewGuid().ToString("N")[..6], KeyRight.Write);
        await user.CreateAsync("a.md", "a\n");
        await user.CreateAsync("p/b.md", "b\n");
        var made = await user.ShAsync("mkcp /Arbeit/p cp", write);
        Assert.False(made.IsError, made.Text);

        using var response = await ExportAsync(user, $"/api/vaults/{user.Vault}/checkpoints/cp/export");

        using var zip = await ZipAsync(response);
        Assert.Equal(["Arbeit/", "Arbeit/p/b.md"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Checkpoint_entries_carry_the_time_it_was_made()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        await user.CreateAsync("p/b.md", "b\n");
        await MakeCheckpointAsync(user, "cp");
        using var list = await user.GetAsync($"/api/vaults/{user.Vault}/checkpoints");
        var madeAt = (await RestClient.JsonAsync(list))["checkpoints"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "cp")!["createdAt"]!.GetValue<DateTime>().ToUniversalTime();

        using var response = await ExportAsync(user, $"/api/vaults/{user.Vault}/checkpoints/cp/export");

        var bytes = await BytesAsync(response);
        foreach (var path in new[] { "a.md", "p/b.md" })
        {
            Assert.InRange((DosTime(bytes, $"Arbeit/{path}") - madeAt).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Checkpoint_export_is_for_sessions_only()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        await MakeCheckpointAsync(user, "cp");
        var url = $"/api/vaults/{user.Vault}/checkpoints/cp/export";

        using var read = await user.GetAsync(url, bearer: await user.KeyAsync("r", KeyRight.Read));
        using var write = await user.GetAsync(url, bearer: await user.KeyAsync("w", KeyRight.Write));
        using var dev = await user.GetAsync(url, bearer: KumbuAppFactory.Token);

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, dev.StatusCode);
    }

    [Fact]
    public async Task Checkpoint_export_of_an_unknown_vault_or_checkpoint_is_not_found()
    {
        var user = await RestClient.NewAsync(Api);
        var other = await RestClient.NewAsync(Api, vault: "Fremd" + Guid.NewGuid().ToString("N")[..6]);
        await other.CreateAsync("geheim.md", "secret\n");
        await MakeCheckpointAsync(other, "cp");

        using var foreign = await ExportAsync(user, $"/api/vaults/{other.Vault}/checkpoints/cp/export");
        using var unknown = await ExportAsync(user, $"/api/vaults/{user.Vault}/checkpoints/nope/export");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("application/json", unknown.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void Checkpoint_zip_name_is_dated_in_the_readers_zone()
    {
        var at = new DateTime(2026, 10, 19, 23, 30, 0, DateTimeKind.Utc);

        Assert.Equal("Ideas checkpoint before-cleanup 2026-10-20.zip", ExportApi.CheckpointZipName("Ideas", "before-cleanup", at, TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna")));
        Assert.Equal("Ideas checkpoint before-cleanup 2026-10-19.zip", ExportApi.CheckpointZipName("Ideas", "before-cleanup", at, TimeZoneInfo.Utc));
    }

    private static byte[] Png(int width, int height) => FileApiTests.Png(width, height);

    private static readonly byte[] Pdf = "%PDF-1.4\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF\n"u8.ToArray();

    private static byte[] Bytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string ZipUrl(RestClient user, params string[] paths) =>
        $"/api/files/{user.Vault}/zip?" + string.Join('&', paths.Select(p => "path=" + Uri.EscapeDataString(p)));

    private static async Task TurnPasswordOffAsync(RestClient user)
    {
        using var off = await user.SendAsync(HttpMethod.Put, "/api/account/preferences", new { askPasswordForDownloads = false, password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
    }

    [Fact]
    public async Task A_zip_of_several_files_needs_a_ticket_by_default()
    {
        var user = await RestClient.NewAsync(Api);
        var png = Png(4, 3);
        await user.CreateFileAsync("a.png", png);
        await user.CreateFileAsync("Bücher/b.pdf", Pdf);
        await user.CreateAsync("n.md", "not asked for\n");

        using var without = await user.GetAsync(ZipUrl(user, "a.png", "Bücher/b.pdf"));
        var before = DateTime.UtcNow;
        using var with = await ExportAsync(user, ZipUrl(user, "Bücher/b.pdf", "/a.png", "a.png"));

        Assert.Equal(HttpStatusCode.Forbidden, without.StatusCode);
        Assert.Contains("POST /api/export/ticket", await RestClient.ErrorAsync(without), StringComparison.Ordinal);
        using var zip = await ZipAsync(with);
        // At their paths in the vault, no vault folder on top; a file named twice is there once.
        Assert.Equal(["Bücher/b.pdf", "a.png"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(png, Bytes(zip.GetEntry("a.png")!));
        Assert.Equal(Pdf, Bytes(zip.GetEntry("Bücher/b.pdf")!));
        AssertDated("Arbeit files {date}.zip", with.Content.Headers.ContentDisposition!.FileNameStar, before);
    }

    [Fact]
    public async Task With_the_preference_off_no_ticket_is_needed_and_the_download_is_still_logged()
    {
        var log = new CapturedLog();
        await using var logged = Api.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var user = await RestClient.NewAsync(logged, "Geheimprojekt");
        await user.CreateFileAsync("plan.png", Png(2, 2));
        await user.CreateFileAsync("x/scan.pdf", Pdf);
        using var asking = await ExportAsync(user, ZipUrl(user, "plan.png", "x/scan.pdf"));
        await TurnPasswordOffAsync(user);

        using var response = await user.GetAsync(ZipUrl(user, "plan.png", "x/scan.pdf"));

        using var zip = await ZipAsync(response);
        Assert.Equal(2, zip.Entries.Count);
        var lines = log.Lines.Where(l => l.Contains($"User {user.User.Id} downloaded 2 files", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("with the password", lines[0], StringComparison.Ordinal);
        Assert.Contains("without the password", lines[1], StringComparison.Ordinal);
        Assert.Contains(log.Lines, l => l.Contains($"User {user.User.Id} turned off the password", StringComparison.Ordinal));
        // Ids and counts, never a vault or file name.
        foreach (var name in new[] { "Geheimprojekt", "plan", "scan" }) Assert.DoesNotContain(log.Lines, l => l.Contains(name, StringComparison.Ordinal));
    }

    /// <summary>The web app's form post: a session with its own Origin, the fields in a form body.</summary>
    private static Task<HttpResponseMessage> PostZipAsync(RestClient user, IEnumerable<string> paths, string? ticket = null, string? bearer = null)
    {
        var fields = paths.Select(p => new KeyValuePair<string, string>("path", p)).Append(new("tz", "Europe/Vienna"));
        if (ticket is not null) fields = fields.Append(new("ticket", ticket));
        return user.SendContentAsync(HttpMethod.Post, $"/api/files/{user.Vault}/zip", new FormUrlEncodedContent(fields), bearer);
    }

    [Fact]
    public async Task A_zip_posted_as_a_form_takes_the_same_checks_as_the_get()
    {
        var user = await RestClient.NewAsync(Api);
        var png = Png(4, 3);
        await user.CreateFileAsync("a.png", png);
        await user.CreateFileAsync("Bücher/b.pdf", Pdf);

        using var without = await PostZipAsync(user, ["a.png", "Bücher/b.pdf"]);
        using var with = await PostZipAsync(user, ["a.png", "Bücher/b.pdf"], await TicketAsync(user));
        using var tooMany = await PostZipAsync(user, Enumerable.Range(0, 101).Select(i => $"f{i}.png"), await TicketAsync(user));
        using var json = await user.SendAsync(HttpMethod.Post, $"/api/files/{user.Vault}/zip", new { path = new[] { "a.png" } });
        using var key = await PostZipAsync(user, ["a.png"], bearer: await user.KeyAsync("w", KeyRight.Write));

        Assert.Equal(HttpStatusCode.Forbidden, without.StatusCode);
        Assert.Contains("POST /api/export/ticket", await RestClient.ErrorAsync(without), StringComparison.Ordinal);
        using var zip = await ZipAsync(with);
        Assert.Equal(["Bücher/b.pdf", "a.png"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(png, Bytes(zip.GetEntry("a.png")!));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("at most 100 files", await RestClient.ErrorAsync(tooMany), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, key.StatusCode);
        Assert.Contains("signed-in session", await RestClient.ErrorAsync(key), StringComparison.Ordinal);

        // The gallery posts into a hidden frame of its own and reads an error there: only this route may be framed,
        // and only by the app's own origin.
        string Frames(HttpResponseMessage r) => r.Headers.GetValues("Content-Security-Policy").Single().Split("; ").Single(d => d.StartsWith("frame-ancestors", StringComparison.Ordinal));
        Assert.Equal("frame-ancestors 'self'", Frames(without));
        Assert.Equal("frame-ancestors 'self'", Frames(with));
        using var get = await user.GetAsync(ZipUrl(user, "a.png"));
        Assert.Equal("frame-ancestors 'none'", Frames(get));

        // With the preference off, no ticket.
        await TurnPasswordOffAsync(user);
        using var off = await PostZipAsync(user, ["a.png"]);
        Assert.Single((await ZipAsync(off)).Entries);
    }

    [Fact]
    public async Task A_zip_form_from_another_origin_is_refused()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateFileAsync("a.png", Png(1, 1));
        await TurnPasswordOffAsync(user);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/files/{user.Vault}/zip")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("path", "a.png")]),
        };
        request.Headers.Add("Cookie", $"{CallerMiddleware.SessionCookie}={user.Session}");
        request.Headers.Add("Origin", "https://elsewhere.example");
        using var client = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Kumbu's own pages", await RestClient.ErrorAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_zip_of_101_files_is_a_400()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateFileAsync("a.png", Png(1, 1));
        var paths = Enumerable.Range(0, 101).Select(i => $"f{i}.png").ToArray();

        using var tooMany = await ExportAsync(user, ZipUrl(user, paths));
        using var hundred = await ExportAsync(user, ZipUrl(user, [.. Enumerable.Repeat("a.png", 100)]));

        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("at most 100 files", await RestClient.ErrorAsync(tooMany), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, hundred.StatusCode);
    }

    [Fact]
    public async Task A_zip_refuses_bad_paths_missing_files_and_notes_before_the_first_byte()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateFileAsync("a.png", Png(1, 1));
        await user.CreateAsync("n.md", "n\n");

        using var none = await ExportAsync(user, $"/api/files/{user.Vault}/zip");
        using var missing = await ExportAsync(user, ZipUrl(user, "a.png", "gone.png"));
        using var note = await ExportAsync(user, ZipUrl(user, "a.png", "n.md"));
        using var escape = await ExportAsync(user, ZipUrl(user, "a.png", "../Fremd/b.png"));
        using var unsupported = await ExportAsync(user, ZipUrl(user, "a.svg"));

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/json", missing.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest, note.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, escape.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
    }

    [Fact]
    public async Task A_zip_of_another_users_vault_is_not_found_and_keys_cannot_download_one()
    {
        var user = await RestClient.NewAsync(Api);
        var other = await RestClient.NewAsync(Api, vault: "Fremd" + Guid.NewGuid().ToString("N")[..6]);
        await other.CreateFileAsync("a.png", Png(1, 1));
        await user.CreateFileAsync("a.png", Png(1, 1));

        using var foreign = await ExportAsync(user, $"/api/files/{other.Vault}/zip?path=a.png");
        using var key = await user.GetAsync(ZipUrl(user, "a.png"), bearer: await user.KeyAsync("w", KeyRight.Write));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, key.StatusCode);
        Assert.Contains("signed-in session", await RestClient.ErrorAsync(key), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_zip_route_leaves_a_folder_named_zip_to_the_file_api()
    {
        var user = await RestClient.NewAsync(Api);
        var png = Png(5, 5);
        await user.CreateFileAsync("zip/x.png", png);
        await TurnPasswordOffAsync(user);

        using var read = await user.GetAsync(user.Url("zip/x.png", "files"));
        using var zipped = await user.GetAsync(ZipUrl(user, "zip/x.png"));
        // No file can be named "zip": it has no extension, so a write to it is refused as any such name is.
        using var put = await user.PutFileAsync("zip", png);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(png, await read.Content.ReadAsByteArrayAsync(Ct));
        using var zip = await ZipAsync(zipped);
        Assert.Equal(png, Bytes(zip.GetEntry("zip/x.png")!));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task A_vault_export_contains_its_attachments()
    {
        var user = await RestClient.NewAsync(Api);
        var png = Png(7, 3);
        await user.CreateAsync("a.md", "![[shots/s.png]]\n");
        await user.CreateFileAsync("shots/s.png", png);
        await user.CreateFileAsync("Bücher/ü.pdf", Pdf);

        using var one = await ExportAsync(user, $"/api/vaults/{user.Vault}/export");
        using var several = await ExportAsync(user, $"/api/export?vault={user.Vault}");

        foreach (var response in new[] { one, several })
        {
            using var zip = await ZipAsync(response);
            Assert.Equal(["Arbeit/", "Arbeit/Bücher/ü.pdf", "Arbeit/a.md", "Arbeit/shots/s.png"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            Assert.Equal(png, Bytes(zip.GetEntry("Arbeit/shots/s.png")!));
            Assert.Equal(Pdf, Bytes(zip.GetEntry("Arbeit/Bücher/ü.pdf")!));
        }
    }

    [Fact]
    public async Task A_checkpoint_export_contains_its_attachments_as_they_were()
    {
        var user = await RestClient.NewAsync(Api);
        var then = Png(3, 3);
        var version = await user.CreateFileAsync("s.png", then);
        await MakeCheckpointAsync(user, "cp");
        using var rewritten = await user.PutFileAsync("s.png", Png(9, 9), version);
        Assert.Equal(HttpStatusCode.OK, rewritten.StatusCode);
        await user.CreateFileAsync("later.png", Png(1, 1));

        using var response = await ExportAsync(user, $"/api/vaults/{user.Vault}/checkpoints/cp/export");

        using var zip = await ZipAsync(response);
        Assert.Equal(["Arbeit/", "Arbeit/s.png"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(then, Bytes(zip.GetEntry("Arbeit/s.png")!));
    }

    [Fact]
    public async Task A_large_file_streams_through_the_zip_intact()
    {
        var user = await RestClient.NewAsync(Api);
        // A PDF of a few MB of incompressible bytes: written in pieces, not as one buffer.
        var pdf = new byte[3 * 1024 * 1024 + 17];
        Random.Shared.NextBytes(pdf);
        "%PDF-1.4\n"u8.CopyTo(pdf);
        await user.CreateFileAsync("big.pdf", pdf);
        await user.CreateFileAsync("small.png", Png(1, 1));

        using var response = await ExportAsync(user, ZipUrl(user, "big.pdf", "small.png"));

        using var zip = await ZipAsync(response);
        Assert.Equal(pdf, Bytes(zip.GetEntry("big.pdf")!));
    }

    /// <summary>Breaks the stored bytes of the smallest blob of the user's vault, as a change outside Kumbu² would.</summary>
    private async Task BreakSmallestBlobAsync(RestClient user)
    {
        var vault = (await Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == user.Vault);
        var blobs = Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<BsonDocument>("blobs");
        var smallest = (await blobs.Find(Builders<BsonDocument>.Filter.Eq("vault", vault.Id)).ToListAsync(Ct)).MinBy(d => d["blob"].AsByteArray.Length)!;
        var broken = smallest["blob"].AsByteArray.ToArray();
        broken[^1] ^= 0xFF;
        await blobs.UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("_id", smallest["_id"]), Builders<BsonDocument>.Update.Set("blob", broken), cancellationToken: Ct);
    }

    /// <summary>The download either fails outright or isn't a 200 with a ZIP that opens holding every one of <paramref name="expected"/>.</summary>
    private static async Task AssertNoCompleteZipAsync(Func<Task<HttpResponseMessage>> download, params string[] expected)
    {
        HttpResponseMessage response;
        byte[] bytes;
        try
        {
            response = await download();
            bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            return;
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK) return;
            try
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                Assert.False(expected.All(n => zip.GetEntry(n) is not null), "A ZIP missing nothing arrived although a file's content doesn't open.");
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    [Fact]
    public async Task A_file_whose_content_does_not_open_fails_the_download_instead_of_leaving_it_out()
    {
        var log = new CapturedLog();
        await using var logged = Api.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var user = await RestClient.NewAsync(logged);
        // A large first file, so the response has begun when the broken one comes.
        var big = new byte[512 * 1024];
        Random.Shared.NextBytes(big);
        "%PDF-1.4\n"u8.CopyTo(big);
        await user.CreateFileAsync("a.pdf", big);
        await user.CreateFileAsync("b.png", Png(2, 2));
        await BreakSmallestBlobAsync(user);
        await TurnPasswordOffAsync(user);

        await AssertNoCompleteZipAsync(() => user.GetAsync(ZipUrl(user, "a.pdf", "b.png")), "a.pdf", "b.png");
        await AssertNoCompleteZipAsync(() => ExportAsync(user, $"/api/vaults/{user.Vault}/export"), "Arbeit/a.pdf", "Arbeit/b.png");

        Assert.Contains(log.Lines, l => l.Contains("A ZIP download was cut off", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains($"User {user.User.Id} downloaded", StringComparison.Ordinal));
    }
}
