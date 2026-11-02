// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.Json.Nodes;
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Search;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Search;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>
/// The owner renames her vault by a display name (rename spec §1.2, §1.4, §1.7), above all with a new vault under the
/// old name beside it: the two have one stored name and differ by id. <see cref="RestClient.Vault"/> stays
/// <c>Gartenarbeit</c>, so ids and keys are taken before the rename and later paths are spelt out.
/// </summary>
[Collection("app")]
public sealed class VaultRenameTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    private static Task<HttpResponseMessage> RenameAsync(RestClient user, string vault, string name) =>
        user.SendAsync(HttpMethod.Put, $"/api/vaults/{Uri.EscapeDataString(vault)}/name", new { name });

    private static async Task<string[]> NamesAsync(RestClient user)
    {
        using var response = await user.GetAsync("/api/vaults");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response)).AsArray().Select(v => v!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Alice with <c>Gartenarbeit</c>, its <c>plan.md</c>, and the vault's id, key and an API key, all taken before any rename.</summary>
    private async Task<(RestClient Alice, string Id, byte[] VaultKey, string Key)> AliceAsync(WebApplicationFactory<Program>? api = null)
    {
        var alice = await RestClient.NewAsync(api ?? Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "Beete\n");
        var id = await alice.VaultIdAsync();
        return (alice, id, await alice.VaultKeyAsync(), await KeyForAsync(alice, id, "Work"));
    }

    /// <summary>An API key of <paramref name="user"/> for the vault <paramref name="vaultId"/>, named by id rather than by name.</summary>
    private static async Task<string> KeyForAsync(RestClient user, string vaultId, string name) =>
        (await user.Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            user.User, user.UserKey, DateTime.UtcNow, name, new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, Ct)).Key;

    /// <summary>Renames Alice's vault to <c>Garten</c> and makes a new <c>Gartenarbeit</c> with its own <c>plan.md</c>; the new vault's id.</summary>
    private static async Task<string> RenamedBesideNewAsync(RestClient alice)
    {
        using (var renamed = await RenameAsync(alice, "Gartenarbeit", "Garten"))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        using (var created = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Gartenarbeit" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using (var written = await alice.SendAsync(HttpMethod.Put, "/api/notes/Gartenarbeit/plan.md", new { content = "Andere\n" }))
        {
            Assert.Equal(HttpStatusCode.Created, written.StatusCode);
        }

        return await alice.Api.Services.GetRequiredService<VaultCatalog>().FindForUserAsync(alice.User.Id, alice.UserKey, "Gartenarbeit", Ct)
            ?? throw new InvalidOperationException("The new vault isn't found.");
    }

    private static async Task<string> ContentAsync(RestClient user, string path, string? bearer = null)
    {
        using var response = await user.GetAsync(path, bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await RestClient.JsonAsync(response))["content"]!.GetValue<string>();
    }

    private static async Task<string> TreeAsync(RestClient user, string vault)
    {
        using var response = await user.GetAsync($"/api/vaults/{vault}/tree");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private async Task<JsonObject> RawMetaAsync(string id, byte[] vaultKey)
    {
        var doc = await app.Database.GetCollection<VaultDocument>("vaults").Find(v => v.Id == id).SingleAsync(Ct);
        return JsonNode.Parse(VaultCrypto.Open(VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), doc.Meta, $"vault:{id}"))!.AsObject();
    }

    private async Task<byte[]> NameIdAsync(string id) =>
        (await Catalog.Slots.Find(s => s.Vault == id && s.Kind == SlotDocument.User).SingleAsync(Ct)).NameId!;

    private async Task<bool> ExistsAsync(string id) =>
        await app.Database.GetCollection<VaultDocument>("vaults").Find(v => v.Id == id).AnyAsync(Ct);

    [Fact]
    public async Task A_new_vault_may_take_the_old_name_and_they_are_two_vaults()
    {
        var (alice, id, _, _) = await AliceAsync();

        using (var renamed = await RenameAsync(alice, "Gartenarbeit", "Garten"))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        Assert.Equal(["Garten"], await NamesAsync(alice));
        using (var created = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Gartenarbeit" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        Assert.Equal(["Garten", "Gartenarbeit"], await NamesAsync(alice));
        using (var read = await alice.GetAsync("/api/notes/Garten/plan.md"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var note = await RestClient.JsonAsync(read);
            Assert.Equal(("Beete\n", "/Garten/plan.md"), (note["content"]!.GetValue<string>(), note["path"]!.GetValue<string>()));
        }

        using (var missing = await alice.GetAsync("/api/notes/Gartenarbeit/plan.md"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using (var written = await alice.SendAsync(HttpMethod.Put, "/api/notes/Gartenarbeit/plan.md", new { content = "Andere\n" }))
        {
            Assert.Equal(HttpStatusCode.Created, written.StatusCode);
        }

        Assert.Equal("Beete\n", await ContentAsync(alice, "/api/notes/Garten/plan.md"));
        Assert.Equal("Andere\n", await ContentAsync(alice, "/api/notes/Gartenarbeit/plan.md"));
        var vaults = await Catalog.OpenForUserAsync(alice.User.Id, alice.UserKey, Ct);
        Assert.Equal(2, vaults.Select(v => v.Id).Distinct().Count());
        Assert.All(vaults, v => Assert.Equal("Gartenarbeit", v.Vault.Name));
        Assert.Equal(["Garten", "Gartenarbeit"], vaults.OrderBy(v => v.Id == id ? 0 : 1).Select(v => v.Name).ToArray());
    }

    [Fact]
    public async Task Writes_to_either_stay_there()
    {
        var (alice, _, _, _) = await AliceAsync();
        await RenamedBesideNewAsync(alice);
        foreach (var (vault, text) in new[] { ("Garten", "Kompost im Garten\n"), ("Gartenarbeit", "Kompost in Gartenarbeit\n") })
        {
            using var written = await alice.SendAsync(HttpMethod.Put, $"/api/notes/{vault}/notiz.md", new { content = text });
            Assert.Equal(HttpStatusCode.Created, written.StatusCode);
        }

        await Api.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);
        using (var found = await alice.GetAsync("/api/search?q=Kompost&mode=exact"))
        {
            Assert.Equal(HttpStatusCode.OK, found.StatusCode);
            var hits = (await RestClient.JsonAsync(found))["results"]!.AsArray()
                .Select(r => (r!["path"]!.GetValue<string>(), r["snippet"]!.GetValue<string>().Contains("Gartenarbeit", StringComparison.Ordinal)))
                .Order().ToArray();
            Assert.Equal([("/Garten/notiz.md", false), ("/Gartenarbeit/notiz.md", true)], hits);
        }

        // Moves and removals in the new vault leave the renamed one as it was, and the other way round.
        var renamedTree = await TreeAsync(alice, "Garten");
        using (var moved = await alice.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Gartenarbeit/notiz.md", to = "/Gartenarbeit/alt.md" }))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        using (var removed = await alice.SendAsync(HttpMethod.Delete, "/api/notes/Gartenarbeit/plan.md"))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Equal(renamedTree, await TreeAsync(alice, "Garten"));
        Assert.Equal("Beete\n", await ContentAsync(alice, "/api/notes/Garten/plan.md"));

        var newTree = await TreeAsync(alice, "Gartenarbeit");
        using (var moved = await alice.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Garten/notiz.md", to = "/Garten/alt.md" }))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        using (var removed = await alice.SendAsync(HttpMethod.Delete, "/api/notes/Garten/plan.md"))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Equal(newTree, await TreeAsync(alice, "Gartenarbeit"));
        Assert.Equal("Kompost in Gartenarbeit\n", await ContentAsync(alice, "/api/notes/Gartenarbeit/alt.md"));
        Assert.Equal("Kompost im Garten\n", await ContentAsync(alice, "/api/notes/Garten/alt.md"));
    }

    [Fact]
    public async Task The_key_of_the_renamed_vault_does_not_reach_the_new_one()
    {
        var (alice, _, _, key) = await AliceAsync();
        await RenamedBesideNewAsync(alice);

        var (_, listed) = await alice.ShAsync("ls /", key);
        Assert.Contains("Garten/", listed);
        Assert.DoesNotContain("Gartenarbeit", listed);
        var (catError, cat) = await alice.ShAsync("cat /Garten/plan.md", key);
        Assert.False(catError, cat);
        Assert.Contains("Beete", cat);
        var (otherError, other) = await alice.ShAsync("cat /Gartenarbeit/plan.md", key);
        Assert.True(otherError, other);
        Assert.DoesNotContain("Andere", other);

        using var read = await alice.GetAsync("/api/notes/Gartenarbeit/plan.md", key);
        using var versions = await alice.GetAsync("/api/vaults/Gartenarbeit/versions", key);
        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (read.StatusCode, versions.StatusCode));
        Assert.Equal("Beete\n", await ContentAsync(alice, "/api/notes/Garten/plan.md", key));
    }

    [Fact]
    public async Task A_key_of_the_new_vault_does_not_reach_the_renamed_one()
    {
        var (alice, _, _, _) = await AliceAsync();
        var newId = await RenamedBesideNewAsync(alice);
        var key = await KeyForAsync(alice, newId, "Beet");

        var (_, listed) = await alice.ShAsync("ls /", key);
        Assert.Contains("Gartenarbeit/", listed);
        Assert.DoesNotContain("Garten/", listed);
        var (error, text) = await alice.ShAsync("cat /Garten/plan.md", key);
        Assert.True(error, text);
        Assert.DoesNotContain("Beete", text);
        using var read = await alice.GetAsync("/api/notes/Garten/plan.md", key);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal("Andere\n", await ContentAsync(alice, "/api/notes/Gartenarbeit/plan.md", key));
    }

    [Fact]
    public async Task Renaming_back_is_refused_while_the_old_name_is_taken_and_works_once_it_is_free()
    {
        var (alice, id, vaultKey, _) = await AliceAsync();
        var newId = await RenamedBesideNewAsync(alice);

        using (var taken = await RenameAsync(alice, "Garten", "Gartenarbeit"))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            Assert.Equal("You already have a vault named 'Gartenarbeit'.", await RestClient.ErrorAsync(taken));
        }

        Assert.Equal(["Garten", "Gartenarbeit"], await NamesAsync(alice));

        using (var deleted = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/delete", new { password = RestClient.Password, confirm = "Gartenarbeit" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.False(await ExistsAsync(newId));
        using (var back = await RenameAsync(alice, "Garten", "Gartenarbeit"))
        {
            Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        }

        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
        Assert.Equal("Beete\n", await ContentAsync(alice, "/api/notes/Gartenarbeit/plan.md"));
        var meta = await RawMetaAsync(id, vaultKey);
        Assert.False(meta.ContainsKey("DisplayName"), meta.ToJsonString());
        Assert.Equal("Gartenarbeit", meta["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Deleting_the_new_vault_leaves_the_renamed_one()
    {
        var (alice, id, _, _) = await AliceAsync();
        var newId = await RenamedBesideNewAsync(alice);

        using var deleted = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/delete", new { password = RestClient.Password, confirm = "Gartenarbeit" });

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal((true, false), (await ExistsAsync(id), await ExistsAsync(newId)));
        Assert.Equal(["Garten"], await NamesAsync(alice));
        Assert.Equal("Beete\n", await ContentAsync(alice, "/api/notes/Garten/plan.md"));
    }

    [Fact]
    public async Task Deleting_the_renamed_vault_leaves_the_new_one()
    {
        var (alice, id, _, _) = await AliceAsync();
        var newId = await RenamedBesideNewAsync(alice);

        using var deleted = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Garten/delete", new { password = RestClient.Password, confirm = "Garten" });

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal((false, true), (await ExistsAsync(id), await ExistsAsync(newId)));
        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
        Assert.Equal("Andere\n", await ContentAsync(alice, "/api/notes/Gartenarbeit/plan.md"));
    }

    [Fact]
    public async Task The_name_survives_a_reload()
    {
        var (alice, id, _, _) = await AliceAsync();
        await RenamedBesideNewAsync(alice);

        Catalog.Forget(Catalog.Current(id)!, new InvalidOperationException("test"));

        Assert.Equal(["Garten", "Gartenarbeit"], await NamesAsync(alice));
        Assert.Equal("Beete\n", await ContentAsync(alice, "/api/notes/Garten/plan.md"));
        Assert.Equal("Andere\n", await ContentAsync(alice, "/api/notes/Gartenarbeit/plan.md"));
        Assert.Equal("Garten", Catalog.Current(id)!.Shown);
    }

    [Fact]
    public async Task A_renamed_vault_in_its_two_key_period_keeps_its_name_beside_the_new_one()
    {
        var (alice, id, _, _) = await AliceAsync();
        await RenamedBesideNewAsync(alice);
        await Share.SetRekeyStartedAsync(app.Database, id, DateTime.UtcNow);
        try
        {
            Catalog.Forget(Catalog.Current(id)!, new InvalidOperationException("test"));

            // The placeholder under the stored name would give her two vaults of one name, and no request of hers would be served.
            using var response = await alice.GetAsync("/api/vaults");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var vaults = (await RestClient.JsonAsync(response)).AsArray()
                .Select(v => (v!["name"]!.GetValue<string>(), v["pending"]!.GetValue<bool>())).Order().ToArray();
            Assert.Equal([("Garten", true), ("Gartenarbeit", false)], vaults);
        }
        finally
        {
            await Share.SetRekeyStartedAsync(app.Database, id, null);
        }
    }

    [Fact]
    public async Task A_rename_whose_outcome_is_unknown_lets_the_object_go()
    {
        var log = new CapturedLog();
        await using var api = app.PublicApi.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var (alice, id, _, _) = await AliceAsync(api);
        var catalog = api.Services.GetRequiredService<VaultCatalog>();
        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));

        // As a cancellation or a lost answer after the commit: the exception's message may hold the name asked for.
        catalog.MetaFaultAt = point => point == "inside-rename" ? throw new InvalidOperationException("The answer to renaming to Zq7Rename was lost.") : Task.CompletedTask;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RenameVaultAsync(alice.User.Id, alice.UserKey, catalog.Current(id)!, "Zq7Rename", Ct));
        }
        finally
        {
            catalog.MetaFaultAt = null;
        }

        Assert.Null(catalog.Current(id));
        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
        Assert.DoesNotContain(log.Lines, line => line.Contains("Zq7Rename", StringComparison.Ordinal));

        // A refusal as taken is an outcome known: the object stays.
        using (var created = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Work" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var before = catalog.Current(id);
        Assert.NotNull(before);
        using (var taken = await RenameAsync(alice, "Gartenarbeit", "Work"))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        }

        Assert.Same(before, catalog.Current(id));
    }

    [Fact]
    public async Task A_rename_through_an_older_object_tells_the_current_one()
    {
        var (alice, id, _, _) = await AliceAsync();
        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
        var old = Catalog.Current(id)!;
        Catalog.Forget(old, new InvalidOperationException("test"));
        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
        var current = Catalog.Current(id)!;
        Assert.NotSame(old, current);

        await Catalog.RenameVaultAsync(alice.User.Id, alice.UserKey, old, "Garten", Ct);

        Assert.Equal("Garten", current.DisplayName);
        Assert.Same(current, Catalog.Current(id));
        Assert.Equal(["Garten"], await NamesAsync(alice));
    }

    [Fact]
    public async Task A_meta_that_does_not_open_answers_just_reencrypted()
    {
        var (alice, id, _, _) = await AliceAsync();
        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
        var before = Catalog.Current(id);
        Assert.NotNull(before);
        var nameId = await NameIdAsync(id);
        var vaults = app.Database.GetCollection<VaultDocument>("vaults");
        var meta = (await vaults.Find(v => v.Id == id).SingleAsync(Ct)).Meta;
        var flipped = (byte[])meta.Clone();
        flipped[^1] ^= 0x01;
        await vaults.UpdateOneAsync(v => v.Id == id, Builders<VaultDocument>.Update.Set(v => v.Meta, flipped), cancellationToken: Ct);
        try
        {
            using var renamed = await RenameAsync(alice, "Gartenarbeit", "Garten");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, renamed.StatusCode);
            Assert.Equal(VaultBusyException.JustReencrypted, await RestClient.ErrorAsync(renamed));
            Assert.Same(before, Catalog.Current(id));
            Assert.Equal(nameId, await NameIdAsync(id));
        }
        finally
        {
            await vaults.UpdateOneAsync(v => v.Id == id, Builders<VaultDocument>.Update.Set(v => v.Meta, meta), cancellationToken: Ct);
        }
    }

    /// <summary>The public instance with a second embedding provider, "remote", as <c>EmbeddingApiTests</c> has it.</summary>
    private WebApplicationFactory<Program> RemoteApi() => app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(services =>
    {
        services.RemoveAll<EmbeddingProviders>();
        services.AddSingleton(sp => EmbeddingProviders.Of(
            sp.GetRequiredService<IEmbeddingDriver>(), new EmbeddingProvider("remote", "Remote AI", "EU, zero data retention", new FakeEmbedder(model: "remote:qwen3@64"))));
    }));

    private Task<byte[]> StoredMetaAsync(string id) =>
        app.Database.GetCollection<VaultDocument>("vaults").Find(v => v.Id == id).Project(v => v.Meta).SingleAsync(Ct);

    private Task WriteMetaAsync(string id, byte[] meta) =>
        app.Database.GetCollection<VaultDocument>("vaults").UpdateOneAsync(v => v.Id == id, Builders<VaultDocument>.Update.Set(v => v.Meta, meta), cancellationToken: Ct);

    private static async Task RenamedToGartenAsync(RestClient alice)
    {
        using var renamed = await RenameAsync(alice, "Gartenarbeit", "Garten");
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
    }

    private static async Task ChooseAsync(RestClient alice, string vault, string provider)
    {
        using var chosen = await alice.SendAsync(HttpMethod.Put, $"/api/vaults/{vault}/embeddings", new { provider });
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
    }

    private static (string? Name, string? DisplayName, string? Embeddings) Fields(JsonObject meta) =>
        (meta["Name"]?.GetValue<string>(), meta["DisplayName"]?.GetValue<string>(), meta["Embeddings"]?.GetValue<string>());

    [Fact]
    public async Task Reencrypting_keeps_the_name()
    {
        var (alice, id, _, _) = await AliceAsync();
        await RenamedToGartenAsync(alice);

        using (var rekeyed = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Garten/rekey", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
        }

        Assert.Equal(["Garten"], await NamesAsync(alice));
        Catalog.Forget(Catalog.Current(id)!, new InvalidOperationException("test"));
        Assert.Equal(["Garten"], await NamesAsync(alice));
        Assert.Equal("Beete\n", await ContentAsync(alice, "/api/notes/Garten/plan.md"));
        // Opened by the object loaded under the new key.
        var meta = Catalog.Current(id)!.OpenMeta(await StoredMetaAsync(id));
        Assert.Equal(("Gartenarbeit", "Garten"), (meta.Name, meta.DisplayName));
    }

    [Fact]
    public async Task A_provider_change_keeps_the_name()
    {
        await using var api = RemoteApi();
        var catalog = api.Services.GetRequiredService<VaultCatalog>();
        var (alice, id, vaultKey, _) = await AliceAsync(api);
        await RenamedToGartenAsync(alice);

        await ChooseAsync(alice, "Garten", "remote");

        Assert.Equal(("Gartenarbeit", "Garten", "remote"), Fields(await RawMetaAsync(id, vaultKey)));
        catalog.Forget(catalog.Current(id)!, new InvalidOperationException("test"));
        Assert.Equal(["Garten"], await NamesAsync(alice));
        Assert.Equal("Beete\n", await ContentAsync(alice, "/api/notes/Garten/plan.md"));
    }

    [Fact]
    public async Task A_rename_keeps_the_provider_chosen()
    {
        await using var api = RemoteApi();
        var (alice, id, vaultKey, _) = await AliceAsync(api);
        await ChooseAsync(alice, "Gartenarbeit", "remote");

        await RenamedToGartenAsync(alice);

        Assert.Equal(("Gartenarbeit", "Garten", "remote"), Fields(await RawMetaAsync(id, vaultKey)));
    }

    [Fact]
    public async Task A_rename_does_not_pin_the_default_provider()
    {
        var (alice, id, vaultKey, _) = await AliceAsync();

        await RenamedToGartenAsync(alice);

        // Never chosen, it goes on following the admin's default.
        Assert.Equal(("Gartenarbeit", "Garten", null), Fields(await RawMetaAsync(id, vaultKey)));
    }

    [Fact]
    public async Task A_provider_change_through_an_older_object_keeps_the_name()
    {
        await using var api = RemoteApi();
        var catalog = api.Services.GetRequiredService<VaultCatalog>();
        var (alice, id, vaultKey, _) = await AliceAsync(api);
        var old = catalog.Current(id)!;
        catalog.Forget(old, new InvalidOperationException("test"));
        // Through a new object: the old one is told nothing.
        await RenamedToGartenAsync(alice);
        Assert.Null(old.DisplayName);

        await catalog.SetEmbeddingProviderAsync(old, "remote", Ct);

        Assert.Equal(("Gartenarbeit", "Garten", "remote"), Fields(await RawMetaAsync(id, vaultKey)));
        Assert.Equal("Garten", old.DisplayName);
        // With the stored name a second time, a reload would give her two vaults of one name.
        using (var created = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Gartenarbeit" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        catalog.Forget(catalog.Current(id)!, new InvalidOperationException("test"));
        Assert.Equal(["Garten", "Gartenarbeit"], await NamesAsync(alice));
    }

    [Fact]
    public async Task A_provider_change_on_a_meta_that_does_not_open_answers_just_reencrypted()
    {
        await using var api = RemoteApi();
        var (alice, id, vaultKey, _) = await AliceAsync(api);
        await RenamedToGartenAsync(alice);
        var meta = await StoredMetaAsync(id);
        var flipped = (byte[])meta.Clone();
        flipped[^1] ^= 0x01;
        await WriteMetaAsync(id, flipped);
        try
        {
            using var chosen = await alice.SendAsync(HttpMethod.Put, "/api/vaults/Garten/embeddings", new { provider = "remote" });

            Assert.Equal(HttpStatusCode.ServiceUnavailable, chosen.StatusCode);
            Assert.Equal(VaultBusyException.JustReencrypted, await RestClient.ErrorAsync(chosen));
            Assert.Equal(flipped, await StoredMetaAsync(id));
        }
        finally
        {
            await WriteMetaAsync(id, meta);
        }

        Assert.Equal(("Gartenarbeit", "Garten", null), Fields(await RawMetaAsync(id, vaultKey)));
    }

    [Fact]
    public async Task A_provider_change_conflicts_with_another_writer()
    {
        await using var api = RemoteApi();
        var catalog = api.Services.GetRequiredService<VaultCatalog>();
        var (alice, id, vaultKey, _) = await AliceAsync(api);
        await RenamedToGartenAsync(alice);
        var vault = catalog.Current(id)!;
        var written = 0;
        // Another writer between the read and the write: here an explicit internal, which a blind write would overwrite.
        catalog.MetaFaultAt = async point =>
        {
            if (point != "before-provider-write" || Interlocked.Exchange(ref written, 1) == 1) return;
            await WriteMetaAsync(id, vault.SealMeta(vault.OpenMeta(await StoredMetaAsync(id)) with { Embeddings = EmbeddingProviders.Internal }));
        };
        try
        {
            await Assert.ThrowsAsync<VaultBusyException>(() => catalog.SetEmbeddingProviderAsync(vault, "remote", Ct));
        }
        finally
        {
            catalog.MetaFaultAt = null;
        }

        Assert.Equal(1, written);
        Assert.Equal(("Gartenarbeit", "Garten", EmbeddingProviders.Internal), Fields(await RawMetaAsync(id, vaultKey)));
    }

    [Fact]
    public async Task A_rename_keeps_a_provider_written_meanwhile()
    {
        await using var api = RemoteApi();
        var catalog = api.Services.GetRequiredService<VaultCatalog>();
        var (alice, id, vaultKey, _) = await AliceAsync(api);
        var (written, runs) = (0, 0);
        // A direct write, outside the rename's session: the rename holds the gate a real provider change would wait on.
        catalog.MetaFaultAt = async point =>
        {
            if (point != "inside-rename") return;
            Interlocked.Increment(ref runs);
            if (Interlocked.Exchange(ref written, 1) == 1) return;
            var c = catalog.Current(id)!;
            await WriteMetaAsync(id, c.SealMeta(c.OpenMeta(await StoredMetaAsync(id)) with { Embeddings = "remote" }));
        };
        try
        {
            await RenamedToGartenAsync(alice);
        }
        finally
        {
            catalog.MetaFaultAt = null;
        }

        // The write conflicted, and the transaction ran again from the new bytes.
        Assert.Equal((1, 2), (written, runs));
        Assert.Equal(("Gartenarbeit", "Garten", "remote"), Fields(await RawMetaAsync(id, vaultKey)));
    }

    [Fact]
    public async Task Renaming_during_a_run_answers_being_reencrypted()
    {
        var (alice, id, vaultKey, _) = await AliceAsync();
        var nameId = await NameIdAsync(id);

        async Task RefusedAsync()
        {
            using var renamed = await RenameAsync(alice, "Gartenarbeit", "Garten");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, renamed.StatusCode);
            Assert.Equal(VaultBusyException.BeingReencrypted, await RestClient.ErrorAsync(renamed));
        }

        await Share.SetRekeyStartedAsync(app.Database, id, DateTime.UtcNow);
        try
        {
            await RefusedAsync();
        }
        finally
        {
            await Share.SetRekeyStartedAsync(app.Database, id, null);
        }

        Assert.Equal(("Gartenarbeit", null, null), Fields(await RawMetaAsync(id, vaultKey)));
        await using (await alice.HoldRekeyAsync())
        {
            await RefusedAsync();
        }

        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
        Assert.Equal(nameId, await NameIdAsync(id));
    }

    [Fact]
    public async Task Recovery_names_a_vault_by_its_shown_name()
    {
        var world = new VaultWorld(app.NewDatabase());
        var catalog = world.Catalog();
        string? recovery = null;
        var vault = await catalog.CreateAsync("Gartenarbeit", VaultWorld.Token, (_, key) => { recovery = key; return Task.CompletedTask; }, Ct);
        // Renamed as a test may: the meta written directly.
        var vaults = world.Db.GetCollection<VaultDocument>("vaults");
        var stored = await vaults.Find(v => v.Id == vault.Id).Project(v => v.Meta).SingleAsync(Ct);
        await vaults.UpdateOneAsync(
            v => v.Id == vault.Id, Builders<VaultDocument>.Update.Set(v => v.Meta, vault.SealMeta(vault.OpenMeta(stored) with { DisplayName = "Garten" })), cancellationToken: Ct);

        // Another process, which loads the vault from what is stored.
        Assert.Equal([("Garten", true)], await world.Catalog().RecoverAsync(recovery!, "a-new-token-0123456789abcdef", Ct));
    }

    [Fact]
    public async Task A_bug_inside_the_rename_is_not_answered_as_not_found()
    {
        var (alice, id, vaultKey, _) = await AliceAsync();
        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
        // An ArgumentException's subclass comes from a bug, not from a vault that isn't hers.
        Catalog.MetaFaultAt = point => point == "inside-rename" ? throw new ArgumentNullException("vault") : Task.CompletedTask;
        try
        {
            using var renamed = await RenameAsync(alice, "Gartenarbeit", "Garten");

            Assert.Equal(HttpStatusCode.InternalServerError, renamed.StatusCode);
        }
        finally
        {
            Catalog.MetaFaultAt = null;
        }

        // Not an outcome known: the object is let go and read again.
        Assert.Null(Catalog.Current(id));
        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
        Assert.Equal(("Gartenarbeit", null, null), Fields(await RawMetaAsync(id, vaultKey)));
    }

    [Fact]
    public async Task A_case_variant_of_the_stored_name_is_a_display_name_until_renamed_back_exactly()
    {
        var (alice, id, vaultKey, _) = await AliceAsync();

        using (var renamed = await RenameAsync(alice, "Gartenarbeit", "gartenarbeit"))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        Assert.Equal(["gartenarbeit"], await NamesAsync(alice));
        Assert.Equal(("Gartenarbeit", "gartenarbeit", null), Fields(await RawMetaAsync(id, vaultKey)));
        Assert.Equal("gartenarbeit", Catalog.Current(id)!.DisplayName);
        using (var read = await alice.GetAsync("/api/notes/gartenarbeit/plan.md"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("/gartenarbeit/plan.md", (await RestClient.JsonAsync(read))["path"]!.GetValue<string>());
        }

        using (var back = await RenameAsync(alice, "gartenarbeit", "Gartenarbeit"))
        {
            Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        }

        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
        Assert.Equal(("Gartenarbeit", null, null), Fields(await RawMetaAsync(id, vaultKey)));
        Assert.Null(Catalog.Current(id)!.DisplayName);
        Assert.Equal("Beete\n", await ContentAsync(alice, "/api/notes/Gartenarbeit/plan.md"));
    }

    [Fact]
    public async Task A_meta_without_a_display_name_is_as_before()
    {
        var (alice, id, vaultKey, _) = await AliceAsync();
        Assert.Equal(["Name"], (await RawMetaAsync(id, vaultKey)).Select(p => p.Key).ToArray());

        using (var renamed = await RenameAsync(alice, "Gartenarbeit", "Garten"))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        var meta = await RawMetaAsync(id, vaultKey);
        Assert.Equal(["DisplayName", "Name"], meta.Select(p => p.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(("Gartenarbeit", "Garten"), (meta["Name"]!.GetValue<string>(), meta["DisplayName"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_key_may_not_rename_the_vault()
    {
        var (alice, _, _, key) = await AliceAsync();

        using var refused = await alice.SendAsync(HttpMethod.Put, "/api/vaults/Gartenarbeit/name", new { name = "Garten" }, bearer: key);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
    }

    [Fact]
    public async Task No_password_is_asked()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var api = app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(time);
        }));
        var alice = await RestClient.NewAsync(api, vault: "Gartenarbeit", remember: true);
        // 31 minutes without a request: a remembered session's password window closes, the session stays.
        time.Advance(TimeSpan.FromMinutes(31));
        using (var session = await alice.GetAsync("/api/session"))
        {
            Assert.False((await RestClient.JsonAsync(session))["passwordWindowOpen"]!.GetValue<bool>());
        }

        using var renamed = await RenameAsync(alice, "Gartenarbeit", "Garten");

        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Equal(["Garten"], await NamesAsync(alice));
    }

    [Fact]
    public async Task A_members_alias_is_untouched()
    {
        var (alice, id, _, _) = await AliceAsync();
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "Beet", MemberRole.Write);
        var key = await KeyForAsync(bob, id, "Work");
        // What his agent was told so far goes out now: whatever comes after is the rename's.
        await bob.ShBlocksAsync("ls /", key);
        var activity = await KindsAsync(bob, "/api/vaults/Beet/activity");
        var feed = await KindsAsync(bob, "/api/vault-events");

        await RenamedToGartenAsync(alice);

        Assert.Equal(["Beet", "Work"], await NamesAsync(bob));
        using (var read = await bob.GetAsync("/api/notes/Beet/plan.md"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("/Beet/plan.md", (await RestClient.JsonAsync(read))["path"]!.GetValue<string>());
        }

        Assert.Equal(activity, await KindsAsync(bob, "/api/vaults/Beet/activity"));
        Assert.Equal(feed, await KindsAsync(bob, "/api/vault-events"));
        Assert.DoesNotContain(VaultEvents.VaultRenamed, await KindsAsync(bob, "/api/vaults/Beet/activity"));
        var blocks = await bob.ShBlocksAsync("ls /", key);
        Assert.Contains("Beet/", blocks[0]);
        Assert.DoesNotContain(blocks, b => b.Contains("Notice:", StringComparison.Ordinal) || b.Contains("Garten", StringComparison.Ordinal));
        // Hers names it so.
        Assert.Contains(VaultEvents.VaultRenamed, await KindsAsync(alice, "/api/vaults/Garten/activity"));
    }

    /// <summary>The kinds of the events at <paramref name="path"/> (a vault's activity, or a member's feed), as the user sees them.</summary>
    private static async Task<string[]> KindsAsync(RestClient user, string path)
    {
        using var response = await user.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await RestClient.JsonAsync(response);
        var events = body is JsonArray feed ? feed : body["events"]!.AsArray();
        return [.. events.Select(e => e!["kind"]!.GetValue<string>())];
    }

    [Fact]
    public async Task A_case_only_rename_works()
    {
        var alice = await RestClient.NewAsync(Api, vault: "beet");
        await alice.CreateAsync("plan.md", "Beete\n");
        var id = await alice.VaultIdAsync();
        var nameId = await NameIdAsync(id);

        using (var renamed = await RenameAsync(alice, "beet", "Beet"))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        Assert.Equal(["Beet"], await NamesAsync(alice));
        Assert.Equal(nameId, await NameIdAsync(id));
        Assert.Equal("Beete\n", await ContentAsync(alice, "/api/notes/Beet/plan.md"));
        // The lookup is exact: the stored name no longer reaches it.
        using var old = await alice.GetAsync("/api/notes/beet/plan.md");
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
    }

    [Fact]
    public async Task Renaming_to_the_same_name_changes_nothing()
    {
        var (alice, id, _, _) = await AliceAsync();
        await RenamedToGartenAsync(alice);
        var meta = await StoredMetaAsync(id);
        var events = await KindsAsync(alice, "/api/vaults/Garten/activity");

        using var same = await RenameAsync(alice, "Garten", "Garten");

        Assert.Equal(HttpStatusCode.NoContent, same.StatusCode);
        Assert.Equal(meta, await StoredMetaAsync(id));
        Assert.Equal(events, await KindsAsync(alice, "/api/vaults/Garten/activity"));
        Assert.Equal(["Garten"], await NamesAsync(alice));
    }

    [Fact]
    public async Task A_name_taken_by_an_alias_is_refused()
    {
        var (alice, id, vaultKey, _) = await AliceAsync();
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await bob.ShareWithAsync(alice, "Beet", MemberRole.Write);
        var nameId = await NameIdAsync(id);

        using var taken = await RenameAsync(alice, "Gartenarbeit", "beet");

        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal("You already have a vault named 'beet'.", await RestClient.ErrorAsync(taken));
        Assert.Equal(["Beet", "Gartenarbeit"], await NamesAsync(alice));
        Assert.Equal(nameId, await NameIdAsync(id));
        Assert.Equal(("Gartenarbeit", null, null), Fields(await RawMetaAsync(id, vaultKey)));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData("65 characters")]
    public async Task Invalid_names_are_refused(string name)
    {
        var (alice, id, vaultKey, _) = await AliceAsync();
        if (name == "65 characters") name = string.Concat(Enumerable.Repeat("Garten", 11))[..65];

        using var refused = await RenameAsync(alice, "Gartenarbeit", name);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
        Assert.Equal(("Gartenarbeit", null, null), Fields(await RawMetaAsync(id, vaultKey)));
    }

    [Fact]
    public async Task Renames_are_rate_limited_with_aliases()
    {
        await using var api = app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<AccountLimits>();
            s.AddSingleton(new AccountLimits(AccountLimits.RedeemFailuresPerNetwork, AccountLimits.RedeemFailuresPerServer, aliasRenamesPerUser: 2));
        }));
        var alice = await RestClient.NewAsync(api, vault: "Gartenarbeit");
        var bob = await RestClient.NewAsync(api, vault: "Work");
        var membership = await bob.ShareWithAsync(alice, "Beet", MemberRole.Write);

        // One budget: an alias rename and a vault rename use it up together.
        using (var alias = await alice.SendAsync(HttpMethod.Put, $"/api/memberships/{membership}/alias", new { alias = "Work" }))
        {
            Assert.Equal(HttpStatusCode.NoContent, alias.StatusCode);
        }

        using (var renamed = await RenameAsync(alice, "Gartenarbeit", "Garten"))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        using var limited = await RenameAsync(alice, "Garten", "Beet");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal(["Garten", "Work"], await NamesAsync(alice));
    }

    [Fact]
    public async Task The_new_name_reaches_no_log()
    {
        const string secret = "Zz.Geheim-7";
        var log = new CapturedLog();
        await using var api = app.PublicApi.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var (alice, _, _, key) = await AliceAsync(api);
        using (var created = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Work" }))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        // Her open tab and her agent's socket hear of each rename.
        using var tab = await SyncSockets.OpenAsync(api, alice);
        using var device = await SyncSockets.OpenAsync(api, alice, key);

        using (var renamed = await RenameAsync(alice, "Gartenarbeit", secret))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        using (var taken = await RenameAsync(alice, "Work", secret))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        }

        using (var again = await RenameAsync(alice, secret, "Garten"))
        {
            Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        }

        Assert.Equal(["Garten", "Work"], await NamesAsync(alice));
        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, line => line.Contains(secret, StringComparison.OrdinalIgnoreCase));
    }
}
