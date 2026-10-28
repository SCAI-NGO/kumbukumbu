// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Links;

/// <summary>Links and embeds that lead to attachments: <c>![[shot.png]]</c>, <c>![alt](/Screens/shot.png)</c>,
/// <c>[text](/report.pdf)</c>.</summary>
public class AttachmentLinkTests
{
    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static readonly LinkResolver Resolver = new("W",
        [P("/W/inbox.md"), P("/W/Screens/notes.md"), P("/W/shot.md")],
        [P("/W/Screens/shot.png"), P("/W/Docs/My report.pdf"), P("/W/a/dup.png"), P("/W/b/dup.png"), P("/Other/elsewhere.png")]);

    private static string Resolve(LinkResolver resolver, string source, string target)
    {
        var r = resolver.Resolve(P(source), target);
        return r.Kind switch
        {
            LinkKind.Note => "note " + r.Target!.Value,
            LinkKind.Attachment => r.Target is { } t ? "attachment " + t.Value : "attachment",
            LinkKind.Ambiguous => "ambiguous: " + string.Join(", ", r.Candidates.Select(c => c.Value).Order()),
            _ => r.Kind.ToString(),
        };
    }

    private static Shell ShellOf(InMemoryNoteStore store)
    {
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        return new Shell(store, new NoteWriter(store, time, new FixedCaller(Writers.Dev)), new NoteMover(store, time, new FixedCaller(Writers.Dev)));
    }

    [Fact]
    public void Markdown_image_links_are_found()
    {
        var links = MarkdownLink.Parse("A ![shot](/Screens/shot.png) and ![a b](<img/a b.png> \"title\") and ![x](My%20report.pdf#page=2)\n");

        Assert.All(links, l => Assert.True(l.Embed));
        Assert.Equal(["/Screens/shot.png", "img/a b.png", "My report.pdf"], links.Select(l => l.Target));
        Assert.Equal(["![shot](/Screens/shot.png)", "![a b](<img/a b.png> \"title\")", "![x](My%20report.pdf#page=2)"], links.Select(l => l.Raw));
        Assert.Equal([2, 33], links.Take(2).Select(l => l.Start));
        // A rewrite keeps the '!', the alt text, the angle brackets and the title.
        Assert.Equal("![a b](</Archive/a%20b.png> \"title\")", links[1].WithPath(P("/W/Archive/a b.png")));
    }

    [Theory]
    [InlineData("/W/inbox.md", "shot.png", "attachment /W/Screens/shot.png")]
    [InlineData("/W/inbox.md", "SHOT.PNG", "attachment /W/Screens/shot.png")]
    [InlineData("/W/inbox.md", "Screens/shot.png", "attachment /W/Screens/shot.png")]
    [InlineData("/W/inbox.md", "/Screens/shot.png", "attachment /W/Screens/shot.png")]
    [InlineData("/W/Screens/notes.md", "./shot.png", "attachment /W/Screens/shot.png")]
    [InlineData("/W/inbox.md", "My report.pdf", "attachment /W/Docs/My report.pdf")]
    // Without its extension the name is a note's.
    [InlineData("/W/inbox.md", "shot", "note /W/shot.md")]
    [InlineData("/W/inbox.md", "dup.png", "ambiguous: /W/a/dup.png, /W/b/dup.png")]
    [InlineData("/W/a/x.md", "dup.png", "attachment /W/a/dup.png")]
    // A file of a kind the vault keeps that isn't there leads nowhere; another kind of file is still just a file.
    [InlineData("/W/inbox.md", "missing.png", "NotFound")]
    [InlineData("/W/inbox.md", "elsewhere.png", "NotFound")]
    [InlineData("/W/inbox.md", "report.docx", "attachment")]
    public void A_wiki_embed_of_a_png_resolves_to_the_attachment(string source, string target, string expected) =>
        Assert.Equal(expected, Resolve(Resolver, source, target));

    [Fact]
    public void Without_the_vaults_attachments_a_file_link_stays_a_file()
    {
        var resolver = new LinkResolver("W", [P("/W/inbox.md")]);

        Assert.Equal("attachment", Resolve(resolver, "/W/inbox.md", "shot.png"));
    }

    [Fact]
    public async Task Links_show_the_attachment_an_embed_leads_to()
    {
        var store = new InMemoryNoteStore(("/W/inbox.md", "![[shot.png]] ![s](Screens/shot.png) [[gone.png]]\n"));
        store.AddAttachment("/W/Screens/shot.png");

        Assert.Equal(
            """
            1: ![[shot.png]] → /W/Screens/shot.png
            1: ![s](Screens/shot.png) → /W/Screens/shot.png
            1: [[gone.png]] → not found
            """,
            (await ShellOf(store).RunAsync("links /W/inbox.md", CancellationToken.None)).Output);
    }

    [Fact]
    public async Task Backlinks_of_an_attachment_list_the_embedding_notes()
    {
        var store = new InMemoryNoteStore(
            ("/W/inbox.md", "# Inbox\n![[shot.png]]\n"),
            ("/W/Screens/notes.md", "![a](shot.png \"x\")\n"),
            ("/W/report.md", "Text\n[the shot](/Screens/shot.png)\n![[other.png]]\n"),
            ("/W/none.md", "[[shot]]\n"),
            ("/Other/x.md", "![[shot.png]]\n"));
        store.AddAttachment("/W/Screens/shot.png");
        store.AddAttachment("/W/other.png");

        var result = await ShellOf(store).RunAsync("backlinks /W/Screens/shot.png", CancellationToken.None);

        Assert.False(result.IsError, result.Output);
        Assert.Equal(
            """
            /W/inbox.md:2:![[shot.png]]
            /W/report.md:2:[the shot](/Screens/shot.png)
            /W/Screens/notes.md:1:![a](shot.png "x")
            """,
            result.Output);
        Assert.Equal("/W/Screens/shot.png", result.Version!.Path);
    }

    [Theory]
    [InlineData("![x](https://example.org/shot.png)")]
    [InlineData("![x](http://example.org/Screens/shot.png)")]
    [InlineData("![x](//cdn.example.org/shot.png)")]
    [InlineData("![x](data:image/png;base64,iVBORw0KGgo=)")]
    [InlineData("[x](file:///W/Screens/shot.png)")]
    public async Task An_external_image_url_is_never_a_vault_link(string line)
    {
        Assert.Empty(NoteLink.Parse(line));

        var store = new InMemoryNoteStore(("/W/inbox.md", line + "\n"));
        store.AddAttachment("/W/Screens/shot.png");
        var sh = ShellOf(store);

        Assert.Equal("(no notes link here)", (await sh.RunAsync("backlinks /W/Screens/shot.png", CancellationToken.None)).Output);
        Assert.False((await sh.RunAsync("mv /W/Screens/shot.png /W/Archive/shot.png", CancellationToken.None)).IsError);
        Assert.Equal(line + "\n", await store.ReadAsync(P("/W/inbox.md"), CancellationToken.None));
    }
}
