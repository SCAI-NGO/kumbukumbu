// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>tags</c>, and <c>--tag</c> on <c>rg</c>, <c>semsearch</c> and <c>board</c>.</summary>
public class TagsCommandTests
{
    private readonly InMemoryNoteStore _store = new(
        ("/Work/capabilities.md", "---\ntags:\n  - research\n---\n# Capabilities\nModelling capabilities, first notes.\n"),
        ("/Work/kumbu.md", "# Kumbu\nCapabilities of the server. #project/kumbu #Research\n"),
        ("/Work/other.md", "# Other\nCapabilities elsewhere. #project/other\n\n```\n#research in code\n```\n"),
        ("/Work/plain.md", "# Plain\nCapabilities without tags.\n"),
        ("/Ideas/idea.md", "An idea #research\n"));

    private readonly FakeTimeProvider _time = new(new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc));
    private readonly Shell _sh;
    private readonly NoteWriter _writer;

    public TagsCommandTests()
    {
        _writer = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));
        _sh = new Shell(_store, _writer, new NoteMover(_store, _time, new FixedCaller(Writers.Dev)), search: new FakeIndex());
    }

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, CancellationToken.None);

    [Fact]
    public async Task Lists_tags_with_counts_most_used_first()
    {
        Assert.Equal(ShellResult.Ok("""
            #research (3)
            #project/kumbu (1)
            #project/other (1)
            """), await Run("tags"));
        Assert.Equal(ShellResult.Ok("""
            #research (2)
            #project/kumbu (1)
            #project/other (1)
            """), await Run("tags /Work"));
    }

    [Fact]
    public async Task A_listing_shows_the_spelling_used_most()
    {
        var shell = TestShell.Of(("/V/a.md", "#Research"), ("/V/b.md", "#research"), ("/V/c.md", "#research"));
        Assert.Equal("#research (3)", (await shell.RunAsync("tags /V", CancellationToken.None)).Output);

        var tie = TestShell.Of(("/V/a.md", "#Research"), ("/V/b.md", "#research"));
        Assert.Equal("#Research (2)", (await tie.RunAsync("tags /V", CancellationToken.None)).Output);
    }

    [Fact]
    public async Task Lists_the_notes_with_a_tag_as_path_line_text()
    {
        Assert.Equal(ShellResult.Ok("""
            /Work/capabilities.md:3:  - research
            /Work/kumbu.md:2:Capabilities of the server. #project/kumbu #Research
            """), await Run("tags research /Work"));
        Assert.Equal((await Run("tags research /Work")).Output, (await Run("tags '#RESEARCH' /Work")).Output);
    }

    [Fact]
    public async Task A_tag_covers_its_nested_tags()
    {
        Assert.Equal(ShellResult.Ok("""
            /Work/kumbu.md:2:Capabilities of the server. #project/kumbu #Research
            /Work/other.md:2:Capabilities elsewhere. #project/other
            """), await Run("tags project"));
        Assert.Equal("/Work/kumbu.md:2:Capabilities of the server. #project/kumbu #Research", (await Run("tags project/kumbu")).Output);
        Assert.Equal("2", (await Run("tags project /Work | wc -l")).Output);
    }

    [Fact]
    public async Task An_unknown_or_impossible_tag_says_so()
    {
        Assert.Equal(ShellResult.Error("tags: no note below /Work carries #reserch; 'tags /Work' lists the tags"), await Run("tags reserch /Work"));
        Assert.Equal(ShellResult.Error("tags: no note carries #proj; 'tags' lists the tags"), await Run("tags proj"));
        Assert.StartsWith("tags: '2026' can't be a tag", (await Run("tags 2026")).Output);
        Assert.Equal(ShellResult.Error("tags: /Nope/x.md: No such file or directory"), await Run("tags /Nope/x.md"));
        Assert.Equal(ShellResult.Ok("(no tags below /Work/plain.md)"), await Run("tags /Work/plain.md"));
    }

    [Fact]
    public async Task Reads_a_checkpoint()
    {
        Assert.False((await Run("mkcp /Work cp")).IsError);
        var content = await _store.ReadAsync(VaultPath.TryParse("/Work/kumbu.md", out var p) ? p : throw new InvalidOperationException(), CancellationToken.None);
        Assert.False((await _writer.WriteAsync("/Work/kumbu.md", "# Kumbu\nNo tags now.\n", VersionToken.Of(content!), CancellationToken.None)).IsError);

        Assert.DoesNotContain("#project/kumbu", (await Run("tags /Work")).Output);
        var old = await Run("tags --checkpoint cp /Work");
        Assert.Contains("#project/kumbu (1)", old.Output);
        Assert.Equal("/Work/kumbu.md:2:Capabilities of the server. #project/kumbu #Research", (await Run("tags --checkpoint cp project/kumbu /Work")).Output);
        Assert.Contains("tags", (await Run("help checkpoints")).Output);
    }

    [Fact]
    public async Task Help_tags_and_the_command_list()
    {
        Assert.StartsWith("tags [path]", (await Run("help tags")).Output);
        Assert.Contains("tags [tag] [path]", (await Run("help")).Output);
        Assert.Contains("\n  tags ", Manual.ToolDescription);
    }

    [Fact]
    public async Task Rg_tag_narrows_the_notes()
    {
        Assert.Equal(ShellResult.Ok("""
            /Work/capabilities.md:5:# Capabilities
            /Work/capabilities.md:6:Modelling capabilities, first notes.
            /Work/kumbu.md:2:Capabilities of the server. #project/kumbu #Research
            """), await Run("rg --tag research -i capabilit /Work"));
        Assert.Equal("/Work/kumbu.md", (await Run("rg -l --tag research --tag project -i capabilit /Work")).Output);
        Assert.Equal("/Ideas/idea.md\n/Work/capabilities.md\n/Work/kumbu.md", (await Run("rg --files --tag research")).Output);
    }

    [Fact]
    public async Task Rg_tag_refuses_an_unknown_tag_and_a_pipe()
    {
        Assert.Equal(ShellResult.Error("rg: no note below /Work carries #reserch; 'tags /Work' lists the tags"), await Run("rg --tag reserch x /Work"));
        Assert.True((await Run("cat /Work/kumbu.md | rg --tag research x")).IsError);
        Assert.True((await Run("rg --tag research --version abc x /Work/kumbu.md")).IsError);
    }

    [Fact]
    public async Task Semsearch_tag_ranks_only_the_notes_with_the_tag()
    {
        var tagged = await Run("semsearch --words --tag research capabilities /Work");
        Assert.Contains("/Work/capabilities.md", tagged.Output);
        Assert.Contains("/Work/kumbu.md", tagged.Output);
        Assert.DoesNotContain("/Work/other.md", tagged.Output);
        Assert.DoesNotContain("/Work/plain.md", tagged.Output);

        Assert.DoesNotContain("/Work/kumbu.md", (await Run("semsearch --tag research --tag project/other capabilities")).Output);
        Assert.Equal(ShellResult.Error("semsearch: no note carries #nope; 'tags' lists the tags"), await Run("semsearch --tag nope capabilities"));
    }

    [Fact]
    public async Task Semsearch_tag_keeps_the_ranking_of_the_notes_it_keeps()
    {
        // The same scores as a search over only those notes: the filter narrows, it doesn't re-weigh.
        var only = TestShell.Of(
            ("/Work/capabilities.md", "---\ntags:\n  - research\n---\n# Capabilities\nModelling capabilities, first notes.\n"),
            ("/Work/kumbu.md", "# Kumbu\nCapabilities of the server. #project/kumbu #Research\n"));
        var expected = (await only.RunAsync("semsearch --words capabilities /Work", CancellationToken.None)).Output;

        Assert.Equal(expected, (await Run("semsearch --words --tag research capabilities /Work")).Output);
    }

    [Fact]
    public async Task Board_tag_covers_nested_tags_ignoring_case()
    {
        var shell = TestShell.Of(
            ("/Work/Board/Board.md", "---\nkumbu-board: 1\ncolumns:\n  - Doing\n---\n# Work\n"),
            ("/Work/Board/A.md", "---\ncolumn: Doing\nrank: a\ntags: [Project/Kumbu]\n---\n"),
            ("/Work/Board/B.md", "---\ncolumn: Doing\nrank: b\ntags: [projects]\n---\n"),
            ("/Work/Board/C.md", "---\ncolumn: Doing\nrank: c\ntags: [project]\n---\n"));

        var board = (await shell.RunAsync("board /Work --tag '#project'", CancellationToken.None)).Output;

        Assert.Contains("Doing (2)", board);
        Assert.Contains("  A ", board);
        Assert.Contains("  C ", board);
        Assert.DoesNotContain("  B ", board);
    }

    private sealed class FakeIndex : ISearchIndex
    {
        public Task<SemanticResult> SearchAsync(VaultPath scope, string query, int limit, CancellationToken ct) =>
            Task.FromResult(new SemanticResult(SemanticState.Off, [], 0, "off in this test"));
    }
}
