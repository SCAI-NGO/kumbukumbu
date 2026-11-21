// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
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

    // The first reader's build is stood in for by holding the graph's gate: the second reader waits behind it.
    [Fact]
    public async Task A_graph_read_whose_caller_has_given_up_is_cancelled()
    {
        var (_, catalog, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[B]]\n");
        await store.GraphAsync(Work, Ct);
        var vault = catalog.OpenVaults.Single();
        await WriteAsync(store, "/Work/A.md", "see [[C]]\n");

        await vault.GraphGate.WaitAsync(Ct);
        try
        {
            using var givenUp = new CancellationTokenSource();
            var read = store.GraphAsync(Work, givenUp.Token);
            Assert.False(read.IsCompleted);

            await givenUp.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10), Ct));
            Assert.Equal(0, vault.GraphGate.CurrentCount);
        }
        finally
        {
            vault.GraphGate.Release();
        }

        Assert.Equal(["/Work/A.md -> ?C"], Edges(await store.GraphAsync(Work, Ct)));
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

    // What the memory limit weighs (VaultCatalog.Sweep) includes the extracts the load holds and the graph once built:
    // 200 links to names nobody has, 100 characters each, are 20,000 characters of link text in the note, again in its
    // extract (its raw text and its parts), and again in the graph as the ghosts' keys and titles.
    [Fact]
    public async Task Memory_figure_counts_the_extracts_and_the_graph()
    {
        var (world, _, store) = await WorldAsync();
        var targets = Enumerable.Range(0, 200).Select(i => $"{new string('x', 96)}{i:D4}").ToList();
        var content = string.Concat(targets.Select(t => $"[[{t}]]\n"));
        await WriteAsync(store, "/Work/A.md", content);
        var linkText = 2L * targets.Sum(t => t.Length);

        var later = world.Catalog();
        await Store(later).GetAsync(P("/Work/A.md"), Ct);
        var vault = later.OpenVaults.Single();
        var loaded = vault.ApproxBytes;
        Assert.InRange(loaded - 2L * content.Length, 2 * linkText, 20 * linkText);

        await Store(later).GraphAsync(Work, Ct);
        Assert.InRange(vault.ApproxBytes - loaded, 2 * linkText, 20 * linkText);
    }

    // Short links weigh far more than their text: measured, about 156 bytes a link of `[[a]]` in the extract and 84 in
    // the graph (security review of 0.6.0). The figure counts them at that weight.
    [Fact]
    public async Task Memory_figure_counts_a_short_link_at_its_measured_weight()
    {
        var (world, _, store) = await WorldAsync();
        const int links = 10_000;
        var content = string.Concat(Enumerable.Repeat("[[a]]\n", links));
        await WriteAsync(store, "/Work/A.md", content);

        var later = world.Catalog();
        await Store(later).GetAsync(P("/Work/A.md"), Ct);
        var vault = later.OpenVaults.Single();
        var loaded = vault.ApproxBytes;
        Assert.InRange(loaded - 2L * content.Length, 150L * links, 170L * links);

        await Store(later).GraphAsync(Work, Ct);
        Assert.InRange(vault.ApproxBytes - loaded, 80L * links, 95L * links);
    }

    [Fact]
    public async Task Uploading_an_attachment_redirects_a_ghost()
    {
        var (_, _, store) = await WorldAsync();
        await WriteAsync(store, "/Work/A.md", "see [[a.png]]\n");
        var before = await store.GraphAsync(Work, Ct);
        Assert.Equal(["/Work/A.md -> ?A.PNG"], Edges(before));

        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var state = new AttachmentState("image/png", bytes.Length, 1, 1, VersionToken.Of(bytes), await store.PutBlobAsync("Work", bytes, Ct));
        var attachment = new StoredAttachment(P("/Work/a.png"), state, 1, Writers.Dev, T0, [], null, Ref: WriterRef.Server);
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentCreates = { attachment } }, Writers.Dev, WriterRef.Server, T0, Ct));
        var after = await store.GraphAsync(Work, Ct);

        Assert.Equal(["/Work/A.md -> /Work/a.png"], Edges(after));
        Assert.NotEqual(before.Version, after.Version);
    }
}
