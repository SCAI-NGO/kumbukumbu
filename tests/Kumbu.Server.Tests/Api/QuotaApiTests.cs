// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Dav;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Protocol;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Api;

/// <summary>
/// The storage quota on every channel that adds data (WebDAV design §3.10): MCP and <c>sh</c>, the REST API, uploads and
/// imports, sync, WebDAV; restores and checkpoints. Removing and moving work over it. The vault's owner is the account
/// checked, and only the owner learns more than "This vault is out of space."
/// </summary>
[Collection("app")]
public sealed class QuotaApiTests(KumbuAppFactory app) : IAsyncDisposable
{
    private const long OneMb = 1024 * 1024;
    private const string Sentence = "This vault is out of space.";
    private const string OwnerSentence = "This vault is out of space. See `df`.";
    private const string Root = "https://localhost/dav/Work/";

    // Whole seconds, as the other timed tests: MongoDB keeps milliseconds.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program>? _timed;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    /// <summary>A server of its own whose clock the test moves.</summary>
    private WebApplicationFactory<Program> Timed => _timed ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    private readonly List<WebApplicationFactory<Program>> _own = [];

    public async ValueTask DisposeAsync()
    {
        if (_timed is not null) await _timed.DisposeAsync();
        foreach (var api in _own) await api.DisposeAsync();
    }

    /// <summary>A server of its own (its own cache): with <paramref name="quotaDefault"/>, on <paramref name="time"/> if
    /// given, and on a database of its own if <paramref name="freshDatabase"/>, where no user carries an override.</summary>
    private WebApplicationFactory<Program> Own(string? quotaDefault = null, FakeTimeProvider? time = null, bool freshDatabase = false)
    {
        var api = app.PublicApi.WithWebHostBuilder(b =>
        {
            if (quotaDefault is not null) b.UseSetting("Kumbu:Quota:Default", quotaDefault);
            if (freshDatabase) b.UseSetting("Kumbu:Mongo:Database", "t" + Guid.NewGuid().ToString("N"));
            if (time is not null)
            {
                b.ConfigureServices(s =>
                {
                    s.RemoveAll<TimeProvider>();
                    s.AddSingleton<TimeProvider>(time);
                });
            }
        });
        _own.Add(api);
        return api;
    }

    /// <summary>The queries the cache makes for <paramref name="id"/> (or every one, if null), by kind; a kind in
    /// <paramref name="failing"/> throws as MongoDB away would.</summary>
    private static Dictionary<string, int> Watch(StorageQuota quota, string? id = null, params string[] failing)
    {
        var seen = new Dictionary<string, int>();
        quota.Querying = (kind, of) =>
        {
            if (id is not null && of != id) return Task.CompletedTask;
            lock (seen) seen[kind] = seen.GetValueOrDefault(kind) + 1;
            return failing.Contains(kind) ? throw new TimeoutException("MongoDB is away") : Task.CompletedTask;
        };
        return seen;
    }

    private static int Seen(Dictionary<string, int> seen, string kind)
    {
        lock (seen) return seen.GetValueOrDefault(kind);
    }

    /// <summary>Background work that ended is let go, not held for ever.</summary>
    [Fact]
    public async Task Ended_background_work_is_let_go()
    {
        var api = Own(quotaDefault: "1GB");
        var quota = Quota(api);
        var user = await RestClient.NewAsync(api, vault: "Work");
        for (var i = 0; i < 5; i++) await quota.RecountAsync(user.User.Id);
        Assert.True(quota.Tracked <= 1, $"{quota.Tracked} tasks held");
    }

    /// <summary>A count or an owner's lookup that fails is tried again ten minutes on, not on every write.</summary>
    [Fact]
    public async Task A_failed_count_or_lookup_waits_ten_minutes()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var api = Own(quotaDefault: "1GB", time: time);
        var quota = Quota(api);
        var counted = await RestClient.NewAsync(api, vault: "Work");
        var looked = await RestClient.NewAsync(api, vault: "Work");
        var lookedVault = await looked.VaultIdAsync();
        var counts = Watch(quota, counted.User.Id, "limit");
        async Task WriteAsync(RestClient user, string name)
        {
            await user.CreateAsync(name, "x\n");
            await quota.SettledAsync();
        }

