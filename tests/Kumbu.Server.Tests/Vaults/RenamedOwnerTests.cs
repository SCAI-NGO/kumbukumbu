// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kumbu.Core.Attachments;
using Kumbu.Core.Crypto;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Attachments;
using Kumbu.Server.Search;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Attachments;
using Kumbu.Server.Tests.OAuth;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>
/// The owner's matrix of a renamed vault (rename spec §1.7, §1.8): every operation, as a session, as a key over REST and
/// as a key over MCP, on <c>Garten</c> (stored <c>Gartenarbeit</c>) with a new vault <c>Gartenarbeit</c> beside it. A path
/// that falls through to the stored name lands in that vault, whose decoy <c>plan.md</c> must stay as it is; no answer
/// names it, except where a row reads it on purpose. Ids and keys are taken before the rename (<see cref="RestClient.Vault"/>
/// stays <c>Gartenarbeit</c>), and every later path is spelt out.
/// </summary>
[Collection("app")]
public sealed class RenamedOwnerTests(KumbuAppFactory app)
{
    private const string Decoy = "Gartenarbeit";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    /// <summary>Alice's renamed vault <c>Garten</c> and what was taken before the rename; <c>DecoyId</c> is the new <c>Gartenarbeit</c>.</summary>
    private sealed record World(RestClient Alice, string Id, string DecoyId, string Key, byte[] Png, byte[] Pdf)
    {
        public string? Bearer(bool key) => key ? Key : null;
    }

    /// <summary>An answer, read whole: its bytes, as text, and the headers a test looks at.</summary>
    private sealed record Answer(HttpStatusCode Status, byte[] Bytes, string Body, string? ContentDisposition, string? ETag)
    {
        public JsonNode Json => JsonNode.Parse(Body)!;
    }

    // --- The fixture ---

