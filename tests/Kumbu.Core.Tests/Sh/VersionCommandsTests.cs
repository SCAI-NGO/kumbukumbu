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
        _writer = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));
        _sh = new Shell(_store, _writer, new NoteMover(_store, _time, new FixedCaller(Writers.Dev)));
    }

    private static string V(string content) => VersionToken.Of(content);

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, CancellationToken.None);

    private async Task WritePlanV2() =>
        Assert.False((await _writer.WriteAsync("/Demo/plan.md", V2, V(V1), CancellationToken.None)).IsError);

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
    [InlineData("diff --version 000000000000 /Demo/plan.md", "diff: takes versions as arguments: diff <path> <version> [<version>]")]
    [InlineData("diff /Demo/plan.md#Goals", "diff: compares whole notes; to look at one section, cat it with --version")]
    [InlineData("diff", "diff: missing operand")]
    public async Task DiffErrors(string line, string error) => Assert.Equal(ShellResult.Error(error), await Run(line));

    // --- history of a directory ---

    private async Task<string> PlanChangeset() =>
        (await _store.GetAsync(VaultPath.TryParse("/Demo/plan.md", out var p) ? p : null!, CancellationToken.None))!.Changeset!.Short;

    [Fact]
    public async Task HistoryOfANoteHonoursSince()
    {
        _time.Advance(TimeSpan.FromHours(3));
        await WritePlanV2();

        var since = (await Run("history --since 1h /Demo/plan.md")).Output;
        Assert.Single(since.Split('\n'));
        Assert.EndsWith("(current)", since, StringComparison.Ordinal);
        Assert.Equal(2, (await Run("history /Demo/plan.md")).Output.Split('\n').Length);
    }

    [Fact]
    public async Task HistoryOfAFolderShowsMovesAsOneRow()
    {
        await Run("mv /Demo/sub/ /Demo/moved/");
        _time.Advance(TimeSpan.FromMinutes(1));
        await Run("mv /Demo/old.md /Demo/older.md");

        var rows = (await Run("history --since 1h /Demo")).Output.Split('\n');

        // The link to old.md in plan.md was rewritten too: a row of its own, with its changeset.
        Assert.Contains(rows, r => r.StartsWith("2026-10-23 14:01 UTC  dev (mv)  /Demo/plan.md ", StringComparison.Ordinal) && r.Contains("changeset"));
        Assert.Contains("2026-10-23 14:01 UTC  dev (mv)  /Demo/old.md → /Demo/older.md", rows);
        Assert.Contains("2026-10-23 14:00 UTC  dev (mv)  /Demo/sub/x.md → /Demo/moved/x.md", rows);
    }

    [Fact]
    public async Task AFolderMoveIsOneRowWithItsNoteCount()
    {
        var store = new InMemoryNoteStore(("/Demo/a/x.md", "x\n"), ("/Demo/a/deep/y.md", "y\n"));
        var sh = new Shell(store, new NoteWriter(store, _time, new FixedCaller(Writers.Dev)), new NoteMover(store, _time, new FixedCaller(Writers.Dev)));
        await sh.RunAsync("mv /Demo/a/ /Demo/b/", CancellationToken.None);

        Assert.Equal(
            "2026-10-23 14:00 UTC  dev (mv)  /Demo/a/ → /Demo/b/  2 notes",
            (await sh.RunAsync("history --since 1h /Demo", CancellationToken.None)).Output);
    }

    [Fact]
    public async Task AFolderMoveThatKeepsItsNameShowsTheFolder()
    {
        var store = new InMemoryNoteStore(("/Demo/projects/x.md", "x\n"), ("/Demo/projects/y.md", "y\n"));
        var sh = new Shell(store, new NoteWriter(store, _time, new FixedCaller(Writers.Dev)), new NoteMover(store, _time, new FixedCaller(Writers.Dev)));
        await sh.RunAsync("mv /Demo/projects/ /Demo/archive/projects/", CancellationToken.None);

        Assert.Equal(
            "2026-10-23 14:00 UTC  dev (mv)  /Demo/projects/ → /Demo/archive/projects/  2 notes",
            (await sh.RunAsync("history --since 1h /Demo", CancellationToken.None)).Output);
    }

    [Fact]
    public async Task ARestoresMovesAreOneRowEach()
    {
        var store = new InMemoryNoteStore(("/W/r/x.md", "x\n"), ("/W/r/y.md", "y\n"));
        var at = _time.GetUtcNow().UtcDateTime;
        store.AddMove(new MoveRecord(Path("/W/elsewhere/x2.md"), Path("/W/r/x.md"), "dev (restore)", at, Ref: WriterRef.Server));
        store.AddMove(new MoveRecord(Path("/W/r/sub/z.md"), Path("/W/r/y.md"), "dev (restore)", at, Ref: WriterRef.Server));
        var sh = new Shell(store, new NoteWriter(store, _time, new FixedCaller(Writers.Dev)), new NoteMover(store, _time, new FixedCaller(Writers.Dev)));

        var rows = (await sh.RunAsync("history --since 1h /W", CancellationToken.None)).Output.Split('\n');

        Assert.Equal(2, rows.Count(r => r.Contains("→")));
        Assert.Contains("2026-10-23 14:00 UTC  dev (restore)  /W/elsewhere/x2.md → /W/r/x.md", rows.Select(r => r.TrimEnd()));
        Assert.Contains("2026-10-23 14:00 UTC  dev (restore)  /W/r/sub/z.md → /W/r/y.md", rows.Select(r => r.TrimEnd()));
        Assert.DoesNotContain("(mv)", string.Join('\n', rows));
    }

    [Fact]
    public async Task AFolderMoveAndASingleMoveAtOneInstantAreTwoRows()
    {
        var store = new InMemoryNoteStore(("/W/b/x.md", "x\n"), ("/W/b/deep/y.md", "y\n"), ("/W/d.md", "d\n"));
        var at = _time.GetUtcNow().UtcDateTime;
        store.AddMove(new MoveRecord(Path("/W/a/x.md"), Path("/W/b/x.md"), "dev", at, Ref: WriterRef.Server));
        store.AddMove(new MoveRecord(Path("/W/a/deep/y.md"), Path("/W/b/deep/y.md"), "dev", at, Ref: WriterRef.Server));
        store.AddMove(new MoveRecord(Path("/W/c.md"), Path("/W/d.md"), "dev", at, Ref: WriterRef.Server));
        var sh = new Shell(store, new NoteWriter(store, _time, new FixedCaller(Writers.Dev)), new NoteMover(store, _time, new FixedCaller(Writers.Dev)));

        // The "what" column is padded to the longest entry, so the note count lines up.
        Assert.Equal(
            "2026-10-23 14:00 UTC  dev (mv)  /W/a/ → /W/b/      2 notes\n2026-10-23 14:00 UTC  dev (mv)  /W/c.md → /W/d.md",
            (await sh.RunAsync("history --since 1h /W", CancellationToken.None)).Output);
    }

    private static VaultPath Path(string value) => VaultPath.TryParse(value, out var p) ? p : throw new ArgumentException(value);

    [Fact]
    public async Task WritesCloseTogetherAreOneChangesetAcrossNotes()
    {
        await WritePlanV2();
        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.False((await _writer.WriteAsync("/Demo/old.md", "o2\n", V("o\n"), CancellationToken.None)).IsError);
        _time.Advance(TimeSpan.FromMinutes(9));
        // 18 minutes after the first write, but never 10 minutes without one: still the same changeset.
        Assert.False((await _writer.WriteAsync("/Demo/plan.md", V1, V(V2), CancellationToken.None)).IsError);

        var plan = (await _store.GetAsync(VaultPath.TryParse("/Demo/plan.md", out var p) ? p : null!, CancellationToken.None))!;
        var old = (await _store.GetAsync(VaultPath.TryParse("/Demo/old.md", out var o) ? o : null!, CancellationToken.None))!;
        Assert.Equal(plan.Changeset, old.Changeset);
        // One version per note in the changeset: the seeded state is kept, the intermediate V2 is not.
        Assert.Equal([V1], plan.History.Select(h => h.Content));
    }

    [Fact]
    public async Task AChangesetEndsAfterTenIdleMinutesOrAnHour()
    {
        await WritePlanV2();
        _time.Advance(TimeSpan.FromMinutes(11));
        await _writer.WriteAsync("/Demo/plan.md", "v3\n", V(V2), CancellationToken.None);
        var plan = (await _store.GetAsync(VaultPath.TryParse("/Demo/plan.md", out var p) ? p : null!, CancellationToken.None))!;
        Assert.Equal([V2, V1], plan.History.Select(h => h.Content));

        var started = plan.Changeset!;
        var version = "v3\n";
        for (var i = 0; i < 7; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(9));
            var next = $"v{i + 4}\n";
            await _writer.WriteAsync("/Demo/plan.md", next, V(version), CancellationToken.None);
            version = next;
        }

        plan = (await _store.GetAsync(p!, CancellationToken.None))!;
        Assert.NotEqual(started.Id, plan.Changeset!.Id);
        Assert.True(plan.Changeset.Started - started.Started >= Changeset.MaxLength);
    }

    [Fact]
    public async Task ARestoreJoinsNoChangeset()
    {
        await WritePlanV2();
        await Run($"restore /Demo/plan.md {V(V1)}");
        _time.Advance(TimeSpan.FromMinutes(1));
        await _writer.WriteAsync("/Demo/plan.md", "after\n", V(V1), CancellationToken.None);

        var plan = (await _store.GetAsync(VaultPath.TryParse("/Demo/plan.md", out var p) ? p : null!, CancellationToken.None))!;
        Assert.Equal([V1, V2, V1], plan.History.Select(h => h.Content));
        Assert.Null(plan.History[0].Changeset);
    }

    [Fact]
    public async Task HistoryOfAVaultListsTheLatestWritesWithTheirSize()
    {
        _time.Advance(TimeSpan.FromHours(1));
        await WritePlanV2();
        var cs = await PlanChangeset();

        Assert.Equal(
            $"""
            2026-10-23 15:00 UTC  dev   /Demo/plan.md   {V(V2)}  +1 -1  changeset {cs}
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
        var cs = await PlanChangeset();

        Assert.Equal($"2026-10-23 15:00 UTC  dev  /Demo/plan.md  {V(V2)}  +1 -1  changeset {cs}", (await Run("history --since 2h /")).Output);
        Assert.Equal($"2026-10-23 15:00 UTC  dev  /Demo/plan.md  {V(V2)}  +1 -1  changeset {cs}", (await Run("history /Demo --since '2026-10-23 14:30'")).Output);
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

    /// <summary>Shows every writer upper-cased, to see which output goes through it.</summary>
    private sealed class ShoutingLabels : IWriterLabels
    {
        public ValueTask<(string Display, string? Kind)> RenderAsync(string label, WriterRef? reference, CancellationToken ct) =>
            ValueTask.FromResult<(string, string?)>((label.ToUpperInvariant(), null));
    }

    [Fact]
    public async Task History_renders_writers_through_the_labels_service()
    {
        var caller = new FixedCaller("claude");
        var writer = new NoteWriter(_store, _time, caller);
        var sh = new Shell(_store, writer, new NoteMover(_store, _time, caller), labels: new ShoutingLabels());
        Assert.False((await writer.WriteAsync("/Demo/plan.md", V2, V(V1), CancellationToken.None)).IsError);
        Assert.False((await sh.RunAsync("rm /Demo/old.md", CancellationToken.None)).IsError);
        Assert.False((await sh.RunAsync("mkcp /Demo before", CancellationToken.None)).IsError);
        Assert.False((await writer.WriteAsync("/Demo/plan.md", V2 + "more\n", V(V2), CancellationToken.None)).IsError);

        var outputs = new[] { "history /Demo/plan.md", "history /Demo", "trash", "lscp /Demo", $"diff /Demo/plan.md {V(V1)}", "diff /Demo/plan.md --checkpoint before" };
        foreach (var command in outputs)
        {
            var output = (await sh.RunAsync(command, CancellationToken.None)).Output;
            Assert.True(output.Contains("CLAUDE", StringComparison.Ordinal) || output.Contains("SEED", StringComparison.Ordinal), $"{command}: {output}");
            Assert.DoesNotContain("claude", output, StringComparison.Ordinal);
        }

        Assert.Contains("SEED", (await sh.RunAsync("history /Demo/plan.md", CancellationToken.None)).Output, StringComparison.Ordinal);
        Assert.Contains("(by CLAUDE)", (await sh.RunAsync("trash", CancellationToken.None)).Output, StringComparison.Ordinal);
        Assert.Matches(@"before\s+/Demo/\s+\S+ \S+ UTC\s+CLAUDE", (await sh.RunAsync("lscp /Demo", CancellationToken.None)).Output);
    }

    [Fact]
    public async Task An_entry_without_a_reference_shows_as_unknown_in_the_shell()
    {
        // As a store reads a note sealed with a version but no reference; no write may store one.
        var path = VaultPath.TryParse("/Demo/plan.md", out var p) ? p : throw new ArgumentException("bad path");
        _store.Put(NoteWriter.Next((await _store.GetAsync(path, CancellationToken.None))!, V2, "claude", WriterRef.Unknown, _time.GetUtcNow().UtcDateTime));

        var history = (await Run("history /Demo/plan.md")).Output;

        Assert.StartsWith($"{V(V2)}  2026-10-23 14:00 UTC  unknown  ", history, StringComparison.Ordinal);
        Assert.DoesNotContain("claude", history, StringComparison.Ordinal);
    }
}
