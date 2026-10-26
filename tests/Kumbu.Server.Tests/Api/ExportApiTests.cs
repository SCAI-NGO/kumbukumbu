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

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

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

        using var response = await user.GetAsync($"/api/vaults/{user.Vault}/export");

        using var zip = await ZipAsync(response);
        Assert.Equal(["Arbeit/", "Arbeit/a.md", "Arbeit/p/b.md"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal("# A\n\nü and ²\n", Read(zip.GetEntry("Arbeit/a.md")!));
        Assert.Equal("b\n", Read(zip.GetEntry("Arbeit/p/b.md")!));
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal($"Arbeit {Today}.zip", disposition.FileNameStar);
        Assert.Equal($"Arbeit {Today}.zip", disposition.FileName!.Trim('"'));
    }

    [Fact]
    public async Task Entries_carry_the_last_write_time()
    {
        var user = await RestClient.NewAsync(Api);
        await user.CreateAsync("a.md", "a\n");
        await user.CreateAsync("p/b.md", "b\n");

        using var response = await user.GetAsync($"/api/vaults/{user.Vault}/export");

        using var zip = await ZipAsync(response);
        foreach (var path in new[] { "a.md", "p/b.md" })
        {
            var written = (await user.ReadAsync(path))["lastWriteAt"]!.GetValue<DateTime>().ToUniversalTime();
            var entry = zip.GetEntry($"Arbeit/{path}")!;
            Assert.InRange((entry.LastWriteTime.UtcDateTime - written).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(2));
        }
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
        var wanted = Encoding.UTF8.GetBytes(name);
        for (var i = 0; i + 30 <= zip.Length; i++)
        {
            if (zip[i] != 0x50 || zip[i + 1] != 0x4B || zip[i + 2] != 0x03 || zip[i + 3] != 0x04) continue;
            var length = zip[i + 26] | (zip[i + 27] << 8);
            if (zip.AsSpan(i + 30, length).SequenceEqual(wanted)) return zip[i + 6] | (zip[i + 7] << 8);
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

        using var response = await user.GetAsync("/api/export?vault=Arbeit&vault=Ideen");

        using var zip = await ZipAsync(response);
        Assert.Equal(["Arbeit/", "Arbeit/a.md", "Ideen/", "Ideen/x/c.md"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal("c\n", Read(zip.GetEntry("Ideen/x/c.md")!));
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Equal($"Kumbu export {Today}.zip", disposition.FileName!.Trim('"'));
        Assert.Equal($"Kumbu² export {Today}.zip", disposition.FileNameStar);
    }

    [Fact]
    public async Task Export_is_for_sessions_only()
    {
        var user = await RestClient.NewAsync(Api);
        var key = await user.KeyAsync("laptop", KeyRight.Write);

        using var one = await user.GetAsync($"/api/vaults/{user.Vault}/export", bearer: key);
        using var several = await user.GetAsync($"/api/export?vault={user.Vault}", bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, one.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, several.StatusCode);
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

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
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
