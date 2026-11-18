// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Server.Accounts;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Discreet;
using Kumbu.Server.Tests.Sharing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>A person's favourites (<c>/api/account/favourites</c>): commands in the clear, what names a vault sealed, and
/// only what the session lists answered and replaced.</summary>
[Collection("app")]
public sealed class FavouritesTests(KumbuAppFactory app) : IAsyncDisposable
{
    private const string Url = "/api/account/favourites";

    // Ahead of the real clock: MongoDB expires sessions by the real one.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2037, 11, 15, 10, 0, 0, TimeSpan.Zero));

    private WebApplicationFactory<Program>? _api;

    private WebApplicationFactory<Program> Api => _api ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_api is not null) await _api.DisposeAsync();
    }

    private static object Command(string id) => new { kind = "command", id };

    private static object Note(string vault, string path) => new { kind = "note", vault, path };

    private static object In(string kind, string vault) => new { kind, vault };

    private static Task<HttpResponseMessage> PutAsync(RestClient user, params object[] favourites) =>
        user.SendAsync(HttpMethod.Put, Url, new { favourites });

    /// <summary>The list a write or a read answered, each entry as <c>kind id-or-vault[/path]</c> with its <c>added</c>.</summary>
    private static async Task<(string Entry, string Added)[]> ListOfAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await JsonAsync(response))["favourites"]!.AsArray().Select(f => (Describe(f!), f!["added"]!.GetValue<string>()))];
    }

    private static string Describe(JsonNode f)
    {
        var kind = f["kind"]!.GetValue<string>();
        if (kind == "command")
        {
            Assert.Null(f["vault"]);
            return $"command {f["id"]!.GetValue<string>()}";
        }

        Assert.Null(f["id"]);
        var path = f["path"]?.GetValue<string>();
        return $"{kind} {f["vault"]!.GetValue<string>()}{(path is null ? "" : "/" + path)}";
    }

    private static async Task<(string Entry, string Added)[]> ReadAsync(RestClient user)
    {
        using var response = await user.GetAsync(Url);
        return await ListOfAsync(response);
    }

    private static async Task<string[]> EntriesAsync(RestClient user) => [.. (await ReadAsync(user)).Select(f => f.Entry)];

    private static async Task WriteAsync(RestClient user, params object[] favourites)
    {
        using var response = await PutAsync(user, favourites);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Starts_empty()
    {
        var alice = await NewAsync(Api, vault: "Work");

        Assert.Empty(await ReadAsync(alice));
    }

    [Fact]
    public async Task Round_trips_in_the_order_added()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await alice.CreateAsync("plan.md", "# Plan\n");

        await WriteAsync(alice, Command("settings.appearance"));
        _time.Advance(TimeSpan.FromMinutes(1));
        using var written = await PutAsync(alice, Note("Work", "plan.md"), Command("settings.appearance"));
        var answer = await ListOfAsync(written);

        var read = await ReadAsync(alice);
        Assert.Equal(answer, read);
        Assert.Equal(["command settings.appearance", "note Work/plan.md"], read.Select(f => f.Entry));
        Assert.Equal(_time.GetUtcNow().AddMinutes(-1), DateTimeOffset.Parse(read[0].Added, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(_time.GetUtcNow(), DateTimeOffset.Parse(read[1].Added, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_kept_entry_keeps_its_added()
    {
        var alice = await NewAsync(Api, vault: "Work");

        await WriteAsync(alice, In("board", "Work"));
        var before = (await ReadAsync(alice)).Single().Added;
        _time.Advance(TimeSpan.FromMinutes(1));
        await WriteAsync(alice, In("board", "Work"), In("calendar", "Work"));

        var after = await ReadAsync(alice);
        Assert.Equal([("board Work", before)], after.Take(1));
        Assert.NotEqual(before, after[1].Added);
    }

    [Fact]
    public async Task Vault_entries_are_sealed()
    {
        var alice = await NewAsync(Api, vault: "Work");
        var vaultId = await alice.VaultIdAsync();

        await WriteAsync(alice, Note("Work", "plan.md"), In("board", "Work"), Command("theme.dark"));

        var raw = await Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<BsonDocument>("users")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", alice.User.Id)).SingleAsync(Ct);
        var bytes = Encoding.UTF8.GetString(raw.ToBson());
        Assert.DoesNotContain(vaultId, bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("Work", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("plan.md", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("board", bytes, StringComparison.Ordinal);
        Assert.Contains("theme.dark", bytes, StringComparison.Ordinal);
        Assert.True(raw.Contains("favouritesSealed"));
    }

    [Fact]
    public async Task Commands_survive_a_key_rotation_vault_entries_do_not()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await WriteAsync(alice, Command("settings.appearance"), Note("Work", "plan.md"));

        using (var reencrypted = await alice.SendAsync(HttpMethod.Post, "/api/account/reencrypt", new { password = Password }))
        {
            Assert.Equal(HttpStatusCode.OK, reencrypted.StatusCode);
        }

        Assert.Equal(["command settings.appearance"], await EntriesAsync(alice));
        // And the list takes new vault entries under the new key.
        await WriteAsync(alice, Command("settings.appearance"), In("vault", "Work"));
        Assert.Equal(["command settings.appearance", "vault Work"], await EntriesAsync(alice));
    }

    [Fact]
    public async Task A_hidden_discreet_vault_leaves_no_trace()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await WriteAsync(w.Alice, Note("Work", "plan.md"), Note("Tagebuch", "geheim.md"), In("gallery", "Familie"));
        var hiddenAdded = (await ReadAsync(w.Alice)).Where(f => !f.Entry.Contains("Work", StringComparison.Ordinal)).ToArray();
        await w.HideAsync();

        using (var hidden = await w.Alice.GetAsync(Url))
        {
            var body = await hidden.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("Tagebuch", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Familie", body, StringComparison.Ordinal);
            Assert.Equal(["note Work/plan.md"], (await ListOfAsync(hidden)).Select(f => f.Entry));
        }

        _time.Advance(TimeSpan.FromMinutes(5));
        using (var written = await PutAsync(w.Alice, Note("Work", "plan.md"), Command("admin.users")))
        {
            Assert.Equal(["note Work/plan.md", "command admin.users"], (await ListOfAsync(written)).Select(f => f.Entry));
        }

        await w.RevealAsync();
        var shown = await ReadAsync(w.Alice);
        Assert.Equal(["note Work/plan.md", "note Tagebuch/geheim.md", "gallery Familie", "command admin.users"], shown.Select(f => f.Entry));
        Assert.Equal(hiddenAdded, shown[1..3]);
    }

    [Fact]
    public async Task A_vault_no_longer_listed_is_left_out()
    {
        var alice = await NewAsync(Api, vault: "Work");
        var bob = await NewAsync(Api, vault: "Familie");
        var membership = await bob.ShareWithAsync(alice, "Familie");
        await WriteAsync(alice, In("board", "Familie"), In("board", "Work"));

        await Share.SetStatusAsync(Api.Services.GetRequiredService<IMongoDatabase>(), membership, MembershipStatus.Left);

        Assert.Equal(["board Work"], await EntriesAsync(alice));
    }

    public static TheoryData<string, string?> Refusals => new()
    {
        { """{"favourites":[{"kind":"board","vault":"Nowhere"}]}""", "There is no vault 'Nowhere'." },
        { """{"favourites":[{"kind":"board","vault":"Tagebuch"}]}""", "There is no vault 'Tagebuch'." },
        { """{"favourites":[{"kind":"note","vault":"Tagebuch","path":"geheim.md"}]}""", "There is no vault 'Tagebuch'." },
        { """{"favourites":[{"kind":"note","vault":"Work","path":"../x.md"}]}""", null },
        { """{"favourites":[{"kind":"note","vault":"Work","path":"plan.txt"}]}""", null },
        { """{"favourites":[{"kind":"note","vault":"Work","path":"_archive/old.md"}]}""", null },
        { """{"favourites":[{"kind":"note","vault":"Work"}]}""", null },
        { """{"favourites":[{"kind":"board"}]}""", null },
        { """{"favourites":[{"kind":"shelf","vault":"Work"}]}""", null },
        { """{"favourites":[{"id":"settings"}]}""", null },
        { """{"favourites":[{"kind":"command","id":"Settings.Appearance"}]}""", null },
        { """{"favourites":[{"kind":"command","id":"a.b.c.d.e"}]}""", null },
        { """{"favourites":[{"kind":"command","id":"1settings"}]}""", null },
        { """{"favourites":[{"kind":"command"}]}""", null },
        { $$"""{"favourites":[{"kind":"command","id":"{{new string('a', 65)}}"}]}""", null },
        { """{"favourites":[null]}""", null },
        { """{}""", null },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Refuses_what_it_cannot_take(string json, string? sentence)
    {
        var w = await HiddenWorld.NewAsync(Api);
        await WriteAsync(w.Alice, Command("settings.appearance"));
        await w.HideAsync();

        using var response = await w.Alice.SendContentAsync(HttpMethod.Put, Url, new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        if (sentence is not null) Assert.Equal(sentence, await ErrorAsync(response));
        Assert.Equal(["command settings.appearance"], await EntriesAsync(w.Alice));
    }

    [Fact]
    public async Task A_command_id_of_64_characters_is_taken()
    {
        var alice = await NewAsync(Api, vault: "Work");
        var id = "a" + new string('b', 59) + ".c-d";

        await WriteAsync(alice, Command(id));

        Assert.Equal([$"command {id}"], await EntriesAsync(alice));
    }

    [Fact]
    public async Task Hidden_entries_count_towards_the_cap()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await WriteAsync(w.Alice, In("board", "Tagebuch"), In("calendar", "Tagebuch"));
        await w.HideAsync();
        object[] commands = [.. Enumerable.Range(0, 199).Select(i => Command($"c{i}"))];

        using var full = await PutAsync(w.Alice, commands);

        Assert.Equal(HttpStatusCode.BadRequest, full.StatusCode);
        Assert.Equal("Your favourites are full (200).", await ErrorAsync(full));
        Assert.Empty(await ReadAsync(w.Alice));
        await WriteAsync(w.Alice, commands[..198]);
        Assert.Equal(198, (await ReadAsync(w.Alice)).Length);
    }

    [Fact]
    public async Task More_than_200_at_once_are_refused()
    {
        var alice = await NewAsync(Api, vault: "Work");

        using var full = await PutAsync(alice, [.. Enumerable.Range(0, 201).Select(i => Command($"c{i}"))]);

        Assert.Equal(HttpStatusCode.BadRequest, full.StatusCode);
        Assert.Equal("Your favourites are full (200).", await ErrorAsync(full));
    }

    [Fact]
    public async Task Keys_and_connections_get_403()
    {
        var alice = await NewAsync(Api, vault: "Work");
        await WriteAsync(alice, Command("settings.appearance"));
        var key = await alice.KeyAsync("agent" + Guid.NewGuid().ToString("N")[..6], KeyRight.Write);

        using var read = await alice.GetAsync(Url, bearer: key);
        using var write = await alice.SendAsync(HttpMethod.Put, Url, new { favourites = Array.Empty<object>() }, bearer: key);

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (read.StatusCode, write.StatusCode));
        Assert.Equal(["command settings.appearance"], await EntriesAsync(alice));
    }

    [Fact]
    public async Task Concurrent_writes_keep_both()
    {
        var w = await HiddenWorld.NewAsync(Api);
        await w.HideAsync();
        var favourites = Api.Services.GetRequiredService<Favourites>();
        var once = 0;
        favourites.BeforeWrite = async () =>
        {
            if (Interlocked.Exchange(ref once, 1) == 1) return;
            // Another device, which shows Tagebuch, writes between this write's read and its write.
            await favourites.ReplaceVisibleAsync(
                w.Alice.User.Id, w.Alice.UserKey, new HashSet<string> { w.TagebuchId },
                [new StoredFavourite("board", null, w.TagebuchId, null, _time.GetUtcNow())], Ct);
        };
        try
        {
            await WriteAsync(w.Alice, Command("admin.users"));
        }
        finally
        {
            favourites.BeforeWrite = null;
        }

        await w.RevealAsync();
        // Both added at the same instant: commands come first.
        Assert.Equal(["command admin.users", "board Tagebuch"], await EntriesAsync(w.Alice));
    }

    [Fact]
    public async Task Writes_that_keep_conflicting_are_409()
    {
        var alice = await NewAsync(Api, vault: "Work");
        var favourites = Api.Services.GetRequiredService<Favourites>();
        var other = 0L;
        favourites.BeforeWrite = () => Api.Services.GetRequiredService<IMongoDatabase>().GetCollection<UserDocument>("users").UpdateOneAsync(
            u => u.Id == alice.User.Id, Builders<UserDocument>.Update.Set(u => u.Favourites, [$"other@{Interlocked.Increment(ref other)}"]), cancellationToken: Ct);
        try
        {
            using var response = await PutAsync(alice, Command("admin.users"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            favourites.BeforeWrite = null;
        }
    }
}
