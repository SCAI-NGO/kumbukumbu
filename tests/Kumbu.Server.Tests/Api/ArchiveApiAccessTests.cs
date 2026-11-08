// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>
/// The archive's listing for whoever may, and may not, read the vault; a stamp the server's time zone skips; and the
/// account's archive figure counted again with the usage. The server's time zone is Berlin.
/// </summary>
[Collection("app")]
public sealed class ArchiveApiAccessTests(KumbuAppFactory app) : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = Berlin();

    private WebApplicationFactory<Program>? _timed;

    private WebApplicationFactory<Program> Api => _timed ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    public async ValueTask DisposeAsync()
    {
        if (_timed is not null) await _timed.DisposeAsync();
    }

    private static FakeTimeProvider Berlin()
    {
        // Whole seconds, as the other timed tests: MongoDB keeps milliseconds.
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));
        return time;
    }

    private static async Task<JsonNode> ArchiveAsync(RestClient user, string vault)
    {
        using var archive = await user.GetAsync($"/api/vaults/{Uri.EscapeDataString(vault)}/archive");
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        return await RestClient.JsonAsync(archive);
    }

    [Fact]
    public async Task The_archive_of_a_vault_the_caller_has_not_is_a_404()
    {
        var alice = await RestClient.NewAsync(Api, "Work");
        await alice.CreateAsync("_archive/old 2026-11-08 213005.md", "# Old\n");
        var bob = await RestClient.NewAsync(Api, "Garten");

        using var archive = await bob.GetAsync("/api/vaults/Work/archive");
        Assert.Equal(HttpStatusCode.NotFound, archive.StatusCode);
    }

    [Fact]
    public async Task A_reading_member_sees_the_archive_and_may_not_write()
    {
        var alice = await RestClient.NewAsync(Api, "Gartenarbeit");
        await alice.CreateAsync("_archive/Beete 2026-11-08 213005.md", "# Beete\n");
        var bob = await RestClient.NewAsync(Api, "Work");
        await alice.ShareWithAsync(bob, "Garten", MemberRole.Read);

        var archive = await ArchiveAsync(bob, "Garten");
        Assert.False(archive["canWrite"]!.GetValue<bool>());
        Assert.Equal(["/Garten/_archive/Beete 2026-11-08 213005.md"], archive["items"]!.AsArray().Select(i => i!["path"]!.GetValue<string>()));
        Assert.True((await ArchiveAsync(bob, "Work"))["canWrite"]!.GetValue<bool>());
    }

    /// <summary>02:30 on the last Sunday of March does not happen in Berlin: no stamp the server gave, so no time.</summary>
    [Fact]
    public async Task A_stamp_in_the_spring_gap_has_no_time_and_follows_the_dated_items()
    {
        var user = await RestClient.NewAsync(Api, "Work");
        await user.CreateAsync("_archive/gap 2026-03-29 023000.md", "gap\n");
        await user.CreateAsync("_archive/early 2026-01-01 000000.md", "early\n");
        await user.CreateAsync("_archive/loose.md", "put here by hand\n");

        var items = (await ArchiveAsync(user, "Work"))["items"]!.AsArray();
        Assert.Equal(
            ["/Work/_archive/early 2026-01-01 000000.md", "/Work/_archive/gap 2026-03-29 023000.md", "/Work/_archive/loose.md"],
            items.Select(i => i!["path"]!.GetValue<string>()));
        Assert.Equal(new DateTime(2025, 12, 31, 23, 0, 0, DateTimeKind.Utc), items[0]!["archivedAt"]!.GetValue<DateTime>().ToUniversalTime());
        Assert.Null(items[1]!["archivedAt"]);
        Assert.Equal("/Work/gap.md", items[1]!["originalPath"]!.GetValue<string>());
    }

    /// <summary>A recount (a sweep, an emptied trash, a new limit) makes the archive's figure stale as well as the usage.</summary>
    [Fact]
    public async Task A_recount_counts_the_archive_again()
    {
        var user = await RestClient.NewAsync(Api, "Work");
        await user.CreateAsync("_archive/old 2026-11-08 213005.md", "# Old\n");

        async Task<long> ArchiveBytesAsync()
        {
            using var storage = await user.GetAsync("/api/account/storage");
            Assert.Equal(HttpStatusCode.OK, storage.StatusCode);
            return (await RestClient.JsonAsync(storage))["archive"]!.GetValue<long>();
        }

        Assert.Equal(Encoding.UTF8.GetByteCount("# Old\n"), await ArchiveBytesAsync());
        await user.CreateAsync("_archive/more 2026-11-08 213006.md", "more\n");
        await Api.Services.GetRequiredService<StorageQuota>().RecountAsync(user.User.Id);
        Assert.Equal(Encoding.UTF8.GetByteCount("# Old\nmore\n"), await ArchiveBytesAsync());
    }
}
