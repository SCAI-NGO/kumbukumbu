// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Core.Calendar;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Discreet;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Calendar;

/// <summary>The calendar in a web session that hides its discreet vaults (spec §8): nothing of theirs reaches an answer,
/// not even a count or a colour.</summary>
[Collection("app")]
public sealed class CalendarDiscreetTests(KumbuAppFactory app) : IAsyncDisposable
{
    // A Wednesday ahead of the real clock (see CalendarApiTests).
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

    private static readonly string[] WhatsOnQueries = ["/api/calendar/whats-on", "/api/calendar/whats-on?count=true", "/api/calendar/whats-on?tag=privat"];

    /// <summary>A world with entries in Work; with <paramref name="hiddenEntries"/>, also an overdue task and a date that
    /// can't be read in Tagebuch, and an overdue task in Bob's Familie.</summary>
    private async Task<HiddenWorld> WorldAsync(bool hiddenEntries)
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.Alice.CreateAsync("Meeting.md", "---\nwhen: 2037-11-11\n---\n");
        await w.Alice.CreateAsync("Kaputt.md", "---\nwhen: tomorrow\n---\n");
        if (hiddenEntries)
        {
            var tagebuch = w.In("Tagebuch");
            await tagebuch.CreateAsync("Frist.md", "---\nwhen: 2037-11-02\ntask: open\n---\n#privat\n");
            await tagebuch.CreateAsync("Unklar.md", "---\nwhen: someday\n---\n#privat\n");
            await w.Bob.CreateAsync("Erbe.md", "---\nwhen: 2037-11-03\ntask: open\n---\n#privat\n");
        }

        return w;
    }

    private static async Task<string[]> AnswersAsync(HiddenWorld w)
    {
        var answers = new List<string>();
        foreach (var query in WhatsOnQueries)
        {
            using var response = await w.Alice.GetAsync(query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            answers.Add(await response.Content.ReadAsStringAsync(Ct));
        }

        return [.. answers];
    }

    [Fact]
    public async Task Whats_on_says_nothing_of_a_hidden_vault()
    {
        var with = await WorldAsync(hiddenEntries: true);
        var without = await WorldAsync(hiddenEntries: false);
        await with.HideAsync();
        await without.HideAsync();

        var hidden = await AnswersAsync(with);
        Assert.Equal(await AnswersAsync(without), hidden);
        Assert.DoesNotContain("Tagebuch", hidden[0], StringComparison.Ordinal);
        Assert.Contains("\"overdue\":false", hidden[1], StringComparison.Ordinal);

        await with.RevealAsync();
        using var response = await with.Alice.GetAsync("/api/calendar/whats-on");
        var shown = await JsonAsync(response);
        var items = shown["items"]!.AsArray();
        Assert.Contains(items, i => i!["path"]!.GetValue<string>() == "/Tagebuch/Frist.md" && i["state"]!.GetValue<string>() == "overdue");
        Assert.Contains(items, i => i!["path"]!.GetValue<string>() == "/Familie/Erbe.md");
        Assert.True(shown["overdue"]!.GetValue<bool>());
        Assert.Equal(2, shown["couldntRead"]!.GetValue<int>());
        Assert.Contains("Tagebuch", shown["couldntReadVaults"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Contains(shown["vaults"]!.AsArray(), v => v!["vault"]!.GetValue<string>() == "Tagebuch" && v["overdue"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Every_write_on_a_hidden_vault_is_the_unknown_vaults_404()
    {
        var w = await WorldAsync(hiddenEntries: true);
        await w.HideAsync();

        var writes = new (string Action, Func<string, object?>? Body)[]
        {
            ("set", v => new { path = $"/{v}/Frist.md", when = "2037-11-20" }),
            ("move", v => new { path = $"/{v}/Frist.md", when = "2037-11-20" }),
            ("close", v => new { path = $"/{v}/Frist.md" }),
            ("close", v => new { path = $"/{v}/Nothing.md", reason = "gone" }),
            ("reopen", v => new { path = $"/{v}/Frist.md" }),
            ("unset", v => new { path = $"/{v}/Frist.md" }),
            ("later", v => new { path = $"/{v}/Frist.md", part = "postpone", step = "day" }),
            ("later", v => new { path = $"/{v}/Frist.md", part = "sideways" }),
            ("today", null),
            ("this-week", null),
            ("entries", _ => new { day = "2037-11-12", title = "Neu" }),
            ("entries", _ => new { day = "never", title = "" }),
        };
        foreach (var vault in new[] { "Tagebuch", "Familie" })
        {
            foreach (var (action, body) in writes) await w.AsNowhereAsync(vault, HttpMethod.Post, $"/api/vaults/{{v}}/calendar/{action}", body);
        }

        // Asked from a vault it shows, a hidden vault's note is no more than a path elsewhere.
        await w.AsNowhereAsync("Tagebuch", HttpMethod.Post, "/api/vaults/Work/calendar/close", v => new { path = $"/{v}/Frist.md" });

        var tagebuch = w.In("Tagebuch");
        await w.RevealAsync();
        Assert.Equal("---\nwhen: 2037-11-02\ntask: open\n---\n#privat\n", (await tagebuch.ReadAsync("Frist.md"))["content"]!.GetValue<string>());
        using var closed = await w.Alice.SendAsync(HttpMethod.Post, "/api/vaults/Tagebuch/calendar/close", new { path = "/Tagebuch/Frist.md" });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
    }

    [Fact]
    public async Task A_hidden_vaults_calendar_and_entry_are_404_as_an_unknown_vault()
    {
        var w = await WorldAsync(hiddenEntries: true);
        await w.HideAsync();

        foreach (var vault in new[] { "Tagebuch", "Familie" })
        {
            await w.AsNowhereAsync(vault, HttpMethod.Get, "/api/vaults/{v}/calendar");
            await w.AsNowhereAsync(vault, HttpMethod.Get, "/api/vaults/{v}/calendar?month=2037-11&tag=privat");
            await w.AsNowhereAsync(vault, HttpMethod.Get, "/api/vaults/{v}/calendar/entry?path=/{v}/Frist.md");
            await w.AsNowhereAsync(vault, HttpMethod.Get, "/api/vaults/{v}/calendar/entry?path=/{v}/Nothing.md");
        }

        // Asked from a vault it shows, a hidden vault's note is no more than a path elsewhere.
        await w.AsNowhereAsync("Tagebuch", HttpMethod.Get, "/api/vaults/Work/calendar/entry?path=/{v}/Frist.md");

        await w.RevealAsync();
        using var calendar = await w.Alice.GetAsync("/api/vaults/Tagebuch/calendar?week=2037-W45");
        using var entry = await w.Alice.GetAsync("/api/vaults/Tagebuch/calendar/entry?path=/Tagebuch/Frist.md");
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (calendar.StatusCode, entry.StatusCode));
        Assert.Equal("overdue", (await JsonAsync(entry))["entry"]!["state"]!.GetValue<string>());
    }
}
