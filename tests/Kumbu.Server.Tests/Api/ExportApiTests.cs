// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using System.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary><c>GET /api/vaults/{vault}/export</c> and <c>GET /api/export?vault=…</c>: vaults as ZIP folders of their notes.</summary>
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
        using var response = await user.GetAsync($"/api/vaults/{user.Vault}/export");

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
        using var response = await user.GetAsync($"/api/vaults/{user.Vault}/export");

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

        using var inVienna = await user.GetAsync($"/api/vaults/{user.Vault}/export?tz=Europe%2FVienna");
        using var unknown = await user.GetAsync($"/api/vaults/{user.Vault}/export?tz=Mars%2FOlympus");
        using var several = await user.GetAsync($"/api/export?vault={user.Vault}&tz=Europe%2FVienna");

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

        using var response = await user.GetAsync($"/api/vaults/{user.Vault}/export");

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

        using var response = await user.GetAsync($"/api/vaults/{user.Vault}/export");

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
        using var response = await user.GetAsync("/api/export?vault=Arbeit&vault=Ideen");

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

        using var one = await user.GetAsync($"/api/vaults/{other.Vault}/export");
        using var several = await user.GetAsync($"/api/export?vault=Arbeit&vault={other.Vault}");

        Assert.Equal(HttpStatusCode.NotFound, one.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, several.StatusCode);
        Assert.Equal("application/json", several.Content.Headers.ContentType?.MediaType);
        Assert.Contains(other.Vault, await RestClient.ErrorAsync(several));
    }

    [Fact]
    public async Task Export_needs_a_vault()
    {
        var user = await RestClient.NewAsync(Api);

        using var none = await user.GetAsync("/api/export");
        using var twice = await user.GetAsync("/api/export?vault=Arbeit&vault=Arbeit");
        // "Bücher" precomposed and decomposed: one vault after NFC.
        using var spelt = await user.GetAsync("/api/export?vault=B%C3%BCcher&vault=Bu%CC%88cher");

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
}
