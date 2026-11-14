// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Discreet;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Flags;

/// <summary>Recent changes: one row per note and attachment as it is now, across the session's vaults, newest first.</summary>
[Collection("app")]
public sealed class RecentApiTests(KumbuAppFactory app) : IAsyncDisposable
{
    // Saturday of week 46, 10:00 UTC, ahead of the real clock (see CalendarApiTests).
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2037, 11, 14, 10, 0, 0, TimeSpan.Zero));

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<Program> Api => _api ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
        s.RemoveAll<ServerZone>();
        s.AddSingleton(new ServerZone(TimeZoneInfo.Utc));
    }));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_api is not null) await _api.DisposeAsync();
    }

    private static async Task<JsonNode> RecentAsync(RestClient user, string query = "")
    {
        using var response = await user.GetAsync("/api/recent" + query);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await JsonAsync(response);
    }

    private static string[] Paths(JsonNode recent) => [.. recent["items"]!.AsArray().Select(i => S(i, "path")!)];

    /// <summary>The chips' vaults, sorted; Skills is every user's.</summary>
    private static string[] Vaults(JsonNode recent) => [.. recent["vaults"]!.AsArray().Select(v => v!.GetValue<string>()).Order(StringComparer.Ordinal)];

    private static string? S(JsonNode? node, string key) => node![key]?.GetValue<string>();

    private static JsonNode Item(JsonNode recent, string path) => recent["items"]!.AsArray().Single(i => S(i, "path") == path)!;

    /// <summary>A minute later, so that each write has its own time.</summary>
    private void Later() => _time.Advance(TimeSpan.FromMinutes(1));

    private static RestClient In(RestClient user, string vault) => Existing(user.Api, user.User, user.UserKey, user.Session, vault);

    private static async Task NewVaultAsync(RestClient user, string name)
    {
        using var created = await user.SendAsync(HttpMethod.Post, "/api/vaults", new { name });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Lists_the_newest_first_across_vaults()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await NewVaultAsync(alice, "Home");
        var home = In(alice, "Home");
        await alice.CreateAsync("Pump/Certificate.md", "---\ntags: [flag/red]\n---\n# Certificate\n");
        Later();
        await home.CreateAsync("Garden.md", "# Garden\n");
        Later();
        await alice.CreateFileAsync("Pump/Plate.png", FileApiTests.Png(4, 4));
        Later();
        // Two at the same time: by path.
        await home.CreateAsync("B.md", "b\n");
        await home.CreateAsync("A.md", "a\n");

        var recent = await RecentAsync(alice);

        Assert.Equal(["/Home/A.md", "/Home/B.md", "/Work/Pump/Plate.png", "/Home/Garden.md", "/Work/Pump/Certificate.md"], Paths(recent));
        Assert.False(recent["more"]!.GetValue<bool>());
        Assert.Equal(["Home", "Skills", "Work"], Vaults(recent));
        var note = Item(recent, "/Work/Pump/Certificate.md");
        Assert.Equal(("Work", "Certificate", "Work/Pump", "note", "red"), (S(note, "vault"), S(note, "title"), S(note, "folder"), S(note, "kind"), S(note, "flag")));
        var image = Item(recent, "/Work/Pump/Plate.png");
        Assert.Equal(("Plate.png", "Work/Pump", "image", null), (S(image, "title"), S(image, "folder"), S(image, "kind"), S(image, "flag")));
        Assert.Equal("Home", S(Item(recent, "/Home/Garden.md"), "folder"));
    }

    [Fact]
    public async Task Says_who_and_when()
    {
        var alice = await NewAsync(Api, vault: "Work");
        var key = await alice.KeyAsync("laptop", KeyRight.Write);
        await alice.CreateAsync("Mine.md", "by me\n");
        var mine = _time.GetUtcNow().UtcDateTime;
        Later();
        using (var byAgent = await alice.SendAsync(HttpMethod.Put, alice.Url("Agent.md"), new { content = "by the agent\n" }, bearer: key))
        {
            Assert.Equal(HttpStatusCode.Created, byAgent.StatusCode);
        }

        var recent = await RecentAsync(alice);

        var own = Item(recent, "/Work/Mine.md");
        Assert.Equal((alice.Username, "self"), (S(own, "writer"), S(own, "writerKind")));
        Assert.Equal(mine, own["writtenAt"]!.GetValue<DateTime>().ToUniversalTime());
        var agent = Item(recent, "/Work/Agent.md");
        Assert.Equal(("laptop", "agent"), (S(agent, "writer"), S(agent, "writerKind")));
        Assert.Equal(mine.AddMinutes(1), agent["writtenAt"]!.GetValue<DateTime>().ToUniversalTime());
    }

    [Fact]
    public async Task Filters_by_words_flag_and_vault()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await NewVaultAsync(alice, "Home");
        var home = In(alice, "Home");
        await alice.CreateAsync("Pump/Certificate.md", "---\ntags: [flag/red]\n---\n# Certificate\n");
        await alice.CreateAsync("Pump/Manual.md", "# Manual\n\n#flag/green\n");
        await alice.CreateFileAsync("Pump/Certificate scan.png", FileApiTests.Png(4, 4));
        await home.CreateAsync("Pump certificate.md", "---\ntags: [flag/blue]\n---\n");
        // In a note's text only, not its path.
        await alice.CreateAsync("Other.md", "pump certificate\n");

        Assert.Equal(
            ["/Home/Pump certificate.md", "/Work/Pump/Certificate scan.png", "/Work/Pump/Certificate.md"],
            Paths(await RecentAsync(alice, "?words=" + Uri.EscapeDataString("pump CERT"))).Order(StringComparer.Ordinal));
        Assert.Empty(Paths(await RecentAsync(alice, "?words=" + Uri.EscapeDataString("pump x"))));
        // The vault is part of the path; .md isn't.
        Assert.Equal(["/Home/Pump certificate.md"], Paths(await RecentAsync(alice, "?words=" + Uri.EscapeDataString("home/pump"))));
        Assert.Empty(Paths(await RecentAsync(alice, "?words=.md")));

        Assert.Equal(["/Work/Pump/Certificate.md"], Paths(await RecentAsync(alice, "?flag=red")));
        Assert.Equal(
            ["/Home/Pump certificate.md", "/Work/Pump/Certificate.md", "/Work/Pump/Manual.md"],
            Paths(await RecentAsync(alice, "?flag=any")).Order(StringComparer.Ordinal));
        Assert.Equal(["/Work/Pump/Manual.md"], Paths(await RecentAsync(alice, "?flag=any&words=manual")));

        var skipped = await RecentAsync(alice, "?skip=Home&words=pump");
        Assert.Equal(["/Work/Pump/Certificate scan.png", "/Work/Pump/Certificate.md", "/Work/Pump/Manual.md"], Paths(skipped).Order(StringComparer.Ordinal));
        // The chips stay, every vault switched off included.
        Assert.Equal(["Home", "Skills", "Work"], Vaults(skipped));
        var none = await RecentAsync(alice, "?skip=Home&skip=Work");
        Assert.Empty(Paths(none));
        Assert.Equal(["Home", "Skills", "Work"], Vaults(none));

        foreach (var bad in new[] { "?flag=orange", "?flag=Red", "?limit=0", "?limit=501" })
        {
            using var refused = await alice.GetAsync("/api/recent" + bad);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
    }

    [Fact]
    public async Task Leaves_out_the_archive_and_the_trash()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Kept.md", "kept\n");
        await alice.CreateAsync("_archive/Old 2037-11-01 090000.md", "# Old\n");
        await alice.CreateFileAsync("_archive/Old scan 2037-11-01 090000.png", FileApiTests.Png(4, 4));
        await alice.CreateFileAsync("Empty/.vaultkeep", []);
        var version = await alice.CreateAsync("Gone.md", "gone\n");
        using (var removed = await alice.SendAsync(HttpMethod.Delete, alice.Url("Gone.md") + $"?version={version}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Equal(["/Work/Kept.md"], Paths(await RecentAsync(alice)));
    }

    [Fact]
    public async Task A_moved_file_shows_at_its_new_path()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Draft.md", "# Draft\n");
        await alice.CreateFileAsync("shot.png", FileApiTests.Png(4, 4));
        Later();
        foreach (var (from, to) in new[] { ("/Work/Draft.md", "/Work/Reports/Final.md"), ("/Work/shot.png", "/Work/Reports/shot.png") })
        {
            using var moved = await alice.SendAsync(HttpMethod.Post, "/api/move", new { from, to });
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        var recent = await RecentAsync(alice);

        Assert.Equal(["/Work/Reports/Final.md", "/Work/Reports/shot.png"], Paths(recent).Order(StringComparer.Ordinal));
        Assert.Equal(("Final", "Work/Reports"), (S(Item(recent, "/Work/Reports/Final.md"), "title"), S(Item(recent, "/Work/Reports/Final.md"), "folder")));
    }

    [Fact]
    public async Task A_hidden_discreet_vault_is_absent_chips_included()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.HideAsync();

        using (var response = await w.Alice.GetAsync("/api/recent?limit=500"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(Ct);
            foreach (var hidden in new[] { "Tagebuch", "Familie", "geheim", "bild", HiddenWorld.TagebuchWord, HiddenWorld.FamilieWord })
            {
                Assert.DoesNotContain(hidden, body, StringComparison.OrdinalIgnoreCase);
            }

            var recent = JsonNode.Parse(body)!;
            Assert.Equal(["/Work/plan.md"], Paths(recent));
            Assert.Equal(["Skills", "Work"], Vaults(recent));
        }

        // Naming it changes nothing.
        Assert.Empty(Paths(await RecentAsync(w.Alice, "?words=Tagebuch")));
        Assert.Empty(Paths(await RecentAsync(w.Alice, "?words=geheim")));
        Assert.Equal(["Skills", "Work"], Vaults(await RecentAsync(w.Alice, "?skip=Tagebuch&skip=Familie")));

        await w.RevealAsync();
        var shown = await RecentAsync(w.Alice);
        Assert.Equal(["Familie", "Skills", "Tagebuch", "Work"], Vaults(shown));
        Assert.Contains("/Tagebuch/geheim.md", Paths(shown));
        Assert.Contains("/Tagebuch/bild.png", Paths(shown));
        Assert.Contains("/Familie/geheim.md", Paths(shown));
    }

    [Fact]
    public async Task A_read_only_member_sees_the_shared_vault()
    {
        var alice = await NewAsync(Api, vault: "Work");
        var bob = await NewAsync(Api, vault: "Own");
        await alice.ShareWithAsync(bob, "Shared", MemberRole.Read);
        await alice.CreateAsync("Plan.md", "---\ntags: [flag/green]\n---\n# Plan\n");
        Later();
        await bob.CreateAsync("Mine.md", "mine\n");

        var recent = await RecentAsync(bob);

        Assert.Equal(["/Own/Mine.md", "/Shared/Plan.md"], Paths(recent));
        Assert.Equal(["Own", "Shared", "Skills"], Vaults(recent));
        var plan = Item(recent, "/Shared/Plan.md");
        Assert.Equal(("Shared", "green"), (S(plan, "vault"), S(plan, "flag")));
        // As Activity shows the same write to him.
        using var activity = await bob.GetAsync("/api/vaults/Shared/activity");
        Assert.Equal(HttpStatusCode.OK, activity.StatusCode);
        var row = (await JsonAsync(activity))["rows"]!.AsArray().Single()!;
        Assert.Equal((S(row, "writer"), S(row, "writerKind")), (S(plan, "writer"), S(plan, "writerKind")));
        Assert.Equal("user", S(plan, "writerKind"));
        Assert.Equal((bob.Username, "self"), (S(Item(recent, "/Own/Mine.md"), "writer"), S(Item(recent, "/Own/Mine.md"), "writerKind")));
    }

    [Fact]
    public async Task Show_more_takes_the_next_ones()
    {
        var alice = await NewAsync(Api, vault: "Work");
        foreach (var i in Enumerable.Range(1, 5))
        {
            await alice.CreateAsync($"Note {i}.md", $"{i}\n");
            Later();
        }

        var first = await RecentAsync(alice, "?limit=2");
        Assert.Equal(["/Work/Note 5.md", "/Work/Note 4.md"], Paths(first));
        Assert.True(first["more"]!.GetValue<bool>());
        var next = await RecentAsync(alice, "?limit=4");
        Assert.Equal(["/Work/Note 5.md", "/Work/Note 4.md", "/Work/Note 3.md", "/Work/Note 2.md"], Paths(next));
        Assert.True(next["more"]!.GetValue<bool>());
        var all = await RecentAsync(alice, "?limit=5");
        Assert.Equal(5, Paths(all).Length);
        Assert.False(all["more"]!.GetValue<bool>());
        Assert.Equal(5, Paths(await RecentAsync(alice)).Length);
    }

    [Fact]
    public async Task A_key_is_refused()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Plan.md", "# Plan\n");
        var key = await alice.KeyAsync("laptop", KeyRight.Write);

        using var asKey = await alice.GetAsync("/api/recent", bearer: key);
        using var anonymous = await SendAsync(Api, HttpMethod.Get, "/api/recent", null, session: null, bearer: null);

        Assert.Equal(HttpStatusCode.Forbidden, asKey.StatusCode);
        Assert.DoesNotContain("Plan", await asKey.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    /// <summary>Spec, "Reading cost": Recent changes and the tree over a vault of 5000 notes. Measured, not asserted.</summary>
    [Fact(Explicit = true)]
    public async Task Recent_changes_for_5000_notes_over_the_server()
    {
        var alice = await NewAsync(Api, vault: "Work");
        var store = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(alice.User.Id, alice.UserKey));
        var start = _time.GetUtcNow().UtcDateTime;
        await Parallel.ForEachAsync(Enumerable.Range(0, 5000), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = Ct }, async (i, ct) =>
        {
            var path = VaultPath.TryParse($"/Work/Folder {i % 50}/Note {i}.md", out var p) ? p : throw new ArgumentException(null, nameof(i));
            // One in ten flagged; each written a second after the one before.
            var front = i % 10 == 0 ? "---\ntags: [flag/red]\n---\n" : "";
            var content = front + $"# Note {i}\n\n" + string.Concat(Enumerable.Repeat("Some words about the work, with a #tag and a [[link]].\n\n", 20));
            Assert.True(await store.TryCreateAsync(new StoredNote(path, content, 1, alice.Username, start.AddSeconds(i), [], Ref: WriterRef.Server), ct));
        });

        // Cold: the vault locked (idle, swept), so the request opens and decrypts it from MongoDB; then warm, twice.
        var recent = await MeasureAsync(alice, "/api/recent");
        var words = await MeasureAsync(alice, "/api/recent?words=" + Uri.EscapeDataString("folder 7 note"));
        var tree = await MeasureAsync(alice, "/api/vaults/Work/tree");
        var answer = await RecentAsync(await alice.SignInAgainAsync());
        var timesOf = (List<long> t) => $"cold {t[0]} ms, warm {t[1]} ms, {t[2]} ms";

        TestContext.Current.TestOutputHelper?.WriteLine($"Recent changes over 5000 notes: {timesOf(recent)}");
        TestContext.Current.TestOutputHelper?.WriteLine($"Recent changes over 5000 notes, by words: {timesOf(words)}");
        TestContext.Current.TestOutputHelper?.WriteLine($"The tree of 5000 notes: {timesOf(tree)}");
        Assert.Equal("/Work/Folder 49/Note 4999.md", Paths(answer)[0]);
        Assert.True(answer["more"]!.GetValue<bool>());
    }

    /// <summary>Three runs of <paramref name="path"/>, the first on the vault swept from memory.</summary>
    private async Task<List<long>> MeasureAsync(RestClient user, string path)
    {
        // A day idle: the old session gone, the vault locked.
        _time.Advance(TimeSpan.FromDays(1));
        Api.Services.GetRequiredService<VaultCatalog>().Sweep();
        var again = await user.SignInAgainAsync();
        var timings = new List<long>();
        for (var run = 0; run < 3; run++)
        {
            var clock = Stopwatch.StartNew();
            using var response = await again.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync(Ct);
            timings.Add(clock.ElapsedMilliseconds);
        }

        return timings;
    }
}
