// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

/// <summary>outline, and section addresses in cat and the filters.</summary>
public class SectionCommandsTests
{
    private const string Plan = """
        ---
        tags: [plan]
        ---
        Intro line.

        # Plan

        ## Goals
        - fast
        - small

        ## Notes
        a

        ## Notes
        b

        """;

    private static readonly Shell Sh = TestShell.Of(("/W/plan.md", Plan), ("/W/flat.md", "no headings\n"), ("/W/My Note.md", "# Ä\nx\n"));

    private static Task<ShellResult> Run(string line) => Sh.RunAsync(line, CancellationToken.None);

    [Fact]
    public async Task OutlineListsHeadingsWithRangesAndSizes()
    {
        var result = await Run("outline /W/plan.md");

        Assert.Equal(
            """
            4-4    (before the first heading)  ~4 tokens
            6-16   # Plan                      ~19 tokens
            8-10     ## Goals                  ~8 tokens
            12-13    ## Notes  [1]             ~4 tokens
            15-16    ## Notes  [2]             ~4 tokens
            Read a section: cat "/W/plan.md#Heading"; nested: #Parent#Child; duplicates: #Heading[2]
            """,
            result.Output);
        Assert.Equal(VersionToken.Of(Plan), result.Version!.Token);
    }

    [Fact]
    public async Task OutlineOfANoteWithoutHeadings() =>
        Assert.Equal("/W/flat.md: no headings (1 line)", (await Run("outline /W/flat.md")).Output);

    [Theory]
    [InlineData("outline", "outline: missing operand")]
    [InlineData("outline /W/plan.md#Goals", "outline: give the note without a section; the outline shows all of them")]
    [InlineData("outline /W/nope.md", "outline: /W/nope.md: No such file or directory")]
    public async Task OutlineErrors(string line, string error) => Assert.Equal(ShellResult.Error(error), await Run(line));

    [Fact]
    public async Task CatOfASectionCarriesTheWholeNotesVersion()
    {
        var result = await Run("cat /W/plan.md#Goals");

        Assert.Equal("## Goals\n- fast\n- small", result.Output);
        Assert.Equal(new NoteVersion("/W/plan.md", VersionToken.Of(Plan)), result.Version);
    }

    [Theory]
    [InlineData("cat /W/plan.md#Plan#Notes[2]", "## Notes\nb")]
    [InlineData("cat \"/W/My Note.md#ä\"", "# Ä\nx")]
    [InlineData("cat /W/plan.md#Plan | head -n 1", "# Plan")]
    [InlineData("wc -l /W/plan.md#Goals", "3 /W/plan.md#Goals")]
    [InlineData("head -n 2 /W/plan.md#Goals", "## Goals\n- fast")]
    [InlineData("sort -r /W/plan.md#Goals", "## Goals\n- small\n- fast")]
    public async Task SectionsWorkWhereverOneNoteIsRead(string line, string expected)
    {
        var result = await Run(line);
        Assert.False(result.IsError, result.Output);
        Assert.Equal(expected, result.Output);
    }

    [Theory]
    [InlineData("cat /W/plan.md#Nope", "cat: /W/plan.md#Nope: no such section ('outline /W/plan.md' lists them)")]
    [InlineData("cat /W/plan.md#Notes", "cat: /W/plan.md#Notes: ambiguous, it matches #Plan#Notes[1], #Plan#Notes[2]")]
    [InlineData("cat /W/plan.md#", "cat: /W/plan.md#: not a section address; use note.md#Heading or note.md#Parent#Child")]
    [InlineData("cat /W/nope.md#X", "cat: /W/nope.md: No such file or directory")]
    [InlineData("cat \"/W/My Note.md#Nope\"", "cat: /W/My Note.md#Nope: no such section ('outline \"/W/My Note.md\"' lists them)")]
    [InlineData("rg x /W/plan.md#Goals", "rg: /W/plan.md#Goals: rg searches whole notes; for one section use: cat \"/W/plan.md#Goals\" | rg …")]
    public async Task SectionErrors(string line, string error) => Assert.Equal(ShellResult.Error(error), await Run(line));
}
