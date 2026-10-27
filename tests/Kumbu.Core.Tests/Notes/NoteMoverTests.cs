// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Notes;

/// <summary>mv of attachments, alone or with their folder: one batch, links to them rewritten, moves recorded.</summary>
public class NoteMoverTests
{
    private readonly InMemoryNoteStore _store = new(
        ("/W/inbox.md", "![[shot.png]] ![[Screens/shot.png|small]]\n![s](/Screens/shot.png) [r](/Docs/My%20report.pdf#page=2)\n"),
        ("/W/Screens/notes.md", "![a](shot.png \"title\") and ![b](./shot.png)\n"),
        ("/W/Trip/day.md", "![](img/beach.png) ![[beach.png]] [map](<img/map.pdf>)\n"),
        ("/W/other.md", "![](/Trip/img/beach.png) [[Trip/img/map.pdf]]\n"));

    private readonly FakeTimeProvider _time = new(InMemoryNoteStore.Seeded.AddDays(1));

    private readonly NoteMover _mover;

    public NoteMoverTests()
    {
        _store.AddAttachment("/W/Screens/shot.png");
        _store.AddAttachment("/W/Docs/My report.pdf");
        _store.AddAttachment("/W/Trip/img/beach.png");
        _store.AddAttachment("/W/Trip/img/map.pdf");
        _mover = new NoteMover(_store, _time, new FixedCaller(Writers.Dev));
    }

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private async Task<string?> Content(string path) => await _store.ReadAsync(P(path), Ct);

    private Task<ShellResult> Move(string from, string to) => _mover.MoveAsync(P(from), P(to), rewriteLinks: true, Ct);

    [Fact]
    public async Task Moving_an_attachment_rewrites_wiki_and_markdown_links()
    {
        var before = (await _store.GetAttachmentAsync(P("/W/Screens/shot.png"), Ct))!;

        var result = await Move("/W/Screens/shot.png", "/W/Archive/final.png");

        Assert.False(result.IsError, result.Output);
        Assert.Equal("moved /W/Screens/shot.png to /W/Archive/final.png; rewrote 5 links in /W/inbox.md, /W/Screens/notes.md", result.Output);
        Assert.Equal(new NoteVersion("/W/Archive/final.png", before.Version), result.Version);
        Assert.Null(await _store.GetAttachmentAsync(P("/W/Screens/shot.png"), Ct));
        var moved = (await _store.GetAttachmentAsync(P("/W/Archive/final.png"), Ct))!;
        // A move changes no bytes: same state, same writer, no new version.
        Assert.Equal((before.Current, before.LastWriter, before.Revision + 1), (moved.Current, moved.LastWriter, moved.Revision));
        Assert.Equal(
            "![[final.png]] ![[Archive/final.png|small]]\n![s](/Archive/final.png) [r](/Docs/My%20report.pdf#page=2)\n",
            await Content("/W/inbox.md"));
        // Relative Markdown links become absolute, as for notes.
        Assert.Equal("![a](/Archive/final.png \"title\") and ![b](/Archive/final.png)\n", await Content("/W/Screens/notes.md"));
        Assert.Equal(1, _store.Commits);
        var record = (await _store.FindMoveAsync(P("/W/Screens/shot.png"), Ct))!;
        Assert.Equal((P("/W/Archive/final.png"), Writers.Dev), (record.To, record.MovedBy));
    }

    [Fact]
    public async Task Moving_a_pdf_rewrites_its_encoded_markdown_link()
    {
        var result = await Move("/W/Docs/My report.pdf", "/W/Archive/My report.pdf");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(
            "![[shot.png]] ![[Screens/shot.png|small]]\n![s](/Screens/shot.png) [r](/Archive/My%20report.pdf#page=2)\n",
            await Content("/W/inbox.md"));
        Assert.NotNull(await _store.GetAttachmentAsync(P("/W/Archive/My report.pdf"), Ct));
    }

    [Fact]
    public async Task Moving_a_folder_moves_its_images_and_keeps_relative_links_valid()
    {
        var result = await _mover.MoveFolderAsync(P("/W/Trip"), P("/W/Travel/Rome"), rewriteLinks: true, Ct);

        Assert.False(result.IsError, result.Output);
        Assert.Equal("moved /W/Trip/ to /W/Travel/Rome/ (1 note, 2 attachments); rewrote 2 links in /W/other.md", result.Output);
        Assert.Empty(await _store.ListAttachmentsUnderAsync(P("/W/Trip"), Ct));
        Assert.Equal(
            [P("/W/Travel/Rome/img/beach.png"), P("/W/Travel/Rome/img/map.pdf")],
            (await _store.ListAttachmentsUnderAsync(P("/W/Travel"), Ct)).Select(a => a.Path).OrderBy(p => p.Value));
        // Links inside the folder still lead where they did, so they stay as written.
        Assert.Equal("![](img/beach.png) ![[beach.png]] [map](<img/map.pdf>)\n", await Content("/W/Travel/Rome/day.md"));
        Assert.Equal("![](/Travel/Rome/img/beach.png) [[Rome/img/map.pdf]]\n", await Content("/W/other.md"));
        Assert.Equal(1, _store.Commits);
        Assert.Equal(3, (await _store.ListMovesAsync(P("/W/Travel"), Ct)).Count);
    }

