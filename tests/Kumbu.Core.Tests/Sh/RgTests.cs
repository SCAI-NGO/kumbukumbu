// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Sh;

public class RgTests
{
    private static readonly Shell Sh = TestShell.Of(
        ("/Demo/welcome.md", "# Welcome\n\nSee [[kumbu]] and [[Meeting Notes]].\n\n## Try it\n\n- TODO: more\n"),
        ("/Demo/projects/kumbu.md", "# Kumbu²\n\nTODO: pipes\nTODO: rg\ndone: ls\n"),
        ("/Demo/Meeting Notes.md", "# Meetings\n\nKumbu stays stateless.\n"),
        ("/Ideas/Überlegungen.md", "# Überlegungen\n\nGrößere Ideen über Kumbu.\n"),
        ("/Ideas/ctx.md", "a\nb\nMATCH 1\nc\nd\ne\nf\nMATCH 2\ng\nMATCH 3\nh\n"));

    private static async Task<string> Ok(string line)
    {
        var result = await Sh.RunAsync(line, CancellationToken.None);
        Assert.False(result.IsError, result.Output);
        return result.Output;
    }

    private static async Task<string> Err(string line)
    {
        var result = await Sh.RunAsync(line, CancellationToken.None);
        Assert.True(result.IsError, result.Output);
        return result.Output;
    }

    [Fact]
    public async Task SearchesEveryVaultSortedByPathWithLineNumbers() => Assert.Equal(
        "/Demo/projects/kumbu.md:3:TODO: pipes\n/Demo/projects/kumbu.md:4:TODO: rg\n/Demo/welcome.md:7:- TODO: more",
        await Ok("rg TODO"));

    [Fact]
    public async Task SearchesBelowAPath() =>
        Assert.Equal("/Demo/welcome.md:7:- TODO: more", await Ok("rg TODO /Demo/welcome.md"));

    [Fact]
    public async Task SeveralPathsAreSearchedOnce() =>
        Assert.Equal("/Demo/welcome.md:7:- TODO: more", await Ok("rg TODO /Demo/welcome.md /Demo/welcome.md"));

    [Fact]
    public async Task CaseSensitiveByDefault() => Assert.Equal("", await Ok("rg todo /Demo"));

    [Fact]
    public async Task IgnoreCaseAlsoForUmlauts() =>
        Assert.Equal("/Ideas/Überlegungen.md:1:# Überlegungen", await Ok("rg -i '^# üBERLEGUNGEN' /Ideas"));

    [Fact]
    public async Task FilesWithMatches() => Assert.Equal(
        "/Demo/Meeting Notes.md\n/Demo/projects/kumbu.md\n/Demo/welcome.md\n/Ideas/Überlegungen.md",
        await Ok("rg -il kumbu"));

    [Fact]
    public async Task CountPerNoteSkipsNotesWithoutMatches() =>
        Assert.Equal("/Demo/projects/kumbu.md:2\n/Demo/welcome.md:1", await Ok("rg -c TODO"));

    [Fact]
    public async Task NoLineNumbers() =>
        Assert.Equal("/Demo/welcome.md:- TODO: more", await Ok("rg -N TODO /Demo/welcome.md"));

    [Fact]
    public async Task FixedStringsTreatsBracketsLiterally() =>
        Assert.Equal("/Demo/welcome.md:3:See [[kumbu]] and [[Meeting Notes]].", await Ok("rg -F '[[kumbu]]'"));

    [Fact]
    public async Task EscapedRegexFindsWikilinks() =>
        Assert.Equal("/Demo/welcome.md", await Ok(@"rg -l '\[\[kumbu\]\]'"));

    [Fact]
    public async Task WordRegexp()
    {
        Assert.Equal("/Demo/Meeting Notes.md:3:Kumbu stays stateless.\n/Demo/projects/kumbu.md:1:# Kumbu²", await Ok("rg -w Kumbu /Demo"));
        Assert.Equal("", await Ok("rg -w Kumb /Demo"));
    }

    [Fact]
    public async Task InvertMatch() =>
        Assert.Equal("/Demo/projects/kumbu.md:5:done: ls", await Ok("rg -v 'TODO|^#|^$' /Demo/projects"));

    [Fact]
    public async Task MaxCountPerNote() =>
        Assert.Equal("/Demo/projects/kumbu.md:3:TODO: pipes\n/Demo/welcome.md:7:- TODO: more", await Ok("rg -m 1 TODO"));