    private async Task<World> BeforeRenameAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.CreateAsync("plan.md", "Beete\n");
        var png = FileApiTests.Png(4, 3);
        await alice.CreateFileAsync("Bilder/a.png", png);
        var pdf = PdfJobTests.Pdf("a paper about the garden");
        await alice.CreateFileAsync("x.pdf", pdf);
        var id = await alice.VaultIdAsync();
        return new World(alice, id, "", await KeyForAsync(alice, id, "Claude", KeyRight.Maintain), png, pdf);
    }

    /// <summary>The world, renamed to <c>Garten</c>, with the new <c>Gartenarbeit</c> and its decoy <c>plan.md</c> beside it.
    /// <paramref name="beforeRename"/> runs just before the rename.</summary>
    private async Task<World> RenamedAsync(Func<World, Task>? beforeRename = null)
    {
        var w = await BeforeRenameAsync();
        if (beforeRename is not null) await beforeRename(w);
        using (var renamed = await RenameAsync(w.Alice, "Gartenarbeit", "Garten"))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        return w with { DecoyId = await NewDecoyAsync(w.Alice) };
    }

    private static Task<HttpResponseMessage> RenameAsync(RestClient user, string vault, string name) =>
        user.SendAsync(HttpMethod.Put, $"/api/vaults/{Uri.EscapeDataString(vault)}/name", new { name });

    private static async Task<string> NewDecoyAsync(RestClient alice)
    {
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

    /// <summary>An API key of <paramref name="user"/> for the vault <paramref name="vaultId"/>, named by id rather than by name.</summary>
    private static async Task<string> KeyForAsync(RestClient user, string vaultId, string name, KeyRight right, ApiKeyKind kind = ApiKeyKind.Agent) =>
        (await user.Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            user.User, user.UserKey, DateTime.UtcNow, name, new Dictionary<string, KeyRight> { [vaultId] = right }, Ct, kind: kind)).Key;

    /// <summary>The renamed world, <paramref name="body"/>, then the decoy as it was.</summary>
    private async Task WithRenamedAsync(Func<World, Task> body)
    {
        var w = await RenamedAsync();
        await body(w);
        await AssertDecoyUntouchedAsync(w);
    }

    /// <summary>The new vault holds its <c>plan.md</c> and nothing else: one version, no trash, checkpoints or moves.</summary>
    private async Task AssertDecoyUntouchedAsync(World w)
    {
        var tree = await CallAsync(w, HttpMethod.Get, "/api/vaults/Gartenarbeit/tree", decoy: true);
        Assert.Equal(HttpStatusCode.OK, tree.Status);
        var root = tree.Json["root"]!;
        Assert.Empty(root["folders"]!.AsArray());
        Assert.Equal(["/Gartenarbeit/plan.md"], root["notes"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()));
        var note = await CallAsync(w, HttpMethod.Get, "/api/notes/Gartenarbeit/plan.md", decoy: true);
        Assert.Equal(HttpStatusCode.OK, note.Status);
        Assert.Equal("Andere\n", note.Json["content"]!.GetValue<string>());
        var history = await CallAsync(w, HttpMethod.Get, "/api/history/Gartenarbeit/plan.md", decoy: true);
        Assert.Single(history.Json.AsArray());
        var trash = await CallAsync(w, HttpMethod.Get, "/api/vaults/Gartenarbeit/trash", decoy: true);
        Assert.Empty(trash.Json["notes"]!.AsArray());
        var checkpoints = await CallAsync(w, HttpMethod.Get, "/api/vaults/Gartenarbeit/checkpoints", decoy: true);
        Assert.Empty(checkpoints.Json["checkpoints"]!.AsArray());
        foreach (var (collection, count) in new[] { ("notes", 1L), ("moves", 0L), ("attachments", 0L), ("attachment_trash", 0L), ("trash", 0L), ("checkpoints", 0L) })
        {
            Assert.True(count == await RawCountAsync(collection, w.DecoyId), $"{collection} of the new vault");
        }
    }

    private Task<long> RawCountAsync(string collection, string vaultId) =>
        app.Database.GetCollection<BsonDocument>(collection).CountDocumentsAsync(new BsonDocument("vault", vaultId), cancellationToken: Ct);

    // --- Requests ---

    /// <summary>A request as the session, or as <paramref name="bearer"/>. Unless <paramref name="decoy"/>, neither the body nor a header names the new vault.</summary>
    private static async Task<Answer> CallAsync(
        World w, HttpMethod method, string path, object? body = null, string? bearer = null, bool decoy = false, HttpContent? content = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var response = content is null
            ? await w.Alice.SendAsync(method, path, body, bearer, headers)
            : await w.Alice.SendContentAsync(method, path, content, bearer);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        var text = Encoding.UTF8.GetString(bytes);
        var headerText = string.Join('\n', response.Headers.Concat(response.Content.Headers).SelectMany(h => h.Value));
        if (!decoy)
        {
            Assert.DoesNotContain(Decoy, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Decoy, headerText, StringComparison.Ordinal);
        }

        return new Answer(
            response.StatusCode, bytes, text, response.Content.Headers.ContentDisposition?.ToString(), response.Headers.ETag?.ToString());
    }

    private static async Task<Answer> OkAsync(World w, HttpMethod method, string path, object? body = null, string? bearer = null, bool decoy = false)
    {
        var answer = await CallAsync(w, method, path, body, bearer, decoy);
        Assert.True(answer.Status is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent, $"{method} {path}: {(int)answer.Status} {answer.Body}");
        return answer;
    }

    private static Task<Answer> GetAsync(World w, string path, string? bearer = null) => OkAsync(w, HttpMethod.Get, path, bearer: bearer);

    private static async Task<JsonNode> NoteAsync(World w, string path, string? bearer = null) => (await GetAsync(w, "/api/notes/Garten/" + path, bearer)).Json;

    private static async Task<string> VersionAsync(World w, string path) => (await NoteAsync(w, path))["version"]!.GetValue<string>();

    /// <summary>One <c>sh</c> command as the key: it works, and none of its text blocks names the new vault.</summary>
    private static async Task<string> ShAsync(World w, string command)
    {
        var (failed, text) = await ShAnyAsync(w, command);
        Assert.False(failed, $"{command}: {text}");
        Assert.DoesNotContain(Decoy, text, StringComparison.Ordinal);
        return text;
    }

    /// <summary>One <c>sh</c> command as the key, worked or not, with every text block of its answer: the notices
    /// appended to it as well, not only the first (<see cref="RestClient.ShAsync"/>).</summary>
    private static async Task<(bool Failed, string Text)> ShAnyAsync(World w, string command)
    {
        var result = await w.Alice.ToolAsync("sh", new Dictionary<string, object?> { ["command"] = command }, w.Key);
        return (result.IsError ?? false, string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text)));
    }

    /// <summary>A tool call as the key; its text names no new vault.</summary>
    private static async Task<(CallToolResult Result, string Text)> ToolAsync(World w, string tool, Dictionary<string, object?> args, bool fails = false)
    {
        var result = await w.Alice.ToolAsync(tool, args, w.Key);
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(fails == (result.IsError ?? false), $"{tool}: {text}");
        Assert.DoesNotContain(Decoy, text, StringComparison.Ordinal);
        return (result, text);
    }

    private static Task<(CallToolResult Result, string Text)> ShToolAsync(World w, string command) =>
        ToolAsync(w, "sh", new Dictionary<string, object?> { ["command"] = command });

    /// <summary>Every string of <paramref name="node"/> that is a path names <c>/Garten</c> or something in it; how many there were.</summary>
    private static int AssertUnderGarten(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                return o.Sum(p => AssertUnderGarten(p.Value));
            case JsonArray a:
                return a.Sum(AssertUnderGarten);
            case JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith('/') && !s.StartsWith("/api/", StringComparison.Ordinal):
                Assert.True(s is "/Garten" or "/Garten/" || s.StartsWith("/Garten/", StringComparison.Ordinal), $"{s} is not in /Garten");
                return 1;
            default:
                return 0;
        }
    }

    private static void AssertSomeUnderGarten(JsonNode node) => Assert.True(AssertUnderGarten(node) > 0, node.ToJsonString());

    // --- reads ---

    private Task ReadsAsync(bool key) => WithRenamedAsync(async w =>
    {
        var note = await NoteAsync(w, "plan.md", w.Bearer(key));
        Assert.Equal(("/Garten/plan.md", "Beete\n"), (note["path"]!.GetValue<string>(), note["content"]!.GetValue<string>()));
    });

    [Fact]
    public Task Session_reads() => ReadsAsync(key: false);

    [Fact]
    public Task KeyRest_reads() => ReadsAsync(key: true);

    [Fact]
    public Task KeySh_reads() => WithRenamedAsync(async w =>
    {
        Assert.Contains("Beete", await ShAsync(w, "cat /Garten/plan.md"), StringComparison.Ordinal);
        var (failed, text) = await ShAnyAsync(w, "cat /Gartenarbeit/plan.md");
        Assert.True(failed, text);
        Assert.DoesNotContain("Andere", text, StringComparison.Ordinal);
    });

    // --- lists ---

    private Task ListsAsync(bool key) => WithRenamedAsync(async w =>
    {
        // The session's list names the new vault on purpose; the renamed vault's entry still mustn't.
        var vaults = await OkAsync(w, HttpMethod.Get, "/api/vaults", bearer: w.Bearer(key), decoy: !key);
        string[] expected = key ? ["Garten"] : ["Garten", "Gartenarbeit"];
        Assert.Equal(expected, vaults.Json.AsArray().Select(v => v!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        var garten = vaults.Json.AsArray().Single(v => v!["name"]!.GetValue<string>() == "Garten")!.ToJsonString();
        Assert.DoesNotContain(Decoy, garten, StringComparison.Ordinal);
        var tree = (await GetAsync(w, "/api/vaults/Garten/tree", w.Bearer(key))).Json;
        Assert.Equal("/Garten", tree["root"]!["path"]!.GetValue<string>());
        AssertSomeUnderGarten(tree);
        Assert.Contains("\"/Garten/plan.md\"", tree.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("\"/Garten/Bilder/a.png\"", tree.ToJsonString(), StringComparison.Ordinal);
    });

    [Fact]
    public Task Session_lists() => ListsAsync(key: false);

    [Fact]
    public Task KeyRest_lists() => ListsAsync(key: true);

    [Fact]
    public Task KeySh_lists() => WithRenamedAsync(async w =>
    {
        Assert.Contains("Garten/", await ShAsync(w, "ls /"), StringComparison.Ordinal);
        var listing = await ShAsync(w, "ls /Garten");
        Assert.Contains("plan.md", listing, StringComparison.Ordinal);
        Assert.Contains("Bilder/", listing, StringComparison.Ordinal);
    });

    // --- writes ---

    private Task WritesAsync(bool key) => WithRenamedAsync(async w =>
    {
        var created = await OkAsync(w, HttpMethod.Put, "/api/notes/Garten/neu.md", new { content = "Neu\n" }, w.Bearer(key));
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var first = created.Json["version"]!.GetValue<string>();
        AssertUnderGarten(created.Json);
        var note = await NoteAsync(w, "neu.md");
        Assert.Equal(("/Garten/neu.md", "Neu\n"), (note["path"]!.GetValue<string>(), note["content"]!.GetValue<string>()));

        var updated = await OkAsync(w, HttpMethod.Put, "/api/notes/Garten/neu.md", new { content = "Neu und mehr\n", version = first }, w.Bearer(key));
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        var stale = await CallAsync(w, HttpMethod.Put, "/api/notes/Garten/neu.md", new { content = "Alt\n", version = first }, w.Bearer(key));
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Contains("/Garten/neu.md", stale.Body, StringComparison.Ordinal);
        Assert.Equal("Neu und mehr\n", (await NoteAsync(w, "neu.md"))["content"]!.GetValue<string>());
    });

    [Fact]
    public Task Session_writes() => WritesAsync(key: false);

    [Fact]
    public Task KeyRest_writes() => WritesAsync(key: true);

    [Fact]
    public Task KeySh_writes() => WithRenamedAsync(async w =>
    {
        await ToolAsync(w, "write", new Dictionary<string, object?> { ["path"] = "/Garten/neu.md", ["content"] = "Neu\n" });
        var note = await NoteAsync(w, "neu.md");
        Assert.Equal(("/Garten/neu.md", "Neu\n"), (note["path"]!.GetValue<string>(), note["content"]!.GetValue<string>()));
        var first = note["version"]!.GetValue<string>();

        await ToolAsync(w, "edit", new Dictionary<string, object?> { ["path"] = "/Garten/neu.md", ["version"] = first, ["new_text"] = "Mehr\n" });
        Assert.Equal("Neu\nMehr\n", (await NoteAsync(w, "neu.md"))["content"]!.GetValue<string>());
        var (_, stale) = await ToolAsync(w, "edit", new Dictionary<string, object?> { ["path"] = "/Garten/neu.md", ["version"] = first, ["new_text"] = "Alt\n" }, fails: true);
        Assert.Contains("/Garten/neu.md", stale, StringComparison.Ordinal);
        var (_, staleWrite) = await ToolAsync(w, "write", new Dictionary<string, object?> { ["path"] = "/Garten/neu.md", ["content"] = "Alt\n", ["version"] = first }, fails: true);
        Assert.Contains("/Garten/neu.md", staleWrite, StringComparison.Ordinal);
    });

    // --- moves ---

    private Task MovesAsync(bool key) => WithRenamedAsync(async w =>
    {
        var moved = await OkAsync(w, HttpMethod.Post, "/api/move", new { from = "/Garten/plan.md", to = "/Garten/p/plan.md" }, w.Bearer(key));
        Assert.Equal("/Garten/p/plan.md", moved.Json["path"]!.GetValue<string>());
        AssertUnderGarten(moved.Json);
        var gone = await CallAsync(w, HttpMethod.Get, "/api/notes/Garten/plan.md", bearer: w.Bearer(key));
        Assert.Equal(HttpStatusCode.NotFound, gone.Status);
        Assert.Contains("/Garten/p/plan.md", gone.Body, StringComparison.Ordinal);
        Assert.Equal("Beete\n", (await NoteAsync(w, "p/plan.md"))["content"]!.GetValue<string>());

        var folder = await OkAsync(w, HttpMethod.Post, "/api/move", new { from = "/Garten/Bilder/", to = "/Garten/Fotos/" }, w.Bearer(key));
        AssertUnderGarten(folder.Json);
        Assert.Equal(w.Png, (await GetAsync(w, "/api/files/Garten/Fotos/a.png")).Bytes);
    });

    [Fact]
    public Task Session_moves() => MovesAsync(key: false);

    [Fact]
    public Task KeyRest_moves() => MovesAsync(key: true);

    [Fact]
    public Task KeySh_moves() => WithRenamedAsync(async w =>
    {
        await ShAsync(w, "mv /Garten/plan.md /Garten/p/plan.md");
        var (failed, gone) = await ShAnyAsync(w, "cat /Garten/plan.md");
        Assert.True(failed, gone);
        Assert.Contains("/Garten/p/plan.md", gone, StringComparison.Ordinal);
        Assert.DoesNotContain(Decoy, gone, StringComparison.Ordinal);
        await ShAsync(w, "mv /Garten/Bilder/ /Garten/Fotos/");
        Assert.Equal("Beete\n", (await NoteAsync(w, "p/plan.md"))["content"]!.GetValue<string>());
        Assert.Equal(w.Png, (await GetAsync(w, "/api/files/Garten/Fotos/a.png")).Bytes);
    });

    // --- removes ---

    private Task RemovesAsync(bool key) => WithRenamedAsync(async w =>
    {
        await OkAsync(w, HttpMethod.Delete, "/api/notes/Garten/plan.md", bearer: w.Bearer(key));
        var trash = (await GetAsync(w, "/api/vaults/Garten/trash", w.Bearer(key))).Json;
        Assert.Equal(["/Garten/plan.md"], trash["notes"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()));
        AssertSomeUnderGarten(trash);
    });

    [Fact]
    public Task Session_removes() => RemovesAsync(key: false);

    [Fact]
    public Task KeyRest_removes() => RemovesAsync(key: true);

    [Fact]
    public Task KeySh_removes() => WithRenamedAsync(async w =>
    {
        await ShAsync(w, "rm /Garten/plan.md");
        Assert.Contains("/Garten/plan.md", await ShAsync(w, "trash /Garten"), StringComparison.Ordinal);
        var trash = (await GetAsync(w, "/api/vaults/Garten/trash")).Json;
        Assert.Equal(["/Garten/plan.md"], trash["notes"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()));
    });

    // --- restores ---

    private Task RestoresAsync(bool key) => WithRenamedAsync(async w =>
    {
        await OkAsync(w, HttpMethod.Delete, "/api/notes/Garten/plan.md", bearer: w.Bearer(key));
        var restored = await OkAsync(w, HttpMethod.Post, "/api/restore/Garten/plan.md", new { }, w.Bearer(key));
        AssertUnderGarten(restored.Json);
        Assert.Equal("Beete\n", (await NoteAsync(w, "plan.md", w.Bearer(key)))["content"]!.GetValue<string>());
        Assert.Empty((await GetAsync(w, "/api/vaults/Garten/trash", w.Bearer(key))).Json["notes"]!.AsArray());
    });

    [Fact]
    public Task Session_restores() => RestoresAsync(key: false);

    [Fact]
    public Task KeyRest_restores() => RestoresAsync(key: true);

    [Fact]
    public Task KeySh_restores() => WithRenamedAsync(async w =>
    {
        await ShAsync(w, "rm /Garten/plan.md");
        await ShAsync(w, "restore /Garten/plan.md");
        Assert.Contains("Beete", await ShAsync(w, "cat /Garten/plan.md"), StringComparison.Ordinal);
        Assert.DoesNotContain("plan.md", await ShAsync(w, "trash /Garten"), StringComparison.Ordinal);
        Assert.Empty((await GetAsync(w, "/api/vaults/Garten/trash")).Json["notes"]!.AsArray());
    });

    // --- searches by words ---

    private Task SearchesByWordsAsync(bool key) => WithRenamedAsync(async w =>
    {
        var found = await GetAsync(w, "/api/search?q=Beete&mode=exact&path=/Garten", w.Bearer(key));
        Assert.Equal(["/Garten/plan.md"], found.Json["results"]!.AsArray().Select(r => r!["path"]!.GetValue<string>()).Distinct());
        AssertSomeUnderGarten(found.Json);
        Assert.DoesNotContain("Andere", found.Body, StringComparison.Ordinal);
    });

    [Fact]
    public Task Session_searches_by_words() => SearchesByWordsAsync(key: false);

    [Fact]
    public Task KeyRest_searches_by_words() => SearchesByWordsAsync(key: true);

    [Fact]
    public Task KeySh_searches_by_words() => WithRenamedAsync(async w =>
    {
        var found = await ShAsync(w, "rg Beete /Garten");
        Assert.Contains("/Garten/plan.md", found, StringComparison.Ordinal);
        Assert.DoesNotContain("Andere", found, StringComparison.Ordinal);
    });

    // --- searches across vaults ---

    [Fact]
    public Task Session_searches_across_vaults() => WithRenamedAsync(async w =>
    {
        var found = await OkAsync(w, HttpMethod.Get, "/api/search?q=e&mode=exact&n=50", decoy: true);
        var results = found.Json["results"]!.AsArray();
        var hits = results.Where(r => r!["path"]!.GetValue<string>().EndsWith("/plan.md", StringComparison.Ordinal)).Select(r => (r!["path"]!.GetValue<string>(), r["snippet"]!.GetValue<string>())).Order().ToArray();
        Assert.Equal([("/Garten/plan.md", "Beete"), ("/Gartenarbeit/plan.md", "Andere")], hits);

        // The answer names the new vault on purpose, in its own hit and nowhere else.
        Assert.All(results, r =>
        {
            var (path, hit) = (r!["path"]!.GetValue<string>(), r.ToJsonString());
            if (path == "/Gartenarbeit/plan.md")
            {
                Assert.Equal("Andere", r["snippet"]!.GetValue<string>());
                return;
            }

            Assert.StartsWith("/Garten/", path, StringComparison.Ordinal);
            AssertUnderGarten(r);
            Assert.DoesNotContain(Decoy, hit, StringComparison.Ordinal);
            Assert.DoesNotContain("Andere", hit, StringComparison.Ordinal);
        });
        var rest = found.Json.DeepClone().AsObject();
        rest.Remove("results");
        Assert.DoesNotContain(Decoy, rest.ToJsonString(), StringComparison.Ordinal);
    });

    [Fact]
    public Task KeySh_searches_across_vaults() => WithRenamedAsync(async w =>
    {
        var found = await ShAsync(w, "rg -i e /");
        Assert.Contains("/Garten/plan.md", found, StringComparison.Ordinal);
        Assert.All(found.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.StartsWith("/Garten/", line, StringComparison.Ordinal));
        Assert.DoesNotContain("Andere", found, StringComparison.Ordinal);
    });

    // --- searches by meaning ---

    /// <summary>The index may not have queued the notes yet when it reports idle, so the search is asked again until it
    /// finds <c>plan.md</c>; each answer is checked as it comes.</summary>
    private async Task<T> FoundByMeaningAsync<T>(Func<Task<T>> search, Func<T, bool> found)
    {
        await app.IndexedAsync();
        var answer = await search();
        await PdfJobTests.EventuallyAsync(async () => found(answer) || found(answer = await search()), "plan.md is found by meaning");
        return answer;
    }

    private Task SearchesByMeaningAsync(bool key) => WithRenamedAsync(async w =>
    {
        var found = await FoundByMeaningAsync(
            async () =>
            {
                var answer = await GetAsync(w, "/api/search?q=Beete&mode=meaning&path=/Garten", w.Bearer(key));
                AssertUnderGarten(answer.Json);
                Assert.DoesNotContain("Andere", answer.Body, StringComparison.Ordinal);
                return answer;
            },
            answer => answer.Json["results"]!.AsArray().Any(r => r!["path"]!.GetValue<string>() == "/Garten/plan.md"));
        AssertSomeUnderGarten(found.Json);
    });

    [Fact]
    public Task Session_searches_by_meaning() => SearchesByMeaningAsync(key: false);

    [Fact]
    public Task KeyRest_searches_by_meaning() => SearchesByMeaningAsync(key: true);

    [Fact]
    public Task KeySh_searches_by_meaning() => WithRenamedAsync(async w =>
    {
        await FoundByMeaningAsync(
            async () =>
            {
                var answer = await ShAsync(w, "semsearch -n 3 --meaning Beete");
                Assert.DoesNotContain("Andere", answer, StringComparison.Ordinal);
                return answer;
            },
            answer => answer.Contains("/Garten/plan.md", StringComparison.Ordinal));
    });

    // --- history ---

    /// <summary>A second version of <c>plan.md</c>; the first one's token.</summary>
    private static async Task<string> SecondVersionAsync(World w)
    {
        var first = await VersionAsync(w, "plan.md");
        await OkAsync(w, HttpMethod.Put, "/api/notes/Garten/plan.md", new { content = "Wege\n", version = first });
        return first;
    }

    private Task SeesHistoryAsync(bool key) => WithRenamedAsync(async w =>
    {
        var first = await SecondVersionAsync(w);
        var history = (await GetAsync(w, "/api/history/Garten/plan.md", w.Bearer(key))).Json.AsArray();
        Assert.Equal(2, history.Count);
        Assert.Contains(first, history.Select(h => h!["version"]!.GetValue<string>()));
        var old = (await GetAsync(w, $"/api/notes/Garten/plan.md?version={first}", w.Bearer(key))).Json;
        Assert.Equal(("/Garten/plan.md", "Beete\n"), (old["path"]!.GetValue<string>(), old["content"]!.GetValue<string>()));
    });

    [Fact]
    public Task Session_sees_history() => SeesHistoryAsync(key: false);

    [Fact]
    public Task KeyRest_sees_history() => SeesHistoryAsync(key: true);

    [Fact]
    public Task KeySh_sees_history() => WithRenamedAsync(async w =>
    {
        var first = await SecondVersionAsync(w);
        Assert.Contains(first, await ShAsync(w, "history /Garten/plan.md"), StringComparison.Ordinal);
        Assert.Contains("Beete", await ShAsync(w, $"cat --version {first} /Garten/plan.md"), StringComparison.Ordinal);
    });

    // --- activity: the session's ---

    [Fact]
    public Task Session_sees_activity() => WithRenamedAsync(async w =>
    {
        await OkAsync(w, HttpMethod.Put, "/api/notes/Garten/neu.md", new { content = "Neu\n" });
        var activity = (await GetAsync(w, "/api/vaults/Garten/activity")).Json;
        AssertSomeUnderGarten(activity["rows"]!);
        Assert.Contains("\"/Garten/neu.md\"", activity["rows"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("You renamed this vault.", activity["events"]!.AsArray().Select(e => e!["text"]!.GetValue<string>()));
    });

    [Fact]
    public Task KeyRest_sees_activity() => WithRenamedAsync(async w =>
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(w, HttpMethod.Get, "/api/vaults/Garten/activity", bearer: w.Key)).Status);
    });

    // --- checkpoints ---

    private static async Task<Answer> CheckpointAsync(World w, string name, string? bearer = null)
    {
        var made = await OkAsync(w, HttpMethod.Post, "/api/vaults/Garten/checkpoints", new { name }, bearer);
        AssertUnderGarten(made.Json);
        return made;
    }

    private static async Task<JsonArray> CheckpointsAsync(World w, string? bearer = null)
    {
        var list = (await GetAsync(w, "/api/vaults/Garten/checkpoints", bearer)).Json;
        AssertUnderGarten(list);
        return list["checkpoints"]!.AsArray();
    }

    private Task MakesCheckpointsAsync(bool key) => WithRenamedAsync(async w =>
    {
        var name = key ? "gfd2" : "gfd";
        await CheckpointAsync(w, name, w.Bearer(key));
        var made = Assert.Single(await CheckpointsAsync(w));
        Assert.Equal((name, "/Garten/"), (made!["name"]!.GetValue<string>(), made["scope"]!.GetValue<string>()));
    });

    [Fact]
    public Task Session_makes_checkpoints() => MakesCheckpointsAsync(key: false);

    [Fact]
    public Task KeyRest_makes_checkpoints() => MakesCheckpointsAsync(key: true);

    [Fact]
    public Task KeySh_makes_checkpoints() => WithRenamedAsync(async w =>
    {
        await ShAsync(w, "mkcp /Garten gfd2");
        var made = Assert.Single(await CheckpointsAsync(w));
        Assert.Equal(("gfd2", "/Garten/"), (made!["name"]!.GetValue<string>(), made["scope"]!.GetValue<string>()));
    });

    private Task ListsCheckpointsAsync(bool key) => WithRenamedAsync(async w =>
    {
        await CheckpointAsync(w, "gfd");
        var listed = Assert.Single(await CheckpointsAsync(w, w.Bearer(key)));
        Assert.Equal(("gfd", "/Garten/"), (listed!["name"]!.GetValue<string>(), listed["scope"]!.GetValue<string>()));
    });

    [Fact]
    public Task Session_lists_checkpoints() => ListsCheckpointsAsync(key: false);

    [Fact]
    public Task KeyRest_lists_checkpoints() => ListsCheckpointsAsync(key: true);

    [Fact]
    public Task KeySh_lists_checkpoints() => WithRenamedAsync(async w =>
    {
        await CheckpointAsync(w, "gfd");
        var listed = await ShAsync(w, "lscp /Garten");
        Assert.Contains("gfd", listed, StringComparison.Ordinal);
        Assert.Contains("gfd", await ShAsync(w, "lscp"), StringComparison.Ordinal);
    });

    private Task RestoresCheckpointsAsync(bool key) => WithRenamedAsync(async w =>
    {
        await CheckpointAsync(w, "gfd");
        await SecondVersionAsync(w);
        var preview = (await OkAsync(w, HttpMethod.Post, "/api/vaults/Garten/checkpoints/gfd/preview", bearer: w.Bearer(key))).Json;
        AssertSomeUnderGarten(preview);
        Assert.Contains("\"/Garten/plan.md\"", preview["changes"]!.ToJsonString(), StringComparison.Ordinal);
        var restored = await OkAsync(w, HttpMethod.Post, "/api/vaults/Garten/checkpoints/gfd/restore", new { plan = preview["plan"]!.GetValue<string>() }, w.Bearer(key));
        AssertUnderGarten(restored.Json);
        Assert.Equal("Beete\n", (await NoteAsync(w, "plan.md"))["content"]!.GetValue<string>());
    });

    [Fact]
    public Task Session_restores_checkpoints() => RestoresCheckpointsAsync(key: false);

    [Fact]
    public Task KeyRest_restores_checkpoints() => RestoresCheckpointsAsync(key: true);

    [Fact]
    public Task KeySh_restores_checkpoints() => WithRenamedAsync(async w =>
    {
        await ShAsync(w, "mkcp /Garten gfd");
        await SecondVersionAsync(w);
        await ShAsync(w, "restore --checkpoint gfd /Garten/plan.md");
        Assert.Equal("Beete\n", (await NoteAsync(w, "plan.md"))["content"]!.GetValue<string>());

        await SecondVersionAsync(w);
        var preview = await ShAsync(w, "restore --checkpoint gfd /Garten");
        Assert.Contains("/Garten/plan.md", preview, StringComparison.Ordinal);
        var plan = Regex.Match(preview, @"plan: (\S+)").Groups[1].Value;
        Assert.NotEmpty(plan);
        await ShAsync(w, $"restore --checkpoint gfd --plan {plan} /Garten");
        Assert.Equal("Beete\n", (await NoteAsync(w, "plan.md"))["content"]!.GetValue<string>());
    });

    private Task ReadsACheckpointAsync(bool key) => WithRenamedAsync(async w =>
    {
        await CheckpointAsync(w, "gfd");
        await SecondVersionAsync(w);
        var tree = (await GetAsync(w, "/api/vaults/Garten/checkpoints/gfd/tree", w.Bearer(key))).Json;
        Assert.Equal("/Garten", tree["root"]!["path"]!.GetValue<string>());
        AssertSomeUnderGarten(tree);
        var note = (await GetAsync(w, "/api/vaults/Garten/checkpoints/gfd/notes/plan.md", w.Bearer(key))).Json;
        Assert.Equal(("/Garten/plan.md", "Beete\n"), (note["path"]!.GetValue<string>(), note["content"]!.GetValue<string>()));
        Assert.Equal(w.Png, (await GetAsync(w, "/api/vaults/Garten/checkpoints/gfd/files/Bilder/a.png", w.Bearer(key))).Bytes);
    });

    [Fact]
    public Task Session_reads_a_checkpoint() => ReadsACheckpointAsync(key: false);

    [Fact]
    public Task KeyRest_reads_a_checkpoint() => ReadsACheckpointAsync(key: true);

    private Task RemovesCheckpointsAsync(bool key) => WithRenamedAsync(async w =>
    {
        await CheckpointAsync(w, "gfd");
        await OkAsync(w, HttpMethod.Delete, "/api/vaults/Garten/checkpoints/gfd", bearer: w.Bearer(key));
        Assert.Empty(await CheckpointsAsync(w));
    });

    [Fact]
    public Task Session_removes_checkpoints() => RemovesCheckpointsAsync(key: false);

    [Fact]
    public Task KeyRest_removes_checkpoints() => RemovesCheckpointsAsync(key: true);

    [Fact]
    public Task KeySh_removes_checkpoints() => WithRenamedAsync(async w =>
    {
        await ShAsync(w, "mkcp /Garten gfd2");
        await ShAsync(w, "rmcp /Garten gfd2");
        Assert.DoesNotContain("gfd2", await ShAsync(w, "lscp /Garten"), StringComparison.Ordinal);
        Assert.Empty(await CheckpointsAsync(w));
    });

    // --- attachments ---

    private static ByteArrayContent Bytes(byte[] bytes) => new(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } };

    private Task WritesAttachmentsAsync(bool key) => WithRenamedAsync(async w =>
    {
        var png = FileApiTests.Png(5, 2);
        var put = await CallAsync(w, HttpMethod.Put, "/api/files/Garten/Bilder/b.png", bearer: w.Bearer(key), content: Bytes(png));
        Assert.Equal(HttpStatusCode.Created, put.Status);
        Assert.Equal("/Garten/Bilder/b.png", put.Json["path"]!.GetValue<string>());
        Assert.Equal(png, (await GetAsync(w, "/api/files/Garten/Bilder/b.png")).Bytes);

        // The upload takes Markdown only, and its answer names files relative to the folder: no path of it may leave /Garten.
        var files = new[] { new { path = "c.md", content = "Notiz\n" }, new { path = "d.png", content = "kein Bild\n" } };
        var upload = await OkAsync(w, HttpMethod.Post, "/api/upload/Garten/Bilder", new { files }, w.Bearer(key));
        Assert.Equal(1, upload.Json["written"]!.GetValue<int>());
        Assert.Equal(["d.png"], upload.Json["skipped"]!.AsArray().Select(s => s!["path"]!.GetValue<string>()));
        AssertUnderGarten(upload.Json);
        var note = await NoteAsync(w, "Bilder/c.md");
        Assert.Equal(("/Garten/Bilder/c.md", "Notiz\n"), (note["path"]!.GetValue<string>(), note["content"]!.GetValue<string>()));

        var again = await OkAsync(w, HttpMethod.Post, "/api/upload/Garten/Bilder", new { files = new[] { new { path = "c.md", content = "Anders\n" } } }, w.Bearer(key));
        var conflict = Assert.Single(again.Json["conflicts"]!.AsArray())!;
        Assert.Equal(("c.md", note["version"]!.GetValue<string>()), (conflict["path"]!.GetValue<string>(), conflict["version"]!.GetValue<string>()));
        AssertUnderGarten(again.Json);
    });

    [Fact]
    public Task Session_writes_attachments() => WritesAttachmentsAsync(key: false);

    [Fact]
    public Task KeyRest_writes_attachments() => WritesAttachmentsAsync(key: true);

    private Task ReadsAttachmentsAsync(bool key) => WithRenamedAsync(async w =>
    {
        Assert.Equal(w.Png, (await GetAsync(w, "/api/files/Garten/Bilder/a.png", w.Bearer(key))).Bytes);
        Assert.Equal(w.Pdf, (await GetAsync(w, "/api/files/Garten/x.pdf", w.Bearer(key))).Bytes);
        if (key) return;

        // The pixels the shell's pdf and img get, as the owner opens them.
        var store = new EncryptedNoteStore(Catalog, new UserCredential(w.Alice.User.Id, w.Alice.UserKey));
        var pixels = new AttachmentPixels(
            store, Catalog, Api.Services.GetRequiredService<IPdfSidecar>(), Api.Services.GetRequiredService<PdfAnalyses>(), Api.Services.GetRequiredService<PdfAnalysisRunner>());
        var pdf = P("/Garten/x.pdf");
        var page = await pixels.PageAsync(pdf, (await store.GetAttachmentAsync(pdf, Ct))!.Current, 1, SizeStep.Small, null, 0, Ct);
        Assert.NotEmpty(page.Data);
        var png = P("/Garten/Bilder/a.png");
        var image = await pixels.ImageAsync(png, (await store.GetAttachmentAsync(png, Ct))!.Current, SizeStep.Small, null, 0, Ct);
        Assert.NotEmpty(image.Data);
    });

    [Fact]
    public Task Session_reads_attachments() => ReadsAttachmentsAsync(key: false);

    [Fact]
    public Task KeyRest_reads_attachments() => ReadsAttachmentsAsync(key: true);

    [Fact]
    public Task KeySh_reads_attachments() => WithRenamedAsync(async w =>
    {
        await AnalysedAsync(w, "Garten");
        var (img, imgText) = await ShToolAsync(w, "img /Garten/Bilder/a.png");
        Assert.Contains("/Garten/Bilder/a.png", imgText, StringComparison.Ordinal);
        Assert.NotEmpty(Assert.Single(img.Content.OfType<ImageContentBlock>()).DecodedData.ToArray());
        var (pdf, pdfText) = await ShToolAsync(w, "pdf /Garten/x.pdf 1 --images=all");
        Assert.Contains("Page 1 text", pdfText, StringComparison.Ordinal);
        Assert.NotEmpty(Assert.Single(pdf.Content.OfType<ImageContentBlock>()).DecodedData.ToArray());
        Assert.Contains("/Garten/x.pdf:p2: Page 2 text", await ShAsync(w, "rg 'Page 2 text' /Garten"), StringComparison.Ordinal);
    });

    /// <summary>Until the PDF's analysis is stored.</summary>
    private async Task AnalysedAsync(World w, string vaultName)
    {
        var store = new EncryptedNoteStore(Catalog, new UserCredential(w.Alice.User.Id, w.Alice.UserKey));
        var blobId = Convert.FromBase64String((await store.GetAttachmentAsync(P($"/{vaultName}/x.pdf"), Ct))!.Current.BlobId);
        var analyses = Api.Services.GetRequiredService<PdfAnalyses>();
        await PdfJobTests.EventuallyAsync(
            async () => Catalog.Current(w.Id) is { } vault && await analyses.HeaderAsync(vault, blobId, Ct) is not null, "the analysis is stored");
    }

    private Task RemovesAndRestoresAttachmentsAsync(bool key) => WithRenamedAsync(async w =>
    {
        await OkAsync(w, HttpMethod.Delete, "/api/files/Garten/Bilder/a.png", bearer: w.Bearer(key));
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(w, HttpMethod.Get, "/api/files/Garten/Bilder/a.png")).Status);
        var trash = (await GetAsync(w, "/api/vaults/Garten/trash", w.Bearer(key))).Json;
        Assert.Contains("/Garten/Bilder/a.png", trash["notes"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()));
        await OkAsync(w, HttpMethod.Post, "/api/restore/Garten/Bilder/a.png", new { }, w.Bearer(key));
        Assert.Equal(w.Png, (await GetAsync(w, "/api/files/Garten/Bilder/a.png", w.Bearer(key))).Bytes);
    });

    [Fact]
    public Task Session_removes_and_restores_attachments() => RemovesAndRestoresAttachmentsAsync(key: false);

    [Fact]
    public Task KeyRest_removes_and_restores_attachments() => RemovesAndRestoresAttachmentsAsync(key: true);

    [Fact]
    public Task KeySh_removes_and_restores_attachments() => WithRenamedAsync(async w =>
    {
        await ShAsync(w, "rm /Garten/Bilder/a.png");
        Assert.Contains("/Garten/Bilder/a.png", await ShAsync(w, "trash /Garten"), StringComparison.Ordinal);
        await ShAsync(w, "restore /Garten/Bilder/a.png");
        Assert.Equal(w.Png, (await GetAsync(w, "/api/files/Garten/Bilder/a.png")).Bytes);
    });

    // --- the board ---

    private static void AssertBoard(JsonNode board, params string[] cards)
    {
        Assert.Equal("Garten", board["title"]!.GetValue<string>());
        var paths = board["columns"]!.AsArray().SelectMany(c => c!["cards"]!.AsArray()).Select(c => c!["path"]!.GetValue<string>()).Order(StringComparer.Ordinal);
        Assert.Equal(cards.Select(c => $"/Garten/Board/{c}.md"), paths);
    }

    private Task UsesTheBoardAsync(bool key) => WithRenamedAsync(async w =>
    {
        var created = await OkAsync(w, HttpMethod.Post, "/api/vaults/Garten/board", bearer: w.Bearer(key));
        Assert.Equal("Garten", created.Json["title"]!.GetValue<string>());
        AssertBoard((await OkAsync(w, HttpMethod.Post, "/api/vaults/Garten/board/cards", new { title = "Saat", column = "Ideas" }, w.Bearer(key))).Json, "Saat");
        AssertBoard((await OkAsync(w, HttpMethod.Post, "/api/vaults/Garten/board/move", new { card = "Saat", column = "Doing" }, w.Bearer(key))).Json, "Saat");
        var board = (await GetAsync(w, "/api/vaults/Garten/board", w.Bearer(key))).Json;
        AssertBoard(board, "Saat");
        Assert.Equal(["Saat"], board["columns"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "Doing")!["cards"]!.AsArray().Select(c => c!["title"]!.GetValue<string>()));
    });

    [Fact]
    public Task Session_uses_the_board() => UsesTheBoardAsync(key: false);

    [Fact]
    public Task KeyRest_uses_the_board() => UsesTheBoardAsync(key: true);

    [Fact]
    public Task KeySh_uses_the_board() => WithRenamedAsync(async w =>
    {
        await ToolAsync(w, "board", new Dictionary<string, object?> { ["vault"] = "Garten", ["action"] = "create" });
        await ToolAsync(w, "board", new Dictionary<string, object?> { ["vault"] = "Garten", ["action"] = "add", ["title"] = "Saat", ["column"] = "Ideas" });
        await ToolAsync(w, "board", new Dictionary<string, object?> { ["vault"] = "Garten", ["action"] = "move", ["card"] = "Saat", ["column"] = "Doing" });
        var shown = await ShAsync(w, "board /Garten");
        Assert.Contains("Garten", shown, StringComparison.Ordinal);
        Assert.Contains("Saat", shown, StringComparison.Ordinal);
        AssertBoard((await GetAsync(w, "/api/vaults/Garten/board")).Json, "Saat");
    });

    // --- links and tags ---

    private static Task LinkedAsync(World w) => OkAsync(w, HttpMethod.Put, "/api/notes/Garten/links.md", new { content = "[[plan]] #saat\n" });

    private Task SeesLinksAndTagsAsync(bool key) => WithRenamedAsync(async w =>
    {
        await LinkedAsync(w);
        var links = (await GetAsync(w, "/api/links/Garten/links.md", w.Bearer(key))).Json;
        Assert.Equal(["/Garten/plan.md"], links.AsArray().Select(l => l!["path"]!.GetValue<string>()));
        var backlinks = (await GetAsync(w, "/api/backlinks/Garten/plan.md", w.Bearer(key))).Json;
        Assert.Equal(["/Garten/links.md"], backlinks.AsArray().Select(l => l!["path"]!.GetValue<string>()));
        var tags = await GetAsync(w, "/api/tags?path=/Garten", w.Bearer(key));
        Assert.Contains("saat", tags.Body, StringComparison.Ordinal);
        var tagged = (await GetAsync(w, "/api/tags/notes?tag=saat&path=/Garten", w.Bearer(key))).Json;
        Assert.Equal(["/Garten/links.md"], tagged.AsArray().Select(l => l!["path"]!.GetValue<string>()));
        Assert.Equal(["/Garten/links.md"], (await GetAsync(w, "/api/tags/notes?tag=saat", w.Bearer(key))).Json.AsArray().Select(l => l!["path"]!.GetValue<string>()));
    });

    [Fact]
    public Task Session_sees_links_and_tags() => SeesLinksAndTagsAsync(key: false);

    [Fact]
    public Task KeyRest_sees_links_and_tags() => SeesLinksAndTagsAsync(key: true);

    [Fact]
    public Task KeySh_sees_links_and_tags() => WithRenamedAsync(async w =>
    {
        await LinkedAsync(w);
        Assert.Contains("/Garten/plan.md", await ShAsync(w, "links /Garten/links.md"), StringComparison.Ordinal);
        Assert.Contains("/Garten/links.md", await ShAsync(w, "backlinks /Garten/plan.md"), StringComparison.Ordinal);
        Assert.Contains("saat", await ShAsync(w, "tags"), StringComparison.Ordinal);
        Assert.Contains("/Garten/links.md", await ShAsync(w, "tags saat /Garten"), StringComparison.Ordinal);
    });

    // --- sync ---

    private Task SyncsAsync(bool key) => WithRenamedAsync(async w =>
    {
        var bearer = w.Bearer(key);
        var full = await GetAsync(w, "/api/vaults/Garten/versions", bearer);
        Assert.Equal(["/Garten/plan.md"], full.Json["notes"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()));
        AssertSomeUnderGarten(full.Json);
        Assert.Equal(StateOf(full.Json), full.Json["state"]!.GetValue<string>());
        var same = await CallAsync(w, HttpMethod.Get, "/api/vaults/Garten/versions", bearer: bearer, headers: new Dictionary<string, string> { ["If-None-Match"] = full.ETag! });
        Assert.Equal(HttpStatusCode.NotModified, same.Status);

        var withFiles = (await GetAsync(w, "/api/vaults/Garten/versions?attachments=1", bearer)).Json;
        Assert.Equal(
            ["/Garten/Bilder/a.png", "/Garten/plan.md", "/Garten/x.pdf"],
            withFiles["notes"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.Equal(StateOf(withFiles), withFiles["state"]!.GetValue<string>());

        await OkAsync(w, HttpMethod.Put, "/api/notes/Garten/neu.md", new { content = "Neu\n" }, bearer);
        var cursor = Uri.EscapeDataString(full.Json["cursor"]!.GetValue<string>());
        var since = (await GetAsync(w, $"/api/vaults/Garten/versions?since={cursor}", bearer)).Json;
        Assert.Contains("/Garten/neu.md", since["notes"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()));
        AssertSomeUnderGarten(since);
    });

    /// <summary>The <c>state</c> a client computes from a full answer: a hash over the paths and versions it lists, as it names them.</summary>
    private static string StateOf(JsonNode versions)
    {
        var text = new StringBuilder();
        foreach (var n in versions["notes"]!.AsArray().OrderBy(n => n!["path"]!.GetValue<string>(), StringComparer.Ordinal))
        {
            text.Append(n!["path"]!.GetValue<string>()).Append('\t').Append(n["version"]!.GetValue<string>()).Append('\n');
        }

        text.Append("damaged\t").Append(versions["damaged"]!.GetValue<int>()).Append('\n');
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    [Fact]
    public Task Session_syncs() => SyncsAsync(key: false);

    [Fact]
    public Task KeyRest_syncs() => SyncsAsync(key: true);

    // --- export: the session's ---

    private static async Task<string> TicketAsync(World w) =>
        Uri.EscapeDataString((await OkAsync(w, HttpMethod.Post, "/api/export/ticket", new { password = RestClient.Password })).Json["ticket"]!.GetValue<string>());

    private static Dictionary<string, string> Entries(Answer answer)
    {
        using var zip = new ZipArchive(new MemoryStream(answer.Bytes), ZipArchiveMode.Read);
        return zip.Entries.Where(e => e.Name.Length > 0).ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public Task Session_exports() => WithRenamedAsync(async w =>
    {
        var vault = await GetAsync(w, $"/api/vaults/Garten/export?ticket={await TicketAsync(w)}");
        var entries = Entries(vault);
        Assert.All(entries.Keys, e => Assert.StartsWith("Garten/", e, StringComparison.Ordinal));
        Assert.Equal("Beete\n", entries["Garten/plan.md"]);
        Assert.Contains("Garten/Bilder/a.png", entries.Keys);
        Assert.Contains("Garten", vault.ContentDisposition, StringComparison.Ordinal);

        await CheckpointAsync(w, "gfd");
        var checkpoint = await GetAsync(w, $"/api/vaults/Garten/checkpoints/gfd/export?ticket={await TicketAsync(w)}");
        var saved = Entries(checkpoint);
        Assert.All(saved.Keys, e => Assert.StartsWith("Garten/", e, StringComparison.Ordinal));
        Assert.Equal("Beete\n", saved["Garten/plan.md"]);
        Assert.Contains("Garten", checkpoint.ContentDisposition, StringComparison.Ordinal);

        var files = await GetAsync(w, $"/api/files/Garten/zip?path=Bilder/a.png&ticket={await TicketAsync(w)}");
        using (var zip = new ZipArchive(new MemoryStream(files.Bytes), ZipArchiveMode.Read))
        {
            Assert.Equal(["Bilder/a.png"], zip.Entries.Select(e => e.FullName));
        }

        Assert.Contains("Garten", files.ContentDisposition, StringComparison.Ordinal);

        var both = await OkAsync(w, HttpMethod.Get, $"/api/export?vault=Garten&vault=Gartenarbeit&ticket={await TicketAsync(w)}", decoy: true);
        var two = Entries(both);
        Assert.Equal(["Garten", "Gartenarbeit"], two.Keys.Select(e => e.Split('/')[0]).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(("Beete\n", "Andere\n"), (two["Garten/plan.md"], two["Gartenarbeit/plan.md"]));
    });

    [Fact]
    public Task KeyRest_exports() => WithRenamedAsync(async w =>
    {
        await CheckpointAsync(w, "gfd");
        foreach (var url in new[] { "/api/vaults/Garten/export", "/api/vaults/Garten/checkpoints/gfd/export", "/api/files/Garten/zip?path=Bilder/a.png", "/api/export?vault=Garten" })
        {
            // Refused for being a key, not for lacking a ticket: a key never gets as far as the ticket.
            var refused = await CallAsync(w, HttpMethod.Get, url, bearer: w.Key);
            Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
            Assert.Contains("needs a signed-in session", refused.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("ticket", refused.Body, StringComparison.OrdinalIgnoreCase);
        }
    });

    // --- damage ---

    /// <summary>Damages <c>plan.md</c>'s stored note, found by its stored path, and lets the vault load again.</summary>
    private async Task DamagedAsync(World w)
    {
        var vault = Catalog.Current(w.Id)!;
        var notes = app.Database.GetCollection<BsonDocument>("notes");
        var filter = Builders<BsonDocument>.Filter.Eq("vault", w.Id) & Builders<BsonDocument>.Filter.Eq("pid", new BsonBinaryData(vault.PathId(P("/Gartenarbeit/plan.md"))));
        var doc = await notes.Find(filter).SingleAsync(Ct);
        await notes.UpdateOneAsync(
            filter, new BsonDocument("$set", new BsonDocument("blob", new BsonBinaryData([.. doc["blob"].AsByteArray.Take(40), .. new byte[20]]))), cancellationToken: Ct);
        Catalog.Forget(vault, new InvalidOperationException("test"));
    }

    private Task IsToldOfDamageAsync(bool key) => WithRenamedAsync(async w =>
    {
        await DamagedAsync(w);
        var read = await CallAsync(w, HttpMethod.Get, "/api/notes/Garten/plan.md", bearer: w.Bearer(key));
        Assert.Equal(HttpStatusCode.NotFound, read.Status);
        Assert.Contains("/Garten/plan.md: damaged", read.Body, StringComparison.Ordinal);
    });

    [Fact]
    public Task Session_is_told_of_damage() => IsToldOfDamageAsync(key: false);

    [Fact]
    public Task KeyRest_is_told_of_damage() => IsToldOfDamageAsync(key: true);

    [Fact]
    public Task KeySh_is_told_of_damage() => WithRenamedAsync(async w =>
    {
        await DamagedAsync(w);
        var (failed, text) = await ShAnyAsync(w, "cat /Garten/plan.md");
        Assert.True(failed, text);
        Assert.Contains("/Garten/plan.md: damaged", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Decoy, text, StringComparison.Ordinal);
    });

    // --- beyond the matrix ---

    private async Task<JsonObject> RawMetaAsync(string id, byte[] vaultKey)
    {
        var doc = await app.Database.GetCollection<VaultDocument>("vaults").Find(v => v.Id == id).SingleAsync(Ct);
        return JsonNode.Parse(VaultCrypto.Open(VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []), doc.Meta, $"vault:{id}"))!.AsObject();
    }

    /// <summary>The raw documents of <paramref name="collection"/> that <paramref name="filter"/> selects, in id order, as BSON bytes.</summary>
    private async Task<byte[][]> RawAsync(string collection, FilterDefinition<BsonDocument> filter) =>
        [.. (await app.Database.GetCollection<BsonDocument>(collection).Find(filter).SortBy(d => d["_id"]).ToListAsync(Ct)).Select(d => d.ToBson())];

    private static FilterDefinition<BsonDocument> Of(string vaultId) => Builders<BsonDocument>.Filter.Eq("vault", vaultId);

    /// <summary>The documents of <paramref name="user"/> alone: the "app" database is shared by every test of the collection.</summary>
    private static FilterDefinition<BsonDocument> OfUser(RestClient user) => Builders<BsonDocument>.Filter.Eq("user", user.User.Id);

    private static async Task<string[]> NamesAsync(RestClient user, string? bearer = null)
    {
        using var response = await user.GetAsync("/api/vaults", bearer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await RestClient.JsonAsync(response)).AsArray().Select(v => v!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task The_worked_example_of_the_spec()
    {
        // 1. Alice's Gartenarbeit with plan.md, a board and a checkpoint gfd; Bob a member under Beet; her key Claude and a device.
        var w = await BeforeRenameAsync();
        var alice = w.Alice;
        using (var board = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/board")) Assert.Equal(HttpStatusCode.Created, board.StatusCode);
        using (var gfd = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/checkpoints", new { name = "gfd" })) Assert.Equal(HttpStatusCode.Created, gfd.StatusCode);
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await alice.ShareWithAsync(bob, "Beet", MemberRole.Write);
        var bobKey = await KeyForAsync(bob, w.Id, "Claude", KeyRight.Write);
        var device = await KeyForAsync(alice, w.Id, "Work", KeyRight.Write, ApiKeyKind.Device);
        string historyBefore;
        using (var bobHistory = await bob.GetAsync("/api/history/Beet/plan.md"))
        {
            Assert.Equal(HttpStatusCode.OK, bobHistory.StatusCode);
            historyBefore = await bobHistory.Content.ReadAsStringAsync(Ct);
        }
        var notesBefore = await RawAsync("notes", Of(w.Id));

        using (var renamed = await RenameAsync(alice, "Gartenarbeit", "Garten"))
        {
            Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        }

        // 2. Stored notes don't change: plan.md stays stored under its stored path.
        Assert.Equal(notesBefore, await RawAsync("notes", Of(w.Id)));
        var stored = Catalog.Current(w.Id)!;
        Assert.Contains(stored.Notes.Keys, p => p.Value == "/Gartenarbeit/plan.md");

        // 3. Alice's tab shows Garten, with plan.md; her activity tells of the rename.
        Assert.Equal(["Garten"], await NamesAsync(alice));
        Assert.Equal("Beete\n", (await NoteAsync(w, "plan.md"))["content"]!.GetValue<string>());
        var activity = (await GetAsync(w, "/api/vaults/Garten/activity")).Json;
        Assert.Contains("You renamed this vault.", activity["events"]!.AsArray().Select(e => e!["text"]!.GetValue<string>()));

        // 4. Claude.
        Assert.Contains("Garten/", await ShAsync(w, "ls /"), StringComparison.Ordinal);
        Assert.Contains("Beete", await ShAsync(w, "cat /Garten/plan.md"), StringComparison.Ordinal);
        Assert.Contains("gfd", await ShAsync(w, "lscp /Garten"), StringComparison.Ordinal);
        Assert.Contains("Garten", await ShAsync(w, "board /Garten"), StringComparison.Ordinal);
        var (unknown, unknownText) = await ShAnyAsync(w, "cat /Gartenarbeit/plan.md");
        Assert.True(unknown, unknownText);

        // 5. Bob sees Beet as before; his history and his agent don't notice.
        Assert.Equal(["Beet", "Work"], await NamesAsync(bob));
        using (var read = await bob.GetAsync("/api/notes/Beet/plan.md"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("/Beet/plan.md", (await RestClient.JsonAsync(read))["path"]!.GetValue<string>());
        }

        using (var history = await bob.GetAsync("/api/history/Beet/plan.md"))
        {
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            Assert.Equal(historyBefore, await history.Content.ReadAsStringAsync(Ct));
        }

        using (var events = await bob.GetAsync("/api/vault-events"))
        {
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
            Assert.DoesNotContain("Garten", await events.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        var (_, bobsList) = await bob.ShAsync("ls /", bobKey);
        Assert.Contains("Beet/", bobsList, StringComparison.Ordinal);
        Assert.DoesNotContain("Garten", bobsList, StringComparison.Ordinal);
        var (_, bobsCat) = await bob.ShAsync("cat /Beet/plan.md", bobKey);
        Assert.Contains("Beete", bobsCat, StringComparison.Ordinal);
        Assert.DoesNotContain("Garten", bobsCat, StringComparison.Ordinal);

        // 6. The device's old name answers 404, which the plugin reads as "can't open".
        using (var versions = await alice.GetAsync("/api/vaults/Gartenarbeit/versions", device))
        {
            Assert.Equal(HttpStatusCode.NotFound, versions.StatusCode);
        }

        // Its key keeps its access, now under the new name (the rename removes none): what pairing again gets.
        var renamedVersions = (await GetAsync(w, "/api/vaults/Garten/versions", device)).Json;
        Assert.Contains("/Garten/plan.md", renamedVersions["notes"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()));
        AssertSomeUnderGarten(renamedVersions);
        Assert.Equal(StateOf(renamedVersions), renamedVersions["state"]!.GetValue<string>());

        // 7. A new vault Gartenarbeit: a vault of its own.
        w = w with { DecoyId = await NewDecoyAsync(alice) };
        Assert.NotEqual(w.Id, w.DecoyId);
        Assert.Equal(["Garten", "Gartenarbeit"], await NamesAsync(alice));
        using (var versions = await alice.GetAsync("/api/vaults/Gartenarbeit/versions", device))
        {
            Assert.Equal(HttpStatusCode.NotFound, versions.StatusCode);
        }

        // 8. Re-encrypted, it stays Garten.
        using (var rekeyed = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Garten/rekey", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
        }

        Assert.Equal(["Garten", "Gartenarbeit"], await NamesAsync(alice));
        Assert.Equal("Beete\n", (await NoteAsync(w, "plan.md"))["content"]!.GetValue<string>());
        await AssertDecoyUntouchedAsync(w);

        // 9. Back to Gartenarbeit: refused while the name is taken; once the new vault is gone, no display name is left.
        using (var refused = await RenameAsync(alice, "Garten", "Gartenarbeit"))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("You already have a vault named 'Gartenarbeit'.", await RestClient.ErrorAsync(refused));
        }

        using (var deleted = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/delete", new { password = RestClient.Password, confirm = "Gartenarbeit" }))
        {
            Assert.True(deleted.IsSuccessStatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        }

        using (var back = await RenameAsync(alice, "Garten", "Gartenarbeit"))
        {
            Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        }

        var meta = await RawMetaAsync(w.Id, await Catalog.OwnerVaultKeyAsync(alice.User.Id, alice.UserKey, w.Id, Ct));
        Assert.Equal("Gartenarbeit", meta["Name"]!.GetValue<string>());
        Assert.False(meta.ContainsKey("DisplayName"));
        Assert.Equal(["Gartenarbeit"], await NamesAsync(alice));
    }

    [Fact]
    public async Task The_name_reaches_a_key_and_a_connection_without_touching_their_slots()
    {
        string connectionId = "";
        string access = "";
        byte[][] slots = [], keys = [], connections = [];
        var slotFilter = Builders<BsonDocument>.Filter.Eq("vault", "") & Builders<BsonDocument>.Filter.In("kind", new[] { SlotDocument.ApiKey, SlotDocument.Connection });
        var w = await RenamedAsync(async before =>
        {
            using (var created = await before.Alice.SendAsync(HttpMethod.Post, "/api/connections", new { name = "Work", vaults = new Dictionary<string, string> { ["Gartenarbeit"] = "write" } }))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                connectionId = (await RestClient.JsonAsync(created))["id"]!.GetValue<string>();
            }

            var flow = await OAuthFlow.StartAsync(Api, before.Alice, connectionId, await OAuthFlow.RegisterAsync(Api, "https://claude.test/cb"), "https://claude.test/cb");
            await flow.AllowAsync();
            using (var token = await flow.TokenAsync())
            {
                Assert.Equal(HttpStatusCode.OK, token.StatusCode);
                access = (await RestClient.JsonAsync(token))["access_token"]!.GetValue<string>();
            }

            slotFilter = Builders<BsonDocument>.Filter.Eq("vault", before.Id) & Builders<BsonDocument>.Filter.In("kind", new[] { SlotDocument.ApiKey, SlotDocument.Connection });
            slots = await RawAsync("slots", slotFilter);
            keys = await RawAsync("api_keys", OfUser(before.Alice));
            connections = await RawAsync(Connections.CollectionName, OfUser(before.Alice));
        });

        // Read before anything uses the key or the connection again, which records their use.
        Assert.True(slots.Length >= 3, $"{slots.Length} slots");
        Assert.Equal(slots, await RawAsync("slots", slotFilter));
        Assert.NotEmpty(keys);
        Assert.NotEmpty(connections);
        Assert.Equal(keys, await RawAsync("api_keys", OfUser(w.Alice)));
        Assert.Equal(connections, await RawAsync(Connections.CollectionName, OfUser(w.Alice)));

        var listedKeys = (await GetAsync(w, "/api/keys")).Json.AsArray();
        Assert.All(listedKeys, k => Assert.Equal(["Garten"], k!["vaults"]!.AsObject().Select(v => v.Key)));
        var listedConnection = (await GetAsync(w, "/api/connections")).Json.AsArray().Single(c => c!["id"]!.GetValue<string>() == connectionId)!;
        Assert.Equal(["Garten"], listedConnection["vaults"]!.AsObject().Select(v => v.Key));
        Assert.Contains("Garten/", await ShAsync(w, "ls /"), StringComparison.Ordinal);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(OAuthFlow.ResourceOf(connectionId)),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {access}", ["X-Forwarded-For"] = $"198.28.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}" },
            },
            Api.CreateClient(),
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: Ct);
        var listing = await client.CallToolAsync("sh", new Dictionary<string, object?> { ["command"] = "ls /" }, cancellationToken: Ct);
        var text = Assert.IsType<TextContentBlock>(listing.Content[0]).Text;
        Assert.NotEqual(true, listing.IsError);
        Assert.Contains("Garten/", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Decoy, text, StringComparison.Ordinal);
        await AssertDecoyUntouchedAsync(w);
    }

    [Fact]
    public async Task Stored_documents_do_not_change()
    {
        // No new vault under the old name here: renaming back needs the name free.
        var w = await BeforeRenameAsync();
        await AnalysedAsync(w, "Gartenarbeit");
        using (var gfd = await w.Alice.SendAsync(HttpMethod.Post, "/api/vaults/Gartenarbeit/checkpoints", new { name = "gfd" })) Assert.Equal(HttpStatusCode.Created, gfd.StatusCode);
        using (var trashed = await w.Alice.SendAsync(HttpMethod.Put, "/api/notes/Gartenarbeit/alt.md", new { content = "Alt\n" })) Assert.Equal(HttpStatusCode.Created, trashed.StatusCode);
        using (var removed = await w.Alice.SendAsync(HttpMethod.Delete, "/api/notes/Gartenarbeit/alt.md")) Assert.True(removed.IsSuccessStatusCode);
        string[] collections = ["notes", "attachments", "checkpoints", "trash", "blobs"];
        var before = new Dictionary<string, byte[][]>();
        foreach (var c in collections) before[c] = await RawAsync(c, Of(w.Id));
        Assert.All(collections, c => Assert.NotEmpty(before[c]));

        using (var renamed = await RenameAsync(w.Alice, "Gartenarbeit", "Garten")) Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        foreach (var c in collections) Assert.True(Enumerable.SequenceEqual(before[c], await RawAsync(c, Of(w.Id)), BytesComparer), $"{c} after the rename");
        using (var back = await RenameAsync(w.Alice, "Garten", "Gartenarbeit")) Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        foreach (var c in collections) Assert.True(Enumerable.SequenceEqual(before[c], await RawAsync(c, Of(w.Id)), BytesComparer), $"{c} after renaming back");
    }

    private static readonly IEqualityComparer<byte[]> BytesComparer = EqualityComparer<byte[]>.Create((a, b) => a!.AsSpan().SequenceEqual(b), a => a.Length);

    [Fact]
    public Task A_new_note_after_the_rename_is_stored_under_the_stored_name() => WithRenamedAsync(async w =>
    {
        await OkAsync(w, HttpMethod.Put, "/api/notes/Garten/neu.md", new { content = "Neu\n" });

        var vault = Catalog.Current(w.Id)!;
        Assert.All(vault.Notes.Keys, p => Assert.StartsWith("/Gartenarbeit/", p.Value, StringComparison.Ordinal));
        Assert.Contains(vault.Notes.Keys, p => p.Value == "/Gartenarbeit/neu.md");
        var pathId = vault.PathId(P("/Gartenarbeit/neu.md"));
        var stored = await app.Database.GetCollection<BsonDocument>("notes")
            .Find(Of(w.Id) & Builders<BsonDocument>.Filter.Eq("pid", new BsonBinaryData(pathId))).CountDocumentsAsync(Ct);
        Assert.Equal(1, stored);
        Assert.Equal(0, await app.Database.GetCollection<BsonDocument>("notes")
            .Find(Of(w.Id) & Builders<BsonDocument>.Filter.Eq("pid", new BsonBinaryData(vault.PathId(P("/Garten/neu.md"))))).CountDocumentsAsync(Ct));
    });
}
