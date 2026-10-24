// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>Earlier versions: --version on reading commands, diff, and history of a directory.</summary>
public class VersionCommandsTests
{
    private const string V1 = "# Plan\n## Goals\nship it\n[[old]]\n";
    private const string V2 = "# Plan\n## Goals\nship it soon\n[[old]]\n";

    private readonly InMemoryNoteStore _store = new(("/Demo/plan.md", V1), ("/Demo/old.md", "o\n"), ("/Demo/sub/x.md", "x\n"), ("/Ideas/i.md", "i\n"));
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 10, 23, 14, 0, 0, DateTimeKind.Utc));
    private readonly NoteWriter _writer;
    private readonly Shell _sh;

    public VersionCommandsTests()
    {
        _writer = new NoteWriter(_store, _time);
        _sh = new Shell(_store, _writer, new NoteMover(_store, _time));
    }

    private static string V(string content) => VersionToken.Of(content);

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, CancellationToken.None);

    private async Task WritePlanV2() =>
        Assert.False((await _writer.WriteAsync("/Demo/plan.md", V2, V(V1), Writers.Dev, CancellationToken.None)).IsError);

    // --- --version ---

    [Fact]
    public async Task CatReadsAnEarlierVersionWithoutAVersionBlock()
    {
        await WritePlanV2();

        var result = await Run($"cat /Demo/plan.md --version {V(V1)}");

        Assert.Equal(ShellResult.Ok(V1), result);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task VersionWorksWithSectionsAndOnEveryReadingCommand()
    {
        await WritePlanV2();
        var v = V(V1);

        Assert.Equal("## Goals\nship it\n[[old]]", (await Run($"cat \"/Demo/plan.md#Goals\" --version={v}")).Output);
        Assert.Equal("# Plan", (await Run($"head -n 1 --version {v} /Demo/plan.md")).Output);
        Assert.Equal("[[old]]", (await Run($"tail -n 1 --version {v} /Demo/plan.md")).Output);
        Assert.Equal("4 /Demo/plan.md", (await Run($"wc -l --version {v} /Demo/plan.md")).Output);
        Assert.Equal("/Demo/plan.md:3:ship it", (await Run($"rg -n --version {v} 'ship it$' /Demo/plan.md")).Output);
        Assert.Contains("## Goals", (await Run($"outline --version {v} /Demo/plan.md")).Output);
        Assert.Contains("[[old]] → /Demo/old.md", (await Run($"links --version {v} /Demo/plan.md")).Output);
        Assert.DoesNotContain("soon", (await Run($"sort --version {v} /Demo/plan.md")).Output);
    }

    [Fact]
    public async Task TheCurrentTokenIsAVersionToo() =>
        Assert.Equal(V1, (await Run($"cat --version {V(V1)} /Demo/plan.md")).Output);

    [Theory]
    [InlineData("cat --version 000000000000 /Demo/plan.md", "cat: /Demo/plan.md has no version 000000000000 ('history /Demo/plan.md' lists them)")]
    [InlineData("ls --version 000000000000 /Demo", "ls: --version names a version of one note; ls lists directories, which have none")]
    [InlineData("rg --version 000000000000 ship /Demo", "rg: --version names a version of one note; give rg a single note")]
    [InlineData("rg --version 000000000000 ship /Demo/plan.md /Demo/old.md", "rg: --version names a version of one note; give rg a single note")]
    public async Task VersionErrors(string line, string error) => Assert.Equal(ShellResult.Error(error), await Run(line));

    [Fact]
    public async Task VersionIsRefusedForPipedInputAndBacklinks()
    {
        Assert.True((await Run("cat /Demo/plan.md | head --version 000000000000")).IsError);
        Assert.True((await Run("backlinks --version 000000000000 /Demo/old.md")).IsError);
    }

    // --- diff ---

    [Fact]
    public async Task DiffShowsTheLastChange()
    {
        await WritePlanV2();

        Assert.Equal(
            $"""
            --- /Demo/plan.md  {V(V1)}  2026-10-01 12:00 UTC  seed
            +++ /Demo/plan.md  {V(V2)}  2026-10-23 14:00 UTC  dev
            @@ -1,4 +1,4 @@
             # Plan
             ## Goals
            -ship it
            +ship it soon
             [[old]]
            """,
            (await Run("diff /Demo/plan.md")).Output);
    }

    [Fact]
    public async Task DiffFromAVersionToTheCurrentOneWithWords()
    {
        await WritePlanV2();

        var output = (await Run($"diff --word /Demo/plan.md {V(V1)}")).Output;

        Assert.Contains("ship it{+ soon+}", output);
    }

    [Fact]
    public async Task DiffBetweenTwoVersionsAndTwoNotes()
    {
        await WritePlanV2();

        Assert.Equal(
            $"(no differences between {V(V1)} and {V(V1)})",
            (await Run($"diff /Demo/plan.md {V(V1)} {V(V1)}")).Output);
        Assert.Contains("+++ /Demo/sub/x.md", (await Run("diff /Demo/old.md /Demo/sub/x.md")).Output);
    }

    [Theory]
    [InlineData("diff /Demo/plan.md", "diff: /Demo/plan.md has no earlier version ('history /Demo/plan.md')")]
    [InlineData("diff /Demo/plan.md 000000000000", "diff: /Demo/plan.md has no version 000000000000 ('history /Demo/plan.md' lists them)")]
    [InlineData("diff /Demo/plan.md#Goals", "diff: compares whole notes; to look at one section, cat it with --version")]
    [InlineData("diff", "diff: missing operand")]
    public async Task DiffErrors(string line, string error) => Assert.Equal(ShellResult.Error(error), await Run(line));

    // --- history of a directory ---

    [Fact]
    public async Task HistoryOfAVaultListsTheLatestWritesWithTheirSize()
    {
        _time.Advance(TimeSpan.FromHours(1));
        await WritePlanV2();

        Assert.Equal(
            $"""
            2026-10-23 15:00 UTC  dev   /Demo/plan.md   {V(V2)}  +1 -1
            2026-10-01 12:00 UTC  seed  /Demo/old.md    {V("o\n")}  +1 -0
            2026-10-01 12:00 UTC  seed  /Demo/plan.md   {V(V1)}  +4 -0
            2026-10-01 12:00 UTC  seed  /Demo/sub/x.md  {V("x\n")}  +1 -0
            """,
            (await Run("history /Demo")).Output);
    }

    [Fact]
    public async Task HistorySinceKeepsRecentWritesOnly()
    {
        _time.Advance(TimeSpan.FromHours(1));
        await WritePlanV2();

        Assert.Equal($"2026-10-23 15:00 UTC  dev  /Demo/plan.md  {V(V2)}  +1 -1", (await Run("history --since 2h /")).Output);
        Assert.Equal($"2026-10-23 15:00 UTC  dev  /Demo/plan.md  {V(V2)}  +1 -1", (await Run("history /Demo --since '2026-10-23 14:30'")).Output);
        Assert.Equal("(no writes below /Ideas since 2026-10-23 14:30 UTC)", (await Run("history --since 30m /Ideas")).Output);
    }

    [Fact]
    public void SinceParsesRelativeAndAbsoluteTimes()
    {
        var now = new DateTime(2026, 10, 23, 14, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddMinutes(-30), Kumbu.Core.Sh.Commands.History.ParseSince("30m", now));
        Assert.Equal(now.AddDays(-1), Kumbu.Core.Sh.Commands.History.ParseSince("1d", now));
        Assert.Equal(new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc), Kumbu.Core.Sh.Commands.History.ParseSince("2026-10-23T12:00Z", now));
        Assert.Null(Kumbu.Core.Sh.Commands.History.ParseSince("yesterday", now));
    }

    [Fact]
    public async Task HistoryOfAMissingDirectory() =>
        Assert.Equal(ShellResult.Error("history: /Demo/nope: No such file or directory"), await Run("history /Demo/nope"));
}
