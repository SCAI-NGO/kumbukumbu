// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Notes;

/// <summary>The replace tool (preview, plan, apply) and edit's replace_all.</summary>
public class ReplacerTests
{
    private readonly InMemoryNoteStore _store = new(
        ("/Work/a.md", "Hecate is the plan.\nHecates are many. Hecate!\n"),
        ("/Work/sub/b.md", "About Hecate.\n"),
        ("/Work/c.md", "nothing here\n"),
        ("/Ideas/d.md", "Hecate elsewhere\n"));

    private readonly FakeTimeProvider _time = new(InMemoryNoteStore.Seeded.AddDays(1));
    private readonly Plans _plans = new();
    private readonly Replacer _replacer;

    public ReplacerTests() => _replacer = new Replacer(_store, _time, _plans, new FixedCaller(Writers.Dev));

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private async Task<string> Content(string path) => (await _store.ReadAsync(P(path), Ct))!;

    private Task<ShellResult> Run(string path, string oldText, string newText, bool word = false, string? plan = null) =>
        _replacer.RunAsync(path, oldText, newText, word, plan, Ct);

    private static string Plan(ShellResult preview) =>
        preview.Output.Split('\n').Single(l => l.StartsWith("plan: ", StringComparison.Ordinal))["plan: ".Length..];

    [Fact]
    public async Task PreviewListsEveryNoteAndWritesNothing()
    {
        var preview = await Run("/Work", "Hecate", "Selene");

        Assert.False(preview.IsError);
        var lines = preview.Output.Split('\n');
        Assert.Equal(
            """
            4 occurrences of "Hecate" in 2 notes below /Work, to become "Selene":
              /Work/a.md      3
              /Work/sub/b.md  1

            /Work/a.md:1: [-Hecate-]{+Selene+} is the plan.
            /Work/a.md:2: [-Hecate-]{+Selene+}s are many. Hecate!
            /Work/a.md:2: Hecates are many. [-Hecate-]{+Selene+}!
            /Work/sub/b.md:1: About [-Hecate-]{+Selene+}.

            """,
            string.Join('\n', lines[..^2]));
        Assert.Equal(VersionToken.Of(Lines.Join(lines[..^2])).Length, Plan(preview).Length);
        Assert.Equal("Hecate is the plan.\nHecates are many. Hecate!\n", await Content("/Work/a.md"));
    }

    [Fact]
    public async Task ApplyChangesExactlyThePreviewedNotesWithAMarkedHistoryEntry()
    {
        var preview = await Run("/Work", "Hecate", "Selene", word: true);
        Assert.Contains("3 occurrences", preview.Output);

        var applied = await Run("/Work", "Hecate", "Selene", word: true, plan: Plan(preview));

        Assert.Equal(ShellResult.Ok("replaced 3 occurrences in 2 notes"), applied);
        Assert.Equal("Selene is the plan.\nHecates are many. Selene!\n", await Content("/Work/a.md"));
        Assert.Equal("About Selene.\n", await Content("/Work/sub/b.md"));
        Assert.Equal("Hecate elsewhere\n", await Content("/Ideas/d.md"));
        var note = (await _store.GetAsync(P("/Work/a.md"), Ct))!;
        Assert.Equal("dev (replace)", note.LastWriter);
        Assert.NotNull(note.Changeset);
        Assert.Equal("Hecate is the plan.\nHecates are many. Hecate!\n", note.History[0].Content);
    }

    [Fact]
    public async Task APlanWorksOnce()
    {
        var plan = Plan(await Run("/Work/sub/b.md", "Hecate", "Selene"));
        Assert.False((await Run("/Work/sub/b.md", "Hecate", "Selene", plan: plan)).IsError);

        Assert.Equal(
            ShellResult.Error($"replace: plan {plan} was already applied at 12:00 UTC (replaced 1 occurrence in 1 note); a plan is applied once"),
            await Run("/Work/sub/b.md", "Hecate", "Selene", plan: plan));
    }

    [Fact]
    public async Task AFailedPlanSaysWhyAndStaysUsed()
    {
        var plan = Plan(await Run("/Work", "Hecate", "Selene"));
        var b = (await _store.GetAsync(P("/Work/sub/b.md"), Ct))!;
        await _store.TryReplaceAsync(b, NoteWriter.Next(b, "About Hecate, changed.\n", "other", WriterRef.Server, _time.GetUtcNow().UtcDateTime), Ct);
        await Run("/Work", "Hecate", "Selene", plan: plan);

        Assert.Equal(
            ShellResult.Error($"replace: plan {plan} failed at 12:00 UTC: /Work/sub/b.md changed since the preview; preview again"),
            await Run("/Work", "Hecate", "Selene", plan: plan));
    }

