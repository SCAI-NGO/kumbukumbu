// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Kumbu.Core.Graph;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>The link extracts stored, sealed, as a cache of the graph (graph backend spec §4.1, §4.2): written after a
/// note's commit, read on unlock where they match the note's state, parsed anew and written back where they don't.</summary>
[Collection("mongo")]
public sealed class LinkExtractStoreTests(MongoFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime T0 = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    private static readonly VaultPath Work = P("/Work");

    private async Task<(VaultWorld World, VaultCatalog Catalog, EncryptedNoteStore Store)> WorldAsync()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        await catalog.CreateAsync("Work", Token, (_, _) => Task.CompletedTask, Ct);
        return (world, catalog, Store(catalog));
    }

    private static async Task WriteAsync(EncryptedNoteStore store, string path, string content)
    {
        var written = await store.GetAsync(P(path), Ct) is { } current
            ? await store.TryReplaceAsync(current, NoteWriter.Next(current, content, Writers.Dev, WriterRef.Server, T0), Ct)
            : await store.TryCreateAsync(new StoredNote(P(path), content, 1, Writers.Dev, T0, [], Ref: WriterRef.Server), Ct);
        Assert.True(written);
    }

    /// <summary>The content's full SHA-256, which an extract is sealed with.</summary>
    private static string Sha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static IMongoCollection<SealedDocument> Links(VaultWorld world) => world.Db.GetCollection<SealedDocument>("links");

    private static IMongoCollection<BsonDocument> Raw(VaultWorld world, string name) => world.Db.GetCollection<BsonDocument>(name);

    /// <summary>The stored extracts, opened, by path.</summary>
    private static async Task<Dictionary<VaultPath, (string? Hash, int Scheme, LinkExtract Extract)>> StoredAsync(VaultWorld world, UnlockedVault vault)
    {
        var result = new Dictionary<VaultPath, (string?, int, LinkExtract)>();
        foreach (var doc in await Links(world).Find(d => d.Vault == vault.Id).ToListAsync(Ct))
        {
            var (path, version, scheme, extract) = vault.OpenExtract(doc);
            Assert.Equal(vault.PathId(path), doc.PathId);
            result[path] = (version, scheme, extract);
        }

        return result;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition did not hold in time");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>Whether every note of the vault has a current extract stored, and nothing else is stored.</summary>
    private static async Task<bool> AllCurrentAsync(VaultWorld world, UnlockedVault vault)
    {
        try
        {
            var stored = await StoredAsync(world, vault);
            return stored.Count == vault.Notes.Count && vault.Notes.All(n =>
                stored.TryGetValue(n.Key, out var s) && s.Hash == Sha256(n.Value.Note.Content) && s.Scheme == LinkExtract.SchemeVersion);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static string[] Edges(LinkGraph graph) =>
        [.. graph.Edges.Select(e => $"{graph.Nodes[e.From].Key} -> {graph.Nodes[e.To].Key}")];

    [Fact]
    public async Task Extract_is_written_after_the_commit_sealed()
    {
        var (world, catalog, store) = await WorldAsync();

        await WriteAsync(store, "/Work/A.md", "see [[Secret target]] #hidden-tag\n");

        var vault = catalog.OpenVaults.Single();
        var doc = await Links(world).Find(d => d.Vault == vault.Id).SingleAsync(Ct);
        Assert.Equal(vault.PathId(P("/Work/A.md")), doc.PathId);
        var blob = Encoding.UTF8.GetString(doc.Blob);
        Assert.DoesNotContain("Secret target", blob);
        Assert.DoesNotContain("hidden-tag", blob);
        var (path, version, scheme, extract) = vault.OpenExtract(doc);
        Assert.Equal(P("/Work/A.md"), path);
        Assert.Equal(Sha256("see [[Secret target]] #hidden-tag\n"), version);
        Assert.Equal(LinkExtract.SchemeVersion, scheme);
        Assert.Equal(["Secret target"], extract.Links.Select(l => l.Target));
        Assert.Equal(["hidden-tag"], extract.Tags);
    }

    [Fact]
    public async Task Sealed_extract_opens_to_what_was_parsed()
    {
        var (_, catalog, _) = await WorldAsync();
        var vault = catalog.OpenVaults.Single();
        // Everything mv's rewrite needs of a link (the alias, a Markdown link's parts) and a time with its offset.
        const string content = "---\naliases: [Pumpe]\nwhen: 2026-11-20T14:30:15+02:00\nuntil: 2026-11-21T09:00Z\n---\n"
            + "#a/b see ![[Plan#Ziel|the plan]] and | [[x\\|y]] | and [text *here*](<Some Note.md#^b1> \"title\") #c\n";
        var parsed = LinkExtract.Of(P("/Work/A.md"), content);
        var doc = new SealedDocument { Vault = vault.Id, PathId = vault.PathId(P("/Work/A.md")), Blob = vault.SealExtract(P("/Work/A.md"), "v1", parsed) };

        var (_, version, _, opened) = vault.OpenExtract(doc);

        Assert.Equal("v1", version);
        Assert.Equal(parsed.Links, opened.Links);
        Assert.Equal(parsed.Aliases, opened.Aliases);
        Assert.Equal(parsed.Tags, opened.Tags);
        Assert.NotNull(parsed.When?.Offset);
        Assert.Equal(TimeSpan.Zero, parsed.Until?.Offset);
        Assert.Equal(parsed.When, opened.When);
        Assert.Equal(parsed.Until, opened.Until);
    }

    [Fact]
    public async Task An_extract_under_another_path_id_does_not_open()
    {
        var (_, catalog, _) = await WorldAsync();
        var vault = catalog.OpenVaults.Single();
        var blob = vault.SealExtract(P("/Work/A.md"), "v1", LinkExtract.Of(P("/Work/A.md"), "[[B]]\n"));

        Assert.ThrowsAny<Exception>(() => vault.OpenExtract(new SealedDocument { Vault = vault.Id, PathId = vault.PathId(P("/Work/B.md")), Blob = blob }));
    }

    [Fact]
    public async Task Unlock_uses_stored_extracts_without_parsing()
    {
        var (world, _, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[B]] and [[Anna]] #x\n");
        await WriteAsync(store, "/Work/B.md", "back to [[A]]\n");
        var before = await store.GraphAsync(Work, Ct);

        var later = world.Catalog();
        var after = await Store(later).GraphAsync(Work, Ct);

        Assert.Equal(0, later.OpenVaults.Single().Parses);
        Assert.True(before.SameAs(after));
        Assert.Equal(["/Work/A.md -> /Work/B.md", "/Work/A.md -> ?ANNA", "/Work/B.md -> /Work/A.md"], Edges(after).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("scheme")]
    // What an extract held before it was sealed with the full hash: the 12-hex version token, trusted no more.
    [InlineData("token")]
    public async Task Stale_version_or_scheme_is_parsed_and_written_back(string stale)
    {
        var (world, catalog, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[B]]\n");
        await WriteAsync(store, "/Work/B.md", "b\n");
        var before = await store.GraphAsync(Work, Ct);
        var vault = catalog.OpenVaults.Single();
        // A's extract claims another state: one that linked to C.
        var wrong = LinkExtract.Of(P("/Work/A.md"), "see [[C]]\n");
        var blob = stale switch
        {
            "version" => vault.SealExtract(P("/Work/A.md"), "another-state", wrong),
            "token" => vault.SealExtract(P("/Work/A.md"), vault.Notes[P("/Work/A.md")].Note.Version, wrong),
            _ => vault.SealExtract(P("/Work/A.md"), Sha256(vault.Notes[P("/Work/A.md")].Note.Content), wrong, scheme: LinkExtract.SchemeVersion - 1),
        };
        await Links(world).UpdateOneAsync(d => d.Vault == vault.Id && d.PathId == vault.PathId(P("/Work/A.md")), Builders<SealedDocument>.Update.Set(d => d.Blob, blob), cancellationToken: Ct);

        var later = world.Catalog();
        var after = await Store(later).GraphAsync(Work, Ct);

        Assert.True(before.SameAs(after));
        var reloaded = later.OpenVaults.Single();
        Assert.Equal(1, reloaded.Parses);
        await WaitUntilAsync(() => AllCurrentAsync(world, reloaded));
        Assert.Equal(["B"], (await StoredAsync(world, reloaded))[P("/Work/A.md")].Extract.Links.Select(l => l.Target));
    }

    [Fact]
    public async Task Missing_extract_never_changes_the_graph()
    {
        var (world, _, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[B]] and [[Anna]]\n");
        await WriteAsync(store, "/Work/B.md", "---\naliases: [Bee]\n---\n[[Bee]] #tag\n");
        var before = await store.GraphAsync(Work, Ct);
        await Links(world).DeleteManyAsync(FilterDefinition<SealedDocument>.Empty, Ct);

        var later = world.Catalog();
        var after = await Store(later).GraphAsync(Work, Ct);

        Assert.True(before.SameAs(after));
        await WaitUntilAsync(() => AllCurrentAsync(world, later.OpenVaults.Single()));
    }

    [Fact]
    public async Task Damaged_extract_is_ignored()
    {
        var (world, _, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[B]]\n");
        var before = await store.GraphAsync(Work, Ct);
        var links = Raw(world, "links");
        var tampered = (await links.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync(Ct))["blob"].AsByteArray.ToArray();
        tampered[^1] ^= 1;
        await links.UpdateOneAsync(FilterDefinition<BsonDocument>.Empty, Builders<BsonDocument>.Update.Set("blob", tampered), cancellationToken: Ct);

        var later = world.Catalog();
        var laterStore = Store(later);
        var after = await laterStore.GraphAsync(Work, Ct);

        Assert.True(before.SameAs(after));
        Assert.Equal(0, await laterStore.CountDamagedAsync(Work, Ct));
        Assert.NotNull(await laterStore.GetAsync(P("/Work/A.md"), Ct));
        await WaitUntilAsync(() => AllCurrentAsync(world, later.OpenVaults.Single()));
    }

    [Fact]
    public async Task Damaged_note_is_left_out()
    {
        var (world, catalog, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[B]]\n");
        await WriteAsync(store, "/Work/B.md", "b\n");
        var vault = catalog.OpenVaults.Single();
        var notes = Raw(world, "notes");
        var filter = new BsonDocument("pid", new BsonBinaryData(vault.PathId(P("/Work/A.md"))));
        var tampered = (await notes.Find(filter).SingleAsync(Ct))["blob"].AsByteArray.ToArray();
        tampered[^1] ^= 1;
        await notes.UpdateOneAsync(filter, Builders<BsonDocument>.Update.Set("blob", tampered), cancellationToken: Ct);

        var later = world.Catalog();
        var laterStore = Store(later);
        var graph = await laterStore.GraphAsync(Work, Ct);

        // Its extract opens, but the note doesn't: it isn't in the graph, and is reported by its own document only.
        Assert.Equal(["/Work/B.md"], graph.Nodes.Select(n => n.Key));
        Assert.Empty(graph.Edges);
        Assert.Equal(1, await laterStore.CountDamagedAsync(Work, Ct));
    }

    [Fact]
    public async Task Removed_note_removes_its_extract()
    {
        var (world, catalog, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[B]]\n");
        await WriteAsync(store, "/Work/B.md", "b\n");
        var vault = catalog.OpenVaults.Single();

        Assert.True(await store.TryTrashAsync((await store.GetAsync(P("/Work/A.md"), Ct))!, Writers.Dev, WriterRef.Server, T0, Ct));

        Assert.Equal([P("/Work/B.md")], (await StoredAsync(world, vault)).Keys);
    }

    [Fact]
    public async Task Moved_note_moves_its_extract()
    {
        var (world, catalog, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[B]]\n");
        var vault = catalog.OpenVaults.Single();
        var old = (await store.GetAsync(P("/Work/A.md"), Ct))!;

        Assert.Empty(await store.TryCommitAsync(
            new NoteBatch { Moves = { (old, old with { Path = P("/Work/Sub/A.md"), Revision = old.Revision + 1 }) } }, Writers.Dev, WriterRef.Server, T0, Ct));

        var stored = await StoredAsync(world, vault);
        Assert.Equal([P("/Work/Sub/A.md")], stored.Keys);
        Assert.Equal(Sha256(old.Content), stored[P("/Work/Sub/A.md")].Hash);
    }
}
