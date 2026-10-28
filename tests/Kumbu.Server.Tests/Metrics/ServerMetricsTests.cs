// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Json;
using Kumbu.Server.Accounts;
using Kumbu.Server.Search;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;

namespace Kumbu.Server.Tests.Metrics;

[Collection("app")]
public sealed class ServerMetricsTests(KumbuAppFactory app)
{
    private const string Password = "correct horse battery staple";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unlocking_a_vault_is_counted()
    {
        var before = Scrape.Value(await Scrape.TextAsync(app), "kumbu_vault_unlocks_total");
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        var users = app.Services.GetRequiredService<UserCatalog>();
        await users.CreateAsync(username, Password, Roles.User, Ct);
        var (user, userKey, readAt) = (await users.TrySignInAsync(username, Password, Ct))!.Value;

        await app.Services.GetRequiredService<VaultCatalog>().CreateForUserAsync("Work", user.Id, userKey, Ct, readAt);

        var after = await Scrape.TextAsync(app);
        Assert.True(Scrape.Value(after, "kumbu_vault_unlocks_total") >= before + 1);
        Assert.True(Scrape.Value(after, "kumbu_vaults_unlocked") >= 1);
    }

    [Fact]
    public async Task Sign_ins_are_counted_by_outcome()
    {
        const string failure = "kumbu_sign_ins_total{outcome=\"failure\"}";
        const string success = "kumbu_sign_ins_total{outcome=\"success\"}";
        var before = await Scrape.TextAsync(app);
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await app.PublicApi.Services.GetRequiredService<UserCatalog>().CreateAsync(username, Password, Roles.User, Ct);

        await SignInAsync(username, "wrong password entirely");
        await SignInAsync(username, Password);

        var after = await Scrape.TextAsync(app);
        Assert.True(Scrape.Value(after, failure) >= Scrape.Value(before, failure) + 1);
        Assert.True(Scrape.Value(after, success) >= Scrape.Value(before, success) + 1);
    }

    private async Task SignInAsync(string username, string password)
    {
        using var client = app.PublicApi.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
            BaseAddress = new Uri("https://localhost"),
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session") { Content = JsonContent.Create(new { username, password }) };
        request.Headers.Add("Origin", KumbuAppFactory.PublicUrl);
        request.Headers.Add("X-Forwarded-For", $"198.20.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        await client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Indexing_counts_batches_and_shows_the_backlog()
    {
        const string series = "kumbu_embedding_batches_total{provider_kind=\"internal\"}";
        var before = Scrape.Value(await Scrape.TextAsync(app), series);
        await using var client = await app.ConnectAsync();
        await client.CallToolAsync("write", new Dictionary<string, object?> { ["path"] = "/Ideas/zz-server-metrics.md", ["content"] = "Something to embed.\n" }, cancellationToken: Ct);

        await app.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);
        // Other tests list the sample vault: leave it as it was.
        await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = "rm /Ideas/zz-server-metrics.md" }, cancellationToken: Ct);

        var after = await Scrape.TextAsync(app);
        Assert.True(Scrape.Value(after, series) >= before + 1);
        Assert.Contains("kumbu_embedding_backlog ", after);
        Assert.Contains("kumbu_embedding_batch_duration_seconds_count{provider_kind=\"internal\"}", after);
    }
}

[Collection("mongo")]
public sealed class VaultLockMetricsTests(MongoFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_idle_vault_lock_is_counted()
    {
        const string series = "kumbu_vault_locks_total{reason=\"idle\"}";
        var before = Scrape.Value(await ExportAsync(), series);
        var world = new Vaults.VaultWorld(fixture.NewDatabase());
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var catalog = world.Catalog(idleMinutes: 15, time: clock);
        await catalog.CreateAsync("Work", Vaults.VaultWorld.Token, (_, _) => Task.CompletedTask, Ct);

        clock.Advance(TimeSpan.FromMinutes(16));
        catalog.Sweep();

        Assert.Equal(0, catalog.OpenCount);
        Assert.True(Scrape.Value(await ExportAsync(), series) >= before + 1);
    }

    private static async Task<string> ExportAsync()
    {
        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream, Ct);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