    [Fact]
    public async Task SeveralPatternsWithE() => Assert.Equal(
        "/Demo/projects/kumbu.md:4:TODO: rg\n/Demo/projects/kumbu.md:5:done: ls",
        await Ok("rg -e done -e 'rg$' /Demo"));

    [Fact]
    public async Task PatternStartingWithDash() =>
        Assert.Equal("/Demo/welcome.md:7:- TODO: more", await Ok("rg -e '- TODO' /Demo"));

    [Fact]
    public async Task ContextAroundMatchesWithSeparators() => Assert.Equal(
        """
        /Ideas/ctx.md-2-b
        /Ideas/ctx.md:3:MATCH 1
        /Ideas/ctx.md-4-c
        --
        /Ideas/ctx.md-7-f
        /Ideas/ctx.md:8:MATCH 2
        /Ideas/ctx.md-9-g
        /Ideas/ctx.md:10:MATCH 3
        /Ideas/ctx.md-11-h
        """,
        await Ok("rg -C1 MATCH /Ideas/ctx.md"));

    [Fact]
    public async Task AfterAndBeforeContext()
    {
        Assert.Equal("/Ideas/ctx.md:10:MATCH 3\n/Ideas/ctx.md-11-h", await Ok("rg -A 1 'MATCH 3' /Ideas/ctx.md"));
        Assert.Equal("/Ideas/ctx.md-1-a\n/Ideas/ctx.md-2-b\n/Ideas/ctx.md:3:MATCH 1", await Ok("rg -B 5 'MATCH 1' /Ideas/ctx.md"));
    }

    [Fact]
    public async Task ContextSeparatesNotes() => Assert.Equal(
        "/Demo/projects/kumbu.md:3:TODO: pipes\n/Demo/projects/kumbu.md-4-TODO: rg\n--\n/Demo/welcome.md:7:- TODO: more",
        await Ok("rg -A1 -m1 TODO /Demo"));

    [Fact]
    public async Task FilesListsEveryNote() => Assert.Equal(
        "/Demo/Meeting Notes.md\n/Demo/projects/kumbu.md\n/Demo/welcome.md\n/Ideas/ctx.md\n/Ideas/Überlegungen.md",
        await Ok("rg --files"));

    [Fact]
    public async Task FilesThenFilterFindsANoteByName() =>
        Assert.Equal("/Demo/projects/kumbu.md", await Ok("rg --files | rg -i 'kumbu\\.md$'"));

    [Fact]
    public async Task FilesBelowPaths() =>
        Assert.Equal("/Demo/projects/kumbu.md\n/Ideas/ctx.md", await Ok("rg --files /Demo/projects /Ideas/ctx.md"));

    [Fact]
    public async Task FiltersPipedLinesWithoutPrefix() =>
        Assert.Equal("TODO: pipes\nTODO: rg", await Ok("cat /Demo/projects/kumbu.md | rg TODO"));

    [Fact]
    public async Task FilterWithLineNumbersAndContext() =>
        Assert.Equal("3:TODO: pipes\n4:TODO: rg\n5-done: ls", await Ok("cat /Demo/projects/kumbu.md | rg -n -A1 TODO"));

    [Fact]
    public async Task FilterCountIsATotal() => Assert.Equal("4", await Ok("rg -il kumbu | rg -c /"));

    [Fact]
    public async Task ResultsCanBeCounted() => Assert.Equal("3", await Ok("rg TODO | wc -l"));

    [Theory]
    [InlineData("rg", "rg: missing pattern (try 'help rg')")]
    [InlineData("rg x Demo", "rg: Demo: paths must be absolute (there is no working directory)")]
    [InlineData("rg x /Nope", "rg: /Nope: No such file or directory")]
    [InlineData("rg --files /Nope", "rg: /Nope: No such file or directory")]
    [InlineData("rg -m x TODO", "rg: invalid number for --max-count: 'x'")]
    [InlineData("rg -C -1 TODO", "rg: invalid number for --context: '-1'")]
    [InlineData("ls / | rg x /Demo", "rg: reads from the pipe, so it takes no path (got '/Demo')")]
    [InlineData("ls / | rg -l x", "rg: -l lists notes, so it needs paths, not piped input")]
    [InlineData("ls / | rg --files", "rg: --files lists notes; it does not read from a pipe")]
    [InlineData("rg --nope x", "rg: unrecognized option '--nope'")]
    public async Task Errors(string line, string expected) => Assert.Equal(expected, await Err(line));

