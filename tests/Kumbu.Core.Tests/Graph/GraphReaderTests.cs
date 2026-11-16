// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Graph;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Graph;

/// <summary>The callers answer from <see cref="INoteReader.GraphAsync"/>, not from a scan of their own.</summary>
public class GraphReaderTests
{
    private static VaultPath P(string path) => VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path);

    /// <summary>The store's notes as they are; a graph built from <paramref name="graphNotes"/>, which may say otherwise.</summary>
    private static GraphReader Reader(InMemoryNoteStore store, params (string Path, string Content)[] graphNotes) =>
        new(store, LinkGraph.Build("W", graphNotes.ToDictionary(n => P(n.Path), n => LinkExtract.Of(P(n.Path), n.Content)), []));

    [Fact]
    public async Task Backlinks_parse_no_note()
    {
        var store = new InMemoryNoteStore(("/W/A.md", "line zero\nline one\n"), ("/W/B.md", ""));
        var reader = Reader(store, ("/W/A.md", "x\n[[B]]\n"), ("/W/B.md", ""));

        var backlinks = await NoteLinks.ToAsync(reader, P("/W/B.md"), CancellationToken.None);

        Assert.Equal([new Backlink(P("/W/A.md"), 1, "line one", Ambiguous: false)], backlinks);
        Assert.True(reader.NotesRead <= 1, $"{reader.NotesRead} reads of the vault's notes");
    }

    [Fact]
    public async Task Backlinks_leave_out_what_the_content_no_longer_has()
    {
        // Written between the graph and the read of the line texts: C is gone, A got shorter.
        var store = new InMemoryNoteStore(("/W/A.md", "[[B]]\n"), ("/W/B.md", ""));
        var reader = Reader(store, ("/W/A.md", "[[B]]\nx\n[[B]]\n"), ("/W/B.md", ""), ("/W/C.md", "[[B]]\n"));

        var backlinks = await NoteLinks.ToAsync(reader, P("/W/B.md"), CancellationToken.None);

        Assert.Equal([new Backlink(P("/W/A.md"), 0, "[[B]]", Ambiguous: false)], backlinks);
    }

    [Fact]
    public async Task Links_of_the_current_content_come_from_the_graph()
    {
        var store = new InMemoryNoteStore(("/W/A.md", "nothing\n"), ("/W/B.md", ""));
        var reader = Reader(store, ("/W/A.md", "[[B]]\n"), ("/W/B.md", ""));

        var link = Assert.Single(await NoteLinks.OfAsync(reader, P("/W/A.md"), "nothing\n", CancellationToken.None));

        Assert.Equal("B", link.Link.Target);
        Assert.Equal(P("/W/B.md"), link.Resolution.Target);
        Assert.Equal(0, reader.NotesRead);
    }

    [Fact]
    public async Task Links_of_another_version_resolve_against_the_graph()
    {
        var store = new InMemoryNoteStore(("/W/A.md", "nothing\n"), ("/W/B.md", ""));
        // The graph knows a note C the store doesn't: the version's links resolve by the graph's names.
        var reader = Reader(store, ("/W/A.md", ""), ("/W/B.md", ""), ("/W/C.md", ""));

        var link = Assert.Single(await NoteLinks.OfAsync(reader, P("/W/A.md"), "old [[C]]\n", CancellationToken.None));

        Assert.Equal(LinkKind.Note, link.Resolution.Kind);
        Assert.Equal(P("/W/C.md"), link.Resolution.Target);
    }

    [Fact]
    public async Task Default_graph_resolves_the_vaults_attachments()
    {
        var store = new InMemoryNoteStore(("/W/A.md", "![[shot.png]]\n"));
        store.AddAttachment("/W/img/shot.png");

        var graph = await ((INoteReader)store).GraphAsync(P("/W"), CancellationToken.None);

        Assert.Equal(P("/W/img/shot.png"), Assert.Single(graph.LinksOf(P("/W/A.md"))).Resolution.Target);
    }

    /// <summary>Passes the store's calls on, answers <see cref="GraphAsync"/> with a given graph and counts the reads of
    /// a vault's notes.</summary>
    private sealed class GraphReader(InMemoryNoteStore inner, LinkGraph graph) : INoteReader
    {
        public int NotesRead { get; private set; }

        public Task<LinkGraph> GraphAsync(VaultPath vault, CancellationToken ct) => Task.FromResult(graph);

        public Task<IReadOnlyList<Note>> ReadNotesUnderAsync(VaultPath dir, CancellationToken ct)
        {
            NotesRead++;
            return inner.ReadNotesUnderAsync(dir, ct);
        }

        public Task<string?> ReadAsync(VaultPath path, CancellationToken ct) => inner.ReadAsync(path, ct);
        public Task<string?> ReadVersionAsync(VaultPath path, string version, CancellationToken ct) => inner.ReadVersionAsync(path, version, ct);
        public Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct) => inner.ListNotesUnderAsync(dir, ct);
        public Task<IReadOnlyList<string>> ListVaultsAsync(CancellationToken ct) => inner.ListVaultsAsync(ct);
        public Task<MoveRecord?> FindMoveAsync(VaultPath from, CancellationToken ct) => inner.FindMoveAsync(from, ct);
        public Task<IReadOnlyList<MoveRecord>> ListMovesAsync(VaultPath dir, CancellationToken ct) => inner.ListMovesAsync(dir, ct);
        public Task<bool> IsDamagedAsync(VaultPath path, CancellationToken ct) => inner.IsDamagedAsync(path, ct);
        public Task<int> CountDamagedAsync(VaultPath vaultOrRoot, CancellationToken ct) => inner.CountDamagedAsync(vaultOrRoot, ct);
        public Task<int> CountLockedVaultsAsync(CancellationToken ct) => inner.CountLockedVaultsAsync(ct);
    }
}
