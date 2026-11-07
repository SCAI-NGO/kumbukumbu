// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Dav;
using Kumbu.Server.Tests.OAuth;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Api;

/// <summary>
/// Emptying a vault's trash (WebDAV design §3.11): all of it or one entry, for good, by the vault's owner in her session
/// with the password or an open password window. No member, key, device, connection, dev token or share can, and no
/// command of <c>sh</c> deletes permanently. Works over the quota; the blobs go with the next blob sweep.
/// </summary>
[Collection("app")]
public sealed class EmptyTrashTests(KumbuAppFactory app) : IAsyncDisposable
{
    private const string Callback = "https://claude.test/cb";
    private const string SessionOnlySentence = "This needs a signed-in session: API keys, connected apps and the dev token can't do it.";
    private const long OneMb = 1024 * 1024;

    // Whole seconds, as the other timed tests: MongoDB keeps milliseconds.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program>? _timed;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private WebApplicationFactory<Program> Timed => _timed ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    public async ValueTask DisposeAsync()
    {
        if (_timed is not null) await _timed.DisposeAsync();
    }

    private static string Purge(RestClient user, string? vault = null) => $"/api/vaults/{Uri.EscapeDataString(vault ?? user.Vault)}/trash/purge";

    /// <summary>The vault's trash as the web app lists it: path and version of each entry.</summary>
    private static async Task<(string Path, string Version)[]> TrashAsync(RestClient user, string? vault = null)
    {
        using var response = await user.GetAsync($"/api/vaults/{Uri.EscapeDataString(vault ?? user.Vault)}/trash");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await RestClient.JsonAsync(response))["notes"]!.AsArray().Select(e => (e!["path"]!.GetValue<string>(), e["version"]!.GetValue<string>()))];
    }

    /// <summary>Two notes and a picture, removed: three entries in the trash.</summary>
    private static async Task FillTrashAsync(RestClient user)
    {
        await user.CreateAsync("old.md", "# Old\n");
        await user.CreateAsync("older.md", "# Older\n");
        await user.CreateFileAsync("pic.png", FileApiTests.Png(2, 2));
        foreach (var url in new[] { user.Url("old.md"), user.Url("older.md"), user.Url("pic.png", "files") })
        {
            using var removed = await user.SendAsync(HttpMethod.Delete, url);
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Equal(3, (await TrashAsync(user)).Length);
    }

    private static async Task<string[]> EventTextsAsync(RestClient user, string? vault = null)
    {
        using var response = await user.GetAsync($"/api/vaults/{Uri.EscapeDataString(vault ?? user.Vault)}/activity");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await RestClient.JsonAsync(response))["events"]!.AsArray().Select(e => e!["text"]!.GetValue<string>())];
    }

    [Fact]
    public async Task The_owner_empties_the_trash_with_the_password()
    {
        var user = await RestClient.NewAsync(Api, vault: "Work");
        await FillTrashAsync(user);

        using var purged = await user.SendAsync(HttpMethod.Post, Purge(user), new { password = RestClient.Password });

        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        Assert.Equal(3, (await RestClient.JsonAsync(purged))["deleted"]!.GetValue<int>());
        Assert.Empty(await TrashAsync(user));
        // In MongoDB too, not only in the working set: the history the note entries carried is gone with them.
        var db = Api.Services.GetRequiredService<IMongoDatabase>();
        var vaultId = await user.VaultIdAsync();
        Assert.Equal(0, await db.GetCollection<SealedDocument>("trash").CountDocumentsAsync(d => d.Vault == vaultId, cancellationToken: Ct));
        Assert.Equal(0, await db.GetCollection<SealedDocument>("attachment_trash").CountDocumentsAsync(d => d.Vault == vaultId, cancellationToken: Ct));
        // Nothing comes back.
        using var restore = await user.SendAsync(HttpMethod.Post, user.Url("old.md", "restore"), new { });
        Assert.Equal(HttpStatusCode.NotFound, restore.StatusCode);
    }

    [Fact]
    public async Task The_owner_deletes_one_entry_permanently()
    {
        var user = await RestClient.NewAsync(Api, vault: "Work");
        await FillTrashAsync(user);
        var old = (await TrashAsync(user)).Single(e => e.Path == "/Work/old.md");

        using var purged = await user.SendAsync(
            HttpMethod.Post, Purge(user), new { password = RestClient.Password, entries = new[] { new { path = old.Path, version = old.Version } } });

        Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        Assert.Equal(1, (await RestClient.JsonAsync(purged))["deleted"]!.GetValue<int>());
        Assert.Equal(["/Work/older.md", "/Work/pic.png"], (await TrashAsync(user)).Select(e => e.Path).Order(StringComparer.Ordinal));

        // An attachment's entry the same way; one of another version, or gone already, deletes nothing.
        var pic = (await TrashAsync(user)).Single(e => e.Path == "/Work/pic.png");
        using var other = await user.SendAsync(HttpMethod.Post, Purge(user), new { entries = new[] { new { path = "/Work/older.md", version = old.Version }, new { path = old.Path, version = old.Version } } });
        Assert.Equal(0, (await RestClient.JsonAsync(other))["deleted"]!.GetValue<int>());
        using var file = await user.SendAsync(HttpMethod.Post, Purge(user), new { entries = new[] { new { path = pic.Path, version = pic.Version } } });
        Assert.Equal(1, (await RestClient.JsonAsync(file))["deleted"]!.GetValue<int>());
        Assert.Equal(["/Work/older.md"], (await TrashAsync(user)).Select(e => e.Path));

        // An entry outside the vault is a mistake.
        using var outside = await user.SendAsync(HttpMethod.Post, Purge(user), new { entries = new[] { new { path = "/Garten/older.md", version = old.Version } } });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
    }

    [Fact]
    public async Task Within_the_password_window_no_password_is_needed()
    {
        // A session without "keep me signed in": its window is always open.
        var user = await RestClient.NewAsync(Api, vault: "Work");
        await FillTrashAsync(user);
        using (var purged = await user.SendAsync(HttpMethod.Post, Purge(user), new { })) Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        Assert.Empty(await TrashAsync(user));

        // A remembered one with its window closed asks for the password again, and gets it.
        var remembered = await RestClient.NewAsync(Timed, vault: "Work", remember: true);
        await FillTrashAsync(remembered);
        _time.Advance(TimeSpan.FromMinutes(31));
        using (var closed = await remembered.SendAsync(HttpMethod.Post, Purge(remembered), new { }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, closed.StatusCode);
            Assert.Equal("password-required", (await RestClient.JsonAsync(closed))["type"]?.GetValue<string>());
        }

        Assert.Equal(3, (await TrashAsync(remembered)).Length);
        using (var given = await remembered.SendAsync(HttpMethod.Post, Purge(remembered), new { password = RestClient.Password })) Assert.Equal(HttpStatusCode.OK, given.StatusCode);
        Assert.Empty(await TrashAsync(remembered));
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        var user = await RestClient.NewAsync(Api, vault: "Work");
        await FillTrashAsync(user);

        using var wrong = await user.SendAsync(HttpMethod.Post, Purge(user), new { password = "not the password" });

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("The password is wrong.", await RestClient.ErrorAsync(wrong));
        Assert.Equal(3, (await TrashAsync(user)).Length);
    }

    [Fact]
    public async Task A_member_is_refused()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        var bob = await RestClient.NewAsync(Api, vault: "Own");
        await alice.ShareWithAsync(bob, "Alices", MemberRole.Maintain);
        await FillTrashAsync(alice);

        using var refused = await bob.SendAsync(HttpMethod.Post, Purge(bob, "Alices"), new { password = RestClient.Password });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("Only the owner of Alices can empty its trash.", await RestClient.ErrorAsync(refused));
        Assert.Equal(3, (await TrashAsync(alice)).Length);
    }

    [Fact]
    public async Task An_api_key_a_connection_a_device_and_a_share_are_refused()
    {
        var user = await RestClient.NewAsync(Api, vault: "Work");
        await FillTrashAsync(user);
        var agent = await user.KeyAsync("agent", KeyRight.Maintain);
        var device = await user.KeyAsync("Work – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device);

        // Keys and the dev token authenticate on /api, and are told it needs a session.
        foreach (var bearer in new[] { agent, device, KumbuAppFactory.Token })
        {
            using var refused = await user.SendAsync(HttpMethod.Post, Purge(user), new { password = RestClient.Password }, bearer: bearer);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(SessionOnlySentence, await RestClient.ErrorAsync(refused));
        }

        // A connection's token and a share's credentials open nothing under /api at all.
        var connection = await ConnectionTokenAsync(user);
        var share = await DavClient.NewAsync(Api, user, right: "write");
        using (var request = new HttpRequestMessage(HttpMethod.Post, Purge(user)) { Content = JsonContent(new { password = RestClient.Password }) })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection);
            using var refused = await Api.CreateClient().SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, Purge(user)) { Content = JsonContent(new { password = RestClient.Password }) })
        {
            request.Headers.TryAddWithoutValidation("Authorization", share.Authorization);
            using var refused = await Api.CreateClient().SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // Nor does a share's DELETE of a removed path reach the trash.
        using (var deleted = await share.SendAsync(HttpMethod.Delete, "/dav/Work/old.md")) Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        Assert.Equal(3, (await TrashAsync(user)).Length);
    }

    [Fact]
    public async Task No_sh_command_deletes_permanently()
    {
        var user = await RestClient.NewAsync(Api, vault: "Work");
        await FillTrashAsync(user);
        var agent = await user.KeyAsync("agent", KeyRight.Maintain);

        var (_, help) = await user.ShAsync("help", agent);
        Assert.DoesNotContain("purge", help, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permanent", help, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("empty the trash", help, StringComparison.OrdinalIgnoreCase);
        var (_, trashHelp) = await user.ShAsync("help trash", agent);
        Assert.DoesNotContain("purge", trashHelp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("empty", trashHelp, StringComparison.OrdinalIgnoreCase);

        // rm of a path whose note is in the trash finds no note; the entry stays.
        var (isError, _) = await user.ShAsync("rm /Work/old.md", agent);
        Assert.True(isError);
        var (pictureError, _) = await user.ShAsync("rm /Work/pic.png", agent);
        Assert.True(pictureError);
        Assert.Equal(3, (await TrashAsync(user)).Length);
    }

    [Fact]
    public async Task The_activity_records_the_emptying()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        var bob = await RestClient.NewAsync(Api, vault: "Own");
        await alice.ShareWithAsync(bob, "Alices", MemberRole.Write);
        await FillTrashAsync(alice);
        var old = (await TrashAsync(alice)).Single(e => e.Path == "/Work/old.md");

        using (var one = await alice.SendAsync(HttpMethod.Post, Purge(alice), new { entries = new[] { new { path = old.Path, version = old.Version } } })) Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        using (var all = await alice.SendAsync(HttpMethod.Post, Purge(alice), new { })) Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        // An empty trash emptied again records nothing.
        using (var none = await alice.SendAsync(HttpMethod.Post, Purge(alice), new { })) Assert.Equal(0, (await RestClient.JsonAsync(none))["deleted"]!.GetValue<int>());

        var texts = await EventTextsAsync(alice);
        Assert.Equal(["Emptied the trash: 2 entries.", "Emptied the trash: 1 entry."], texts.Where(t => t.StartsWith("Emptied", StringComparison.Ordinal)));
        // The owner's alone: a member doesn't see it.
        Assert.DoesNotContain(await EventTextsAsync(bob, "Alices"), t => t.Contains("trash", StringComparison.OrdinalIgnoreCase));
        // A count, no name or path, in the clear like every event's fields.
        var vaultId = await alice.VaultIdAsync();
        var stored = await Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<VaultEventDocument>("vault_events")
            .Find(e => e.Vault == vaultId && e.Kind == VaultEvents.TrashEmptied).SortBy(e => e.At).ThenBy(e => e.Id).ToListAsync(Ct);
        Assert.Equal([1, 2], stored.Select(e => e.Count));
    }

    [Fact]
    public async Task Emptying_works_over_the_quota_and_frees_space_after_the_blob_sweep()
    {
        var user = await RestClient.NewAsync(Timed, vault: "Work");
        var quota = Timed.Services.GetRequiredService<StorageQuota>();
        var catalog = Timed.Services.GetRequiredService<VaultCatalog>();
        await user.CreateFileAsync("big.png", [.. FileApiTests.Png(2, 2), .. System.Security.Cryptography.RandomNumberGenerator.GetBytes((int)(OneMb + OneMb / 4))]);
        using (var removed = await user.SendAsync(HttpMethod.Delete, user.Url("big.png", "files"))) Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        await Timed.Services.GetRequiredService<IMongoDatabase>().GetCollection<UserDocument>("users")
            .UpdateOneAsync(u => u.Id == user.User.Id, Builders<UserDocument>.Update.Set(u => u.Quota, OneMb), cancellationToken: Ct);
        await quota.FigureAsync(user.User.Id, Ct);
        using (var full = await user.SendAsync(HttpMethod.Put, user.Url("a.md"), new { content = "a\n" })) Assert.Equal(HttpStatusCode.InsufficientStorage, full.StatusCode);

        using (var purged = await user.SendAsync(HttpMethod.Post, Purge(user), new { password = RestClient.Password })) Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        Assert.Empty(await TrashAsync(user));

        var vaultId = await user.VaultIdAsync();
        var released = new TaskCompletionSource();
        void OnReleased(string id)
        {
            if (id == vaultId) released.TrySetResult();
        }

        catalog.BlobsReleased += OnReleased;
        try
        {
            _time.Advance(BlobSweep.Grace + TimeSpan.FromMinutes(1));
            catalog.Sweep();
            await released.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        finally
        {
            catalog.BlobsReleased -= OnReleased;
        }

        await quota.SettledAsync();
        Assert.True(quota.KnownUsage(user.User.Id) < OneMb);
        await user.CreateAsync("a.md", "a\n");
    }

    private static StringContent JsonContent(object body) =>
        new(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    /// <summary>An access token of a connection of <paramref name="owner"/> with maintain on its vault.</summary>
    private async Task<string> ConnectionTokenAsync(RestClient owner)
    {
        using var created = await owner.SendAsync(HttpMethod.Post, "/api/connections", new { name = "claude", vaults = new Dictionary<string, string> { [owner.Vault] = "maintain" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
        var flow = await OAuthFlow.StartAsync(Api, owner, id, await OAuthFlow.RegisterAsync(Api, Callback), Callback);
        await flow.AllowAsync();
        using var token = await flow.TokenAsync();
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        return (await RestClient.JsonAsync(token))["access_token"]!.GetValue<string>();
    }
}
