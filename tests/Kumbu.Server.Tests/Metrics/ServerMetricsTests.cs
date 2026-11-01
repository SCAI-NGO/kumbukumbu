// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Json;
using Kumbu.Server.Accounts;
using Kumbu.Server.Search;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    [Fact]
    public async Task An_accepted_invitation_counts_once()
    {
        const string accepted = "kumbu_invitations_total{outcome=\"accepted\"}";
        const string created = "kumbu_invitations_total{outcome=\"created\"}";
        const string refused = "kumbu_invitations_total{outcome=\"refused\"}";
        var memberships = app.PublicApi.Services.GetRequiredService<Kumbu.Server.Sharing.Memberships>();
        var alice = await Api.RestClient.NewAsync(app.PublicApi, vault: "Gartenarbeit");
        var vaultId = await Kumbu.Server.Tests.Sharing.Share.VaultIdAsync(alice);
        // The smallest of three increments: tests of other collections may accept invitations at the same time, which
        // only ever adds.
        var deltas = new List<(double Accepted, double Created)>();
        for (var i = 0; i < 3; i++)
        {
            var bob = await Api.RestClient.NewAsync(app.PublicApi, vault: "Work");
            var before = await Scrape.TextAsync(app);
            var invite = await memberships.InviteAsync(alice.User.Id, alice.UserKey, vaultId, "Bob", Kumbu.Server.Sharing.MemberRole.Read, TimeSpan.FromDays(7), false, Ct);
            await memberships.AcceptAsync(bob.User.Id, bob.UserKey, invite.Secret, "garten", Ct);
            // Used up: a second acceptance is no invitation, and counts nowhere.
            await Assert.ThrowsAsync<Kumbu.Server.Sharing.InvitationNotFoundException>(() => memberships.AcceptAsync(bob.User.Id, bob.UserKey, invite.Secret, "beete", Ct));
            var after = await Scrape.TextAsync(app);
            deltas.Add((Scrape.Value(after, accepted) - Scrape.Value(before, accepted), Scrape.Value(after, created) - Scrape.Value(before, created)));
        }

        Assert.Equal((1.0, 1.0), (deltas.Min(d => d.Accepted), deltas.Min(d => d.Created)));

        // A refused acceptance (the alias taken) counts as refused, not as accepted.
        var carol = await Api.RestClient.NewAsync(app.PublicApi, vault: "garten");
        var third = await memberships.InviteAsync(alice.User.Id, alice.UserKey, vaultId, "Carol", Kumbu.Server.Sharing.MemberRole.Read, TimeSpan.FromDays(7), false, Ct);
        var start = await Scrape.TextAsync(app);
        await Assert.ThrowsAsync<VaultNameTakenException>(() => memberships.AcceptAsync(carol.User.Id, carol.UserKey, third.Secret, "garten", Ct));
        var end = await Scrape.TextAsync(app);
        Assert.True(Scrape.Value(end, refused) >= Scrape.Value(start, refused) + 1);
    }

    [Fact]
    public async Task Gauges_count_shared_vaults_and_memberships()
    {
        const string shared = "kumbu_shared_vaults";
        string Status(string s) => $"kumbu_memberships{{status=\"{s}\"}}";
        var memberships = app.PublicApi.Services.GetRequiredService<Kumbu.Server.Sharing.Memberships>();
        var alice = await Api.RestClient.NewAsync(app.PublicApi, vault: "Gartenarbeit");
        var bob = await Api.RestClient.NewAsync(app.PublicApi, vault: "Work");
        var carol = await Api.RestClient.NewAsync(app.PublicApi, vault: "Notizen");
        // A host of this process whose database is away (as the start-up tests' are): it holds up neither the scrape nor
        // the other databases' figures.
        await using var away = new AwayHost();
        Assert.NotNull(away.Server);
        var before = await Scrape.TextAsync(app);

        var bobs = await Kumbu.Server.Tests.Sharing.Share.ShareWithAsync(alice, bob, "garten");
        await Kumbu.Server.Tests.Sharing.Share.ShareWithAsync(alice, carol, "beete");
        await memberships.InviteAsync(alice.User.Id, alice.UserKey, await Kumbu.Server.Tests.Sharing.Share.VaultIdAsync(alice), "Dora", Kumbu.Server.Sharing.MemberRole.Read, TimeSpan.FromDays(7), false, Ct);
        await memberships.RevokeAsync(await Kumbu.Server.Tests.Sharing.Share.VaultIdAsync(alice), bobs, Ct);
        var after = await Scrape.TextAsync(app);

        // One vault shared (with Carol still), one active, one pending, one revoked.
        Assert.Equal(
            (1.0, 1.0, 1.0, 1.0, 0.0),
            (Scrape.Value(after, shared) - Scrape.Value(before, shared), Scrape.Value(after, Status("active")) - Scrape.Value(before, Status("active")),
             Scrape.Value(after, Status("pending")) - Scrape.Value(before, Status("pending")), Scrape.Value(after, Status("revoked")) - Scrape.Value(before, Status("revoked")),
             Scrape.Value(after, Status("left")) - Scrape.Value(before, Status("left"))));
    }
}

/// <summary>A server whose MongoDB is never reachable; it starts, since nothing at start needs the database.</summary>
internal sealed class AwayHost : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Kumbu:Mongo:ConnectionString", "mongodb://localhost:1");
        builder.UseSetting("Kumbu:Mongo:Database", "away");
        builder.UseSetting("Kumbu:DevToken", "");
        builder.UseSetting("Kumbu:Seed:Path", "");
        builder.UseSetting("Kumbu:Metrics:Url", "http://127.0.0.1:0");
        builder.ConfigureLogging(l => l.ClearProviders());
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
