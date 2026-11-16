// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Crypto;
using Kumbu.Core.Graph;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>The link graph in the working set (graph backend spec §4.4): one snapshot per state of the notes and
/// attachments, versioned by what it holds.</summary>
[Collection("mongo")]
public sealed class GraphStoreTests(MongoFixture fixture)
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

    private static string[] Edges(LinkGraph graph) =>
        [.. graph.Edges.Select(e => $"{graph.Nodes[e.From].Key} -> {graph.Nodes[e.To].Key}")];

    [Fact]
    public async Task Graph_follows_a_write()
    {
        var (_, _, store) = await WorldAsync();

        await WriteAsync(store, "/Work/A.md", "see [[B]]\n");
        Assert.Equal(["/Work/A.md -> ?B"], Edges(await store.GraphAsync(Work, Ct)));

        await WriteAsync(store, "/Work/B.md", "b\n");
        Assert.Equal(["/Work/A.md -> /Work/B.md"], Edges(await store.GraphAsync(Work, Ct)));

        await WriteAsync(store, "/Work/A.md", "no link now\n");
        Assert.Empty(Edges(await store.GraphAsync(Work, Ct)));
    }

    [Fact]
    public async Task Version_unchanged_by_prose()
    {
        var (_, _, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[B]]\n");
        var before = await store.GraphAsync(Work, Ct);

        await WriteAsync(store, "/Work/A.md", "see [[B]], a tpyo fixed\n");
        var after = await store.GraphAsync(Work, Ct);

        Assert.NotSame(before, after);
        Assert.NotNull(before.Version);
        Assert.Equal(before.Version, after.Version);
    }

    [Fact]
    public async Task Version_raised_by_a_new_link_a_tag_and_a_redirecting_note()
    {
        var (_, _, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[Anna]]\n");
        var versions = new List<string?> { (await store.GraphAsync(Work, Ct)).Version };

        await WriteAsync(store, "/Work/A.md", "see [[Anna]] and [[Ben]]\n");
        versions.Add((await store.GraphAsync(Work, Ct)).Version);
        await WriteAsync(store, "/Work/A.md", "see [[Anna]] and [[Ben]] #people\n");
        versions.Add((await store.GraphAsync(Work, Ct)).Version);
        // The ghost "Anna" now leads to the note.
        await WriteAsync(store, "/Work/People/Anna.md", "Anna\n");
        var redirected = await store.GraphAsync(Work, Ct);
        versions.Add(redirected.Version);

        Assert.Equal(4, versions.Distinct().Count());
        Assert.Contains("/Work/A.md -> /Work/People/Anna.md", Edges(redirected));
    }

    [Fact]
    public async Task Version_never_repeats_after_a_reload()
    {
        var (world, _, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[B]]\n");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var catalog = world.Catalog(idleMinutes: 15, time: clock);
        var before = await Store(catalog).GraphAsync(Work, Ct);

        clock.Advance(TimeSpan.FromMinutes(16));
        catalog.Sweep();
        Assert.Equal(0, catalog.OpenCount);
        var after = await Store(catalog).GraphAsync(Work, Ct);

        Assert.True(before.SameAs(after));
        Assert.NotEqual(before.Version, after.Version);
    }

    [Fact]
    public async Task Second_read_without_change_builds_nothing()
    {
        var (_, catalog, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[B]]\n");
        await WriteAsync(store, "/Work/B.md", "b\n");
        var first = await store.GraphAsync(Work, Ct);

        Assert.Same(first, await Store(catalog).GraphAsync(Work, Ct));
    }

    [Fact]
    public async Task Only_changed_notes_are_parsed_again()
    {
        var (_, catalog, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[B]]\n");
        await WriteAsync(store, "/Work/B.md", "b\n");
        await store.GraphAsync(Work, Ct);
        var vault = catalog.OpenVaults.Single();
        var parsed = vault.Parses;

        await WriteAsync(store, "/Work/B.md", "b, longer\n");
        await store.GraphAsync(Work, Ct);

        Assert.Equal(parsed + 1, vault.Parses);
    }

    // A note made again at its path starts at revision 1 again: its extract must not be the old note's.
    [Fact]
    public async Task A_note_made_again_at_its_path_is_parsed_again()
    {
        var (_, _, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[B]]\n");
        await store.GraphAsync(Work, Ct);

        Assert.True(await store.TryTrashAsync((await store.GetAsync(P("/Work/A.md"), Ct))!, Writers.Dev, WriterRef.Server, T0, Ct));
        await WriteAsync(store, "/Work/A.md", "see [[C]]\n");

        Assert.Equal(["/Work/A.md -> ?C"], Edges(await store.GraphAsync(Work, Ct)));
    }
}
