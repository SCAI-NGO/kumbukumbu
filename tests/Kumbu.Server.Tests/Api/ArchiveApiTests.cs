// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;
using Kumbu.Server.Accounts;
using Kumbu.Server.Publishing;
using Kumbu.Server.Search;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Server.Tests.Api;

/// <summary>
/// The vault's archive in the web app's API (ingest spec §3.2): the tree leaves it out and sums it up, its own listing
/// shows it item by item, a folder goes to the trash with <c>rm</c>'s rules, the search finds it only when asked, and the
/// account's storage names its bytes.
/// The server's time zone is Berlin, so a stamp's time is an hour ahead of UTC in November.
/// </summary>
[Collection("app")]
public sealed class ArchiveApiTests(KumbuAppFactory app) : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = Berlin();

    private WebApplicationFactory<Program>? _timed;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    private static byte[] Png => FileApiTests.Png(2, 2);

    private static async Task<JsonNode> TreeAsync(RestClient user, string? bearer = null)
    {
        using var tree = await user.GetAsync($"/api/vaults/{user.Vault}/tree", bearer);
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        return await RestClient.JsonAsync(tree);
    }

    private static async Task<JsonNode> ArchiveAsync(RestClient user, string? bearer = null)
    {
        using var archive = await user.GetAsync($"/api/vaults/{user.Vault}/archive", bearer);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        return await RestClient.JsonAsync(archive);
    }

    private static Task<HttpResponseMessage> TrashFolderAsync(RestClient user, string path, string? bearer = null) =>
        user.SendAsync(HttpMethod.Post, "/api/folders/trash", new { path }, bearer: bearer);

    private static string[] Names(JsonNode folder, string list) => [.. folder[list]!.AsArray().Select(n => n!["name"]!.GetValue<string>())];

    private static async Task<string[]> TrashedAsync(RestClient user)
    {
        using var trash = await user.GetAsync($"/api/vaults/{user.Vault}/trash");
        return [.. (await RestClient.JsonAsync(trash))["notes"]!.AsArray().Select(e => e!["path"]!.GetValue<string>()).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task The_tree_leaves_the_archive_out_and_sums_it_up()
    {
        var user = await RestClient.NewAsync(Api, "Work");
        await user.CreateAsync("Inbox/plan.md", "# Plan\n");
        await user.CreateAsync("_archive/Inbox/old plan 2026-11-08 213005.md", "# Old plan\n");
        await user.CreateFileAsync("_archive/Inbox/shot 2026-11-08 213005.png", Png);
        await user.CreateFileAsync("_archive/.vaultkeep", []);
        // Only the vault's own archive: one deeper down is an ordinary folder.
        await user.CreateAsync("Inbox/_archive/kept.md", "kept\n");

        var tree = await TreeAsync(user);
        Assert.Equal(["Inbox"], Names(tree["root"]!, "folders"));
        var inbox = tree["root"]!["folders"]![0]!;
        Assert.Equal(["_archive"], Names(inbox, "folders"));
        Assert.Equal(["plan.md"], Names(inbox, "notes"));
        Assert.DoesNotContain("old plan", tree.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(2, tree["archive"]!["files"]!.GetValue<int>());
        Assert.Equal(Encoding.UTF8.GetByteCount("# Old plan\n") + Png.Length, tree["archive"]!["bytes"]!.GetValue<long>());
    }

    [Fact]
    public async Task An_archive_holding_only_its_marker_has_no_summary()
    {
        var user = await RestClient.NewAsync(Api, "Work");
        await user.CreateAsync("plan.md", "# Plan\n");
        Assert.Null((await TreeAsync(user))["archive"]);

        await user.CreateFileAsync("_archive/.vaultkeep", []);
        var tree = await TreeAsync(user);
        Assert.Null(tree["archive"]);
        Assert.Empty(tree["root"]!["folders"]!.AsArray());
    }

    [Fact]
    public async Task The_archive_lists_its_items_stamped_ones_first_newest_first()
    {
        var user = await RestClient.NewAsync(Api, "Work");
        await user.CreateFileAsync("_archive/Inbox/report 2026-11-08 213005.png", Png);
        await user.CreateAsync("_archive/v1.2 notes 2026-11-08 220000-2.md", "notes\n");
        await user.CreateFileAsync("_archive/Manual 2026-11-09 080000/p1.png", Png);
        await user.CreateAsync("_archive/Manual 2026-11-09 080000/Overview.md", "# Manual\n");
        // Inside a stamped folder, a stamped name is the folder's, not an item of its own.
        await user.CreateAsync("_archive/Manual 2026-11-09 080000/old 2026-01-01 000000.md", "old\n");
        await user.CreateFileAsync("_archive/Manual 2026-11-09 080000/.vaultkeep", []);
        await user.CreateFileAsync("_archive/.vaultkeep", []);
        await user.CreateAsync("_archive/Inbox/loose.md", "put here by hand\n");

        var archive = await ArchiveAsync(user);
        Assert.True(archive["canWrite"]!.GetValue<bool>());
        var items = archive["items"]!.AsArray();
        Assert.Equal(
            ["/Work/_archive/Manual 2026-11-09 080000", "/Work/_archive/v1.2 notes 2026-11-08 220000-2.md", "/Work/_archive/Inbox/report 2026-11-08 213005.png", "/Work/_archive/Inbox/loose.md"],
            items.Select(i => i!["path"]!.GetValue<string>()));
        Assert.Equal(["/Work/Manual", "/Work/v1.2 notes.md", "/Work/Inbox/report.png", "/Work/Inbox/loose.md"], items.Select(i => i!["originalPath"]!.GetValue<string>()));
        Assert.Equal(["folder", "note", "image", "note"], items.Select(i => i!["kind"]!.GetValue<string>()));
        Assert.Equal(
            [new DateTime(2026, 11, 9, 7, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 8, 21, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 8, 20, 30, 5, DateTimeKind.Utc), _time.GetUtcNow().UtcDateTime],
            items.Select(i => i!["archivedAt"]!.GetValue<DateTime>().ToUniversalTime()));

        var manual = items[0]!;
        Assert.Equal("Manual 2026-11-09 080000", manual["name"]!.GetValue<string>());
        Assert.True(manual["folder"]!.GetValue<bool>());
        Assert.Equal(3, manual["files"]!.GetValue<int>());
        Assert.Equal(Png.Length + Encoding.UTF8.GetByteCount("# Manual\nold\n"), manual["bytes"]!.GetValue<long>());
        var report = items[2]!;
        Assert.False(report["folder"]!.GetValue<bool>());
        Assert.Equal(1, report["files"]!.GetValue<int>());
        Assert.Equal(Png.Length, report["bytes"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_reader_sees_the_archive_but_may_not_trash_a_folder()
    {
        var user = await RestClient.NewAsync(Api, "Work");
        await user.CreateAsync("_archive/Manual 2026-11-09 080000/Overview.md", "# Manual\n");
        var reader = await user.KeyAsync("reader", KeyRight.Read);

        var archive = await ArchiveAsync(user, reader);
        Assert.False(archive["canWrite"]!.GetValue<bool>());
        Assert.Single(archive["items"]!.AsArray());

        using var trashed = await TrashFolderAsync(user, "/Work/_archive/Manual 2026-11-09 080000/", reader);
        Assert.Equal(HttpStatusCode.Forbidden, trashed.StatusCode);
        Assert.Equal("read-only", (await RestClient.JsonAsync(trashed))["code"]!.GetValue<string>());
        Assert.Single((await ArchiveAsync(user))["items"]!.AsArray());
    }

    [Fact]
    public async Task A_folder_goes_to_the_trash_with_everything_in_it()
    {
        var user = await RestClient.NewAsync(Api, "Work");
        await user.CreateFileAsync("_archive/Manual 2026-11-09 080000/p1.png", Png);
        await user.CreateAsync("_archive/Manual 2026-11-09 080000/sub/Overview.md", "# Manual\n");
        await user.CreateAsync("_archive/kept.md", "kept\n");

        using var trashed = await TrashFolderAsync(user, "/Work/_archive/Manual 2026-11-09 080000/");
        Assert.Equal(HttpStatusCode.NoContent, trashed.StatusCode);

        Assert.Equal(["/Work/_archive/kept.md"], (await ArchiveAsync(user))["items"]!.AsArray().Select(i => i!["path"]!.GetValue<string>()));
        Assert.Equal(["/Work/_archive/Manual 2026-11-09 080000/p1.png", "/Work/_archive/Manual 2026-11-09 080000/sub/Overview.md"], await TrashedAsync(user));

        using var again = await TrashFolderAsync(user, "/Work/_archive/Manual 2026-11-09 080000/");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Theory]
    [InlineData("/Work/_archive/kept.md")]
    [InlineData("/Work/_archive/kept.md/")]
    [InlineData("/Work/")]
    [InlineData("/")]
    [InlineData("Work/_archive/")]
    [InlineData("/Work/_archive/../Inbox/")]
    [InlineData("/Work/_archive/./")]
    [InlineData("/Work/_archive\\Inbox/")]
    public async Task Folder_trash_takes_only_a_folder_path(string path)
    {
        var user = await RestClient.NewAsync(Api, "Work");
        await user.CreateAsync("_archive/kept.md", "kept\n");
        await user.CreateAsync("Inbox/plan.md", "plan\n");

        using var trashed = await TrashFolderAsync(user, path);
        Assert.Equal(HttpStatusCode.BadRequest, trashed.StatusCode);
        Assert.Empty(await TrashedAsync(user));
    }

    [Fact]
    public async Task Folder_trash_in_a_vault_the_caller_has_not_is_a_404()
    {
        var user = await RestClient.NewAsync(Api, "Work");
        using var trashed = await TrashFolderAsync(user, "/Private/_archive/");
        Assert.Equal(HttpStatusCode.NotFound, trashed.StatusCode);
    }

    /// <summary><c>rm</c>'s structural rule: a folder holding the board's definition takes maintain.</summary>
    [Fact]
    public async Task A_writer_may_not_trash_a_folder_with_the_board_in_it()
    {
        var user = await RestClient.NewAsync(Api, "Work");
        await user.CreateAsync("Board/Board.md", "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n---\n# Roadmap\n");
        await user.CreateAsync("Board/Task.md", "---\ncolumn: Ideas\nrank: a1\n---\nTask.\n");
        var writer = await user.KeyAsync("writer", KeyRight.Write);

        using var trashed = await TrashFolderAsync(user, "/Work/Board/", writer);
        Assert.Equal(HttpStatusCode.Forbidden, trashed.StatusCode);
        Assert.Equal("structural", (await RestClient.JsonAsync(trashed))["code"]!.GetValue<string>());
        Assert.Empty(await TrashedAsync(user));

        var maintainer = await user.KeyAsync("maintainer", KeyRight.Maintain);
        using var allowed = await TrashFolderAsync(user, "/Work/Board/", maintainer);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    /// <summary>A published note the caller may not withdraw keeps the whole folder where it is (publish spec §3.7).</summary>
    [Fact]
    public async Task A_folder_with_a_published_note_stays_for_a_key_without_publishing()
    {
        var user = await RestClient.NewAsync(Api, "Work");
        await user.CreateAsync("_archive/Routing 2026-11-08 213005/Questions.md", "# Questions on routing\n");
        await user.CreateAsync("_archive/Routing 2026-11-08 213005/Answers.md", "# Answers\n");
        var notes = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey));
        var access = (await notes.UnlockedVaultsAsync(Ct)).Single(a => a.Name == "Work");
        Assert.True(VaultPath.TryParse("/Work/_archive/Routing 2026-11-08 213005/Questions.md", out var questions));
        await Api.Services.GetRequiredService<Publications>().PublishAsync(
            access, notes, questions, user.User.Id, user.Username, WriterRef.ForOwner(WriterRef.Session, null), KumbuAppFactory.PublicUrl, Ct);
        var writer = await user.KeyAsync("writer", KeyRight.Write);

        using var trashed = await TrashFolderAsync(user, "/Work/_archive/Routing 2026-11-08 213005/", writer);
        Assert.Equal(HttpStatusCode.Forbidden, trashed.StatusCode);
        Assert.Equal(Refusals.Published, (await RestClient.JsonAsync(trashed))["code"]!.GetValue<string>());
        Assert.Empty(await TrashedAsync(user));
    }

    [Theory]
    [InlineData("exact")]
    [InlineData("hybrid")]
    public async Task The_search_leaves_the_archive_out_unless_asked_or_searched_in(string mode)
    {
        var user = await RestClient.NewAsync(Api, "Work");
        await user.CreateAsync("Inbox/plan.md", "# Plan\n\nThe pump needs a new seal.\n");
        await user.CreateAsync("_archive/Inbox/old plan 2026-11-08 213005.md", "# Old plan\n\nThe pump had an old seal.\n");
        await Api.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);

        async Task<string[]> FoundAsync(string query)
        {
            using var search = await user.GetAsync($"/api/search?q=seal&mode={mode}{query}");
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            return [.. (await RestClient.JsonAsync(search))["results"]!.AsArray().Select(r => r!["path"]!.GetValue<string>()).Distinct().Order(StringComparer.Ordinal)];
        }

        const string Archived = "/Work/_archive/Inbox/old plan 2026-11-08 213005.md";
        Assert.Equal(["/Work/Inbox/plan.md"], await FoundAsync(""));
        Assert.Equal(["/Work/Inbox/plan.md"], await FoundAsync("&archive=false&path=/Work"));
        Assert.Equal(["/Work/Inbox/plan.md", Archived], await FoundAsync("&archive=true"));
        Assert.Equal([Archived], await FoundAsync("&path=/Work/_archive/"));
        Assert.Equal([Archived], await FoundAsync("&path=/Work/_archive/Inbox/"));
    }

    [Fact]
    public async Task Account_storage_names_the_archives_of_her_own_vaults()
    {
        var user = await RestClient.NewAsync(Api, "Work");
        await user.CreateAsync("plan.md", "# Plan\n");
        await user.CreateAsync("_archive/old 2026-11-08 213005.md", "# Old\n");
        await user.CreateFileAsync("_archive/shot 2026-11-08 213005.png", Png);

        async Task<JsonNode> StorageAsync()
        {
            using var storage = await user.GetAsync("/api/account/storage");
            Assert.Equal(HttpStatusCode.OK, storage.StatusCode);
            return await RestClient.JsonAsync(storage);
        }

        var first = await StorageAsync();
        Assert.Equal(Encoding.UTF8.GetByteCount("# Old\n") + Png.Length, first["archive"]!.GetValue<long>());
        Assert.True(first["used"]!.GetValue<long>() > first["archive"]!.GetValue<long>());

        // Counted at most once per ShownFor, as the usage is.
        await user.CreateAsync("_archive/more 2026-11-08 213006.md", "more\n");
        Assert.Equal(first["archive"]!.GetValue<long>(), (await StorageAsync())["archive"]!.GetValue<long>());
        _time.Advance(StorageQuota.ShownFor);
        Assert.Equal(Encoding.UTF8.GetByteCount("# Old\nmore\n") + Png.Length, (await StorageAsync())["archive"]!.GetValue<long>());
    }
}
