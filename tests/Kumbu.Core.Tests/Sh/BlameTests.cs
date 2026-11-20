// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>blame</c> (blame spec §6): runs of lines by the writer as shown, by line, by section, per note of a
/// folder; what the record can't know read "earlier than".</summary>
public class BlameTests
{
    private const string Protocol = "# Protocol\nstep one\nstep two\nstep three\n";

    private readonly InMemoryNoteStore _store = new(("/Work/protocol.md", Protocol));
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc));

    private static readonly FixedCaller Brita = new("Brita", Human: true) { Reference = WriterRef.ForOwner(WriterRef.Session, null) };
    private static readonly FixedCaller Veyalt = new("Veyalt") { Reference = WriterRef.ForOwner(WriterRef.Agent, "k1") };

    private static readonly DateTime Earlier = new(2026, 11, 17, 18, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private Task<ShellResult> Run(string line, IWriterLabels? labels = null)
    {
        var writer = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));
        return new Shell(_store, writer, new NoteMover(_store, _time, new FixedCaller(Writers.Dev)), labels: labels).RunAsync(line, Ct);
    }

    private async Task Write(ICaller caller, string path, string content)
    {
        _time.Advance(TimeSpan.FromMinutes(1));
        var version = (await _store.GetAsync(P(path), Ct))?.Version;
        var result = await new NoteWriter(_store, _time, caller).WriteAsync(path, content, version, Ct);
        Assert.False(result.IsError, result.Output);
    }

    /// <summary>A note as a store holds it with the record given, of its own content.</summary>
    private void Put(string path, string content, DateTime? earlier, bool dropped, Authorship.Write[] writes, params Authorship.Run[] runs) =>
        _store.Put(new StoredNote(P(path), content, 5, "Veyalt", _time.GetUtcNow().UtcDateTime, [], Ref: Veyalt.Reference,
            Authorship: new Authorship(VersionToken.Hash(content), earlier, dropped, writes, runs)));

    [Fact]
    public async Task Runs_name_the_writer_who_last_changed_each_span()
    {
        await Write(Veyalt, "/Work/protocol.md", "# Protocol\nstep one\nstep 2\nstep three\nstep four\n");

        var result = await Run("blame /Work/protocol.md");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(
            """
            1-2  seed    2026-10-01 12:00 UTC
            3    Veyalt  2026-11-18 09:01 UTC
            4    seed    2026-10-01 12:00 UTC
            5    Veyalt  2026-11-18 09:01 UTC
            seed 3 lines, Veyalt 2 (blank lines not counted)
            """.ReplaceLineEndings("\n"), result.Output);
        Assert.Equal(new NoteVersion("/Work/protocol.md", (await _store.GetAsync(P("/Work/protocol.md"), Ct))!.Version), result.Version);
    }

    [Fact]
    public async Task A_runs_time_is_its_newest_lines_and_blank_lines_count_nowhere()
    {
        await Write(Veyalt, "/Work/protocol.md", "# Protocol\n\nstep 1\nstep two\nstep three\n");
        await Write(Veyalt, "/Work/protocol.md", "# Protocol\n\nstep 1\nstep 2\nstep three\n");

        var result = await Run("blame /Work/protocol.md");

        Assert.Equal(
            """
            1    seed    2026-10-01 12:00 UTC
            2-4  Veyalt  2026-11-18 09:02 UTC
            5    seed    2026-10-01 12:00 UTC
            seed 2 lines, Veyalt 2 (blank lines not counted)
            """.ReplaceLineEndings("\n"), result.Output);
    }

    [Fact]
    public async Task Lines_prints_every_line_with_its_own_time_and_text()
    {
        await Write(Veyalt, "/Work/protocol.md", "# Protocol\nstep 1\nstep two\nstep three\n");
        await Write(Brita, "/Work/protocol.md", "# Protocol\nstep 1\nstep 2\nstep three\n");

        var result = await Run("blame --lines /Work/protocol.md");

        Assert.Equal(
            """
            1  seed    2026-10-01 12:00 UTC  # Protocol
            2  Veyalt  2026-11-18 09:01 UTC  step 1
            3  Brita   2026-11-18 09:02 UTC  step 2
            4  seed    2026-10-01 12:00 UTC  step three
            """.ReplaceLineEndings("\n"), result.Output);
    }

    [Fact]
    public async Task A_section_limits_it_and_keeps_the_notes_line_numbers()
    {
        await Write(Veyalt, "/Work/protocol.md", "# Protocol\nintro\n## Results\nr1\nr2\n\n## Next\nn1\n");
        await Write(Brita, "/Work/protocol.md", "# Protocol\nintro\n## Results\nr1\nr2, checked\n\n## Next\nn1\n");

        var runs = await Run("blame \"/Work/protocol.md#Results\"");
        var lines = await Run("blame \"/Work/protocol.md#Results\" --lines");

        Assert.Equal(
            """
            3-4  Veyalt  2026-11-18 09:01 UTC
            5    Brita   2026-11-18 09:02 UTC
            Veyalt 2 lines, Brita 1 (blank lines not counted)
            """.ReplaceLineEndings("\n"), runs.Output);
        Assert.Equal("5  Brita   2026-11-18 09:02 UTC  r2, checked", lines.Output.Split('\n')[^1]);
        Assert.Equal(3, lines.Output.Split('\n').Length);
        Assert.Equal("blame: /Work/protocol.md#Nope: no such section ('outline /Work/protocol.md' lists them)", (await Run("blame \"/Work/protocol.md#Nope\"")).Output);
    }

    [Fact]
    public async Task Unrecorded_lines_read_earlier_than_where_the_history_ends()
    {
        Put("/Work/old.md", "a\nb\nc\n", Earlier, false, [new("Veyalt", Veyalt.Reference, _time.GetUtcNow().UtcDateTime)], new(2, -1), new(1, 0));

        var result = await Run("blame /Work/old.md");

        Assert.Equal(
            """
            1-2  earlier than 2026-11-17 18:00 UTC
            3    Veyalt  2026-11-18 09:00 UTC
            Veyalt 1 line, earlier 2 (blank lines not counted)
            """.ReplaceLineEndings("\n"), result.Output);
    }

    [Fact]
    public async Task A_dropped_record_says_why_on_a_last_line()
    {
        Put("/Work/long.md", "a\nb\n", Earlier, true, [], new Authorship.Run(2, -1));

        var result = await Run("blame /Work/long.md");

        Assert.Equal("(the record started again at 2026-11-17 18:00 UTC: more than 4096 changes of writer)", result.Output.Split('\n')[^1]);
        Assert.StartsWith("1-2  earlier than 2026-11-17 18:00 UTC\nearlier 2 lines", result.Output);
    }

    [Fact]
    public async Task A_folder_gives_lines_per_writer_per_note_then_the_total()
    {
        await Write(Veyalt, "/Work/Protocols/a.md", "v1\nv2\n\n");
        await Write(Brita, "/Work/Protocols/b.md", "b1\n");
        await Write(Veyalt, "/Work/Protocols/b.md", "b1\nv1\n");
        _store.AddAttachment("/Work/Protocols/scan.pdf");

        var result = await Run("blame /Work/Protocols/");

        Assert.Equal(
            """
            /Work/Protocols/a.md  Veyalt 2
            /Work/Protocols/b.md  Brita 1, Veyalt 1
            total: Veyalt 3 lines in 2 notes, Brita 1 in 1
            """.ReplaceLineEndings("\n"), result.Output);
    }

    [Fact]
    public async Task A_folder_stops_after_200_notes()
    {
        for (var i = 0; i < 202; i++) _store.Put(new StoredNote(P($"/Big/n{i:D3}.md"), "x\n", 1, "seed", InMemoryNoteStore.Seeded, [], Ref: WriterRef.Server));

        var lines = (await Run("blame /Big")).Output.Split('\n');

        Assert.Equal(202, lines.Length);
        Assert.Equal("/Big/n199.md  seed 1", lines[199]);
        Assert.Equal("… and 2 more notes; blame a folder below it", lines[200]);
        Assert.Equal("total: seed 200 lines in 200 notes", lines[201]);
    }

    [Fact]
    public async Task Two_members_shown_as_another_user_make_one_run()
    {
        var at = _time.GetUtcNow().UtcDateTime;
        Put("/Work/shared.md", "bob\ncarol\n", null, false,
            [new("", WriterRef.ForMember("w-bob"), at.AddMinutes(-5)), new("", WriterRef.ForMember("w-carol"), at)], new(1, 0), new(1, 1));

        var result = await Run("blame /Work/shared.md", new MemberLabels());

        Assert.Equal("1-2  another user  2026-11-18 09:00 UTC\nanother user 2 lines (blank lines not counted)", result.Output);
    }

    [Fact]
    public async Task Only_notes_have_lines()
    {
        _store.AddAttachment("/Work/scan.pdf");

        Assert.Equal(ShellResult.Error("blame: /Work/scan.pdf: only notes have lines"), await Run("blame /Work/scan.pdf"));
        Assert.Equal(ShellResult.Error("blame: /Work/nope.md: No such file or directory"), await Run("blame /Work/nope.md"));
        Assert.Equal(ShellResult.Error("blame: missing operand"), await Run("blame"));
    }

    [Fact]
    public async Task Lines_too_long_for_one_call_ask_for_a_section()
    {
        var content = string.Concat(Enumerable.Range(0, 2000).Select(i => $"line {i} {new string('x', 40)}\n"));
        await Write(Veyalt, "/Work/long.md", content);

        var output = (await Run("blame --lines /Work/long.md")).Output;

        Assert.True(output.Length <= OutputLimit.MaxChars, output.Length.ToString());
        Assert.EndsWith("[too long for one call; blame a section: blame --lines \"/Work/long.md#Section\" ('outline /Work/long.md' lists them)]", output);
    }

    /// <summary>A note of <paramref name="lines"/> lines whose writer changes every line, the record restarted before its last.</summary>
    private void PutAlternating(string path, int lines, int length = 1)
    {
        var at = _time.GetUtcNow().UtcDateTime;
        var content = string.Concat(Enumerable.Range(0, lines).Select(i => new string('x', length) + "\n"));
        var runs = Enumerable.Range(0, lines - 1).Select(i => new Authorship.Run(1, i % 2)).Append(new Authorship.Run(1, -1)).ToArray();
        Put(path, content, Earlier, true, [new("Brita", Brita.Reference, at), new("Veyalt", Veyalt.Reference, at)], runs);
    }

    [Fact]
    public async Task Runs_too_many_for_one_call_keep_the_counts_and_the_restart()
    {
        PutAlternating("/Work/many.md", 3000);

        var output = (await Run("blame /Work/many.md")).Output;
        var lines = output.Split('\n');

        Assert.True(output.Length <= OutputLimit.MaxChars, output.Length.ToString());
        Assert.Equal("[too many runs for one call; blame a section: blame \"/Work/many.md#Section\" ('outline /Work/many.md' lists them)]", lines[^3]);
        Assert.Equal("Brita 1500 lines, Veyalt 1499, earlier 1 (blank lines not counted)", lines[^2]);
        Assert.StartsWith("(the record started again at ", lines[^1]);
    }

    [Fact]
    public async Task Lines_too_many_for_one_call_keep_the_restart_under_the_limit()
    {
        PutAlternating("/Work/many.md", 1000, 40);

        var output = (await Run("blame --lines /Work/many.md")).Output;
        var lines = output.Split('\n');

        Assert.True(output.Length <= OutputLimit.MaxChars, output.Length.ToString());
        Assert.StartsWith("[too long for one call; blame a section", lines[^2]);
        Assert.StartsWith("(the record started again at ", lines[^1]);
    }

    [Fact]
    public async Task A_first_line_too_long_for_one_call_gives_the_hint_alone()
    {
        await Write(Veyalt, "/Work/wide.md", new string('x', OutputLimit.MaxChars) + "\n");

        var output = (await Run("blame --lines /Work/wide.md")).Output;

        Assert.Equal("[too long for one call; blame a section: blame --lines \"/Work/wide.md#Section\" ('outline /Work/wide.md' lists them)]", output);
    }

    [Fact]
    public async Task A_folder_too_long_for_one_call_counts_the_rest_as_more_and_keeps_the_total()
    {
        var folder = "/Big/" + new string('f', 150);
        for (var i = 0; i < 200; i++) _store.Put(new StoredNote(P($"{folder}/n{i:D3}.md"), "x\n", 1, "seed", InMemoryNoteStore.Seeded, [], Ref: WriterRef.Server));

        var output = (await Run("blame /Big")).Output;
        var lines = output.Split('\n');
        var shown = lines.Length - 2;

        Assert.True(output.Length <= OutputLimit.MaxChars, output.Length.ToString());
        Assert.InRange(shown, 100, 199);
        Assert.Equal($"… and {200 - shown} more notes; blame a folder below it", lines[^2]);
        Assert.Equal($"total: seed {shown} lines in {shown} notes", lines[^1]);
    }

    // --lines prints a note's lines; on a folder it would be ignored, so it is refused (help-texts spec §3.10).
    // Lines_prints_every_line_with_its_own_time_and_text keeps it working on a note.
    [Theory]
    [InlineData("blame --lines /Work")]
    [InlineData("blame --lines /Work/Protocols/")]
    [InlineData("blame /Work/Protocols --lines")]
    public async Task Blame_lines_on_a_folder_is_refused(string line)
    {
        await Write(Veyalt, "/Work/Protocols/a.md", "v1\n");

        Assert.Equal(ShellResult.Error("blame: --lines takes a note, not a folder"), await Run(line));
    }

    [Fact]
    public async Task Its_help_says_who_wrote_what()
    {
        Assert.StartsWith("blame [--lines] <path>[#section]\n  Who wrote what:", (await Run("help blame")).Output);
        Assert.Matches(@"\n  blame \[--lines\] <path>\[#section\] +who wrote what", (await Run("help")).Output);
        Assert.Contains("\n  blame [--lines] <path>          who wrote what", Manual.ToolDescription);
    }

    /// <summary>As a member is shown the writes of other members: no name, nothing that tells two of them apart.</summary>
    private sealed class MemberLabels : IWriterLabels
    {
        public ValueTask<(string Display, string? Kind)> RenderAsync(string vault, string label, WriterRef? reference, CancellationToken ct) =>
            ValueTask.FromResult<(string, string?)>(reference is { Owner: false } ? ("another user", "user") : (label, null));
    }
}