    [Fact]
    public async Task ASeriesOfReplacesKeepsTheStateBeforeThem()
    {
        foreach (var (from, to) in new[] { ("Hecate", "Selene"), ("Selene", "Luna"), ("Luna", "Janus") })
        {
            var plan = Plan(await Run("/Work/sub/b.md", from, to));
            Assert.False((await Run("/Work/sub/b.md", from, to, plan: plan)).IsError);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var note = (await _store.GetAsync(P("/Work/sub/b.md"), Ct))!;
        Assert.Equal("About Janus.\n", note.Content);
        Assert.Equal(["About Hecate.\n"], note.History.Select(h => h.Content));
    }

    [Fact]
    public async Task PreviewWarnsAboutLinksThatWouldLeadNowhereAndNamesTheNote()
    {
        var store = new InMemoryNoteStore(
            ("/Work/projects/Hecate.md", "# Hecate\n"),
            ("/Work/projects/Hecate Retro.md", "# Retro\n"),
            ("/Work/log.md", "See [[Hecate]], [[Hecate Retro#Bad|the retro]] and [[Hecate|it]].\nHecate shipped.\n"));

        var preview = await new Replacer(store, _time, _plans, new FixedCaller(Writers.Dev)).RunAsync("/Work/log.md", "Hecate", "Janus", false, null, Ct);

        Assert.Contains(
            """
            Named after "Hecate": /Work/projects/Hecate Retro.md, /Work/projects/Hecate.md
            Read them before applying: a sentence can depend on the name without containing it. To rename one, mv it first.

            3 occurrences inside [[links]]; after the replace:
            3 links would lead nowhere or be ambiguous:
              /Work/log.md:1: [[Hecate]] → [[Janus]]: no such note (now /Work/projects/Hecate.md)
              /Work/log.md:1: [[Hecate Retro#Bad|the retro]] → [[Janus Retro#Bad|the retro]]: no such note (now /Work/projects/Hecate Retro.md)
              /Work/log.md:1: [[Hecate|it]] → [[Janus|it]]: no such note (now /Work/projects/Hecate.md)
            To rename a note, mv it first: that rewrites its links, and this replace then leaves them alone.
            """,
            preview.Output);
    }

    [Fact]
    public async Task PreviewWarnsAboutLinksThatWouldLeadToAnotherNote()
    {
        var store = new InMemoryNoteStore(
            ("/Work/Hecate.md", "h\n"), ("/Work/myth/Janus.md", "the god\n"), ("/Work/log.md", "[[Hecate]] and [[x|Hecate]]\n"));

        var preview = await new Replacer(store, _time, _plans, new FixedCaller(Writers.Dev)).RunAsync("/Work/log.md", "Hecate", "Janus", false, null, Ct);

        Assert.Contains(
            """
            2 occurrences inside [[links]]; after the replace:
            1 link would lead to another note:
              /Work/log.md:1: [[Hecate]] → [[Janus]]: to /Work/myth/Janus.md (now /Work/Hecate.md)
            """,
            preview.Output);
    }

    [Fact]
    public async Task LinksThatStillLeadToTheSameNoteAreOnlyCounted()
    {
        var store = new InMemoryNoteStore(("/Work/x.md", "x\n"), ("/Work/log.md", "[[x|Hecate]] and [[x#Hecate]]\n"));

        var preview = await new Replacer(store, _time, _plans, new FixedCaller(Writers.Dev)).RunAsync("/Work/log.md", "Hecate", "Janus", false, null, Ct);

        Assert.Contains("2 occurrences inside [[links]]; each still leads to the same note afterwards.", preview.Output);
        Assert.DoesNotContain("Named after", preview.Output);
    }

    [Fact]
    public async Task MoreThanFiveNotesNameTheWayToEveryLine()
    {
        var store = new InMemoryNoteStore([.. Enumerable.Range(0, 7).Select(i => ($"/Work/n{i}.md", "it's here\n"))]);

        var preview = await new Replacer(store, _time, _plans, new FixedCaller(Writers.Dev)).RunAsync("/Work", "it's", "it is", true, null, Ct);

        Assert.Contains("… excerpts of the first 5 notes only; every line: rg -n -F -w 'it'\\''s' /Work", preview.Output);
    }

    [Fact]
    public async Task APlanExpiresAfterAnHour()
    {
        var plan = Plan(await Run("/Work", "Hecate", "Selene"));
        _time.Advance(TimeSpan.FromMinutes(61));

        Assert.StartsWith("replace: unknown or expired plan", (await Run("/Work", "Hecate", "Selene", plan: plan)).Output);
    }

    [Fact]
    public async Task ANoteChangedSincePreviewStopsTheWholeApply()
    {
        var plan = Plan(await Run("/Work", "Hecate", "Selene"));
        var b = (await _store.GetAsync(P("/Work/sub/b.md"), Ct))!;
        await _store.TryReplaceAsync(b, NoteWriter.Next(b, "About Hecate, changed.\n", "other", WriterRef.Server, _time.GetUtcNow().UtcDateTime), Ct);

        var applied = await Run("/Work", "Hecate", "Selene", plan: plan);

        Assert.Equal(ShellResult.Error("replace: nothing was replaced: /Work/sub/b.md changed since the preview; preview again"), applied);
        Assert.Equal("Hecate is the plan.\nHecates are many. Hecate!\n", await Content("/Work/a.md"));
    }

    [Fact]
    public async Task ANoteRemovedSincePreviewStopsTheApply()
    {
        var plan = Plan(await Run("/Work", "Hecate", "Selene"));
        await _store.TryTrashAsync((await _store.GetAsync(P("/Work/sub/b.md"), Ct))!, Writers.Dev, WriterRef.Server, _time.GetUtcNow().UtcDateTime, Ct);

        Assert.True((await Run("/Work", "Hecate", "Selene", plan: plan)).IsError);
        Assert.StartsWith("Hecate", await Content("/Work/a.md"));
    }

    [Fact]
    public async Task ANoteAddedSincePreviewIsNotTouched()
    {
        var plan = Plan(await Run("/Work", "Hecate", "Selene"));
        await _store.TryCreateAsync(new StoredNote(P("/Work/new.md"), "Hecate new\n", 1, Writers.Dev, InMemoryNoteStore.Seeded, [], Ref: WriterRef.Server), Ct);

        Assert.Equal("replaced 4 occurrences in 2 notes", (await Run("/Work", "Hecate", "Selene", plan: plan)).Output);
        Assert.Equal("Hecate new\n", await Content("/Work/new.md"));
    }

    [Fact]
    public async Task ARaceAtCommitStopsTheApply()
    {
        var plan = Plan(await Run("/Work", "Hecate", "Selene"));
        _store.Interfere = n => n with { Content = "someone else\n" };

        Assert.True((await Run("/Work", "Hecate", "Selene", plan: plan)).IsError);
        Assert.Equal("About Hecate.\n", await Content("/Work/sub/b.md"));
    }

    [Fact]
    public async Task APlanBelongsToItsCall()
    {
        var plan = Plan(await Run("/Work", "Hecate", "Selene"));

        Assert.StartsWith("replace: this plan was made for a call with another", (await Run("/Work", "Hecate", "Luna", plan: plan)).Output);
        // Still valid for the right call.
        Assert.False((await Run("/Work", "Hecate", "Selene", plan: plan)).IsError);
    }

    [Fact]
    public async Task TooManyMatchesForOnePreview()
    {
        var store = new InMemoryNoteStore([.. Enumerable.Range(0, 2000).Select(i => ($"/Big/folder-with-a-long-name/note-{i:D4}.md", "x Hecate\n"))]);
        var result = await new Replacer(store, _time, _plans, new FixedCaller(Writers.Dev)).RunAsync("/Big", "Hecate", "Selene", false, null, Ct);

        Assert.Equal(ShellResult.Error("replace: 2000 matches in 2000 notes, too many for one preview; narrow the path"), result);
    }

    [Theory]
    [InlineData("/", "Hecate", "Selene", "replace: works within one vault; give a vault, a folder or a note, e.g. /Work")]
    [InlineData("/Work/a.md#X", "Hecate", "Selene", "replace: works on whole notes; to change one section, use edit")]
    [InlineData("/Work", "", "Selene", "replace: old_text is empty")]
    [InlineData("/Work", "Hecate", "Hecate", "replace: old_text and new_text are the same")]
    [InlineData("/Work/nope", "Hecate", "Selene", "replace: /Work/nope: No such file or directory")]
    public async Task Errors(string path, string oldText, string newText, string error) =>
        Assert.Equal(ShellResult.Error(error), await Run(path, oldText, newText));

    [Fact]
    public async Task NoOccurrences() =>
        Assert.StartsWith("no occurrences of \"Luna\"", (await Run("/Work", "Luna", "Selene")).Output);

    [Theory]
    [InlineData("Hecate Hecates _Hecate Hecate.", "Hecate", true, new[] { 0, 23 })]
    [InlineData("aaaa", "aa", false, new[] { 0, 2 })]
    [InlineData("Hécate", "cate", true, new int[0])]
    public void MatchesAreNonOverlappingAndRespectWords(string text, string value, bool word, int[] expected) =>
        Assert.Equal(expected, Replacer.Matches(text, value, word));

    // --- edit replace_all ---

    [Fact]
    public async Task ReplaceAllInANoteAndInASection()
    {
        var store = new InMemoryNoteStore(("/Work/s.md", "# A\nx x\n# B\nx\n"));
        var writer = new NoteWriter(store, _time, new FixedCaller(Writers.Dev));

        var all = await writer.EditAsync("/Work/s.md#A", VersionToken.Of("# A\nx x\n# B\nx\n"), "x", "y", Ct, replaceAll: true);

        Assert.Equal("replaced 2 occurrences in section #A of /Work/s.md", all.Output);
        Assert.Equal("# A\ny y\n# B\nx\n", await store.ReadAsync(P("/Work/s.md"), Ct));
    }

    [Fact]
    public async Task ReplaceAllNeedsOldText()
    {
        var writer = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));
        var result = await writer.EditAsync("/Work/c.md", VersionToken.Of("nothing here\n"), null, "y", Ct, replaceAll: true);

        Assert.Equal(ShellResult.Error("edit: replace_all needs old_text (and no replace_section): it replaces every occurrence of old_text"), result);
    }
}
