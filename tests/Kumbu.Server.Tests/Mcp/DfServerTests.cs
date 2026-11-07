// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Mcp;

/// <summary><c>df</c> on the server (WebDAV design §3.12): the caller's own vaults, with the quota's figure.</summary>
[Collection("app")]
public sealed class DfServerTests(KumbuAppFactory app)
{
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
        var notes = (await bob.ShAsync("rg --files /Work", key)).Text.Split('\n').Length;
        Assert.Equal(notes, int.Parse(row.Groups[2].Value));
        Assert.Equal("0", row.Groups[3].Value);
        Assert.True(long.Parse(row.Groups[1].Value) > "One\nTwo\n".Length);
        Assert.Matches(@"^Total +\d+  of 2147483648 \(\d+ %\)$", lines[^1]);

        var shared = await bob.ShAsync("df /garten", key);
        Assert.True(shared.IsError);
        Assert.Equal("df: /garten: df shows only vaults you own; a shared vault counts against its owner.", shared.Text);

        // Alice's own key sees her vault and its 50 KB, which Bob's writes there would count against.
        var alicesKey = await alice.KeyAsync("Claude", KeyRight.Read);
        var hers = (await alice.ShAsync("df -b", alicesKey)).Text;
        Assert.Matches(@"\n/Gartenarbeit +\d{5,} +1 +0\n", hers);
    }
}