    [Fact]
    public async Task InvalidRegex() => Assert.StartsWith("rg: regex parse error: ", await Err("rg '[a-'"));

    [Theory]
    [InlineData("rg '(?=x)'")]
    [InlineData("rg '(?<!x)y'")]
    [InlineData(@"rg '(a)\1'")]
    public async Task BacktrackingConstructsAreRejected(string line) => Assert.Equal(
        "rg: regex not supported: look-around, backreferences and atomic groups need backtracking, which rg never does",
        await Err(line));

    // A classic catastrophic-backtracking pattern: a backtracking engine takes exponential time on it.
    [Fact]
    public async Task PathologicalPatternIsFast()
    {
        var shell = TestShell.Of(("/W/a.md", new string('a', 5_000) + "!"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await shell.RunAsync("rg '^(a+)+$' /W", CancellationToken.None);
        Assert.Equal(ShellResult.Ok(""), result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    private static readonly Shell NameSh = TestShell.Of(
        ("/Test/Der große Meister.md".Normalize(System.Text.NormalizationForm.FormD), "Nothing about it here.\n"),
        ("/Test/Meister Eckhart.md", "Sermons.\n"),
        ("/Other/Der große Plan.md", "Plans.\n"));

    private static async Task<string> NameOk(string line)
    {
        var result = await NameSh.RunAsync(line, CancellationToken.None);
        Assert.False(result.IsError, result.Output);
        return result.Output;
    }

    [Fact]
    public async Task NoMatchInTheTextPointsToNoteNamesThatMatch() => Assert.Equal(
        "no match in the text; 1 note name matches: /Test/Der große Meister.md (rg --files | rg -i 'große meister' lists them)",
        await NameOk("rg -i 'große meister'"));

    [Fact]
    public async Task TheNameHintFollowsTheScopeAndTheCaseOptions()
    {
        Assert.Equal("", await NameOk("rg 'große meister' /Test"));
        Assert.Equal("", await NameOk("rg -i 'große meister' /Other"));
        Assert.StartsWith("no match in the text; 1 note name matches: /Other/Der große Plan.md", await NameOk("rg -iF 'große plan' /Other"));
    }

    [Fact]
    public async Task TheNameHintListsFiveAndCountsTheRest()
    {
        var sh = TestShell.Of([.. Enumerable.Range(1, 7).Select(i => ($"/V/Meister {i}.md", "text\n"))]);
        var output = (await sh.RunAsync("rg Meister", CancellationToken.None)).Output;

        Assert.Equal(
            "no match in the text; 7 note names match: /V/Meister 1.md, /V/Meister 2.md, /V/Meister 3.md, /V/Meister 4.md, /V/Meister 5.md and 2 more (rg --files | rg Meister lists them)",
            output);
    }

    [Fact]
    public async Task NoHintWhenNoNameMatchesOrTheTextMatches()
    {
        Assert.Equal("", await NameOk("rg -i zzz"));
        Assert.Equal("/Test/Meister Eckhart.md:1:Sermons.", await NameOk("rg Sermons /Test"));
    }

    [Fact]
    public async Task NoHintWhenThereAreHitsOrForInvertedSearchesAndPipes()
    {
        Assert.Equal("", await NameOk("rg -c Meister /Other/\"Der große Plan.md\""));
        Assert.Equal("/Test/Meister Eckhart.md", await NameOk("rg -l Sermons"));
        Assert.Equal("", await NameOk("rg -v Plans /Other"));
        Assert.Equal("0", await NameOk("rg -i 'große meister' | wc -l"));
        Assert.Equal("", await NameOk("rg Meister /Test | head"));
    }

    [Fact]
    public async Task ListingAndCountingFindNothingWithTheHintToo() =>
        Assert.StartsWith("no match in the text; 2 note names match:", await NameOk("rg -lc Meister"));

    [Fact]
    public async Task TheNameHintAlsoWorksOnACheckpoint()
    {
        var result = await NameSh.RunAsync("mkcp /Test cp", CancellationToken.None);
        Assert.False(result.IsError, result.Output);

        Assert.StartsWith("no match in the text; 2 note names match: /Test/Der große Meister.md", await NameOk("rg --checkpoint cp Meister /Test"));
    }
}
