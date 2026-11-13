// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Core.Calendar;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Calendar;

/// <summary>The calendar's writes for the web app: the same <see cref="CalendarService"/> as <c>sh calendar</c>, with the
/// caller's rights.</summary>
[Collection("app")]
public sealed class CalendarWriteApiTests(KumbuAppFactory app) : IAsyncDisposable
{
    // Wednesday of week 46, 10:00 UTC, ahead of the real clock (see CalendarApiTests).
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2037, 11, 11, 10, 0, 0, TimeSpan.Zero));

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

    private static string Url(string vault, string action) => $"/api/vaults/{Uri.EscapeDataString(vault)}/calendar/{action}";

    private static async Task<JsonNode> PostOkAsync(RestClient user, string vault, string action, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await user.SendAsync(HttpMethod.Post, Url(vault, action), body);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await JsonAsync(response);
    }

    private static async Task<(HttpStatusCode Status, string Error)> PostRefusedAsync(RestClient user, string vault, string action, object? body = null, string? bearer = null)
    {
        using var response = await user.SendAsync(HttpMethod.Post, Url(vault, action), body, bearer: bearer);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.False(response.IsSuccessStatusCode, text);
        return (response.StatusCode, JsonNode.Parse(text)!["error"]!.GetValue<string>());
    }

    private static async Task<string> ContentAsync(RestClient user, string path) => (await user.ReadAsync(path))["content"]!.GetValue<string>();

    private static string S(JsonNode? node, string key) => node![key]!.GetValue<string>();

    [Fact]
    public async Task Set_close_reason_and_reopen_through_the_api()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Renew.md", "---\ntags: [admin]\n---\n# Renew the passport\n");

        var set = await PostOkAsync(alice, "Work", "set", new { path = "/Work/Renew.md", when = "2037-11-12", task = true, upcoming = "7d" });
        Assert.Equal("/Work/Renew.md", S(set, "path"));
        Assert.Equal((await alice.ReadAsync("Renew.md"))["version"]!.GetValue<string>(), S(set, "version"));
        Assert.Equal(("task", "open", "2037-11-12", "7d"), (S(set["note"]!["entry"], "kind"), S(set["note"]!["entry"], "task"), S(set["note"]!["entry"], "when"), S(set["note"]!["entry"], "upcoming")));
        Assert.True(set["note"]!["canWrite"]!.GetValue<bool>());
        Assert.Equal("---\ntags: [admin]\nwhen: 2037-11-12\ntask: open\nupcoming: 7d\n---\n# Renew the passport\n", await ContentAsync(alice, "Renew.md"));

        var closed = await PostOkAsync(alice, "Work", "close", new { path = "/Work/Renew.md" });
        Assert.Equal(("closed", "2037-11-11"), (S(closed["note"]!["entry"], "task"), S(closed["note"]!["entry"], "closed")));
        Assert.NotEqual(S(set, "version"), S(closed, "version"));

        var reason = await PostOkAsync(alice, "Work", "set", new { path = "/Work/Renew.md", reason = "Renewed until 2047" });
        Assert.Equal("Renewed until 2047", S(reason["note"]!["entry"], "reason"));
        var noReason = await PostOkAsync(alice, "Work", "set", new { path = "/Work/Renew.md", clearReason = true });
        Assert.Null(noReason["note"]!["entry"]!["reason"]);

        var reopened = await PostOkAsync(alice, "Work", "reopen", new { path = "/Work/Renew.md" });
        Assert.Equal("open", S(reopened["note"]!["entry"], "task"));
        Assert.Null(reopened["note"]!["entry"]!["closed"]);
        Assert.Equal("---\ntags: [admin]\nwhen: 2037-11-12\ntask: open\nupcoming: 7d\n---\n# Renew the passport\n", await ContentAsync(alice, "Renew.md"));

        var withUntil = await PostOkAsync(alice, "Work", "set", new { path = "/Work/Renew.md", until = "2037-11-14", clearUpcoming = true });
        Assert.Equal("2037-11-14", S(withUntil["note"]!["entry"], "until"));
        Assert.Null(withUntil["note"]!["entry"]!["upcoming"]);
        var moved = await PostOkAsync(alice, "Work", "move", new { path = "/Work/Renew.md", when = "2037-11-16" });
        Assert.Equal(("2037-11-16", "2037-11-18"), (S(moved["note"]!["entry"], "when"), S(moved["note"]!["entry"], "until")));
        var noUntil = await PostOkAsync(alice, "Work", "set", new { path = "/Work/Renew.md", clearUntil = true, task = false });
        Assert.Equal(("dated", "none"), (S(noUntil["note"]!["entry"], "kind"), S(noUntil["note"]!["entry"], "task")));

        var unset = await PostOkAsync(alice, "Work", "unset", new { path = "/Work/Renew.md" });
        Assert.Null(unset["note"]!["entry"]);
        Assert.Equal("---\ntags: [admin]\n---\n# Renew the passport\n", await ContentAsync(alice, "Renew.md"));

        var bad = new object[]
        {
            new { path = "/Work/Renew.md", when = "tomorrow" },
            new { path = "/Work/Renew.md", until = "2037-11-20", clearUntil = true },
            new { path = "/Work/Renew.md", upcoming = "7d", clearUpcoming = true },
            new { path = "/Work/Renew.md", reason = "x", clearReason = true },
            new { path = "/Work/Renew.md" },
        };
        foreach (var body in bad) Assert.Equal(HttpStatusCode.BadRequest, (await PostRefusedAsync(alice, "Work", "set", body)).Status);
        var (status, error) = await PostRefusedAsync(alice, "Work", "close", new { path = "/Work/Gone.md" });
        Assert.Equal((HttpStatusCode.NotFound, "/Work/Gone.md: No such note"), (status, error));
    }

    [Fact]
    public async Task A_frontmatter_that_does_not_parse_is_a_conflict()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Broken.md", "---\nwhen: [2037-11-12\n---\n# Broken\n");

        var (status, error) = await PostRefusedAsync(alice, "Work", "close", new { path = "/Work/Broken.md" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.StartsWith("/Work/Broken.md: the frontmatter does not parse", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_member_can_close_nothing()
    {
        var alice = await NewAsync(Api, vault: "Work");
        var bob = await NewAsync(Api, vault: "Own");
        await alice.ShareWithAsync(bob, "Shared", MemberRole.Read);
        const string task = "---\nwhen: 2037-11-02\ntask: open\n---\n# Task\n";
        await alice.CreateAsync("Task.md", task);

        var writes = new (string Action, object? Body)[]
        {
            ("close", new { path = "/Shared/Task.md" }),
            ("reopen", new { path = "/Shared/Task.md" }),
            ("set", new { path = "/Shared/Task.md", when = "2037-11-20" }),
            ("move", new { path = "/Shared/Task.md", when = "2037-11-20" }),
            ("unset", new { path = "/Shared/Task.md" }),
            ("later", new { path = "/Shared/Task.md", part = "postpone", step = "day" }),
            ("today", null),
            ("this-week", null),
            ("entries", new { day = "2037-11-12", title = "Mine" }),
        };
        foreach (var (action, body) in writes)
            Assert.True((await PostRefusedAsync(bob, "Shared", action, body)).Status == HttpStatusCode.Forbidden, action);

        Assert.Equal(task, await ContentAsync(alice, "Task.md"));
        using var tree = await alice.GetAsync("/api/vaults/Work/tree");
        Assert.DoesNotContain("Calendar", await tree.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_key_can_read_but_not_close()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Task.md", "---\nwhen: 2037-11-12\ntask: open\n---\n");
        var reader = await alice.KeyAsync("reader", KeyRight.Read);

        using var read = await alice.GetAsync("/api/vaults/Work/calendar/entry?path=/Work/Task.md", bearer: reader);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.False((await JsonAsync(read))["canWrite"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.Forbidden, (await PostRefusedAsync(alice, "Work", "close", new { path = "/Work/Task.md" }, bearer: reader)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostRefusedAsync(alice, "Work", "entries", new { day = "2037-11-12", title = "Mine" }, bearer: reader)).Status);
        Assert.Equal("---\nwhen: 2037-11-12\ntask: open\n---\n", await ContentAsync(alice, "Task.md"));
    }

    [Fact]
    public async Task Later_on_an_overdue_task_moves_it_from_today()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Overdue.md", "---\nwhen: 2037-11-02T09:30\ntask: open\n---\n");
        await alice.CreateAsync("Window.md", "---\nwhen: 2037-11-09\nuntil: 2037-11-10\ntask: open\n---\n");

        var day = await PostOkAsync(alice, "Work", "later", new { path = "/Work/Overdue.md", part = "postpone", step = "day" });
        Assert.Equal("2037-11-12T09:30", S(day["note"]!["entry"], "when"));
        var week = await PostOkAsync(alice, "Work", "later", new { path = "/Work/Overdue.md", part = "postpone", step = "week" });
        Assert.Equal("2037-11-19T09:30", S(week["note"]!["entry"], "when"));
        var picked = await PostOkAsync(alice, "Work", "later", new { path = "/Work/Overdue.md", part = "postpone", date = "2037-12-01" });
        Assert.Equal("2037-12-01T09:30", S(picked["note"]!["entry"], "when"));

        var more = await PostOkAsync(alice, "Work", "later", new { path = "/Work/Window.md", part = "moreTime", step = "week" });
        Assert.Equal(("2037-11-09", "2037-11-18"), (S(more["note"]!["entry"], "when"), S(more["note"]!["entry"], "until")));

        foreach (var body in new object[]
        {
            new { path = "/Work/Overdue.md", part = "sideways", step = "day" },
            new { path = "/Work/Overdue.md", part = "postpone", step = "year" },
            new { path = "/Work/Overdue.md", part = "postpone" },
            new { path = "/Work/Overdue.md", part = "postpone", step = "day", date = "2037-12-02" },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await PostRefusedAsync(alice, "Work", "later", body)).Status);
        }
    }

    [Fact]
    public async Task Later_is_only_for_something_still_to_do()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Board/Board.md", "---\nkumbu-board: 1\ncolumns:\n  - Doing\n  - Done: [archivable]\n  - Dropped: [reason]\n---\n# Roadmap\n");
        await alice.CreateAsync("Board/Release.md", "---\ncolumn: Doing\nrank: a1\ndue: 2037-11-02\n---\n");
        await alice.CreateAsync("Board/Shipped.md", "---\ncolumn: Done\nrank: a1\ndue: 2037-11-02\n---\n");
        await alice.CreateAsync("Board/Idea.md", "---\ncolumn: Dropped\nrank: a1\ndue: 2037-11-02\nreason: no\n---\n");
        await alice.CreateAsync("Done.md", "---\nwhen: 2037-11-02\ntask: closed\nclosed: 2037-11-03\n---\n");
        await alice.CreateAsync("Meeting.md", "---\nwhen: 2037-11-02\n---\n");
        await alice.CreateAsync("Plain.md", "# Plain\n");

        foreach (var path in new[] { "/Work/Board/Shipped.md", "/Work/Board/Idea.md", "/Work/Done.md", "/Work/Meeting.md", "/Work/Plain.md" })
        {
            var (status, error) = await PostRefusedAsync(alice, "Work", "later", new { path, part = "postpone", step = "day" });
            Assert.True((HttpStatusCode.BadRequest, "Later is for something still to do.") == (status, error), $"{path}: {status} {error}");
        }

        // An open card's due moves through the board, its column stays.
        var card = await PostOkAsync(alice, "Work", "later", new { path = "/Work/Board/Release.md", part = "postpone", step = "day" });
        Assert.Equal(("2037-11-12", "Doing"), (S(card["note"]!["entry"], "when"), S(card["note"]!["entry"], "column")));
        Assert.Contains("due: 2037-11-12", await ContentAsync(alice, "Board/Release.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Today_creates_the_day_note_in_Calendar()
    {
        var alice = await NewAsync(Api, vault: "Work");

        var today = await PostOkAsync(alice, "Work", "today");
        Assert.Equal("/Work/Calendar/2037-11-11.md", S(today, "path"));
        Assert.True(today["note"]!["entry"]!["byName"]!.GetValue<bool>());
        Assert.Equal("# Wednesday, 11 November 2037\n\n", await ContentAsync(alice, "Calendar/2037-11-11.md"));
        var again = await PostOkAsync(alice, "Work", "today");
        Assert.Equal((S(today, "path"), S(today, "version")), (S(again, "path"), S(again, "version")));

        var week = await PostOkAsync(alice, "Work", "this-week");
        Assert.Equal("/Work/Calendar/2037-W46.md", S(week, "path"));
        Assert.Equal("# Week 46, 9–15 November 2037\n\n", await ContentAsync(alice, "Calendar/2037-W46.md"));

        // A day note elsewhere in the vault, changed last, is today's.
        _time.Advance(TimeSpan.FromMinutes(1));
        await alice.CreateAsync("Diary/2037-11-11.md", "# Wednesday\n");
        Assert.Equal("/Work/Diary/2037-11-11.md", S(await PostOkAsync(alice, "Work", "today"), "path"));
    }

    [Fact]
    public async Task A_days_plus_creates_an_entry_with_its_time()
    {
        var alice = await NewAsync(Api, vault: "Work");

        var made = await PostOkAsync(alice, "Work", "entries", new { day = "2037-11-12", title = "Zahnarzt", time = "14:00" }, HttpStatusCode.Created);
        Assert.Equal("/Work/Calendar/Zahnarzt.md", S(made, "path"));
        Assert.Equal(("14:00", "dated"), (S(made["note"]!["entry"], "time"), S(made["note"]!["entry"], "kind")));
        Assert.Equal("---\nwhen: 2037-11-12T14:00\n---\n# Zahnarzt\n", await ContentAsync(alice, "Calendar/Zahnarzt.md"));

        var task = await PostOkAsync(alice, "Work", "entries", new { day = "2037-11-12", title = "Zahnarzt", task = true, upcoming = "3d" }, HttpStatusCode.Created);
        Assert.Equal("/Work/Calendar/Zahnarzt 2.md", S(task, "path"));
        Assert.Equal("---\nwhen: 2037-11-12\ntask: open\nupcoming: 3d\n---\n# Zahnarzt\n", await ContentAsync(alice, "Calendar/Zahnarzt 2.md"));

        foreach (var body in new object[] { new { day = "2037-11-12", title = "" }, new { day = "Friday", title = "X" }, new { day = "2037-11-12", title = "X", time = "2pm" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await PostRefusedAsync(alice, "Work", "entries", body)).Status);
    }

    [Fact]
    public async Task On_a_card_close_is_400_naming_the_board()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Board/Board.md", "---\nkumbu-board: 1\ncolumns:\n  - Doing\n  - Done: [archivable]\n---\n# Roadmap\n");
        const string card = "---\ncolumn: Doing\nrank: a1\ndue: 2037-11-20\n---\n";
        await alice.CreateAsync("Board/Release.md", card);

        var (status, error) = await PostRefusedAsync(alice, "Work", "close", new { path = "/Work/Board/Release.md" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("/Work/Board/Release.md is a card; on a card the column is the status: move it with the board tool", error);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRefusedAsync(alice, "Work", "move", new { path = "/Work/Board/Release.md", when = "2037-11-21" })).Status);
        Assert.Equal(card, await ContentAsync(alice, "Board/Release.md"));
    }

    [Fact]
    public async Task A_path_in_another_vault_is_400()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Task.md", "---\nwhen: 2037-11-12\ntask: open\n---\n");

        foreach (var path in new[] { "/Home/Task.md", "/Task.md", "/Work/Folder", "Work/Task.md" })
        {
            var (status, _) = await PostRefusedAsync(alice, "Work", "close", new { path });
            Assert.True(status == HttpStatusCode.BadRequest, path);
        }

        var (_, error) = await PostRefusedAsync(alice, "Work", "close", new { path = "/Home/Task.md" });
        Assert.Equal("/Home/Task.md is not a note in Work.", error);
        Assert.Equal("---\nwhen: 2037-11-12\ntask: open\n---\n", await ContentAsync(alice, "Task.md"));
    }
}
