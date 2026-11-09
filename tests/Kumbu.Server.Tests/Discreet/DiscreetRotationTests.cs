// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Api;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Discreet;

/// <summary>The list of discreet vaults is sealed again whenever the user key changes: re-encryption and recovery
/// (discreet vaults design, Decision 3).</summary>
[Collection("app")]
public sealed class DiscreetRotationTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMongoCollection<UserDocument> Users => app.PublicApi.Services.GetRequiredService<IMongoDatabase>().GetCollection<UserDocument>("users");

    /// <summary>Marks <c>Tagebuch</c> discreet as the web app does: revealed, marked, hidden again.</summary>
    private static async Task MarkTagebuchAsync(HiddenWorld w)
    {
        await w.RevealAsync();
        using (var marked = await w.Alice.SendAsync(HttpMethod.Put, "/api/account/discreet/vaults/Tagebuch", new { discreet = true }))
        {
            Assert.Equal(HttpStatusCode.NoContent, marked.StatusCode);
        }

        using var hidden = await w.Alice.SendAsync(HttpMethod.Post, "/api/account/discreet/hide");
        Assert.Equal(HttpStatusCode.NoContent, hidden.StatusCode);
        Assert.Equal([("Familie", false), ("Skills", false), ("Work", false)], await w.VaultsAsync());
    }

    private static async Task ReencryptAsync(RestClient client)
    {
        using var reencrypted = await client.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password = RestClient.Password });
        Assert.Equal(HttpStatusCode.OK, reencrypted.StatusCode);
    }

    /// <summary>The user key the session <paramref name="secret"/> holds now.</summary>
    private async Task<byte[]> KeyOfAsync(string secret) =>
        (await app.PublicApi.Services.GetRequiredService<Sessions>().ResolveAsync(secret, Ct, touch: false))!.Value.UserKey;

    /// <summary>The ids of her list, opened with <paramref name="userKey"/>; null if it doesn't open with it.</summary>
    private async Task<IReadOnlySet<string>?> ListAsync(string userId, byte[] userKey)
    {
        var sealedList = await Users.Find(u => u.Id == userId).Project(u => u.DiscreetVaults).FirstAsync(Ct);
        Assert.NotNull(sealedList);
        return DiscreetVaults.OpenList(userId, userKey, sealedList);
    }

    [Fact]
    public async Task Reencrypting_reseals_the_list_and_the_kept_session_still_hides_the_vault()
    {
        var w = await HiddenWorld.NewAsync(app.PublicApi);
        await MarkTagebuchAsync(w);

        await ReencryptAsync(w.Alice);

        var newKey = await KeyOfAsync(w.Alice.Session);
        Assert.NotEqual(w.Alice.UserKey, newKey);
        Assert.Equal([w.TagebuchId], await ListAsync(w.Alice.User.Id, newKey));
        // A list left under the old key wouldn't open, and would hide Work too.
        Assert.Equal([("Familie", false), ("Skills", false), ("Work", false)], await w.VaultsAsync());
    }

    [Fact]
    public async Task A_recovery_reseals_the_list_and_a_new_session_still_hides_the_vault()
    {
        var w = await HiddenWorld.NewAsync(app.PublicApi);
        await MarkTagebuchAsync(w);
        string recoveryKey;
        using (var made = await w.Alice.SendAsync(HttpMethod.Post, "/api/recovery-key", new { currentPassword = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, made.StatusCode);
            recoveryKey = (await RestClient.JsonAsync(made))["recoveryKey"]!.GetValue<string>();
        }

        using (var recovered = await RestClient.SendAsync(
            app.PublicApi, HttpMethod.Post, "/api/recover",
            new { username = w.Alice.Username, recoveryKey, newPassword = RestClient.Password }, session: null, bearer: null))
        {
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        }

        var again = w with { Alice = await w.Alice.SignInAgainAsync() };
        var newKey = await KeyOfAsync(again.Alice.Session);
        Assert.NotEqual(w.Alice.UserKey, newKey);
        Assert.Equal([w.TagebuchId], await ListAsync(w.Alice.User.Id, newKey));
        Assert.Equal([("Familie", false), ("Skills", false), ("Work", false)], await again.VaultsAsync());
    }

    [Fact]
    public async Task A_mark_between_prepare_and_apply_conflicts_and_the_retry_keeps_it()
    {
        var w = await HiddenWorld.NewAsync(app.PublicApi);
        await MarkTagebuchAsync(w);
        var rotation = app.PublicApi.Services.GetRequiredService<UserKeyRotation>();
        var discreet = app.PublicApi.Services.GetRequiredService<DiscreetVaults>();
        var prepared = 0;
        rotation.FaultAt = async at =>
        {
            if (at != "before-apply") return;
            // Once: on the retry the rotation is prepared from the list written here.
            if (Interlocked.Increment(ref prepared) != 1) return;
            await discreet.SetAsync(w.Alice.User.Id, w.Alice.UserKey, w.FamilieId, true, [], Ct);
        };
        try
        {
            await ReencryptAsync(w.Alice);
        }
        finally
        {
            rotation.FaultAt = null;
        }

        Assert.Equal(2, prepared);
        var newKey = await KeyOfAsync(w.Alice.Session);
        Assert.Equal(new HashSet<string> { w.TagebuchId, w.FamilieId }, await ListAsync(w.Alice.User.Id, newKey));
        Assert.Equal([("Skills", false), ("Work", false)], await w.VaultsAsync());
    }

    [Fact]
    public async Task A_user_without_a_list_rotates_without_one()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");

        await ReencryptAsync(alice);

        var doc = await app.PublicApi.Services.GetRequiredService<IMongoDatabase>().GetCollection<BsonDocument>("users")
            .Find(new BsonDocument("_id", alice.User.Id)).FirstAsync(Ct);
        Assert.False(doc.Contains("discreetVaults"));
        using var vaults = await alice.GetAsync("/api/vaults");
        Assert.Equal(HttpStatusCode.OK, vaults.StatusCode);
        Assert.Equal(["Skills", "Work"], (await RestClient.JsonAsync(vaults)).AsArray().Select(v => v!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal));
    }
}