    [Fact]
    public async Task Moving_a_note_keeps_its_image_links_leading_to_the_image()
    {
        var result = await Move("/W/Trip/day.md", "/W/Journal/day.md");

        // A path without './' is matched as a wikilink is, so "img/beach.png" still leads to /W/Trip/img/beach.png.
        Assert.Equal("moved /W/Trip/day.md to /W/Journal/day.md; no links needed rewriting", result.Output);
        Assert.Equal("![](img/beach.png) ![[beach.png]] [map](<img/map.pdf>)\n", await Content("/W/Journal/day.md"));
    }

    [Fact]
    public async Task With_keep_links_an_attachment_moves_alone()
    {
        var result = await _mover.MoveAsync(P("/W/Screens/shot.png"), P("/W/shot.png"), rewriteLinks: false, Ct);

        Assert.Equal("moved /W/Screens/shot.png to /W/shot.png; links left as they are (--keep-links)", result.Output);
        Assert.StartsWith("![[shot.png]] ![[Screens/shot.png|small]]", await Content("/W/inbox.md"));
    }

    [Theory]
    [InlineData("/W/Screens/shot.png", "/W/Screens/shot.jpg", "mv: /W/Screens/shot.jpg: shot.png is image/png; its new name must keep its extension")]
    [InlineData("/W/Screens/shot.png", "/W/Screens/shot.md", "mv: /W/Screens/shot.md: shot.png is image/png; its new name must keep its extension")]
    [InlineData("/W/Screens/shot.png", "/W/Docs/My report.pdf/x.png", "mv: /W/Docs/My report.pdf: Not a directory")]
    [InlineData("/W/Screens/shot.png", "/W/Trip/img/beach.png", "mv: /W/Trip/img/beach.png already exists; rm it first, or choose another name")]
    [InlineData("/W/Screens/shot.png", "/W/.hidden.png", "mv: /W/.hidden.png: '.hidden.png' starts with '.', which hides it in Obsidian")]
    [InlineData("/W/inbox.md", "/W/Screens/shot.png/inbox.md", "mv: /W/Screens/shot.png: Not a directory")]
    [InlineData("/W/inbox.md", "/W/Screens/shot.png", "mv: /W/Screens/shot.png: note names end in .md")]
    [InlineData("/W/gone.png", "/W/x.png", "mv: /W/gone.png: No such file or directory")]
    public async Task Refuses_a_move_that_would_break_the_vault(string from, string to, string error)
    {
        var result = await Move(from, to);

        Assert.True(result.IsError);
        Assert.Equal(error, result.Output);
        Assert.Equal(0, _store.Commits);
    }

    [Fact]
    public async Task A_folder_of_attachments_only_moves_too()
    {
        var result = await _mover.MoveFolderAsync(P("/W/Docs"), P("/W/Papers"), rewriteLinks: true, Ct);

        Assert.Equal("moved /W/Docs/ to /W/Papers/ (1 attachment); rewrote 1 link in /W/inbox.md", result.Output);
        Assert.Equal(
            "mv: /W/Screens/shot.png/: Not a directory (to move the attachment, leave out the '/')",
            (await _mover.MoveFolderAsync(P("/W/Screens/shot.png"), P("/W/x"), rewriteLinks: true, Ct)).Output);
    }

    [Fact]
    public async Task A_moved_attachment_tells_where_it_went()
    {
        await Move("/W/Screens/shot.png", "/W/Archive/shot.png");

        var result = await Move("/W/Screens/shot.png", "/W/x.png");

        Assert.Equal("mv: /W/Screens/shot.png: No such file or directory; it was moved to /W/Archive/shot.png on 2026-10-02 12:00 UTC (dev)", result.Output);
    }

    [Fact]
    public async Task A_note_is_not_written_below_an_attachment()
    {
        var writer = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));

        var below = await writer.WriteAsync("/W/Screens/shot.png/n.md", "text", null, Ct);
        var onFolder = await writer.WriteAsync("/W/Trip/img.md", "text", null, Ct);

        Assert.Equal("write: /W/Screens/shot.png: Not a directory", below.Output);
        Assert.False(onFolder.IsError, onFolder.Output);
        Assert.Null(await Content("/W/Screens/shot.png/n.md"));
    }

    [Fact]
    public async Task A_note_is_not_written_where_attachments_make_a_folder()
    {
        var store = new InMemoryNoteStore(("/W/a.md", "a\n"));
        store.AddAttachment("/W/pics.md/x.png");

        var result = await new NoteWriter(store, _time, new FixedCaller(Writers.Dev)).WriteAsync("/W/pics.md", "text", null, Ct);

        Assert.Equal("write: /W/pics.md: Is a directory", result.Output);
    }
}
