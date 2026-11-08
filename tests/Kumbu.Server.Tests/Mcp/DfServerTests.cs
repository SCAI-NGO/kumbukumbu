// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Mcp;

/// <summary><c>df</c> on the server (WebDAV design §3.12): the caller's own vaults, with the quota's figure.</summary>
[Collection("app")]
public sealed class DfServerTests(KumbuAppFactory app) : IAsyncDisposable
{
    private readonly List<WebApplicationFactory<Program>> _own = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var api in _own) await api.DisposeAsync();
    }

    /// <summary>A server of its own (its own quota cache) on <paramref name="time"/>.</summary>
    private WebApplicationFactory<Program> Own(FakeTimeProvider time)
    {
        var api = app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(time);
        }));
        _own.Add(api);
        return api;
    }

    /// <summary>How often the quota counted <paramref name="ownerId"/>; a count waits for <paramref name="gate"/> if given.</summary>
    private static Func<int> Counts(StorageQuota quota, string ownerId, Task? gate = null, TaskCompletionSource? started = null)
    {
        var counts = 0;
        quota.Querying = async (kind, of) =>
        {
            if (kind != "count" || of != ownerId) return;
            Interlocked.Increment(ref counts);
            started?.TrySetResult();
            if (gate is not null) await gate;
        };
        return () => Volatile.Read(ref counts);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    [Fact]
    public async Task A_members_key_sees_only_her_own_vaults_in_df()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", new string('g', 50_000));
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await bob.CreateAsync("notes/one.md", "One\n");
        await bob.CreateAsync("notes/two.md", "Two\n");
        await alice.ShareWithAsync(bob, "garten", MemberRole.Write);
        var key = (await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Claude",
            new Dictionary<string, KeyRight> { [await bob.VaultIdOfSharedAsync("garten")] = KeyRight.Write, [await bob.VaultIdAsync()] = KeyRight.Write }, Ct)).Key;
        Assert.False((await bob.ShAsync("rm /Work/notes/two.md", key)).IsError);
        await Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<UserDocument>("users")
            .UpdateOneAsync(u => u.Id == bob.User.Id, Builders<UserDocument>.Update.Set(u => u.Quota, 2L << 30), cancellationToken: Ct);

        var (isError, text) = await bob.ShAsync("df -b", key);

        Assert.False(isError, text);
        Assert.DoesNotContain("garten", text, StringComparison.OrdinalIgnoreCase);
        var lines = text.Split('\n');
        Assert.StartsWith("Vault ", lines[0], StringComparison.Ordinal);
        var row = lines.Select(l => Regex.Match(l, @"^/Work +(\d+) +(\d+) +(\d+)$")).FirstOrDefault(m => m.Success);
        Assert.True(row is not null, text);
        // Current notes only: two.md is in the trash; its bytes still count.
        Assert.Equal("1", row.Groups[2].Value);
        Assert.Equal("0", row.Groups[3].Value);
        Assert.True(long.Parse(row.Groups[1].Value) > "One\nTwo\n".Length);
        // His vault Skills, which the key doesn't open, counts against the limit too.
        Assert.Matches(@"^Total +\d+  \(vaults this key opens; your other vaults count against the 2147483648 limit too\)$", lines[^1]);

        var shared = await bob.ShAsync("df /garten", key);
        Assert.True(shared.IsError);
        Assert.Equal("df: /garten: df shows only vaults you own; a shared vault counts against its owner.", shared.Text);

        // Alice's own key sees her vault and its 50 KB, which Bob's writes there would count against.
        var alicesKey = await alice.KeyAsync("Claude", KeyRight.Read);
        var hers = (await alice.ShAsync("df -b", alicesKey)).Text;
        Assert.Matches(@"\n/Gartenarbeit +\d{5,} +1 +0\n", hers);
    }

    /// <summary>A key for one of two vaults its user owns: the row is that vault's, and the total names the limit without a
    /// percentage, since the other vault counts against it too. It learns that there is another vault, not its size.</summary>
    [Fact]
    public async Task A_key_for_some_of_the_vaults_gets_no_percentage()
    {
        var carol = await RestClient.NewAsync(Api, vault: "Work");
        using (var second = await carol.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Privat" })) Assert.True(second.IsSuccessStatusCode);
        await Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<UserDocument>("users")
            .UpdateOneAsync(u => u.Id == carol.User.Id, Builders<UserDocument>.Update.Set(u => u.Quota, 2L << 30), cancellationToken: Ct);
        var key = await carol.KeyAsync("Claude", KeyRight.Read);

        var text = (await carol.ShAsync("df", key)).Text;

        Assert.DoesNotContain("Privat", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%", text, StringComparison.Ordinal);
        Assert.Matches(@"\nTotal +\d+(\.\d)? [KMG]?B  \(vaults this key opens; your other vaults count against the 2\.0 GB limit too\)$", text);
        // A key for both: the percentage is back.
        var vaults = await Api.Services.GetRequiredService<VaultCatalog>().OpenForUserAsync(carol.User.Id, carol.UserKey, Ct);
        var both = (await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            carol.User, carol.UserKey, DateTime.UtcNow, "Codex", vaults.ToDictionary(v => v.Id, _ => KeyRight.Read), Ct)).Key;
        var whole = (await carol.ShAsync("df", both)).Text;
        Assert.Contains("\n/Privat ", whole, StringComparison.Ordinal);
        Assert.Matches(@"\(\d+ %\)$", whole);
    }

    /// <summary>df counts at most every 30 seconds per owner, and calls that meet a count under way share it.</summary>
    [Fact]
    public async Task Two_df_within_30_seconds_count_once()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var api = Own(time);
        var quota = api.Services.GetRequiredService<StorageQuota>();
        var user = await RestClient.NewAsync(api, vault: "Work");
        var key = await user.KeyAsync("Claude", KeyRight.Read);
        var counts = Counts(quota, user.User.Id);

        Assert.False((await user.ShAsync("df", key)).IsError);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False((await user.ShAsync("df", key)).IsError);
        Assert.Equal(1, counts());
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False((await user.ShAsync("df", key)).IsError);
        Assert.Equal(2, counts());

        time.Advance(StorageQuota.ShownFor);
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        counts = Counts(quota, user.User.Id, gate.Task, started);
        var first = quota.RecentFigureAsync(user.User.Id, Ct);
        await started.Task.WaitAsync(Ct);
        var second = quota.RecentFigureAsync(user.User.Id, Ct);
        gate.SetResult();
        Assert.Equal((await first).Used, (await second).Used);
        Assert.Equal(1, counts());
    }
}
