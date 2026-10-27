// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>links, backlinks and mv through the shell.</summary>
public class LinkCommandsTests
{
    private const string Welcome = "# Welcome\nSee [[kumbu]] and [[Meeting Notes|the meeting]].\n![[diagram.png]] [[nope]] [[plan]] [[#Welcome]]\n";
    private const string Meeting = "# Meetings\nRelated: [[kumbu#Goal]]\n```\n[[kumbu]] in code\n```\n";

    private readonly InMemoryNoteStore _store = new(
        ("/Demo/welcome.md", Welcome),
        ("/Demo/Meeting Notes.md", Meeting),
        ("/Demo/projects/kumbu.md", "# Kumbu\n## Goal\nSee [[welcome]].\n"),
        ("/Demo/a/plan.md", "a"), ("/Demo/b/plan.md", "b"),
        ("/Ideas/x.md", "[[kumbu]] from another vault\n"));

    private readonly Shell _sh;

    public LinkCommandsTests()
    {
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        _sh = new Shell(_store, new NoteWriter(_store, time, new FixedCaller(Writers.Dev)), new NoteMover(_store, time, new FixedCaller(Writers.Dev)));
    }

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, CancellationToken.None);

    private async Task<string> Content(string path) =>
        (await _store.ReadAsync(VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path), CancellationToken.None))!;

    [Fact]
    public async Task LinksShowsWhereEachLinkLeads() => Assert.Equal(
        """
        2: [[kumbu]] → /Demo/projects/kumbu.md
        2: [[Meeting Notes|the meeting]] → /Demo/Meeting Notes.md
        3: ![[diagram.png]] → not found
        3: [[nope]] → not found
        3: [[plan]] → ambiguous: /Demo/a/plan.md, /Demo/b/plan.md
        3: [[#Welcome]] → this note
        """,
        (await Run("links /Demo/welcome.md")).Output);

    [Fact]
    public async Task BacklinksListsLinkingLinesInTheVaultOnly() => Assert.Equal(
        """
        /Demo/Meeting Notes.md:2:Related: [[kumbu#Goal]]
        /Demo/welcome.md:2:See [[kumbu]] and [[Meeting Notes|the meeting]].
        """,
        (await Run("backlinks /Demo/projects/kumbu.md")).Output);

    private const string MarkdownNote =
        "[k](/projects/kumbu.md) [m](Meeting%20Notes.md) [w](<welcome.md#Welcome>)\n[p](plan.md) [n](nope.md) [r](report.docx) [h](#Top) [x](https://x.org)\n";

    // Its own store, so the shared fixtures stay as they are.
    private static Func<string, Task<ShellResult>> WithMarkdownNote()
    {
        var store = new InMemoryNoteStore(
            ("/Demo/md.md", MarkdownNote),
            ("/Demo/welcome.md", Welcome),
            ("/Demo/Meeting Notes.md", Meeting),
            ("/Demo/projects/kumbu.md", "# Kumbu\n"),
            ("/Demo/a/plan.md", "a"), ("/Demo/b/plan.md", "b"));
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var sh = new Shell(store, new NoteWriter(store, time, new FixedCaller(Writers.Dev)), new NoteMover(store, time, new FixedCaller(Writers.Dev)));
        return line => sh.RunAsync(line, CancellationToken.None);
    }

    [Fact]
    public async Task MarkdownLinksAreLinks()
    {
        var sh = WithMarkdownNote();

        Assert.Equal(
            """
            1: [k](/projects/kumbu.md) → /Demo/projects/kumbu.md
            1: [m](Meeting%20Notes.md) → /Demo/Meeting Notes.md
            1: [w](<welcome.md#Welcome>) → /Demo/welcome.md
            2: [p](plan.md) → ambiguous: /Demo/a/plan.md, /Demo/b/plan.md
            2: [n](nope.md) → not found
            2: [r](report.docx) → attachment (not a note)
            2: [h](#Top) → this note
            """,
            (await sh("links /Demo/md.md")).Output);
    }

    [Fact]
    public async Task BacklinksFindMarkdownLinks()
    {
        var sh = WithMarkdownNote();

        Assert.Contains("/Demo/md.md:1:[k](/projects/kumbu.md) ", (await sh("backlinks /Demo/projects/kumbu.md")).Output);
    }

    [Fact]
    public async Task RmCountsMarkdownLinks()
    {
        var sh = WithMarkdownNote();

        Assert.Contains("/Demo/md.md", (await sh("rm /Demo/projects/kumbu.md")).Output.Split("now lead nowhere")[0]);
    }

    [Fact]
    public async Task LinksOfADirectoryListsBrokenMarkdownLinks()
    {
        var sh = WithMarkdownNote();

        Assert.Contains("/Demo/md.md:2: [n](nope.md) → not found", (await sh("links /Demo")).Output);
    }

    [Theory]
    [InlineData("links", "links: missing operand")]
    [InlineData("links /Demo/nope.md", "links: /Demo/nope.md: No such file or directory")]
    [InlineData("backlinks /Demo", "backlinks: /Demo: Is a directory")]
    public async Task LinkErrors(string line, string error) => Assert.Equal(ShellResult.Error(error), await Run(line));

    [Fact]
    public async Task MvRewritesTheLinksToTheMovedNote()
    {
        var result = await Run("mv /Demo/projects/kumbu.md /Demo/archive/kumbu-v1.md");

        Assert.Equal(
            "moved /Demo/projects/kumbu.md to /Demo/archive/kumbu-v1.md; rewrote 2 links in /Demo/Meeting Notes.md, /Demo/welcome.md",
            result.Output);
        Assert.Equal(
            "# Welcome\nSee [[kumbu-v1]] and [[Meeting Notes|the meeting]].\n![[diagram.png]] [[nope]] [[plan]] [[#Welcome]]\n",
            await Content("/Demo/welcome.md"));
        // Heading kept; the link in the code block is not a link and stays.
        Assert.Equal("# Meetings\nRelated: [[kumbu-v1#Goal]]\n```\n[[kumbu]] in code\n```\n", await Content("/Demo/Meeting Notes.md"));
        // Another vault's [[kumbu]] never pointed here.
        Assert.Equal("[[kumbu]] from another vault\n", await Content("/Ideas/x.md"));
        Assert.Null(await _store.ReadAsync(VaultPath.TryParse("/Demo/projects/kumbu.md", out var old) ? old : null!, CancellationToken.None));
        Assert.Equal(VersionToken.Of("# Kumbu\n## Goal\nSee [[welcome]].\n"), result.Version!.Token);
    }

    [Fact]
    public async Task MvIntoADirectoryKeepsTheNameAndNameLinksStillWork()
    {
        // [[Meeting Notes]] still finds the note by its unique name: nothing to rewrite, content untouched.
        var result = await Run("mv \"/Demo/Meeting Notes.md\" /Demo/archive/");

        Assert.Equal("moved /Demo/Meeting Notes.md to /Demo/archive/Meeting Notes.md; no links needed rewriting", result.Output);
        Assert.Equal(Welcome, await Content("/Demo/welcome.md"));
        Assert.Equal(
            "2: [[Meeting Notes|the meeting]] → /Demo/archive/Meeting Notes.md",
            (await Run("links /Demo/welcome.md | rg Meeting")).Output);
    }

    [Fact]
    public async Task MvIntoAnExistingDirectoryWithoutSlash()
    {
        var result = await Run("mv /Demo/a/plan.md /Demo/projects");
        Assert.StartsWith("moved /Demo/a/plan.md to /Demo/projects/plan.md", result.Output);
    }

    [Fact]
    public async Task MvProtectsLinksThatANameCollisionWouldCapture()
    {
        // [[kumbu]] in welcome.md means projects/kumbu.md; a second kumbu.md would make it ambiguous.
        await Run("mv /Demo/a/plan.md /Demo/a/kumbu.md");

        Assert.Contains("See [[projects/kumbu]] and", await Content("/Demo/welcome.md"));
        Assert.Contains("[[projects/kumbu#Goal]]", await Content("/Demo/Meeting Notes.md"));
    }

    [Fact]
    public async Task MvResolvesAnAmbiguityItRemoves()
    {
        // [[plan]] was ambiguous; it pointed nowhere, so it is left alone.
        await Run("mv /Demo/b/plan.md /Demo/b/plan-b.md");
        Assert.Contains("[[plan]]", await Content("/Demo/welcome.md"));
    }

    [Fact]
    public async Task MvRewritesTheMovedNotesOwnRelativeLinks()
    {
        await _store.TryCreateAsync(new StoredNote(VaultPath.TryParse("/Demo/projects/rel.md", out var p) ? p : null!, "[[./kumbu]]\n", 1, Writers.Seed, InMemoryNoteStore.Seeded, []), CancellationToken.None);

        await Run("mv /Demo/projects/rel.md /Demo/rel.md");

        Assert.Equal("[[projects/kumbu]]\n", await Content("/Demo/rel.md"));
    }

    [Fact]
    public async Task KeepLinksMovesOnly()
    {
        var result = await Run("mv --keep-links /Demo/projects/kumbu.md /Demo/kumbu2.md");
        Assert.Equal("moved /Demo/projects/kumbu.md to /Demo/kumbu2.md; links left as they are (--keep-links)", result.Output);
        Assert.Equal(Welcome, await Content("/Demo/welcome.md"));
    }

    // Its own store with the shared fixtures and the given notes: the shell and a reader of a note's content.
    private static (Func<string, Task<ShellResult>> Run, Func<string, Task<string?>> Content) With(params (string Path, string Content)[] notes)
    {
        var store = new InMemoryNoteStore([
            ("/Demo/welcome.md", Welcome),
            ("/Demo/Meeting Notes.md", Meeting),
            ("/Demo/projects/kumbu.md", "# Kumbu\n## Goal\n"),
            ("/Demo/a/plan.md", "a"), ("/Demo/b/plan.md", "b"),
            .. notes]);
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var sh = new Shell(store, new NoteWriter(store, time, new FixedCaller(Writers.Dev)), new NoteMover(store, time, new FixedCaller(Writers.Dev)));
        return (line => sh.RunAsync(line, CancellationToken.None),
            path => store.ReadAsync(VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path), CancellationToken.None));
    }

    [Fact]
    public async Task MvRewritesMarkdownLinksToTheMovedNoteAbsolute()
    {
        var (run, content) = With(("/Demo/md.md", "[k](projects/kumbu.md) and [K](kumbu#Goal)\n"));

        var result = await run("mv /Demo/projects/kumbu.md /Demo/archive/kumbu-v1.md");

        Assert.Equal("[k](/archive/kumbu-v1.md) and [K](/archive/kumbu-v1.md#Goal)\n", await content("/Demo/md.md"));
        Assert.Contains("/Demo/md.md", result.Output);
    }

    [Fact]
    public async Task MvRewritesARelativeLinkInTheMovedNote()
    {
        var (run, content) = With(("/Demo/projects/rel.md", "[k](./kumbu.md) [w](../welcome.md)\n"));

        await run("mv /Demo/projects/rel.md /Demo/deep/er/rel.md");

        Assert.Equal("[k](/projects/kumbu.md) [w](/welcome.md)\n", await content("/Demo/deep/er/rel.md"));
    }

    [Fact]
    public async Task MvLeavesAMarkdownLinkItDoesNotHaveToTouch()
    {
        const string note = "[m](Meeting%20Notes.md) [x](https://x.org/kumbu.md) [n](nope.md)\n";
        var (run, content) = With(("/Demo/md.md", note));

        await run("mv /Demo/projects/kumbu.md /Demo/k2.md");

        Assert.Equal(note, await content("/Demo/md.md"));
    }

    [Fact]
    public async Task MvKeepsTextHeadingAngleBracketsAndTitle()
    {
        var (run, content) = With(("/Demo/md.md", "[My *k*](<projects/kumbu.md#Goal> \"t\")\n"));

        await run("mv /Demo/projects/kumbu.md /Demo/archive/k.md");

        Assert.Equal("[My *k*](</archive/k.md#Goal> \"t\")\n", await content("/Demo/md.md"));
    }

    [Fact]
    public async Task MvRewritesMixedLinksOnOneLine()
    {
        var (run, content) = With(("/Demo/md.md", "[[kumbu]] [k](kumbu.md) [[kumbu#Goal]]\n"));

        await run("mv /Demo/projects/kumbu.md /Demo/k3.md");

        Assert.Equal("[[k3]] [k](/k3.md) [[k3#Goal]]\n", await content("/Demo/md.md"));
    }

    [Fact]
    public async Task MvEncodesWhatNeedsIt()
    {
        var (run, content) = With(("/Demo/md.md", "[k](kumbu.md)\n"));

        var result = await run("mv /Demo/projects/kumbu.md \"/Demo/Plan #2 (draft).md\"");

        Assert.False(result.IsError, result.Output);
        Assert.Equal("[k](/Plan%20%232%20%28draft%29.md)\n", await content("/Demo/md.md"));
        Assert.Equal("1: [k](/Plan%20%232%20%28draft%29.md) → /Demo/Plan #2 (draft).md", (await run("links /Demo/md.md")).Output);
    }

    [Fact]
    public async Task KeepLinksLeavesMarkdownLinksAlone()
    {
        var (run, content) = With(("/Demo/md.md", "[k](projects/kumbu.md)\n"));

        await run("mv --keep-links /Demo/projects/kumbu.md /Demo/kumbu2.md");

        Assert.Equal("[k](projects/kumbu.md)\n", await content("/Demo/md.md"));
    }

    [Fact]
    public async Task MvWithoutLinksToRewrite() =>
        Assert.Equal("moved /Demo/a/plan.md to /Demo/a/plan-a.md; no links needed rewriting", (await Run("mv /Demo/a/plan.md /Demo/a/plan-a.md")).Output);

    [Fact]
    public async Task HistoryMovesWithTheNote()
    {
        await Run("mv /Demo/a/plan.md /Demo/a/moved.md");
        Assert.Contains("seed", (await Run("history /Demo/a/moved.md")).Output);
    }

    [Theory]
    [InlineData("mv /Demo/a/plan.md", "mv: missing operand (mv <path> <target>)")]
    [InlineData("mv /Demo/nope.md /Demo/x.md", "mv: /Demo/nope.md: No such file or directory")]
    [InlineData("mv /Demo/a /Demo/c", "mv: /Demo/a: Is a directory (to move the folder, write /Demo/a/)")]
    [InlineData("mv /Demo/a/plan.md /Demo/b/plan.md", "mv: /Demo/b/plan.md already exists; rm it first, or choose another name")]
    [InlineData("mv /Demo/a/plan.md /Demo/a/plan.md", "mv: /Demo/a/plan.md and /Demo/a/plan.md are the same note")]
    [InlineData("mv /Demo/a/plan.md /Ideas/plan.md", "mv: notes can't move between vaults (Demo → Ideas); a vault is its own space")]
    [InlineData("mv /Demo/a/plan.md /Demo/CON.md", "mv: /Demo/CON.md: 'CON.md' is a reserved name on Windows")]
    [InlineData("mv /Demo/a/plan.md /Demo/a/plan.txt", "mv: /Demo/a/plan.txt: note names end in .md")]
    [InlineData("mv /Demo/a/plan.md /Demo/welcome.md/x.md", "mv: /Demo/welcome.md: Not a directory")]
    [InlineData("mv /Demo/a/plan.md#X /Demo/x.md", "mv: moves whole notes; to move a section, edit both notes")]
    [InlineData("mv -f /Demo/a/plan.md /Demo/x.md", "mv: invalid option -- 'f'")]
    public async Task MvErrors(string line, string error) => Assert.Equal(ShellResult.Error(error), await Run(line));

    [Fact]
    public async Task ABadNameCanBeRepaired()
    {
        await _store.TryCreateAsync(new StoredNote(VaultPath.TryParse("/Demo/a?b.md", out var p) ? p : null!, "x", 1, Writers.Seed, InMemoryNoteStore.Seeded, []), CancellationToken.None);
        Assert.False((await Run("mv \"/Demo/a?b.md\" /Demo/a-b.md")).IsError);
    }

    [Fact]
    public async Task APathKeepsItsLengthAndAMoveBackRestoresTheOriginal()
    {
        await _store.TryCreateAsync(new StoredNote(VaultPath.TryParse("/Demo/paths.md", out var p) ? p : null!,
            "[[projects/kumbu]] [[/projects/kumbu]] [[kumbu.md]] [[Kumbu]]\n", 1, Writers.Seed, InMemoryNoteStore.Seeded, []), CancellationToken.None);

        await Run("mv /Demo/projects/kumbu.md /Demo/archive/old/kumbu-v1.md");
        Assert.Equal("[[old/kumbu-v1]] [[/archive/old/kumbu-v1]] [[kumbu-v1.md]] [[kumbu-v1]]\n", await Content("/Demo/paths.md"));

        await Run("mv /Demo/archive/old/kumbu-v1.md /Demo/projects/kumbu.md");
        Assert.Equal("[[projects/kumbu]] [[/projects/kumbu]] [[kumbu.md]] [[kumbu]]\n", await Content("/Demo/paths.md"));
        Assert.Contains("See [[kumbu]] and", await Content("/Demo/welcome.md"));
    }

    [Fact]
    public async Task AKeptFormThatWouldBeAmbiguousFallsBackToTheVaultPath()
    {
        // A second "plan" exists in /Demo/b; the bare name would be ambiguous from welcome.md.
        await _store.TryCreateAsync(new StoredNote(VaultPath.TryParse("/Demo/x/memo.md", out var p) ? p : null!,
            "m", 1, Writers.Seed, InMemoryNoteStore.Seeded, []), CancellationToken.None);
        await _store.TryCreateAsync(new StoredNote(VaultPath.TryParse("/Demo/links.md", out var q) ? q : null!,
            "[[memo]]\n", 1, Writers.Seed, InMemoryNoteStore.Seeded, []), CancellationToken.None);

        await Run("mv /Demo/x/memo.md /Demo/x/plan.md");

        Assert.Equal("[[x/plan]]\n", await Content("/Demo/links.md"));
    }

    [Fact]
    public async Task LinkRewritesAreTheirOwnMarkedHistoryEntry()
    {
        await Run("mv /Demo/projects/kumbu.md /Demo/projects/kumbu2.md");

        var history = (await Run("history /Demo/welcome.md")).Output.Split('\n');
        Assert.Contains("dev (mv)", history[0]);
        Assert.Contains("seed", history[1]);
    }

    [Fact]
    public async Task TheOldPathSaysWhereTheNoteWent()
    {
        await Run("mv /Demo/a/plan.md /Demo/a/plan-b.md");
        await Run("mv /Demo/a/plan-b.md /Demo/c/plan-c.md");

        Assert.Equal(
            ShellResult.Error("cat: /Demo/a/plan.md: No such file or directory; it was moved to /Demo/c/plan-c.md on 2026-10-02 12:00 UTC (dev)"),
            await Run("cat /Demo/a/plan.md"));
        Assert.StartsWith(
            "mv: /Demo/a/plan.md: No such file or directory; it was moved to /Demo/c/plan-c.md",
            (await Run("mv /Demo/a/plan.md /Demo/x.md")).Output);

        await Run("rm /Demo/c/plan-c.md");
        Assert.EndsWith(
            "it was moved to /Demo/c/plan-c.md on 2026-10-02 12:00 UTC (dev), and is not there any more ('trash' may list it)",
            (await Run("cat /Demo/a/plan.md")).Output);
    }

    [Fact]
    public async Task RmSaysWhichLinksNowLeadNowhere() => Assert.Equal(
        "removed /Demo/projects/kumbu.md; it stays in the trash for 30 days ('restore /Demo/projects/kumbu.md' brings it back); 2 links in /Demo/Meeting Notes.md, /Demo/welcome.md now lead nowhere",
        (await Run("rm /Demo/projects/kumbu.md")).Output);

    [Fact]
    public async Task LinksOfOneSection()
    {
        await _store.TryCreateAsync(new StoredNote(VaultPath.TryParse("/Demo/s.md", out var p) ? p : null!,
            "# A\n[[kumbu]]\n# B\n[[welcome]]\n", 1, Writers.Seed, InMemoryNoteStore.Seeded, []), CancellationToken.None);

        Assert.Equal("4: [[welcome]] → /Demo/welcome.md", (await Run("links /Demo/s.md#B")).Output);
        Assert.Equal("cat: /Demo/s.md#Nope: no such section ('outline /Demo/s.md' lists them)".Replace("cat:", "links:"), (await Run("links /Demo/s.md#Nope")).Output);
    }

    [Fact]
    public async Task LinksOfADirectoryListsTheBrokenOnes() => Assert.Equal(
        """
        /Demo/welcome.md:3: ![[diagram.png]] → not found
        /Demo/welcome.md:3: [[nope]] → not found
        /Demo/welcome.md:3: [[plan]] → ambiguous: /Demo/a/plan.md, /Demo/b/plan.md
        """,
        (await Run("links /Demo")).Output);

    [Fact]
    public async Task LinksOfADirectoryWithoutProblems() =>
        Assert.Equal("(no broken or ambiguous links below /Demo/projects)", (await Run("links /Demo/projects")).Output);

    [Fact]
    public async Task BacklinksSaysWhenNothingLinksAndShowsAmbiguousCandidates() => Assert.Equal(
        "/Demo/welcome.md:3:![[diagram.png]] [[nope]] [[plan]] [[#Welcome]]  (ambiguous, may mean this note)",
        (await Run("backlinks /Demo/a/plan.md")).Output);

    [Fact]
    public async Task BacklinksOfAnUnlinkedNote() =>
        Assert.Equal("(no notes link here)", (await Run("backlinks /Ideas/x.md")).Output);
}
