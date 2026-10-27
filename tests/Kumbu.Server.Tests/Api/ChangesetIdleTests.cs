// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Api;

/// <summary>A person's changeset (the session's, a device key's) ends after 2 idle minutes, an agent's after 10 (concept §4).</summary>
[Collection("app")]
public sealed class ChangesetIdleTests(KumbuAppFactory app) : IAsyncDisposable
{
    // Whole seconds, as the other timed tests: MongoDB keeps milliseconds.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<Program> Api => _api ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    public async ValueTask DisposeAsync()
    {
        if (_api is not null) await _api.DisposeAsync();
    }

    private static async Task<string> PutAsync(RestClient user, string content, string? version, string? bearer)
    {
        using var response = await user.SendAsync(HttpMethod.Put, user.Url("plan.md"), new { content, version }, bearer: bearer);
        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        return (await JsonAsync(response))["version"]!.GetValue<string>();
    }

    /// <summary>Three writes by one writer: the second a new changeset, the third three minutes after it. The number of versions in the history.</summary>
    private async Task<int> VersionsAfterAPauseOfThreeMinutesAsync(Func<RestClient, Task<string?>> writer)
    {
        var user = await NewAsync(Api);
        var bearer = await writer(user);
        var v1 = await PutAsync(user, "one\n", null, bearer);
        _time.Advance(TimeSpan.FromMinutes(15));
        var v2 = await PutAsync(user, "two\n", v1, bearer);
        _time.Advance(TimeSpan.FromMinutes(3));
        await PutAsync(user, "three\n", v2, bearer);

        using var history = await user.GetAsync(user.Url("plan.md", "history"));
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        return (await JsonAsync(history)).AsArray().Count;
    }

    [Fact]
    public async Task The_web_apps_session_is_a_person() =>
        Assert.Equal(3, await VersionsAfterAPauseOfThreeMinutesAsync(_ => Task.FromResult<string?>(null)));

    [Fact]
    public async Task An_obsidian_device_is_a_person() =>
        Assert.Equal(3, await VersionsAfterAPauseOfThreeMinutesAsync(async u => await u.KeyAsync("Arbeit – Obsidian Laptop", KeyRight.Write, ApiKeyKind.Device)));

    [Fact]
    public async Task An_agent_key_keeps_ten_minutes() =>
        Assert.Equal(2, await VersionsAfterAPauseOfThreeMinutesAsync(async u => await u.KeyAsync("laptop", KeyRight.Write)));
}
