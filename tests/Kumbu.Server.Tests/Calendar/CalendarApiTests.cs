// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Calendar;

/// <summary>The calendar's reading endpoints for the web app: a vault's week or month, What's on with the badge, and a
/// note's entry for its header.</summary>
[Collection("app")]
public sealed class CalendarApiTests(KumbuAppFactory app) : IAsyncDisposable
{
    // Wednesday of week 46 (9–15 November), 10:00 UTC. 2037 has 2026's calendar and stays ahead of the real clock, by which
    // MongoDB's TTL index removes sessions that expired on the fake one.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2037, 11, 11, 10, 0, 0, TimeSpan.Zero));

    private WebApplicationFactory<Program>? _api;

    /// <summary>The server's zone UTC, whatever the machine's, and the clock at <see cref="_time"/>.</summary>
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

    private static async Task<JsonNode> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await JsonAsync(response);
    }

    private static async Task<JsonNode> GetOkAsync(RestClient user, string path)
    {
        using var response = await user.GetAsync(path);
        return await OkAsync(response);
    }

    private static string Calendar(string vault, string query = "") => $"/api/vaults/{Uri.EscapeDataString(vault)}/calendar{query}";

    private static string Entry(string vault, string path) =>
        $"/api/vaults/{Uri.EscapeDataString(vault)}/calendar/entry?path={Uri.EscapeDataString(path)}";

    private static async Task SetZoneAsync(RestClient user, string zone)
    {
        using var set = await user.SendAsync(HttpMethod.Put, "/api/account/calendar", new { zone });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
    }

    private static string S(JsonNode? node, string key) => node![key]!.GetValue<string>();

    private static string[] Titles(JsonNode? list) => [.. list!.AsArray().Select(e => S(e, "title"))];

    private static JsonNode Day(JsonNode week, string day) => week["days"]!.AsArray().Single(d => S(d, "day") == day)!;

    [Fact]
    public async Task Gets_the_current_week_with_spanning_entries_and_days()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Zahnarzt.md", "---\nwhen: 2037-11-12T14:00\n---\n");
        await alice.CreateAsync("Urlaub.md", "---\nwhen: 2037-11-10\nuntil: 2037-11-13\n---\n");
        await alice.CreateAsync("Diary/Deep/2037-11-11.md", "# Wednesday\n");
        await alice.CreateAsync("2037-W46.md", "# Week\n");
        await alice.CreateAsync("Old.md", "---\nwhen: 2037-11-02\n---\n");
        await alice.CreateAsync("Plain.md", "# No date\n");

        var calendar = await GetOkAsync(alice, Calendar("Work"));

        Assert.Equal("Work", S(calendar, "vault"));
        Assert.Equal("2037-11-11", S(calendar, "today"));
        Assert.Equal(TimeZoneInfo.Utc.Id, S(calendar, "zone"));
        Assert.True(calendar["canWrite"]!.GetValue<bool>());
        var week = calendar["weeks"]!.AsArray().Single()!;
        Assert.Equal((2037, 46, "2037-11-09", true),
            (week["year"]!.GetValue<int>(), week["week"]!.GetValue<int>(), S(week, "monday"), week["current"]!.GetValue<bool>()));
        Assert.Equal(["2037-W46", "Urlaub"], Titles(week["spanning"]));
        Assert.Equal(["2037-11-09", "2037-11-10", "2037-11-11", "2037-11-12", "2037-11-13", "2037-11-14", "2037-11-15"],
            week["days"]!.AsArray().Select(d => S(d, "day")));

        var urlaub = week["spanning"]![1]!;
        Assert.Equal(("/Work/Urlaub.md", "Work", "dated", "2037-11-10", "2037-11-13"),
            (S(urlaub, "path"), S(urlaub, "vault"), S(urlaub, "kind"), S(urlaub, "when"), S(urlaub, "until")));
        Assert.Equal(("ongoing", "until Fri", "2037-11-10", "2037-11-13", "none"),
            (S(urlaub, "state"), S(urlaub, "span"), S(urlaub, "firstDay"), S(urlaub, "lastDay"), S(urlaub, "task")));
        Assert.True(urlaub["spans"]!.GetValue<bool>());
        Assert.Null(urlaub["time"]);

        var wednesday = Day(week, "2037-11-11");
        Assert.Equal(["2037-11-11"], Titles(wednesday["entries"]));
        Assert.True(wednesday["entries"]![0]!["byName"]!.GetValue<bool>());
        Assert.Equal("/Work/Diary/Deep/2037-11-11.md", S(wednesday["entries"]![0], "path"));
        Assert.Equal(["2037-W46", "Urlaub"], Titles(wednesday["alsoOngoing"]));
        Assert.Empty(Day(week, "2037-11-09")["entries"]!.AsArray());
        Assert.Equal(["2037-W46"], Titles(Day(week, "2037-11-09")["alsoOngoing"]));

        var zahnarzt = Day(week, "2037-11-12")["entries"]!.AsArray().Single()!;
        Assert.Equal(("Zahnarzt", "14:00", "notYet", "2037-11-12T14:00"), (S(zahnarzt, "title"), S(zahnarzt, "time"), S(zahnarzt, "state"), S(zahnarzt, "when")));
        Assert.False(zahnarzt["spans"]!.GetValue<bool>());
        Assert.Null(zahnarzt["boardAction"]);

        // Another week, asked for.
        var next = (await GetOkAsync(alice, Calendar("Work", "?week=2037-W45")))["weeks"]!.AsArray().Single()!;
        Assert.Equal((45, false), (next["week"]!.GetValue<int>(), next["current"]!.GetValue<bool>()));
        Assert.Equal(["Old"], Titles(Day(next, "2037-11-02")["entries"]));

        foreach (var bad in new[] { "?week=46", "?week=2037-W54", "?week=2037-W46&month=2037-11", "?month=2037-13", "?month=11" })
        {
            using var refused = await alice.GetAsync(Calendar("Work", bad));
            Assert.True(refused.StatusCode == HttpStatusCode.BadRequest, bad);
        }
    }

    [Fact]
    public async Task Gets_a_month_as_its_weeks()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Release.md", "---\nwhen: 2037-11-30\n---\n");
        await alice.CreateAsync("Templates.md", "---\nwhen: 2037-W47\n---\n");

        var calendar = await GetOkAsync(alice, Calendar("Work", "?month=2037-11"));

        var weeks = calendar["weeks"]!.AsArray();
        Assert.Equal([44, 45, 46, 47, 48, 49], weeks.Select(w => w!["week"]!.GetValue<int>()));
        Assert.Equal([false, false, true, false, false, false], weeks.Select(w => w!["current"]!.GetValue<bool>()));
        Assert.Equal("2037-10-26", S(weeks[0], "monday"));
        Assert.Equal(["Templates"], Titles(weeks[3]!["spanning"]));
        Assert.Equal(["Release"], Titles(Day(weeks[5]!, "2037-11-30")["entries"]));
        Assert.All(weeks, w => Assert.Equal(7, w!["days"]!.AsArray().Count));
    }

    [Fact]
    public async Task A_tag_narrows_the_week_and_whats_on()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Report.md", "---\nwhen: 2037-11-11\n---\nWeekly #kumbu/report\n");
        await alice.CreateAsync("Lunch.md", "---\nwhen: 2037-11-11\n---\nWith Bob\n");

        var all = await GetOkAsync(alice, Calendar("Work"));
        var tagged = await GetOkAsync(alice, Calendar("Work", "?tag=kumbu"));
        var whatsOn = await GetOkAsync(alice, "/api/calendar/whats-on?tag=%23kumbu");

        Assert.Equal(["Lunch", "Report"], Titles(Day(all["weeks"]![0]!, "2037-11-11")["entries"]));
        Assert.Equal(["Report"], Titles(Day(tagged["weeks"]![0]!, "2037-11-11")["entries"]));
        Assert.Equal(["Report"], Titles(whatsOn["items"]));
        Assert.Equal(1, whatsOn["count"]!.GetValue<int>());
        using var notATag = await alice.GetAsync(Calendar("Work", "?tag=" + Uri.EscapeDataString("a b")));
        Assert.Equal(HttpStatusCode.BadRequest, notATag.StatusCode);
    }

    [Fact]
    public async Task Whats_on_covers_every_vault_and_counts_per_vault()
    {
        var alice = await NewAsync(Api, vault: "Work");
        using (var created = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Home" })) Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var home = Existing(Api, alice.User, alice.UserKey, alice.Session, "Home");
        await alice.CreateAsync("Certificate.md", "---\nwhen: 2037-11-09\ntask: open\n---\n");
        await alice.CreateAsync("Workshop.md", "---\nwhen: 2037-11-11\n---\n");
        await alice.CreateAsync("Done.md", "---\nwhen: 2037-11-09\ntask: closed\nclosed: 2037-11-10\n---\n");
        await home.CreateAsync("Birthday.md", "---\nwhen: 2037-11-14\nupcoming: 7d\n---\n");
        await home.CreateAsync("Christmas.md", "---\nwhen: 2037-12-24\n---\n");

        var whatsOn = await GetOkAsync(alice, "/api/calendar/whats-on");
        var badge = await GetOkAsync(alice, "/api/calendar/whats-on?count=true");

        Assert.Equal(["Certificate", "Workshop", "Birthday"], Titles(whatsOn["items"]));
        var certificate = whatsOn["items"]![0]!;
        Assert.Equal(("Work", "overdue", "2 days", "task", "open"),
            (S(certificate, "vault"), S(certificate, "state"), S(certificate, "span"), S(certificate, "kind"), S(certificate, "task")));
        Assert.Equal(("Home", "upcoming", "in 3 days"), (S(whatsOn["items"]![2], "vault"), S(whatsOn["items"]![2], "state"), S(whatsOn["items"]![2], "span")));
        foreach (var answer in new[] { whatsOn, badge })
        {
            Assert.Equal(3, answer["count"]!.GetValue<int>());
            Assert.True(answer["overdue"]!.GetValue<bool>());
            var vaults = answer["vaults"]!.AsArray().ToDictionary(v => S(v, "vault"), v => (v!["count"]!.GetValue<int>(), v["overdue"]!.GetValue<bool>()));
            Assert.Equal(new Dictionary<string, (int, bool)> { ["Work"] = (2, true), ["Home"] = (1, false) }, vaults);
        }

        Assert.Empty(badge["items"]!.AsArray());
    }

    [Fact]
    public async Task The_badge_is_red_only_when_something_is_overdue()
    {
        var alice = await NewAsync(Api, vault: "Work");

        var none = await GetOkAsync(alice, "/api/calendar/whats-on?count=true");
        await alice.CreateAsync("Soon.md", "---\nwhen: 2037-11-12\ntask: open\nupcoming: 2d\n---\n");
        var calm = await GetOkAsync(alice, "/api/calendar/whats-on?count=true");
        await alice.CreateAsync("Late.md", "---\nwhen: 2037-11-10\ntask: open\n---\n");
        var warning = await GetOkAsync(alice, "/api/calendar/whats-on?count=true");

        Assert.Equal((0, false), (none["count"]!.GetValue<int>(), none["overdue"]!.GetValue<bool>()));
        Assert.Empty(none["vaults"]!.AsArray());
        Assert.Equal((1, false), (calm["count"]!.GetValue<int>(), calm["overdue"]!.GetValue<bool>()));
        Assert.Equal((2, true), (warning["count"]!.GetValue<int>(), warning["overdue"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Couldnt_read_lists_the_dates_in_plain_words()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Tomorrow.md", "---\nwhen: tomorrow\n---\n");
        await alice.CreateAsync("Maybe.md", "---\nwhen: 2037-11-20\ntask: maybe\n---\n");

        var calendar = await GetOkAsync(alice, Calendar("Work"));
        var whatsOn = await GetOkAsync(alice, "/api/calendar/whats-on");

        var couldnt = calendar["couldntRead"]!.AsArray();
        Assert.Equal(["/Work/Maybe.md", "/Work/Tomorrow.md"], couldnt.Select(c => S(c, "path")));
        Assert.Equal("Maybe", S(couldnt[0], "title"));
        Assert.Equal("`task: maybe` – I need open or closed", S(couldnt[0], "plain"));
        Assert.Equal("2037-11-20", S(couldnt[0]!["readable"], "when"));
        Assert.Equal("`when: tomorrow` – I need a date like 2026-11-20", S(couldnt[1], "plain"));
        Assert.Empty(couldnt[1]!["readable"]!.AsObject());
        Assert.Equal(2, whatsOn["couldntRead"]!.GetValue<int>());
        Assert.Equal(["Work"], whatsOn["couldntReadVaults"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(0, whatsOn["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_entry_of_a_note_for_its_header()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Board/Board.md", "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Doing\n  - Done: [archivable]\n---\n# Roadmap\n");
        await alice.CreateAsync("Board/Release.md", "---\ncolumn: Doing\nrank: a1\ndue: 2037-11-20\n---\n");
        await alice.CreateAsync("Board/Shipped.md", "---\ncolumn: Done\nrank: a1\ndue: 2037-11-02\n---\n");
        await alice.CreateAsync("Call.md", "---\nwhen: 2037-11-12T14:00\ntz: Europe/London\nupcoming: 1w\n---\n");
        await alice.CreateAsync("Plain.md", "# Nothing on\n");
        await alice.CreateAsync("Tomorrow.md", "---\nwhen: tomorrow\n---\n");

        var release = await GetOkAsync(alice, Entry("Work", "/Work/Board/Release.md"));
        var shipped = await GetOkAsync(alice, Entry("Work", "/Work/Board/Shipped.md"));
        var call = await GetOkAsync(alice, Entry("Work", "/Work/Call.md"));
        var plain = await GetOkAsync(alice, Entry("Work", "/Work/Plain.md"));
        var tomorrow = await GetOkAsync(alice, Entry("Work", "/Work/Tomorrow.md"));

        var card = release["entry"]!;
        Assert.Equal(("card", "Doing", "Done", "open", "2037-11-20"), (S(card, "kind"), S(card, "column"), S(card, "boardAction"), S(card, "task"), S(card, "when")));
        Assert.True(release["canWrite"]!.GetValue<bool>());
        Assert.Null(release["couldntRead"]);
        Assert.Equal(("Done", "closed", "done"), (S(shipped["entry"], "column"), S(shipped["entry"], "task"), S(shipped["entry"], "state")));
        Assert.Null(shipped["entry"]!["boardAction"]);

        Assert.Equal(("14:00 (Europe/London)", "Europe/London", "1w", "upcoming", "tomorrow"),
            (S(call["entry"], "time"), S(call["entry"], "tz"), S(call["entry"], "upcoming"), S(call["entry"], "state"), S(call["entry"], "span")));
        Assert.Null(call["entry"]!["boardAction"]);

        Assert.Null(plain["entry"]);
        Assert.Null(plain["couldntRead"]);
        Assert.Null(tomorrow["entry"]);
        Assert.Equal("`when: tomorrow` – I need a date like 2026-11-20", S(tomorrow["couldntRead"], "plain"));

        using var missing = await alice.GetAsync(Entry("Work", "/Work/Gone.md"));
        using var elsewhere = await alice.GetAsync(Entry("Work", "/Home/Call.md"));
        using var folder = await alice.GetAsync(Entry("Work", "/Work/Board"));
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (missing.StatusCode, elsewhere.StatusCode, folder.StatusCode));
    }

    [Fact]
    public async Task A_board_with_two_archivable_columns_offers_no_move()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Board/Board.md", "---\nkumbu-board: 1\ncolumns:\n  - Doing\n  - Done: [archivable]\n  - Shipped: [archivable]\n---\n# Roadmap\n");
        await alice.CreateAsync("Board/Release.md", "---\ncolumn: Doing\nrank: a1\ndue: 2037-11-20\n---\n");

        var release = await GetOkAsync(alice, Entry("Work", "/Work/Board/Release.md"));

        Assert.Equal("Doing", S(release["entry"], "column"));
        Assert.Null(release["entry"]!["boardAction"]);
    }

    [Fact]
    public async Task A_shared_vault_shows_its_entries_to_every_member_in_their_own_zone()
    {
        // 15:00 UTC: 16:00 in Vienna, 10:00 in New York.
        _time.SetUtcNow(new DateTimeOffset(2037, 11, 11, 15, 0, 0, TimeSpan.Zero));
        var alice = await NewAsync(Api, vault: "Work");
        var bob = await NewAsync(Api, vault: "Own");
        await alice.ShareWithAsync(bob, "Shared", MemberRole.Read);
        await SetZoneAsync(alice, "Europe/Vienna");
        await SetZoneAsync(bob, "America/New_York");
        await alice.CreateAsync("Call.md", "---\nwhen: 2037-11-11T14:00\n---\n");

        var forAlice = await GetOkAsync(alice, Entry("Work", "/Work/Call.md"));
        var forBob = await GetOkAsync(bob, Entry("Shared", "/Shared/Call.md"));
        var bobsWeek = await GetOkAsync(bob, Calendar("Shared"));
        var alicesOn = await GetOkAsync(alice, "/api/calendar/whats-on");
        var bobsOn = await GetOkAsync(bob, "/api/calendar/whats-on");

        Assert.Equal(("14:00", "past"), (S(forAlice["entry"], "time"), S(forAlice["entry"], "state")));
        Assert.Equal(("14:00", "ongoing", "until 14:00"), (S(forBob["entry"], "time"), S(forBob["entry"], "state"), S(forBob["entry"], "span")));
        Assert.Equal(("/Shared/Call.md", "Shared"), (S(forBob["entry"], "path"), S(forBob["entry"], "vault")));
        Assert.True(forAlice["canWrite"]!.GetValue<bool>());
        Assert.False(forBob["canWrite"]!.GetValue<bool>());
        Assert.Equal(("America/New_York", "Shared"), (S(bobsWeek, "zone"), S(bobsWeek, "vault")));
        Assert.False(bobsWeek["canWrite"]!.GetValue<bool>());
        Assert.Equal(["Call"], Titles(Day(bobsWeek["weeks"]![0]!, "2037-11-11")["entries"]));
        Assert.Empty(alicesOn["items"]!.AsArray());
        Assert.Equal(["Call"], Titles(bobsOn["items"]));
    }

    [Fact]
    public async Task The_zone_is_the_callers()
    {
        // 23:30 UTC on Wednesday: Thursday already in Vienna.
        _time.SetUtcNow(new DateTimeOffset(2037, 11, 11, 23, 30, 0, TimeSpan.Zero));
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Due.md", "---\nwhen: 2037-11-11\ntask: open\n---\n");

        var utc = await GetOkAsync(alice, Entry("Work", "/Work/Due.md"));
        await SetZoneAsync(alice, "Europe/Vienna");
        var vienna = await GetOkAsync(alice, Entry("Work", "/Work/Due.md"));
        var calendar = await GetOkAsync(alice, Calendar("Work"));

        Assert.Equal("ongoing", S(utc["entry"], "state"));
        Assert.Equal(("overdue", "1 day"), (S(vienna["entry"], "state"), S(vienna["entry"], "span")));
        Assert.Equal(("2037-11-12", "Europe/Vienna"), (S(calendar, "today"), S(calendar, "zone")));
    }

    /// <summary>Spec, "Reading cost": What's on over a vault of 5000 notes in about a second. Measured, not asserted.</summary>
    [Fact(Explicit = true)]
    public async Task Whats_on_for_5000_notes_over_the_server()
    {
        var alice = await NewAsync(Api, vault: "Work");
        var store = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(alice.User.Id, alice.UserKey));
        await Parallel.ForEachAsync(Enumerable.Range(0, 5000), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = Ct }, async (i, ct) =>
        {
            var path = VaultPath.TryParse($"/Work/Folder {i % 50}/Note {i}.md", out var p) ? p : throw new ArgumentException(null, nameof(i));
            // One in ten an entry, around now; the rest ordinary notes of a few paragraphs.
            var front = i % 10 == 0 ? $"---\nwhen: 2037-11-{1 + i % 28:D2}\n{(i % 20 == 0 ? "task: open\n" : "")}---\n" : "";
            var content = front + $"# Note {i}\n\n" + string.Concat(Enumerable.Repeat("Some words about the work, with a #tag and a [[link]].\n\n", 20));
            Assert.True(await store.TryCreateAsync(new StoredNote(path, content, 1, alice.Username, _time.GetUtcNow().UtcDateTime, [], Ref: WriterRef.Server), ct));
        });

        // Cold: the vault locked (idle, swept), so the request opens and decrypts it from MongoDB; then warm, twice.
        _time.Advance(TimeSpan.FromDays(1));
        Api.Services.GetRequiredService<VaultCatalog>().Sweep();
        var again = await alice.SignInAgainAsync();
        var timings = new List<long>();
        var count = 0;
        for (var run = 0; run < 3; run++)
        {
            var clock = Stopwatch.StartNew();
            var answer = await GetOkAsync(again, "/api/calendar/whats-on");
            timings.Add(clock.ElapsedMilliseconds);
            count = answer["count"]!.GetValue<int>();
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"What's on over 5000 notes: {count} on; cold {timings[0]} ms, warm {timings[1]} ms, {timings[2]} ms");
        Assert.True(count > 0);
    }
}
