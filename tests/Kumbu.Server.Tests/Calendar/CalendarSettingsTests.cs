// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Calendar;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Discreet;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Calendar;

/// <summary>The person's time zone (<c>/api/account/calendar</c>) and her last view per calendar
/// (<c>/api/vaults/{vault}/calendar/view</c>), and the zone her agents' <c>sh calendar</c> uses.</summary>
[Collection("app")]
public sealed class CalendarSettingsTests(KumbuAppFactory app) : IAsyncDisposable
{
    private const string ViewSentence = "Only a signed-in person keeps a calendar view.";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 23, 30, 0, TimeSpan.Zero));

    private WebApplicationFactory<Program>? _api;

    /// <summary>The server's zone UTC, whatever the machine's, and the clock at 15 November 2026, 23:30 UTC.</summary>
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

    private IMongoCollection<UserDocument> Users => Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<UserDocument>("users");

    private static string ViewUrl(string vault) => $"/api/vaults/{Uri.EscapeDataString(vault)}/calendar/view";

    private static async Task<JsonNode> OkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static async Task<(string View, bool Grid)> ViewAsync(RestClient user, string vault)
    {
        using var response = await user.GetAsync(ViewUrl(vault));
        var view = await OkAsync(response);
        return (view["view"]!.GetValue<string>(), view["grid"]!.GetValue<bool>());
    }

    private static async Task SetViewAsync(RestClient user, string vault, string view, bool grid)
    {
        using var response = await user.SendAsync(HttpMethod.Put, ViewUrl(vault), new { view, grid });
        var answer = await OkAsync(response);
        Assert.Equal((view, grid), (answer["view"]!.GetValue<string>(), answer["grid"]!.GetValue<bool>()));
    }

    private static async Task<HttpResponseMessage> SetZoneAsync(RestClient user, string? zone, string? bearer = null) =>
        await user.SendAsync(HttpMethod.Put, "/api/account/calendar", new { zone }, bearer: bearer);

    [Fact]
    public async Task The_zone_starts_empty_and_is_kept()
    {
        var alice = await NewAsync(Api);

        using var first = await alice.GetAsync("/api/account/calendar");
        var before = await OkAsync(first);
        using var set = await SetZoneAsync(alice, "Europe/Vienna");
        var answer = await OkAsync(set);
        using var second = await alice.GetAsync("/api/account/calendar");
        var after = await OkAsync(second);

        Assert.Null(before["zone"]);
        Assert.Equal(TimeZoneInfo.Utc.Id, before["serverZone"]!.GetValue<string>());
        Assert.Equal("Europe/Vienna", answer["zone"]!.GetValue<string>());
        Assert.Equal("Europe/Vienna", after["zone"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(after["serverZone"]!.GetValue<string>()));
    }

    [Fact]
    public async Task An_unknown_zone_is_400_and_null_clears()
    {
        var alice = await NewAsync(Api);
        using (var set = await SetZoneAsync(alice, "America/New_York")) Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        using var unknown = await SetZoneAsync(alice, "Mars/Olympus_Mons");
        using var path = await SetZoneAsync(alice, "../../etc/passwd");
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (unknown.StatusCode, path.StatusCode));
        Assert.Equal(
            "Mars/Olympus_Mons is not a time zone; choose one from the list (an IANA name such as Europe/Vienna).", await ErrorAsync(unknown));
        using (var kept = await alice.GetAsync("/api/account/calendar")) Assert.Equal("America/New_York", (await OkAsync(kept))["zone"]!.GetValue<string>());

        using var cleared = await SetZoneAsync(alice, null);
        Assert.Null((await OkAsync(cleared))["zone"]);
        using var read = await alice.GetAsync("/api/account/calendar");
        Assert.Null((await OkAsync(read))["zone"]);
        Assert.Null(await Users.Find(u => u.Id == alice.User.Id).Project(u => u.CalendarZone).FirstOrDefaultAsync(Ct));
    }

    [Fact]
    public async Task An_api_key_cannot_change_the_zone()
    {
        var alice = await NewAsync(Api);
        var key = await alice.KeyAsync("agent" + Guid.NewGuid().ToString("N")[..6], KeyRight.Write);

        using var read = await alice.GetAsync("/api/account/calendar", bearer: key);
        using var set = await SetZoneAsync(alice, "Europe/Vienna", bearer: key);

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (read.StatusCode, set.StatusCode));
        Assert.Null(await Users.Find(u => u.Id == alice.User.Id).Project(u => u.CalendarZone).FirstOrDefaultAsync(Ct));

        // The view: a key reads the default, and keeps none.
        using var view = await alice.GetAsync(ViewUrl(alice.Vault), bearer: key);
        Assert.Equal("week", (await OkAsync(view))["view"]!.GetValue<string>());
        using var setView = await alice.SendAsync(HttpMethod.Put, ViewUrl(alice.Vault), new { view = "month", grid = true }, bearer: key);
        Assert.Equal(HttpStatusCode.Forbidden, setView.StatusCode);
        Assert.Equal(ViewSentence, await ErrorAsync(setView));
        Assert.Equal(("week", false), await ViewAsync(alice, alice.Vault));
    }

    [Fact]
    public async Task A_view_that_does_not_open_falls_back_to_week()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await SetViewAsync(alice, "Work", "month", grid: true);

        await Users.UpdateOneAsync(
            u => u.Id == alice.User.Id, Builders<UserDocument>.Update.Set(u => u.CalendarViews, RandomNumberGenerator.GetBytes(80)), cancellationToken: Ct);

        Assert.Equal(("week", false), await ViewAsync(alice, "Work"));
        // And a new one is kept over it.
        await SetViewAsync(alice, "Work", "month", grid: false);
        Assert.Equal(("month", false), await ViewAsync(alice, "Work"));
    }

    [Fact]
    public async Task A_view_other_than_week_or_month_is_400()
    {
        var alice = await NewAsync(Api, vault: "Work");

        using var response = await alice.SendAsync(HttpMethod.Put, ViewUrl("Work"), new { view = "year", grid = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(("week", false), await ViewAsync(alice, "Work"));
    }

    [Fact]
    public async Task The_download_preference_is_untouched_by_the_zone()
    {
        var alice = await NewAsync(Api);

        using (var set = await SetZoneAsync(alice, "Europe/Vienna")) Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        using (var cleared = await SetZoneAsync(alice, null)) Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        using var preferences = await alice.GetAsync("/api/account/preferences");
        Assert.True((await OkAsync(preferences))["askPasswordForDownloads"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_view_is_per_user_and_per_vault()
    {
        var alice = await NewAsync(Api, vault: "Work");
        using (var created = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Home" })) Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var bob = await NewAsync(Api);
        await alice.ShareWithAsync(bob, "Work");

        await SetViewAsync(alice, "Work", "month", grid: true);
        await SetViewAsync(alice, "Home", "week", grid: false);

        Assert.Equal(("month", true), await ViewAsync(alice, "Work"));
        Assert.Equal(("week", false), await ViewAsync(alice, "Home"));
        Assert.Equal(("week", false), await ViewAsync(bob, "Work"));
    }

    [Fact]
    public async Task The_view_is_sealed()
    {
        var alice = await NewAsync(Api, vault: "Werkstatt");
        var vaultId = await alice.VaultIdAsync();

        await SetViewAsync(alice, "Werkstatt", "month", grid: true);

        var stored = await Users.Find(u => u.Id == alice.User.Id).Project(u => u.CalendarViews).FirstOrDefaultAsync(Ct);
        Assert.NotNull(stored);
        var raw = Encoding.UTF8.GetString(stored);
        Assert.DoesNotContain(vaultId, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Werkstatt", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("month", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_hidden_discreet_vault_answers_404_for_its_view()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await SetViewAsync(w.Alice, "Tagebuch", "month", grid: true);
        await w.HideAsync();

        await w.AsNowhereAsync("Tagebuch", HttpMethod.Get, "/api/vaults/{v}/calendar/view");
        await w.AsNowhereAsync("Tagebuch", HttpMethod.Put, "/api/vaults/{v}/calendar/view", _ => new { view = "week", grid = false });
        await w.AsNowhereAsync("Familie", HttpMethod.Get, "/api/vaults/{v}/calendar/view");

        await w.RevealAsync();
        Assert.Equal(("month", true), await ViewAsync(w.Alice, "Tagebuch"));
    }

    [Fact]
    public async Task Sh_calendar_uses_the_users_zone()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Certificate.md", "---\nwhen: 2026-11-15\ntask: open\n---\n");
        var key = await alice.KeyAsync("agent" + Guid.NewGuid().ToString("N")[..6], KeyRight.Read);

        // 23:30 UTC on the 15th: still the 15th on the server's UTC.
        var (utcError, utc) = await alice.ShAsync("calendar notifications", key);
        Assert.False(utcError, utc);
        Assert.Contains("ongoing", utc, StringComparison.Ordinal);
        Assert.DoesNotContain("overdue", utc, StringComparison.Ordinal);

        // 00:30 on the 16th in Vienna: the day is over.
        using (var set = await SetZoneAsync(alice, "Europe/Vienna")) Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var (viennaError, vienna) = await alice.ShAsync("calendar notifications", key);
        Assert.False(viennaError, vienna);
        Assert.Contains("overdue · 1 day  Certificate  /Work/Certificate.md", vienna, StringComparison.Ordinal);
    }
}
