// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>links, backlinks, cat, rm and replace on links that resolve by a note's aliases (spec §1, §3.3).</summary>
public class AliasLinkCommandsTests
{
    private const string Kumbukumbu = "---\naliases: [Kumbu, K²]\n---\n# Kumbukumbu\n## Roadmap\nx ^id\n";
    private const string Diary = "Talked with Bob about [[Kumbu]] and the [[K²#Roadmap|roadmap]].\nKumbu needs a calendar.\n";
    private const string Header = "--- embedded notes: shown for reading, not part of this note's text (which ends above) ---";

    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly InMemoryNoteStore _store = new(
        ("/Work/Projects/Kumbukumbu.md", Kumbukumbu),
        ("/Work/Diary/2026-11-12.md", Diary));

    private readonly FakeTimeProvider _time = new(InMemoryNoteStore.Seeded.AddDays(1));
    private readonly Shell _sh;

    public AliasLinkCommandsTests()
    {
        _sh = new Shell(_store, new NoteWriter(_store, _time, new FixedCaller(Writers.Dev)), new NoteMover(_store, _time, new FixedCaller(Writers.Dev)));
    }

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, Ct);

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    // Added only where a test needs it, so the others see the diary as the one linking note.
    private Task Add(string path, string content) =>
        _store.TryCreateAsync(new StoredNote(P(path), content, 1, Writers.Dev, InMemoryNoteStore.Seeded, [], Ref: WriterRef.Server), Ct);

    private Task AddKumbuTeam() => Add("/Work/People/Kumbu Team.md", "---\naliases: [K²]\n---\n");

    [Fact]
    public async Task Links_marks_a_link_resolved_by_alias() => Assert.Equal(
        "1: [[Kumbu]] → /Work/Projects/Kumbukumbu.md (alias)\n1: [[K²#Roadmap|roadmap]] → /Work/Projects/Kumbukumbu.md (alias)",
        (await Run("links /Work/Diary/2026-11-12.md")).Output);

    [Fact]
    public async Task Links_marks_a_block_link_by_alias_after_the_block()
    {
        await Add("/Work/Diary/block.md", "See [[Kumbu#^id]].\n");
        Assert.Equal("1: [[Kumbu#^id]] → /Work/Projects/Kumbukumbu.md#^id (alias)", (await Run("links /Work/Diary/block.md")).Output);
    }

    [Fact]
    public async Task Backlinks_lists_a_line_linking_by_alias() => Assert.Equal(
        "/Work/Diary/2026-11-12.md:1:Talked with Bob about [[Kumbu]] and the [[K²#Roadmap|roadmap]].",
        (await Run("backlinks /Work/Projects/Kumbukumbu.md")).Output);

    [Fact]
    public async Task Links_says_ambiguous_when_two_notes_share_an_alias()
    {
        await AddKumbuTeam();
        Assert.Contains(
            "1: [[K²#Roadmap|roadmap]] → ambiguous: /Work/People/Kumbu Team.md, /Work/Projects/Kumbukumbu.md",
            (await Run("links /Work/Diary/2026-11-12.md")).Output.Split('\n'));
    }

    [Fact]
    public async Task Links_on_a_directory_lists_an_alias_ambiguity()
    {
        await AddKumbuTeam();
        Assert.Equal(
            "/Work/Diary/2026-11-12.md:1: [[K²#Roadmap|roadmap]] → ambiguous: /Work/People/Kumbu Team.md, /Work/Projects/Kumbukumbu.md",
            (await Run("links /Work")).Output);
    }

    [Fact]
    public async Task Cat_embeds_a_note_by_alias()
    {
        await Add("/Work/Diary/embed.md", "![[Kumbu]]\n");
        var result = await Run("cat /Work/Diary/embed.md");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(
            "![[Kumbu]]\n\n" + Header + "\n[line 1: ![[Kumbu]] → /Work/Projects/Kumbukumbu.md]\n" + Kumbukumbu[..^1],
            result.Output);
        Assert.DoesNotContain("(alias)", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rm_counts_links_by_alias() => Assert.EndsWith(
        "; 2 links in /Work/Diary/2026-11-12.md now lead nowhere",
        (await Run("rm /Work/Projects/Kumbukumbu.md")).Output,
        StringComparison.Ordinal);

    [Fact]
    public async Task Replace_preview_sees_links_by_alias()
    {
        var preview = await new Replacer(_store, _time, new Plans(), new FixedCaller(Writers.Dev))
            .RunAsync("/Work/Diary/2026-11-12.md", "Kumbu", "Kumbo", false, null, Ct);

        Assert.Contains(
            "  /Work/Diary/2026-11-12.md:1: [[Kumbu]] → [[Kumbo]]: no such note (now /Work/Projects/Kumbukumbu.md)",
            preview.Output.Split('\n'));
    }

    [Fact]
    public void Help_links_mentions_aliases() => Assert.Contains("aliases", new LinksCommand(_store).Help, StringComparison.Ordinal);

    [Fact]
    public async Task NoteLinks_marks_the_resolution_by_alias()
    {
        var links = await NoteLinks.OfAsync(_store, P("/Work/Diary/2026-11-12.md"), Diary, Ct);

        Assert.Equal(2, links.Count);
        Assert.All(links, l =>
        {
            Assert.Equal(LinkKind.Note, l.Resolution.Kind);
            Assert.Equal(P("/Work/Projects/Kumbukumbu.md"), l.Resolution.Target);
            Assert.True(l.Resolution.ViaAlias);
        });
    }
}