        // Its owner is a member's to know: the lookup comes first, then the count fails.
        await WriteAsync(counted, "a.md");
        await WriteAsync(counted, "b.md");
        var first = Seen(counts, "limit");
        Assert.Equal(1, first);
        await WriteAsync(counted, "c.md");
        Assert.Equal(1, Seen(counts, "limit"));
        time.Advance(StorageQuota.RecountAfter + TimeSpan.FromMinutes(1));
        await WriteAsync(counted, "d.md");
        Assert.Equal(2, Seen(counts, "limit"));

        var lookups = Watch(quota, lookedVault, "owner");
        await WriteAsync(looked, "a.md");
        await WriteAsync(looked, "b.md");
        Assert.Equal(1, Seen(lookups, "owner"));
        time.Advance(StorageQuota.RecountAfter + TimeSpan.FromMinutes(1));
        await WriteAsync(looked, "c.md");
        Assert.Equal(2, Seen(lookups, "owner"));
    }

    /// <summary>A write that commits while a count runs isn't lost when the count's figure comes in.</summary>
    [Fact]
    public async Task A_count_keeps_what_writes_adjusted_while_it_ran()
    {
        var api = Own(quotaDefault: "1GB");
        var quota = Quota(api);
        var user = await RestClient.NewAsync(api, vault: "Work");
        var before = (await quota.FigureAsync(user.User.Id, Ct)).Used;

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        quota.Querying = async (kind, of) =>
        {
            if (kind != "count" || of != user.User.Id) return;
            entered.TrySetResult();
            await release.Task;
        };
        var recount = quota.RecountAsync(user.User.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        quota.Adjust(user.User.Id, 1000);
        release.SetResult();
        await recount;

        Assert.Equal(before + 1000, quota.KnownUsage(user.User.Id));
    }

    /// <summary>No default and no override anywhere: the write path asks MongoDB nothing about anyone's storage, but
    /// whether an override exists; once an admin sets one, it does.</summary>
    [Fact]
    public async Task Without_a_default_or_an_override_nothing_is_looked_up()
    {
        var api = Own(freshDatabase: true);
        var quota = Quota(api);
        var seen = Watch(quota);
        var user = await RestClient.NewAsync(api, vault: "Work");
        var dav = await DavClient.NewAsync(api, user);
        for (var i = 0; i < 3; i++) await user.CreateAsync($"n{i}.md", "x\n");
        using (var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/d.md", Text("d\n"))) Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        await quota.SettledAsync();
        await user.CreateAsync("late.md", "x\n");
        await quota.SettledAsync();

        Assert.Equal(0, Seen(seen, "owner") + Seen(seen, "limit") + Seen(seen, "count"));
        Assert.Equal(1, Seen(seen, "overrides"));

        var admin = await RestClient.NewAsync(api, vault: "Admin", role: Roles.Admin);
        using (var patched = await admin.SendAsync(HttpMethod.Patch, $"/api/admin/users/{user.User.Id}", new { quota = 0 })) Assert.Equal(HttpStatusCode.NoContent, patched.StatusCode);
        using var refused = await user.SendAsync(HttpMethod.Put, user.Url("after.md"), new { content = "x\n" });
        await AssertFullAsync(refused);
    }

    /// <summary>A request that changes the role and the quota applies both or nothing: an admin making herself a user
    /// in it still sets the quota, and a quota that doesn't read changes no state.</summary>
    [Fact]
    public async Task A_patch_with_the_quota_applies_all_or_nothing()
    {
        var admin = await RestClient.NewAsync(Api, vault: "Admin", role: Roles.Admin);
        var user = await RestClient.NewAsync(Api, vault: "Work");
        var users = Users(Api);

        using (var bad = await admin.SendAsync(HttpMethod.Patch, $"/api/admin/users/{user.User.Id}", new { active = false, quota = "lots" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        Assert.True((await users.Find(u => u.Id == user.User.Id).SingleAsync(Ct)).Active);

        using (var both = await admin.SendAsync(HttpMethod.Patch, $"/api/admin/users/{admin.User.Id}", new { role = Roles.User, quota = 5 }))
        {
            Assert.Equal(HttpStatusCode.NoContent, both.StatusCode);
        }

        var self = await users.Find(u => u.Id == admin.User.Id).SingleAsync(Ct);
        Assert.Equal((Roles.User, (long?)5), (self.Role, self.Quota));
    }

    private static StorageQuota Quota(WebApplicationFactory<Program> api) => api.Services.GetRequiredService<StorageQuota>();

    private static IMongoCollection<UserDocument> Users(WebApplicationFactory<Program> api) =>
        api.Services.GetRequiredService<IMongoDatabase>().GetCollection<UserDocument>("users");

    /// <summary>The user's override, written as the admin's would be (null: the default, −1: unlimited).</summary>
    private static Task OverrideAsync(RestClient user, long? quota) =>
        Users(user.Api).UpdateOneAsync(u => u.Id == user.User.Id, Builders<UserDocument>.Update.Set(u => u.Quota, quota), cancellationToken: Ct);

    /// <summary>More than 1 MB in the user's vault, written through a store that knows no quota (as the seeder's).</summary>
    private static async Task FillAsync(RestClient user, string name = "big.md")
    {
        var store = new EncryptedNoteStore(user.Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey));
        var path = VaultPath.TryParse($"/{user.Vault}/{name}", out var p) ? p : throw new ArgumentException(name);
        Assert.True(await store.TryCreateAsync(new StoredNote(path, new string('x', 1_100_000), 1, user.Username, DateTime.UtcNow, [], Ref: WriterRef.Server), Ct));
    }

    /// <summary>A user with a vault "Work" over a 1 MB override, her usage counted; <paramref name="before"/> runs
    /// before she is over it.</summary>
    private async Task<RestClient> FullAsync(WebApplicationFactory<Program>? api = null, Func<RestClient, Task>? before = null)
    {
        var user = await RestClient.NewAsync(api ?? Api, vault: "Work");
        if (before is not null) await before(user);
        await OverrideAsync(user, OneMb);
        await FillAsync(user);
        await Quota(user.Api).FigureAsync(user.User.Id, Ct);
        return user;
    }

    /// <summary>507, and the body exactly: the owner's with the code, a member's the sentence alone.</summary>
    private static async Task AssertFullAsync(HttpResponseMessage response, bool owner = true)
    {
        Assert.Equal(HttpStatusCode.InsufficientStorage, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(owner ? """{"error":"This vault is out of space.","code":"quota"}""" : """{"error":"This vault is out of space."}""", body);
    }

    private static async Task AssertDavFullAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.InsufficientStorage, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(Sentence, (await response.Content.ReadAsStringAsync(Ct)).Trim());
    }

    private static HttpContent Text(string text) => new ByteArrayContent(Encoding.UTF8.GetBytes(text));

    private static string ToolText(CallToolResult result) => Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    /// <summary>An API key of <paramref name="user"/> for the vault she sees as <paramref name="vault"/>, own or shared.</summary>
    private static async Task<string> KeyForAsync(RestClient user, string vault, KeyRight right = KeyRight.Write)
    {
        var access = (await user.Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(user.User.Id, user.UserKey, Ct)).Single(v => v.Name == vault);
        var (key, _) = await user.Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            user.User, user.UserKey, DateTime.UtcNow, "Claude " + vault, new Dictionary<string, KeyRight> { [access.Id] = right }, Ct);
        return key;
    }

    [Fact]
    public async Task Mcp_sh_write_is_refused_with_see_df()
    {
        var user = await FullAsync();
        var key = await user.KeyAsync("Claude", KeyRight.Write);

        var (isError, text) = await user.ShAsync("mkdir /Work/Drafts", key);
        Assert.True(isError);
        Assert.Equal(OwnerSentence, text);

        var written = await user.ToolAsync("write", new Dictionary<string, object?> { ["path"] = "/Work/a.md", ["content"] = "a\n" }, key);
        Assert.True(written.IsError);
        Assert.Equal(OwnerSentence, ToolText(written));
    }

    [Fact]
    public async Task Rest_note_put_is_refused_with_507()
    {
        var user = await FullAsync(before: u => u.CreateAsync("a.md", "a\n"));
        using (var created = await user.SendAsync(HttpMethod.Put, user.Url("new.md"), new { content = "new\n" })) await AssertFullAsync(created);
        var version = (await user.ReadAsync("a.md"))["version"]!.GetValue<string>();
        using (var replaced = await user.SendAsync(HttpMethod.Put, user.Url("a.md"), new { content = "b\n", version })) await AssertFullAsync(replaced);
        Assert.Equal("a\n", (await user.ReadAsync("a.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Upload_is_refused_with_507()
    {
        var user = await FullAsync();
        using (var put = await user.PutFileAsync("pic.png", FileApiTests.Png(2, 2))) await AssertFullAsync(put);

        // With room left, a file larger than the room is refused for its bytes, before its record is written.
        var roomy = await RestClient.NewAsync(Api, vault: "Work");
        await OverrideAsync(roomy, OneMb);
        await Quota(Api).FigureAsync(roomy.User.Id, Ct);
        using (var small = await roomy.PutFileAsync("small.png", FileApiTests.Png(2, 2))) Assert.Equal(HttpStatusCode.Created, small.StatusCode);
        using (var big = await roomy.PutFileAsync("big.png", [.. FileApiTests.Png(2, 2), .. new byte[OneMb + OneMb / 2]])) await AssertFullAsync(big);
    }

    [Fact]
    public async Task Import_is_refused()
    {
        var user = await FullAsync();
        using var upload = await user.SendAsync(
            HttpMethod.Post, "/api/upload/Work/In", new { onConflict = "replace", files = new[] { new { path = "new.md", content = "new\n" } } });
        await AssertFullAsync(upload);
    }

    /// <summary>The Obsidian plugin's device writes through the notes API (brief open point 9): 507, the sentence.</summary>
    [Fact]
    public async Task Sync_push_is_refused_with_507()
    {
        var user = await FullAsync();
        var device = await user.KeyAsync("Phone", KeyRight.Write, ApiKeyKind.Device);
        using var pushed = await user.SendAsync(HttpMethod.Put, user.Url("synced.md"), new { content = "synced\n" }, bearer: device);
        Assert.Equal(HttpStatusCode.InsufficientStorage, pushed.StatusCode);
        Assert.Equal(Sentence, await RestClient.ErrorAsync(pushed));
        using var file = await user.PutFileAsync("synced.png", FileApiTests.Png(2, 2), bearer: device);
        Assert.Equal(HttpStatusCode.InsufficientStorage, file.StatusCode);
    }

    [Fact]
    public async Task Dav_put_is_refused_with_507()
    {
        var user = await FullAsync();
        var dav = await DavClient.NewAsync(Api, user);
        using (var note = await dav.SendAsync(HttpMethod.Put, "/dav/Work/a.md", Text("a\n"))) await AssertDavFullAsync(note);
        using (var file = await dav.SendAsync(HttpMethod.Put, "/dav/Work/pic.png", new ByteArrayContent(FileApiTests.Png(2, 2)))) await AssertDavFullAsync(file);
        using (var folder = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/Drafts")) await AssertDavFullAsync(folder);
    }

    [Fact]
    public async Task Dav_copy_is_refused_with_507()
    {
        var user = await FullAsync(before: u => u.CreateAsync("a.md", "a\n"));
        var dav = await DavClient.NewAsync(Api, user);
        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/a.md", headers: new Dictionary<string, string> { ["Destination"] = Root + "b.md" });
        await AssertDavFullAsync(copy);
    }

    [Fact]
    public async Task Restore_from_history_is_refused()
    {
        string? first = null;
        var user = await FullAsync(before: async u =>
        {
            first = await u.CreateAsync("a.md", "one\n");
            using var second = await u.SendAsync(HttpMethod.Put, u.Url("a.md"), new { content = "two\n", version = first });
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        });
        using var restore = await user.SendAsync(HttpMethod.Post, "/api/restore/Work/a.md", new { version = first });
        await AssertFullAsync(restore);
        Assert.Equal("two\n", (await user.ReadAsync("a.md"))["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Restore_from_trash_is_refused()
    {
        var user = await FullAsync(before: async u =>
        {
            await u.CreateAsync("a.md", "a\n");
            await u.CreateFileAsync("pic.png", FileApiTests.Png(2, 2));
            using (var removed = await u.SendAsync(HttpMethod.Delete, u.Url("a.md"))) Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            using (var removed = await u.SendAsync(HttpMethod.Delete, u.Url("pic.png", "files"))) Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        });
        using (var note = await user.SendAsync(HttpMethod.Post, "/api/restore/Work/a.md", new { })) await AssertFullAsync(note);
        using (var file = await user.SendAsync(HttpMethod.Post, "/api/restore/Work/pic.png", new { })) await AssertFullAsync(file);
    }

    [Fact]
    public async Task Checkpoint_creation_is_refused()
    {
        var user = await FullAsync();
        using (var made = await user.SendAsync(HttpMethod.Post, "/api/vaults/Work/checkpoints", new { name = "before" })) await AssertFullAsync(made);
        var (isError, text) = await user.ShAsync("mkcp /Work before", await user.KeyAsync("Claude", KeyRight.Maintain));
        Assert.True(isError);
        Assert.Equal(OwnerSentence, text);
    }

    /// <summary>Over the quota, what removes or moves still works (spec §3.10), a move whose links rewrite other notes too.</summary>
    [Fact]
    public async Task Rm_mv_dav_delete_and_dav_move_work_over_the_quota()
    {
        var user = await FullAsync(before: async u =>
        {
            foreach (var name in new[] { "a", "b", "c", "d" }) await u.CreateAsync($"{name}.md", name + "\n");
            await u.CreateAsync("e.md", "see [[b]]\n");
        });
        var key = await user.KeyAsync("Claude", KeyRight.Write);
        var dav = await DavClient.NewAsync(Api, user);

        Assert.False((await user.ShAsync("rm /Work/a.md", key)).IsError);
        var moved = await user.ShAsync("mv /Work/b.md /Work/b2.md", key);
        Assert.False(moved.IsError, moved.Text);
        Assert.Contains("[[b2]]", (await user.ReadAsync("e.md"))["content"]!.GetValue<string>());
        using (var rest = await user.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Work/b2.md", to = "/Work/b3.md" })) Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
        using (var deleted = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/c.md")) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/d.md", headers: new Dictionary<string, string> { ["Destination"] = Root + "d2.md" }))
        {
            Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        }

        // Still full: what adds data is refused all the same.
        using (var put = await user.SendAsync(HttpMethod.Put, user.Url("new.md"), new { content = "new\n" })) await AssertFullAsync(put);
    }

    /// <summary>The owner's account is checked, whoever writes; a member learns nothing but the sentence.</summary>
    [Fact]
    public async Task A_member_gets_only_the_neutral_sentence()
    {
        RestClient? bob = null;
        var alice = await FullAsync(before: async a =>
        {
            bob = await RestClient.NewAsync(Api, vault: "Work");
            await a.ShareWithAsync(bob, "garten");
        });

        using (var put = await bob!.SendAsync(HttpMethod.Put, "/api/notes/garten/a.md", new { content = "a\n" })) await AssertFullAsync(put, owner: false);
        using (var file = await bob.SendContentAsync(
            HttpMethod.Put, "/api/files/garten/pic.png", new ByteArrayContent(FileApiTests.Png(2, 2)) { Headers = { ContentType = new("application/octet-stream") } }))
        {
            await AssertFullAsync(file, owner: false);
        }

        var key = await KeyForAsync(bob, "garten");
        var (isError, text) = await bob.ShAsync("mkdir /garten/Drafts", key);
        Assert.True(isError);
        Assert.Equal(Sentence, text);
        var written = await bob.ToolAsync("write", new Dictionary<string, object?> { ["path"] = "/garten/b.md", ["content"] = "b\n" }, key);
        Assert.True(written.IsError);
        Assert.Equal(Sentence, ToolText(written));

        var dav = await DavClient.NewAsync(Api, bob, vaults: new Dictionary<string, string> { ["garten"] = "write" });
        using (var davPut = await dav.SendAsync(HttpMethod.Put, "/dav/garten/c.md", Text("c\n"))) await AssertDavFullAsync(davPut);

        // Her own vault is hers: not full, so she writes there. And a member who is over her own quota writes into a
        // vault whose owner has room.
        await bob.CreateAsync("mine.md", "mine\n");
        var dave = await RestClient.NewAsync(Api, vault: "Work");
        var carol = await FullAsync(before: c => dave.ShareWithAsync(c, "dave"));
        using (var into = await carol.SendAsync(HttpMethod.Put, "/api/notes/dave/a.md", new { content = "a\n" })) Assert.Equal(HttpStatusCode.Created, into.StatusCode);
        Assert.Null(await Quota(Api).LimitAsync(dave.User.Id, Ct));
        Assert.Equal(OneMb, await Quota(Api).LimitAsync(alice.User.Id, Ct));
    }

    [Fact]
    public async Task Quota_zero_refuses_every_write_that_adds_data()
    {
        var user = await RestClient.NewAsync(Api, vault: "Work");
        await user.CreateAsync("a.md", "a\n");
        await OverrideAsync(user, 0);
        await Quota(Api).FigureAsync(user.User.Id, Ct);

        using (var put = await user.SendAsync(HttpMethod.Put, user.Url("b.md"), new { content = "" })) await AssertFullAsync(put);
        using (var file = await user.PutFileAsync("pic.png", FileApiTests.Png(2, 2))) await AssertFullAsync(file);
        using (var removed = await user.SendAsync(HttpMethod.Delete, user.Url("a.md"))) Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Fact]
    public async Task Unlimited_override_beats_the_default()
    {
        await using var limited = Api.WithWebHostBuilder(b => b.UseSetting("Kumbu:Quota:Default", "1MB"));
        var quota = Quota(limited);

        var plain = await RestClient.NewAsync(limited, vault: "Work");
        await FillAsync(plain);
        await quota.FigureAsync(plain.User.Id, Ct);
        Assert.Equal(OneMb, await quota.LimitAsync(plain.User.Id, Ct));
        using (var refused = await plain.SendAsync(HttpMethod.Put, plain.Url("a.md"), new { content = "a\n" })) await AssertFullAsync(refused);

        var free = await RestClient.NewAsync(limited, vault: "Work");
        await OverrideAsync(free, -1);
        await FillAsync(free);
        await quota.FigureAsync(free.User.Id, Ct);
        Assert.Null(await quota.LimitAsync(free.User.Id, Ct));
        await free.CreateAsync("a.md", "a\n");
    }

    /// <summary>The first count after a start runs in the background (brief open point 8): until it is known, writes go through.</summary>
    [Fact]
    public async Task Writes_are_allowed_until_the_first_count_is_known()
    {
        var user = await RestClient.NewAsync(Api, vault: "Work");
        await OverrideAsync(user, OneMb);
        await FillAsync(user);
        var quota = Quota(Api);
        Assert.Null(quota.KnownUsage(user.User.Id));

        // The first write goes through; the cache learns in the background, at most one step a write (whether overrides
        // exist, the vault's owner, her figure), and refuses once it knows.
        await user.CreateAsync("a.md", "a\n");
        HttpStatusCode status;
        var tries = 0;
        do
        {
            await quota.SettledAsync();
            using var next = await user.SendAsync(HttpMethod.Put, user.Url($"b{tries}.md"), new { content = "b\n" });
            status = next.StatusCode;
        }
        while (status != HttpStatusCode.InsufficientStorage && ++tries < 3);

        Assert.Equal(HttpStatusCode.InsufficientStorage, status);
        Assert.True(quota.KnownUsage(user.User.Id) > OneMb);
    }

    /// <summary>What the cache doesn't see (here a write behind its back) counts after the next recount, at most ten minutes on.</summary>
    [Fact]
    public async Task The_recount_corrects_drift_after_ten_minutes()
    {
        var user = await RestClient.NewAsync(Timed, vault: "Work");
        var quota = Quota(Timed);
        await OverrideAsync(user, OneMb);
        await quota.FigureAsync(user.User.Id, Ct);
        await FillAsync(user);
        await user.CreateAsync("a.md", "a\n");

        _time.Advance(TimeSpan.FromMinutes(9));
        await user.CreateAsync("b.md", "b\n");
        await quota.SettledAsync();
        await user.CreateAsync("c.md", "c\n");

        _time.Advance(TimeSpan.FromMinutes(2));
        await user.CreateAsync("d.md", "d\n");
        await quota.SettledAsync();
        using var refused = await user.SendAsync(HttpMethod.Put, user.Url("e.md"), new { content = "e\n" });
        await AssertFullAsync(refused);
    }

    /// <summary>A blob sweep that frees space tells the cache, which counts the owner again at once.</summary>
    [Fact]
    public async Task A_blob_sweep_recounts_whom_it_freed_space_for()
    {
        var user = await RestClient.NewAsync(Timed, vault: "Work");
        var quota = Quota(Timed);
        var catalog = Timed.Services.GetRequiredService<VaultCatalog>();
        await OverrideAsync(user, OneMb);
        var store = new EncryptedNoteStore(catalog, new UserCredential(user.User.Id, user.UserKey));
        // Bytes no record names: the sweep releases them once they are older than its grace.
        await store.PutBlobAsync(user.Vault, System.Security.Cryptography.RandomNumberGenerator.GetBytes((int)(OneMb + OneMb / 4)), Ct);
        await quota.FigureAsync(user.User.Id, Ct);
        using (var refused = await user.SendAsync(HttpMethod.Put, user.Url("a.md"), new { content = "a\n" })) await AssertFullAsync(refused);

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

    [Fact]
    public async Task Parked_bytes_do_not_count()
    {
        var user = await RestClient.NewAsync(Api, vault: "Work");
        await OverrideAsync(user, OneMb);
        var before = (await Quota(Api).FigureAsync(user.User.Id, Ct)).Used;
        var dav = await DavClient.NewAsync(Api, user);

        using (var parked = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.big.bin", new ByteArrayContent(new byte[2 * OneMb]))) Assert.Equal(HttpStatusCode.Created, parked.StatusCode);
        Assert.Equal(before, (await Quota(Api).FigureAsync(user.User.Id, Ct)).Used);
        await user.CreateAsync("a.md", "a\n");
    }

    /// <summary>RFC 4331's properties on the share's root and on the vaults its user owns; a shared vault counts against
    /// someone else, so it has none. Never in <c>allprop</c>.</summary>
    [Fact]
    public async Task Propfind_quota_properties_only_on_owned_vaults()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Garten");
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "garten");
        await OverrideAsync(bob, OneMb);
        await OverrideAsync(alice, OneMb);
        await bob.CreateAsync("a.md", "a\n");
        var used = (await Quota(Api).FigureAsync(bob.User.Id, Ct)).Used;
        var dav = await DavClient.NewAsync(Api, bob, vaults: new Dictionary<string, string> { ["Work"] = "write", ["garten"] = "write" });

        var body = new StringContent("""<?xml version="1.0"?><propfind xmlns="DAV:"><prop><quota-available-bytes/><quota-used-bytes/><displayname/></prop></propfind>""");
        var listing = await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/", depth: "1", body: body));
        foreach (var href in new[] { "/dav/", "/dav/Work/" })
        {
            var response = DavAssert.Response(listing, href);
            Assert.Equal(used.ToString(System.Globalization.CultureInfo.InvariantCulture), DavAssert.Prop(response, "quota-used-bytes")?.Value);
            Assert.Equal((OneMb - used).ToString(System.Globalization.CultureInfo.InvariantCulture), DavAssert.Prop(response, "quota-available-bytes")?.Value);
        }

        var shared = DavAssert.Response(listing, "/dav/garten/");
        Assert.Null(DavAssert.Prop(shared, "quota-used-bytes"));
        Assert.Null(DavAssert.Prop(shared, "quota-available-bytes"));
        Assert.Equal("garten", DavAssert.Prop(shared, "displayname")?.Value);

        var all = await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/", depth: "0"));
        Assert.Null(DavAssert.Prop(DavAssert.Response(all, "/dav/"), "quota-used-bytes"));
    }

    [Fact]
    public async Task Admin_sets_and_clears_an_override()
    {
        var admin = await RestClient.NewAsync(Api, vault: "Admin", role: Roles.Admin);
        var user = await RestClient.NewAsync(Api, vault: "Work");
        await FillAsync(user);

        async Task<(string Quota, long? Limit)> ListedAsync()
        {
            using var list = await admin.GetAsync("/api/admin/users");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var row = (await RestClient.JsonAsync(list)).AsArray().Single(u => u!["id"]!.GetValue<string>() == user.User.Id)!;
            return (row["quota"]!.GetValue<string>(), row["limitBytes"]?.GetValue<long>());
        }

        async Task SetAsync(object quota, HttpStatusCode expected = HttpStatusCode.NoContent)
        {
            using var patched = await admin.SendAsync(HttpMethod.Patch, $"/api/admin/users/{user.User.Id}", new { quota });
            Assert.Equal(expected, patched.StatusCode);
        }

        Assert.Equal(("default", null), await ListedAsync());
        await SetAsync(OneMb);
        Assert.Equal(("custom", OneMb), await ListedAsync());
        // At once in this process: the admin's change counts the user's usage before it answers.
        using (var refused = await user.SendAsync(HttpMethod.Put, user.Url("a.md"), new { content = "a\n" })) await AssertFullAsync(refused);

        await SetAsync("unlimited");
        Assert.Equal(("unlimited", null), await ListedAsync());
        await user.CreateAsync("a.md", "a\n");

        await SetAsync(0);
        Assert.Equal(("custom", 0L), await ListedAsync());
        await SetAsync("default");
        Assert.Equal(("default", null), await ListedAsync());
        await user.CreateAsync("b.md", "b\n");

        await SetAsync("lots", HttpStatusCode.BadRequest);
        await SetAsync(-5, HttpStatusCode.BadRequest);
        await SetAsync(1.5, HttpStatusCode.BadRequest);
        using (var notAdmin = await user.SendAsync(HttpMethod.Patch, $"/api/admin/users/{admin.User.Id}", new { quota = "unlimited" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
        }
    }

    /// <summary>The limit as it is now, the usage as counted within <see cref="StorageQuota.ShownFor"/>: the web app asks
    /// on every focus of its tab.</summary>
    [Fact]
    public async Task Account_storage_reports_used_and_limit()
    {
        var user = await RestClient.NewAsync(Timed, vault: "Work");
        async Task<(long Used, long? Limit)> StorageAsync()
        {
            using var storage = await user.GetAsync("/api/account/storage");
            Assert.Equal(HttpStatusCode.OK, storage.StatusCode);
            var json = await RestClient.JsonAsync(storage);
            return (json["used"]!.GetValue<long>(), json["limit"]?.GetValue<long>());
        }

        Assert.Null((await StorageAsync()).Limit);
        await OverrideAsync(user, OneMb);
        Assert.Equal(OneMb, (await StorageAsync()).Limit);
        await FillAsync(user);
        _time.Advance(StorageQuota.ShownFor);
        Assert.True((await StorageAsync()).Used > OneMb);

        // The session's own figure: not for a key.
        using var keyed = await user.GetAsync("/api/account/storage", bearer: await user.KeyAsync("Claude", KeyRight.Read));
        Assert.Equal(HttpStatusCode.Forbidden, keyed.StatusCode);
    }

    /// <summary><c>GET /api/account/storage</c> counts as <c>df</c> does (security review finding 5): calls within
    /// <see cref="StorageQuota.ShownFor"/> share one count, with a limit or without one; the limit is read each time.</summary>
    [Fact]
    public async Task Account_storage_counts_at_most_once_in_30_seconds()
    {
        var time = new FakeTimeProvider(_time.GetUtcNow());
        var api = Own(time: time);
        var user = await RestClient.NewAsync(api, vault: "Work");
        var seen = Watch(Quota(api), user.User.Id);

        async Task<long?> LimitAsync()
        {
            using var storage = await user.GetAsync("/api/account/storage");
            Assert.Equal(HttpStatusCode.OK, storage.StatusCode);
            return (await RestClient.JsonAsync(storage))["limit"]?.GetValue<long>();
        }

        Assert.Null(await LimitAsync());
        Assert.Null(await LimitAsync());
        Assert.Equal(1, Seen(seen, "count"));

        time.Advance(StorageQuota.ShownFor);
        Assert.Null(await LimitAsync());
        Assert.Equal(2, Seen(seen, "count"));

        // A limit set in between shows at once, from the same count.
        await OverrideAsync(user, OneMb);
        Assert.Equal(OneMb, await LimitAsync());
        Assert.Equal(2, Seen(seen, "count"));
    }

    /// <summary>Emptying the trash counts again at once: Settings asks right after it, and shows what it freed.</summary>
    [Fact]
    public async Task Emptying_the_trash_shows_in_the_storage_at_once()
    {
        var api = Own(time: new FakeTimeProvider(_time.GetUtcNow()));
        var user = await RestClient.NewAsync(api, vault: "Work");
        await FillAsync(user);
        using (var deleted = await user.SendAsync(HttpMethod.Delete, user.Url("big.md"))) Assert.True(deleted.IsSuccessStatusCode);

        async Task<long> UsedAsync()
        {
            using var storage = await user.GetAsync("/api/account/storage");
            return (await RestClient.JsonAsync(storage))["used"]!.GetValue<long>();
        }

        var before = await UsedAsync();
        Assert.True(before > OneMb);
        using (var purged = await user.SendAsync(HttpMethod.Post, $"/api/vaults/Work/trash/purge", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
        }

        Assert.True(await UsedAsync() < OneMb);
    }
}
