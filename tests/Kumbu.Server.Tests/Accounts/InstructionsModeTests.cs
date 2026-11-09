// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Core.Skills;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Tests.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>"Skills in the instructions": a plain field of a key's or connection's document, set by the user's session
/// without the password window. It is no right: it never changes what a credential opens.</summary>
[Collection("app")]
public sealed class InstructionsModeTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private IMongoCollection<BsonDocument> KeyDocs => app.Database.GetCollection<BsonDocument>("api_keys");

    private IMongoCollection<BsonDocument> ConnectionDocs => app.Database.GetCollection<BsonDocument>("connections");

    private async Task<string> NewKeyAsync(RestClient user, string name = "agent")
    {
        using var created = await user.SendAsync(HttpMethod.Post, "/api/keys", new { name, vaults = new Dictionary<string, string> { [user.Vault] = "write" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await RestClient.JsonAsync(created))["apiKey"]!["id"]!.GetValue<string>();
    }

    private async Task<BsonDocument> KeyDocAsync(string id) => await KeyDocs.Find(new BsonDocument("_id", id)).SingleAsync(Ct);

    private static async Task<string> ViewedAsync(RestClient user, string path, string id)
    {
        using var list = await user.GetAsync(path);
        var entry = (await RestClient.JsonAsync(list)).AsArray().Single(k => k!["id"]!.GetValue<string>() == id)!;
        return entry["instructions"]!.GetValue<string>();
    }

    [Fact]
    public async Task A_new_key_lists_and_hints()
    {
        var user = await RestClient.NewAsync(Api);
        var id = await NewKeyAsync(user);

        Assert.Equal("list", await ViewedAsync(user, "/api/keys", id));
        Assert.False((await KeyDocAsync(id)).Contains("instructions"));
    }

    [Fact]
    public async Task Hint_only_is_stored_in_the_clear()
    {
        var user = await RestClient.NewAsync(Api);
        var id = await NewKeyAsync(user);
        var meta = (await KeyDocAsync(id))["meta"].AsBsonBinaryData.Bytes;

        using var put = await user.SendAsync(HttpMethod.Put, $"/api/keys/{id}/instructions", new { mode = "hint" });

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var document = await KeyDocAsync(id);
        Assert.Equal("hint", document["instructions"].AsString);
        Assert.Equal(meta, document["meta"].AsBsonBinaryData.Bytes);
        Assert.Equal("hint", await ViewedAsync(user, "/api/keys", id));
        Assert.Equal(InstructionsMode.HintOnly, await Api.Services.GetRequiredService<ApiKeys>().InstructionsAsync(id, Ct));
    }

    [Fact]
    public async Task List_removes_the_field()
    {
        var user = await RestClient.NewAsync(Api);
        var id = await NewKeyAsync(user);
        using (var hint = await user.SendAsync(HttpMethod.Put, $"/api/keys/{id}/instructions", new { mode = "hint" })) Assert.Equal(HttpStatusCode.NoContent, hint.StatusCode);

        using var list = await user.SendAsync(HttpMethod.Put, $"/api/keys/{id}/instructions", new { mode = "list" });

        Assert.Equal(HttpStatusCode.NoContent, list.StatusCode);
        Assert.False((await KeyDocAsync(id)).Contains("instructions"));
        Assert.Equal("list", await ViewedAsync(user, "/api/keys", id));
        Assert.Equal(InstructionsMode.ListAndHint, await Api.Services.GetRequiredService<ApiKeys>().InstructionsAsync(id, Ct));
    }

    [Fact]
    public async Task It_needs_no_password_window()
    {
        await using var api = Api.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(_time);
        }));
        var user = await RestClient.NewAsync(api, remember: true);
        var id = await NewKeyAsync(user);
        _time.Advance(TimeSpan.FromMinutes(31));

        using (var refused = await user.SendAsync(HttpMethod.Post, "/api/keys", new { name = "late", vaults = new Dictionary<string, string> { [user.Vault] = "read" } }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using var put = await user.SendAsync(HttpMethod.Put, $"/api/keys/{id}/instructions", new { mode = "hint" });

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
    }

    [Fact]
    public async Task A_device_key_has_no_such_setting()
    {
        var user = await RestClient.NewAsync(Api);
        await user.KeyAsync("phone", KeyRight.Write, ApiKeyKind.Device);
        using var list = await user.GetAsync("/api/keys");
        var id = (await RestClient.JsonAsync(list)).AsArray().Single(k => k!["kind"]!.GetValue<string>() == "device")!["id"]!.GetValue<string>();

        using var put = await user.SendAsync(HttpMethod.Put, $"/api/keys/{id}/instructions", new { mode = "hint" });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.False((await KeyDocAsync(id)).Contains("instructions"));
    }

    [Fact]
    public async Task An_unknown_mode_is_400()
    {
        var user = await RestClient.NewAsync(Api);
        var id = await NewKeyAsync(user);

        using var put = await user.SendAsync(HttpMethod.Put, $"/api/keys/{id}/instructions", new { mode = "everything" });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.False((await KeyDocAsync(id)).Contains("instructions"));
    }

    [Fact]
    public async Task Another_users_key_is_404()
    {
        var alice = await RestClient.NewAsync(Api);
        var bob = await RestClient.NewAsync(Api);
        var id = await NewKeyAsync(alice);

        using var put = await bob.SendAsync(HttpMethod.Put, $"/api/keys/{id}/instructions", new { mode = "hint" });
        using var unknown = await bob.SendAsync(HttpMethod.Put, "/api/keys/nosuchkey/instructions", new { mode = "hint" });

        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.False((await KeyDocAsync(id)).Contains("instructions"));
    }

    [Fact]
    public async Task An_api_key_cant_change_it()
    {
        var user = await RestClient.NewAsync(Api);
        var key = await user.KeyAsync("agent", KeyRight.Write);
        var id = await NewKeyAsync(user, "other");

        using var put = await user.SendAsync(HttpMethod.Put, $"/api/keys/{id}/instructions", new { mode = "hint" }, bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.False((await KeyDocAsync(id)).Contains("instructions"));
    }

    [Fact]
    public async Task Connections_take_it_too()
    {
        var user = await RestClient.NewAsync(Api);
        var other = await RestClient.NewAsync(Api);
        using var created = await user.SendAsync(HttpMethod.Post, "/api/connections", new { name = "phone", vaults = new Dictionary<string, string> { [user.Vault] = "read" } });
        var id = (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
        Assert.Equal("list", await ViewedAsync(user, "/api/connections", id));
        var meta = (await ConnectionDocs.Find(new BsonDocument("_id", id)).SingleAsync(Ct))["meta"].AsBsonBinaryData.Bytes;

        using var put = await user.SendAsync(HttpMethod.Put, $"/api/connections/{id}/instructions", new { mode = "hint" });
        using var foreign = await other.SendAsync(HttpMethod.Put, $"/api/connections/{id}/instructions", new { mode = "list" });
        using var bad = await user.SendAsync(HttpMethod.Put, $"/api/connections/{id}/instructions", new { mode = "x" });

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var document = await ConnectionDocs.Find(new BsonDocument("_id", id)).SingleAsync(Ct);
        Assert.Equal("hint", document["instructions"].AsString);
        Assert.Equal(meta, document["meta"].AsBsonBinaryData.Bytes);
        Assert.Equal("hint", await ViewedAsync(user, "/api/connections", id));
        Assert.Equal(InstructionsMode.HintOnly, await Api.Services.GetRequiredService<Connections>().InstructionsAsync(id, Ct));

        using var back = await user.SendAsync(HttpMethod.Put, $"/api/connections/{id}/instructions", new { mode = "list" });
        Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        Assert.False((await ConnectionDocs.Find(new BsonDocument("_id", id)).SingleAsync(Ct)).Contains("instructions"));
    }
}
