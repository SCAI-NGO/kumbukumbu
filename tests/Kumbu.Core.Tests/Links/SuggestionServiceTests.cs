// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Kumbu.Core.Tests.Sh;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Links;

public class SuggestionServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 21, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private SuggestionService Service(InMemoryNoteStore store, ICaller? caller = null) =>
        new(store, new NoteWriter(store, _time, caller ?? new FixedCaller(Writers.Dev)));

    private static async Task<string?> Content(InMemoryNoteStore store, string path) => await store.ReadAsync(P(path), Ct);

    private static void Ok(ShellResult result) => Assert.False(result.IsError, result.Output);

    [Fact]
    public async Task Add_link_starts_a_related_section_at_the_end()
    {
        var store = new InMemoryNoteStore(("/Work/a.md", "# A\n\nText.\n"), ("/Work/Deep/b.md", "B\n"));

        var result = await Service(store).AddLinkAsync("/Work/a.md", "/Work/Deep/b.md", Ct);

        Ok(result);
        Assert.Equal("# A\n\nText.\n\n## Related\n\n- [[b]]\n", await Content(store, "/Work/a.md"));
        Assert.Equal((await store.GetAsync(P("/Work/a.md"), Ct))!.Version, result.Version?.Token);
    }

    [Fact]
    public async Task Add_link_appends_to_an_existing_related_section_before_the_next_heading()
    {
        const string before = "# A\n\n## related\n\n- [[x]]\n\n## Later\nMore.\n";
        var store = new InMemoryNoteStore(("/Work/a.md", before), ("/Work/b.md", "B\n"), ("/Work/x.md", "X\n"));

        Ok(await Service(store).AddLinkAsync("/Work/a.md", "/Work/b.md", Ct));

        Assert.Equal("# A\n\n## related\n\n- [[x]]\n- [[b]]\n\n## Later\nMore.\n", await Content(store, "/Work/a.md"));
    }

    [Fact]
    public async Task Add_link_keeps_crlf_and_a_note_without_a_final_newline()
    {
        var store = new InMemoryNoteStore(("/Work/a.md", "Line\r\nLast"), ("/Work/b.md", "B\n"));

        Ok(await Service(store).AddLinkAsync("/Work/a.md", "/Work/b.md", Ct));

        Assert.Equal("Line\r\nLast\r\n\r\n## Related\r\n\r\n- [[b]]\r\n", await Content(store, "/Work/a.md"));
    }

    [Fact]
    public async Task Add_link_writes_the_path_when_the_name_is_ambiguous()
    {
        var store = new InMemoryNoteStore(("/Work/a.md", "A\n"), ("/Work/One/b.md", "B\n"), ("/Work/Two/b.md", "B too\n"));

        Ok(await Service(store).AddLinkAsync("/Work/a.md", "/Work/Two/b.md", Ct));

        Assert.EndsWith("- [[Two/b]]\n", await Content(store, "/Work/a.md"));
    }

    [Fact]
    public async Task Add_link_again_writes_nothing()
    {
        var store = new InMemoryNoteStore(("/Work/a.md", "A\n\n## Related\n\n- [[b]]\n"), ("/Work/b.md", "B\n"));
        var before = (await store.GetAsync(P("/Work/a.md"), Ct))!.Revision;

        Ok(await Service(store).AddLinkAsync("/Work/a.md", "/Work/b.md", Ct));

        Assert.Equal(before, (await store.GetAsync(P("/Work/a.md"), Ct))!.Revision);
    }

    [Theory]
    [InlineData("/Work/a.md", "/Other/b.md")]
    [InlineData("/Work/a.md", "/Work/a.md")]
    [InlineData("/Work/a.md", "/Work/missing.md")]
    [InlineData("/Work/missing.md", "/Work/b.md")]
    [InlineData("/Work/a.md", "/Work/pic.png")]
    public async Task Add_link_refuses_what_is_no_pair_of_notes_in_one_vault(string path, string target)
    {
        var store = new InMemoryNoteStore(("/Work/a.md", "A\n"), ("/Work/b.md", "B\n"), ("/Other/b.md", "B\n"));

        Assert.True((await Service(store).AddLinkAsync(path, target, Ct)).IsError);
        Assert.Equal("A\n", await Content(store, "/Work/a.md"));
    }

    [Fact]
    public async Task Add_link_needs_the_right_to_write()
    {
        var store = new InMemoryNoteStore(("/Work/a.md", "A\n"), ("/Work/b.md", "B\n"));

        store.Infos["Work"] = new VaultInfo("\"Alice\"", AccessLevel.Read, true);

        var result = await Service(store, new FixedCaller("bob", LevelOf: _ => AccessLevel.Read)).AddLinkAsync("/Work/a.md", "/Work/b.md", Ct);

        Assert.True(result.IsError);
        Assert.Equal(ShellErrorKind.Refused, result.Kind);
    }

    [Fact]
    public async Task Not_related_is_kept_in_the_properties_and_undone()
    {
        const string before = "---\ntitle: A\n---\nText.\n";
        var store = new InMemoryNoteStore(("/Work/a.md", before), ("/Work/Deep/b.md", "B\n"), ("/Work/c.md", "C\n"));
        var service = Service(store);

        Ok(await service.NotRelatedAsync("/Work/a.md", "/Work/Deep/b.md", undo: false, Ct));
        Ok(await service.NotRelatedAsync("/Work/a.md", "/Work/c.md", undo: false, Ct));
        Assert.Equal("---\ntitle: A\nnot-related: [\"Deep/b\", c]\n---\nText.\n", await Content(store, "/Work/a.md"));

        Ok(await service.NotRelatedAsync("/Work/a.md", "/Work/Deep/b.md", undo: true, Ct));
        Ok(await service.NotRelatedAsync("/Work/a.md", "/Work/c.md", undo: true, Ct));
        Assert.Equal(before, await Content(store, "/Work/a.md"));
    }

    [Fact]
    public async Task Not_related_refuses_a_note_whose_frontmatter_does_not_parse()
    {
        const string broken = "---\ntitle: [A\n---\nText.\n";
        var store = new InMemoryNoteStore(("/Work/a.md", broken), ("/Work/b.md", "B\n"));

        Assert.True((await Service(store).NotRelatedAsync("/Work/a.md", "/Work/b.md", undo: false, Ct)).IsError);
        Assert.Equal(broken, await Content(store, "/Work/a.md"));
    }

    [Fact]
    public void Not_related_reads_paths_names_and_links()
    {
        var a = P("/Work/a.md");
        var read = NotRelated.Of(a, "---\nnot-related:\n  - Deep/b\n  - \"[[c]]\"\n  - Deep/d.md\n---\n");

        Assert.Equal(["/Work/Deep/b.md", "/Work/Deep/d.md", "/Work/c.md"], read.Select(p => p.Value).Order(StringComparer.Ordinal));
        Assert.Empty(NotRelated.Of(a, "No frontmatter, not-related: b\n"));
    }

    [Fact]
    public void A_pair_is_rejected_from_either_side()
    {
        var a = new Note(P("/Work/a.md"), "---\nnot-related: [b]\n---\n");
        var b = new Note(P("/Work/b.md"), "B\n");
        var c = new Note(P("/Work/c.md"), "---\nnot-related: [a]\n---\n");

        var leftOut = NotRelated.LeftOutFor(a);

        Assert.True(leftOut(b));
        Assert.True(leftOut(c));
        Assert.False(leftOut(new Note(P("/Work/d.md"), "D\n")));
    }
}
