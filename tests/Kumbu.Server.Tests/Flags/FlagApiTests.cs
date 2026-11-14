// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Core.Calendar;
using Kumbu.Core.Tags;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Discreet;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Flags;

/// <summary>A note's flag for the web app: read with its place in the calendar, set through <see cref="FlagService"/> with the
/// caller's rights.</summary>
[Collection("app")]
public sealed class FlagApiTests(KumbuAppFactory app) : IAsyncDisposable
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

    private static string Url(string vault) => $"/api/vaults/{Uri.EscapeDataString(vault)}/flag";

    private static async Task<JsonNode> GetOkAsync(RestClient user, string vault, string path)
    {
        using var response = await user.GetAsync($"{Url(vault)}?path={Uri.EscapeDataString(path)}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await JsonAsync(response);
    }

    private static async Task<JsonNode> PostOkAsync(RestClient user, string vault, object body)
    {
        using var response = await user.SendAsync(HttpMethod.Post, Url(vault), body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return await JsonAsync(response);
    }

    private static async Task<(HttpStatusCode Status, string Error)> PostRefusedAsync(RestClient user, string vault, object body)
    {
        using var response = await user.SendAsync(HttpMethod.Post, Url(vault), body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.False(response.IsSuccessStatusCode, text);
        return (response.StatusCode, JsonNode.Parse(text)!["error"]!.GetValue<string>());
    }

    private static async Task<string> ContentAsync(RestClient user, string path) => (await user.ReadAsync(path))["content"]!.GetValue<string>();

    private static string? S(JsonNode? node, string key) => node![key]?.GetValue<string>();

    private static bool B(JsonNode? node, string key) => node![key]!.GetValue<bool>();

    [Fact]
    public async Task Sets_and_reads_a_flag()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Plan.md", "---\ntags: [plan]\n---\n# Plan\n");

        var before = await GetOkAsync(alice, "Work", "/Work/Plan.md");
        Assert.Equal((null, null, false, true), (S(before, "flag"), S(before, "inText"), B(before, "openTask"), B(before, "canWrite")));

        var set = await PostOkAsync(alice, "Work", new { path = "/Work/Plan.md", flag = "red" });

        Assert.Equal("/Work/Plan.md", S(set, "path"));
        Assert.Equal((await alice.ReadAsync("Plan.md"))["version"]!.GetValue<string>(), S(set, "version"));
        Assert.Equal(("red", null, true), (S(set["note"], "flag"), S(set["note"], "inText"), B(set["note"], "canWrite")));
        Assert.Equal("---\ntags: [plan, flag/red]\n---\n# Plan\n", await ContentAsync(alice, "Plan.md"));
        Assert.Equal("red", S(await GetOkAsync(alice, "Work", "/Work/Plan.md"), "flag"));

        Assert.Equal(HttpStatusCode.BadRequest, (await PostRefusedAsync(alice, "Work", new { path = "/Work/Plan.md", flag = "orange" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRefusedAsync(alice, "Work", new { path = "/Work/Plan.md", flag = "done", calendar = "later" })).Status);
        Assert.Equal((HttpStatusCode.NotFound, "/Work/Gone.md: No such note"), await PostRefusedAsync(alice, "Work", new { path = "/Work/Gone.md", flag = "red" }));
    }

    [Fact]
    public async Task Removing_gives_the_note_back()
    {
        var alice = await NewAsync(Api, vault: "Work");
        const string note = "---\n# mine\ntitle: Plan\n---\nLine one\n\nLine two  \n";
        await alice.CreateAsync("Plan.md", note);

        await PostOkAsync(alice, "Work", new { path = "/Work/Plan.md", flag = "important" });
        var removed = await PostOkAsync(alice, "Work", new { path = "/Work/Plan.md", flag = (string?)null });

        Assert.Null(S(removed["note"], "flag"));
        Assert.Equal(note, await ContentAsync(alice, "Plan.md"));
    }

    [Fact]
    public async Task A_read_only_member_sees_the_flag_and_can_not_set_it()
    {
        var alice = await NewAsync(Api, vault: "Work");
        var bob = await NewAsync(Api, vault: "Own");
        await alice.ShareWithAsync(bob, "Shared", MemberRole.Read);
        const string note = "---\ntags: [flag/green]\n---\n# Plan\n";
        await alice.CreateAsync("Plan.md", note);

        var seen = await GetOkAsync(bob, "Shared", "/Shared/Plan.md");
        Assert.Equal(("green", false), (S(seen, "flag"), B(seen, "canWrite")));

        Assert.Equal(HttpStatusCode.Forbidden, (await PostRefusedAsync(bob, "Shared", new { path = "/Shared/Plan.md", flag = "red" })).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostRefusedAsync(bob, "Shared", new { path = "/Shared/Plan.md", flag = (string?)null })).Status);
        Assert.Equal(note, await ContentAsync(alice, "Plan.md"));
    }

    [Fact]
    public async Task A_hidden_discreet_vault_answers_as_an_unknown_one()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.HideAsync();

        foreach (var vault in new[] { "Tagebuch", "Familie" })
        {
            await w.AsNowhereAsync(vault, HttpMethod.Get, "/api/vaults/{v}/flag?path=/{v}/geheim.md");
            await w.AsNowhereAsync(vault, HttpMethod.Get, "/api/vaults/{v}/flag?path=/{v}/Nothing.md");
            await w.AsNowhereAsync(vault, HttpMethod.Get, "/api/vaults/{v}/flag?path=/Elsewhere/x.md");
            await w.AsNowhereAsync(vault, HttpMethod.Post, "/api/vaults/{v}/flag", v => new { path = $"/{v}/geheim.md", flag = "red" });
            await w.AsNowhereAsync(vault, HttpMethod.Post, "/api/vaults/{v}/flag", v => new { path = $"/{v}/Nothing.md", flag = "orange" });
            await w.AsNowhereAsync(vault, HttpMethod.Post, "/api/vaults/{v}/flag", v => new { path = "/Elsewhere/x.md", flag = "red" });
        }

        // Asked from a vault it shows, a hidden vault's note is no more than a path elsewhere.
        await w.AsNowhereAsync("Tagebuch", HttpMethod.Get, "/api/vaults/Work/flag?path=/{v}/geheim.md");
        await w.AsNowhereAsync("Tagebuch", HttpMethod.Post, "/api/vaults/Work/flag", v => new { path = $"/{v}/geheim.md", flag = "red" });

        var tagebuch = w.In("Tagebuch");
        await w.RevealAsync();
        Assert.Equal($"# Geheim\n\n{HiddenWorld.TagebuchWord} #privat\n\nSiehe [[alt]]\n", await ContentAsync(tagebuch, "geheim.md"));
        await PostOkAsync(w.Alice, "Tagebuch", new { path = "/Tagebuch/geheim.md", flag = "red" });
    }

    [Fact]
    public async Task A_conflict_is_409()
    {
        var alice = await NewAsync(Api, vault: "Work");
        const string broken = "---\ntags: [plan\n---\n# Broken\n";
        await alice.CreateAsync("Broken.md", broken);

        var (status, error) = await PostRefusedAsync(alice, "Work", new { path = "/Work/Broken.md", flag = "red" });

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.StartsWith("this note's frontmatter does not parse", error, StringComparison.Ordinal);
        Assert.Equal(broken, await ContentAsync(alice, "Broken.md"));
    }

    [Fact]
    public async Task Mark_done_closes_the_task()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Renew.md", "---\nwhen: 2037-11-20\ntask: open\n---\n# Renew\n");
        Assert.True(B(await GetOkAsync(alice, "Work", "/Work/Renew.md"), "openTask"));

        var done = await PostOkAsync(alice, "Work", new { path = "/Work/Renew.md", flag = "done", calendar = "close" });

        Assert.Equal(("done", false), (S(done["note"], "flag"), B(done["note"], "openTask")));
        Assert.Equal("---\nwhen: 2037-11-20\ntask: closed\ntags: [flag/done]\nclosed: 2037-11-14\n---\n# Renew\n", await ContentAsync(alice, "Renew.md"));
        using var entry = await alice.GetAsync("/api/vaults/Work/calendar/entry?path=/Work/Renew.md");
        Assert.Equal("closed", S((await JsonAsync(entry))["entry"], "task"));

        // Close comes only with done; Undo opens it again with the flag it had.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRefusedAsync(alice, "Work", new { path = "/Work/Renew.md", flag = "red", calendar = "close" })).Status);
        var undone = await PostOkAsync(alice, "Work", new { path = "/Work/Renew.md", flag = (string?)null, calendar = "reopen" });
        Assert.True(B(undone["note"], "openTask"));
        Assert.Equal("---\nwhen: 2037-11-20\ntask: open\n---\n# Renew\n", await ContentAsync(alice, "Renew.md"));
    }

    [Theory]
    [InlineData("/Other/Plan.md")]
    [InlineData("/Work")]
    [InlineData("/Work/picture.png")]
    [InlineData("Work/../Plan.md")]
    public async Task A_path_outside_the_vault_is_400(string path)
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Plan.md", "# Plan\n");

        using var read = await alice.GetAsync($"{Url("Work")}?path={Uri.EscapeDataString(path)}");
        Assert.Equal(HttpStatusCode.BadRequest, read.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRefusedAsync(alice, "Work", new { path, flag = "red" })).Status);
    }

    [Fact]
    public async Task The_flag_in_the_text_is_named()
    {
        var alice = await NewAsync(Api, vault: "Work");
        const string note = "# Plan\n\nUrgent #flag/Red\n";
        await alice.CreateAsync("Plan.md", note);

        var read = await GetOkAsync(alice, "Work", "/Work/Plan.md");
        Assert.Equal(("red", "#flag/Red"), (S(read, "flag"), S(read, "inText")));

        var (status, error) = await PostRefusedAsync(alice, "Work", new { path = "/Work/Plan.md", flag = (string?)null });
        Assert.Equal((HttpStatusCode.BadRequest, "This flag is written in the note's text (#flag/Red); change it there."), (status, error));
        Assert.Equal(note, await ContentAsync(alice, "Plan.md"));
    }

    [Fact]
    public async Task A_card_is_no_open_task_for_the_tick()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Board/Board.md", "---\nkumbu-board: 1\ncolumns:\n  - Ideas\n  - Done\n---\n# Work\n");
        await alice.CreateAsync("Board/X.md", "---\ncolumn: Ideas\nrank: V\nwhen: 2037-11-20\ntask: open\n---\nText\n");

        Assert.False(B(await GetOkAsync(alice, "Work", "/Work/Board/X.md"), "openTask"));
    }
}
