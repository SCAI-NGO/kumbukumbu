// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Notes;

public class SectionEditTests
{
    private const string Plan = "# Plan\n\n## Goals\n- fast\n\n## Notes\n- fast\n- later\n\n## Last\nend";

    private readonly InMemoryNoteStore _store = new(("/W/plan.md", Plan));
    private readonly NoteWriter _writer;

    public SectionEditTests() => _writer = new NoteWriter(_store, new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1)), new FixedCaller(Writers.Dev));

    private Task<ShellResult> Edit(string path, string? oldText, string newText, bool replaceSection = false) =>
        _writer.EditAsync(path, VersionToken.Of(Plan), oldText, newText, CancellationToken.None, replaceSection);

    private async Task<string> Content() =>
        (await _store.ReadAsync(VaultPath.TryParse("/W/plan.md", out var p) ? p : throw new InvalidOperationException(), CancellationToken.None))!;

    [Fact]
    public async Task ReplaceInsideASectionDisambiguates()
    {
        var result = await Edit("/W/plan.md#Notes", "- fast", "- quick");

        Assert.Equal("edited /W/plan.md at line 7 (in section #Plan#Notes)", result.Output);
        Assert.Equal("# Plan\n\n## Goals\n- fast\n\n## Notes\n- quick\n- later\n\n## Last\nend", await Content());
    }

    [Fact]
    public async Task OutsideTheSectionDoesNotCount() => Assert.Equal(
        ShellResult.Error("edit: old_text not found in section #Plan#Goals of /W/plan.md; it must match exactly, including whitespace and line breaks (cat it to see the current text)"),
        await Edit("/W/plan.md#Goals", "- later", "x"));

    [Fact]
    public async Task AppendGoesBeforeTheBlankLinesThatEndTheSection()
    {
        var result = await Edit("/W/plan.md#Goals", null, "- small");

        Assert.Equal("appended to section #Plan#Goals of /W/plan.md at line 5", result.Output);
        Assert.Equal("# Plan\n\n## Goals\n- fast\n- small\n\n## Notes\n- fast\n- later\n\n## Last\nend", await Content());
    }

    [Fact]
    public async Task AppendingASectionAfterAnotherInsertsIt()
    {
        await Edit("/W/plan.md#Goals", null, "\n## Risks\n- none\n");
        Assert.Equal("# Plan\n\n## Goals\n- fast\n\n## Risks\n- none\n\n## Notes\n- fast\n- later\n\n## Last\nend", await Content());
    }

    [Fact]
    public async Task AppendToALastSectionWithoutNewline()
    {
        await Edit("/W/plan.md#Last", null, "more");
        Assert.EndsWith("## Last\nend\nmore", await Content());
    }

    [Fact]
    public async Task AppendToAParentGoesAfterItsSubsections()
    {
        await Edit("/W/plan.md#Plan", null, "closing\n");
        Assert.EndsWith("## Last\nend\nclosing\n", await Content());
    }

    [Fact]
    public async Task ReplaceSectionKeepsTheSpacingAroundIt()
    {
        var result = await Edit("/W/plan.md#Notes", null, "## Notes\n- only one", replaceSection: true);

        Assert.Equal("replaced section #Plan#Notes of /W/plan.md (now 10 lines in the note)", result.Output);
        Assert.Equal("# Plan\n\n## Goals\n- fast\n\n## Notes\n- only one\n\n## Last\nend", await Content());
    }

    [Fact]
    public async Task ReplaceSectionWithTextWithoutAHeadingSaysWhereItWent()
    {
        var result = await Edit("/W/plan.md#Notes", null, "- merged into Goals", replaceSection: true);

        Assert.Equal(
            "replaced section #Plan#Notes of /W/plan.md (now 9 lines in the note); the new text has no heading, so it now belongs to #Plan#Goals",
            result.Output);
    }

    [Fact]
    public async Task ReplacingTheFirstHeadingWithoutOneJoinsThePreamble()
    {
        var result = await Edit("/W/plan.md#Plan", null, "just text\n", replaceSection: true);
        Assert.EndsWith("so it now belongs to the text before the first heading", result.Output);
    }

    [Fact]
    public async Task ReplaceSectionWithNothingRemovesIt()
    {
        var result = await Edit("/W/plan.md#Goals", null, "", replaceSection: true);
        Assert.Equal("removed section #Plan#Goals of /W/plan.md (now 9 lines in the note)", result.Output);
        Assert.Equal("# Plan\n\n\n## Notes\n- fast\n- later\n\n## Last\nend", await Content());
    }

    [Theory]
    [InlineData("/W/plan.md", null, true, "edit: replace_section needs a section in the path, e.g. /Work/plan.md#Goals")]
    [InlineData("/W/plan.md#Goals", "- fast", true, "edit: give either old_text or replace_section, not both")]
    [InlineData("/W/plan.md#Nope", null, false, "edit: /W/plan.md#Nope: no such section ('outline /W/plan.md' lists them)")]
    public async Task Errors(string path, string? oldText, bool replaceSection, string error) =>
        Assert.Equal(ShellResult.Error(error), await Edit(path, oldText, "x", replaceSection));

    [Fact]
    public async Task WriteRejectsASection() => Assert.Equal(
        ShellResult.Error("write: replaces whole notes; to change one section use edit with the section in the path (and replace_section for all of it)"),
        await _writer.WriteAsync("/W/plan.md#Goals", "x", VersionToken.Of(Plan), CancellationToken.None));

    [Fact]
    public async Task EditsAtTheEndKeepTheFinalLineBreak()
    {
        var store = new InMemoryNoteStore(("/W/n.md", "# A\nx\n\n# B\ny\n"));
        var writer = new NoteWriter(store, new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1)), new FixedCaller(Writers.Dev));
        var path = VaultPath.TryParse("/W/n.md", out var p) ? p : throw new InvalidOperationException();

        await writer.EditAsync("/W/n.md#B", VersionToken.Of("# A\nx\n\n# B\ny\n"), null, "## B\nz", CancellationToken.None, replaceSection: true);
        Assert.Equal("# A\nx\n\n## B\nz\n", await store.ReadAsync(path, CancellationToken.None));

        await writer.EditAsync("/W/n.md#B", VersionToken.Of("# A\nx\n\n## B\nz\n"), null, "more", CancellationToken.None);
        Assert.Equal("# A\nx\n\n## B\nz\nmore\n", await store.ReadAsync(path, CancellationToken.None));
    }
}
