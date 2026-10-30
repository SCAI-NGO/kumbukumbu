// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>Named checkpoints: mkcp, lscp, rmcp, --checkpoint on reading commands, diff and restore.</summary>
public class CheckpointTests
{
    private readonly InMemoryNoteStore _store = new(
        ("/Demo/arbeit/plan.md", "# Plan\nHecate\n"),
        ("/Demo/arbeit/sub/todo.md", "- one\n"),
        ("/Demo/welcome.md", "hi [[plan]]\n"),
        ("/Ideas/x.md", "x\n"));

    private readonly FakeTimeProvider _time = new(new DateTime(2026, 10, 24, 11, 0, 0, DateTimeKind.Utc));
    private readonly NoteWriter _writer;
    private readonly Shell _sh;

    public CheckpointTests()
    {
        _writer = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));
        _sh = new Shell(_store, _writer, new NoteMover(_store, _time, new FixedCaller(Writers.Dev)), new Checkpoints(_store, _writer, new Plans(), limit: 3));
    }

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, CancellationToken.None);

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private async Task<string?> Content(string path) => await _store.ReadAsync(P(path), CancellationToken.None);

    private async Task Write(string path, string content)
    {
        var current = await Content(path);
        Assert.False((await _writer.WriteAsync(path, content, current is null ? null : VersionToken.Of(current), CancellationToken.None)).IsError);
    }

    private static string Plan(ShellResult preview) =>
        preview.Output.Split('\n').Single(l => l.StartsWith("plan: ", StringComparison.Ordinal))["plan: ".Length..];

    [Fact]
    public async Task MkcpOfAFolderAndLscp()
    {
        Assert.Equal(
            "checkpoint 'before' of /Demo/arbeit/ (2 notes); read from it with --checkpoint before, e.g. ls --checkpoint before /Demo/arbeit",
            (await Run("mkcp /Demo/arbeit before")).Output);
        _time.Advance(TimeSpan.FromMinutes(5));
        await Run("mkcp /Demo/welcome.md hello");

        Assert.Equal(
            """
            before  /Demo/arbeit/     2026-10-24 11:00 UTC  dev  2 notes
            hello   /Demo/welcome.md  2026-10-24 11:05 UTC  dev  1 note
            2 of 3 checkpoints in Demo
            """,
            (await Run("lscp /Demo")).Output);
        Assert.Equal(
            """
            before  /Demo/arbeit/  2026-10-24 11:00 UTC  dev  2 notes
            2 of 3 checkpoints in Demo
            """,
            (await Run("lscp /Demo/arbeit/sub/todo.md")).Output);
        Assert.Equal("(no checkpoints for /Ideas)\n0 of 3 checkpoints in Ideas", (await Run("lscp /Ideas")).Output);
    }

    [Theory]
    [InlineData("mkcp /Demo", "mkcp: missing operand (mkcp <path> <name>)")]
    [InlineData("mkcp / x", "mkcp: a checkpoint covers a vault, a folder or a note, e.g. mkcp /Demo before-cleanup")]
    [InlineData("mkcp /Demo 'with space'", "mkcp: 'with space' is not a checkpoint name: letters, digits, '.', '-' and '_', starting with a letter or digit, at most 64")]
    [InlineData("mkcp /Demo/nope x", "mkcp: /Demo/nope: No such file or directory")]
    [InlineData("mkcp /Nope x", "mkcp: /Nope: No such file or directory")]
    [InlineData("rmcp /Demo x", "rmcp: no checkpoint 'x' in Demo ('lscp /Demo' lists them)")]
    [InlineData("cat --checkpoint x /Demo/welcome.md", "cat: no checkpoint 'x' in Demo ('lscp /Demo' lists them)")]
    [InlineData("history --checkpoint x /Demo", "history: --checkpoint works with ls, cat, outline, rg, links, backlinks, tags, board, head, tail, wc, sort, diff and restore (see 'help checkpoints')")]
    [InlineData("rg --checkpoint x Hecate", "rg: --checkpoint needs a path in the checkpoint's vault, e.g. rg --checkpoint x Hecate /Demo")]
    public async Task Errors(string line, string error) => Assert.Equal(ShellResult.Error(error), await Run(line));

    [Fact]
    public async Task NamesAreUniquePerVaultAndLimited()
    {
        await Run("mkcp /Demo a");
        Assert.StartsWith("mkcp: checkpoint 'a' exists in Demo (of /Demo/, 2026-10-24 11:00 UTC)", (await Run("mkcp /Demo/arbeit a")).Output);
        Assert.False((await Run("mkcp /Ideas a")).IsError);
        await Run("mkcp /Demo b");
        await Run("mkcp /Demo c");

        Assert.Equal(
            ShellResult.Error("mkcp: Demo has 3 checkpoints, the limit; 'lscp /Demo' lists them, 'rmcp /Demo <name>' removes one"),
            await Run("mkcp /Demo d"));
        Assert.Equal("removed checkpoint 'b' (of /Demo/); the notes themselves are unchanged", (await Run("rmcp /Demo/arbeit b")).Output);
        Assert.False((await Run("mkcp /Demo d")).IsError);
    }

    [Fact]
    public async Task ReadingCommandsWorkOnTheCheckpoint()
    {
        await Run("mkcp /Demo/arbeit/ before");
        await Write("/Demo/arbeit/plan.md", "# Plan\nJanus\n");
        await Write("/Demo/arbeit/new.md", "new\n");

        var cat = await Run("cat --checkpoint before /Demo/arbeit/plan.md");
        Assert.Equal(ShellResult.Ok("# Plan\nHecate\n"), cat);
        Assert.Null(cat.Version);
        Assert.Equal("plan.md\nsub/", (await Run("ls --checkpoint before /Demo/arbeit")).Output);
        Assert.Equal("arbeit/", (await Run("ls --checkpoint before /Demo")).Output);
        Assert.Equal("/Demo/arbeit/plan.md:2:Hecate", (await Run("rg -n --checkpoint before Hecate /Demo")).Output);
        Assert.Equal("Hecate", (await Run("cat --checkpoint=before /Demo/arbeit/plan.md | rg Heca")).Output);
        Assert.Equal("2 /Demo/arbeit/plan.md", (await Run("wc -l --checkpoint before /Demo/arbeit/plan.md")).Output);
    }

    [Fact]
    public async Task ReadingOutsideTheCheckpointSaysWhatItCovers()
    {
        await Run("mkcp /Demo/arbeit/ before");

        Assert.Equal(
            ShellResult.Error("cat: /Demo/welcome.md is not in checkpoint 'before' (it covers /Demo/arbeit/)"),
            await Run("cat --checkpoint before /Demo/welcome.md"));
        Assert.True((await Run("cat --checkpoint before --version 000000000000 /Demo/arbeit/plan.md")).IsError);
        Assert.True((await Run("cat /Demo/welcome.md | head --checkpoint before")).IsError);
    }

    [Fact]
    public async Task ACheckpointKeepsItsContentBeyondTheHistory()
    {
        await Run("mkcp /Demo/arbeit/plan.md v1");
        for (var i = 0; i < 12; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(11));
            await Write("/Demo/arbeit/plan.md", $"state {i}\n");
        }

        Assert.DoesNotContain((await _store.GetAsync(P("/Demo/arbeit/plan.md"), CancellationToken.None))!.History, h => h.Content == "# Plan\nHecate\n");
        Assert.Equal("# Plan\nHecate\n", (await Run("cat --checkpoint v1 /Demo/arbeit/plan.md")).Output);
    }

    [Fact]
    public async Task DiffAgainstACheckpoint()
    {
        await Run("mkcp /Demo/arbeit before");
        _time.Advance(TimeSpan.FromMinutes(1));
        await Write("/Demo/arbeit/plan.md", "# Plan\nJanus\n");
        await Write("/Demo/arbeit/new.md", "new\n");
        await _writer.RemoveAsync(P("/Demo/arbeit/sub/todo.md"), CancellationToken.None);

        Assert.Equal(
            """
            changes in /Demo/arbeit/ since checkpoint 'before' (2026-10-24 11:00 UTC, dev):
            new      /Demo/arbeit/new.md  +1
            changed  /Demo/arbeit/plan.md  +1 -1
            removed  /Demo/arbeit/sub/todo.md  (-1)
            diff <note> --checkpoint before shows one note; restore --checkpoint before <path> goes back
            """,
            (await Run("diff --checkpoint before /Demo/arbeit")).Output);
        Assert.Equal(
            $"""
            --- /Demo/arbeit/plan.md  {VersionToken.Of("# Plan\nHecate\n")}  checkpoint 'before'  2026-10-24 11:00 UTC  dev
            +++ /Demo/arbeit/plan.md  {VersionToken.Of("# Plan\nJanus\n")}  2026-10-24 11:01 UTC  dev
            @@ -1,2 +1,2 @@
             # Plan
            -Hecate
            +Janus
            """,
            (await Run("diff /Demo/arbeit/plan.md --checkpoint before")).Output);
        Assert.StartsWith("--- /Demo/arbeit/new.md  (not in checkpoint 'before')", (await Run("diff --checkpoint before /Demo/arbeit/new.md")).Output);
        Assert.Contains("+++ /Demo/arbeit/sub/todo.md  (removed since)", (await Run("diff --checkpoint before /Demo/arbeit/sub/todo.md")).Output);
        await Run("mkcp /Ideas same");
        Assert.Equal("(no changes in /Ideas/ since checkpoint 'same')", (await Run("diff --checkpoint same /Ideas")).Output);
    }

    [Fact]
    public async Task RestoreANoteFromACheckpoint()
    {
        await Run("mkcp /Demo/arbeit before");
        await Write("/Demo/arbeit/plan.md", "# Plan\nJanus\n");

        var restored = await Run("restore --checkpoint before /Demo/arbeit/plan.md");

        Assert.Equal("restored /Demo/arbeit/plan.md to checkpoint 'before'", restored.Output);
        var note = (await _store.GetAsync(P("/Demo/arbeit/plan.md"), CancellationToken.None))!;
        Assert.Equal("# Plan\nHecate\n", note.Content);
        Assert.Equal("dev (restore)", note.LastWriter);
        Assert.Null(note.Changeset);
        Assert.Equal("# Plan\nJanus\n", note.History[0].Content);
        Assert.Equal("/Demo/arbeit/plan.md is already as in checkpoint 'before'", (await Run("restore --checkpoint before /Demo/arbeit/plan.md")).Output);
    }

    [Fact]
    public async Task RestoreAFolderPreviewsThenMatchesTheCheckpointExactly()
    {
        await Run("mkcp /Demo/arbeit before");
        await Write("/Demo/arbeit/plan.md", "# Plan\nJanus\n");
        await Write("/Demo/arbeit/new.md", "new\n");
        await _writer.RemoveAsync(P("/Demo/arbeit/sub/todo.md"), CancellationToken.None);

        var preview = await Run("restore --checkpoint before /Demo/arbeit/");
        Assert.StartsWith(
            """
            restore /Demo/arbeit/ to checkpoint 'before' (2026-10-24 11:00 UTC, dev):
              to trash    /Demo/arbeit/new.md  (created since)
              changed     /Demo/arbeit/plan.md  +1 -1
              back        /Demo/arbeit/sub/todo.md

            plan:
            """,
            preview.Output);
        Assert.Equal("# Plan\nJanus\n", await Content("/Demo/arbeit/plan.md"));

        var applied = await Run($"restore --checkpoint before --plan {Plan(preview)} /Demo/arbeit");

        Assert.Equal("restored /Demo/arbeit/ to checkpoint 'before': 1 changed, 1 back, 0 moved back, 1 to the trash", applied.Output);
        Assert.Equal("# Plan\nHecate\n", await Content("/Demo/arbeit/plan.md"));
        Assert.Equal("- one\n", await Content("/Demo/arbeit/sub/todo.md"));
        Assert.Null(await Content("/Demo/arbeit/new.md"));
        Assert.Contains("/Demo/arbeit/new.md", (await Run("trash /Demo")).Output);
        Assert.Equal("/Demo/arbeit/ already matches checkpoint 'before'; nothing to restore", (await Run("restore --checkpoint before /Demo/arbeit")).Output);
        Assert.StartsWith($"restore: plan {Plan(preview)} was already applied", (await Run($"restore --checkpoint before --plan {Plan(preview)} /Demo/arbeit")).Output);
    }

    [Fact]
    public async Task AWholeCheckpointRestoreNamesEveryChangeThenAppliesItsPlan()
    {
        var checkpoints = new Checkpoints(_store, _writer, new Plans(), limit: 3);
        await Run("mkcp /Demo/arbeit before");
        await Write("/Demo/arbeit/plan.md", "# Plan\nJanus\nMore\n");
        await Write("/Demo/arbeit/new.md", "new\n");
        await _writer.RemoveAsync(P("/Demo/arbeit/sub/todo.md"), CancellationToken.None);

        var (preview, error) = await checkpoints.PreviewWholeAsync("Demo", "before", CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("/Demo/arbeit/", preview!.Checkpoint.Scope);
        Assert.Equal(
            [
                new RestoreChange(RestoreChangeKind.Trash, P("/Demo/arbeit/new.md"), null, null),
                new RestoreChange(RestoreChangeKind.Changed, P("/Demo/arbeit/plan.md"), null, (1, 2)),
                new RestoreChange(RestoreChangeKind.Back, P("/Demo/arbeit/sub/todo.md"), null, null),
            ],
            preview.Changes);
        Assert.NotNull(preview.Plan);
        Assert.Equal("# Plan\nJanus\nMore\n", await Content("/Demo/arbeit/plan.md"));

        var applied = await checkpoints.ApplyWholeAsync("Demo", "before", preview.Plan!, CancellationToken.None);

        Assert.Equal("restored /Demo/arbeit/ to checkpoint 'before': 1 changed, 1 back, 0 moved back, 1 to the trash", applied.Output);
        Assert.Equal("# Plan\nHecate\n", await Content("/Demo/arbeit/plan.md"));
        Assert.StartsWith("restore: plan", (await checkpoints.ApplyWholeAsync("Demo", "before", preview.Plan!, CancellationToken.None)).Output);
        var (again, _) = await checkpoints.PreviewWholeAsync("Demo", "before", CancellationToken.None);
        Assert.Empty(again!.Changes);
        Assert.Null(again.Plan);
    }

    [Fact]
    public async Task AWholeRestoreOfANoteCheckpointPreviewsToo()
    {
        var checkpoints = new Checkpoints(_store, _writer, new Plans(), limit: 3);
        await Run("mkcp /Demo/welcome.md w");
        await Write("/Demo/welcome.md", "changed\n");

        var (preview, _) = await checkpoints.PreviewWholeAsync("Demo", "w", CancellationToken.None);

        Assert.Equal([new RestoreChange(RestoreChangeKind.Changed, P("/Demo/welcome.md"), null, (1, 1))], preview!.Changes);
        Assert.Equal("changed\n", await Content("/Demo/welcome.md"));
        Assert.False((await checkpoints.ApplyWholeAsync("Demo", "w", preview.Plan!, CancellationToken.None)).IsError);
        var note = (await _store.GetAsync(P("/Demo/welcome.md"), CancellationToken.None))!;
        Assert.Equal("hi [[plan]]\n", note.Content);
        Assert.Equal("dev (restore)", note.LastWriter);
        Assert.Equal("changed\n", note.History[0].Content);
    }

    [Fact]
    public async Task AWholeRestoreIsRefusedToAReaderAndForAMissingCheckpoint()
    {
        var reader = new NoteWriter(_store, _time, new FixedCaller("liz", _ => false));
        var checkpoints = new Checkpoints(_store, reader, new Plans(), limit: 3);
        await Run("mkcp /Demo w");

        var (preview, refused) = await checkpoints.PreviewWholeAsync("Demo", "w", CancellationToken.None);
        var (_, missing) = await new Checkpoints(_store, _writer, new Plans()).PreviewWholeAsync("Demo", "nope", CancellationToken.None);

        Assert.Null(preview);
        Assert.Equal(ShellResult.Error("restore: this key can only read vault Demo"), refused);
        Assert.Equal(ShellResult.Error("restore: no checkpoint 'nope' in Demo ('lscp /Demo' lists them)"), missing);
        Assert.True((await checkpoints.ApplyWholeAsync("Demo", "w", "0123", CancellationToken.None)).IsError);
    }

    [Fact]
    public async Task AFolderRestoreStopsIfANoteChangedOrAppearedSinceThePreview()
    {
        await Run("mkcp /Demo/arbeit before");
        await Write("/Demo/arbeit/plan.md", "# Plan\nJanus\n");
        var plan = Plan(await Run("restore --checkpoint before /Demo/arbeit"));
        await Write("/Demo/arbeit/late.md", "late\n");

        Assert.Equal(
            ShellResult.Error("restore: nothing was restored: /Demo/arbeit/late.md changed or appeared since the preview; preview again"),
            await Run($"restore --checkpoint before --plan {plan} /Demo/arbeit"));
        Assert.Equal("# Plan\nJanus\n", await Content("/Demo/arbeit/plan.md"));
    }

    [Fact]
    public async Task RestoreOfAVaultCheckpointToOneFolder()
    {
        await Run("mkcp /Demo all");
        await Write("/Demo/arbeit/plan.md", "# Plan\nJanus\n");
        await Write("/Demo/welcome.md", "changed\n");

        var plan = Plan(await Run("restore --checkpoint all /Demo/arbeit"));
        await Run($"restore --checkpoint all --plan {plan} /Demo/arbeit");

        Assert.Equal("# Plan\nHecate\n", await Content("/Demo/arbeit/plan.md"));
        Assert.Equal("changed\n", await Content("/Demo/welcome.md"));
    }

    [Fact]
    public async Task RestoreBringsBackARemovedNote()
    {
        await Run("mkcp /Demo/welcome.md w");
        await _writer.RemoveAsync(P("/Demo/welcome.md"), CancellationToken.None);

        Assert.Equal("restored /Demo/welcome.md from checkpoint 'w' (it had been removed)", (await Run("restore --checkpoint w /Demo/welcome.md")).Output);
        Assert.Equal("hi [[plan]]\n", await Content("/Demo/welcome.md"));
    }

    [Fact]
    public async Task AFolderRestoreMovesAMovedNoteBackAndItsLinksFollow()
    {
        await Run("mkcp /Demo/arbeit before");
        _time.Advance(TimeSpan.FromMinutes(1));
        await Run("mv /Demo/arbeit/plan.md /Demo/arbeit/plan2.md");
        Assert.Equal("hi [[plan2]]\n", await Content("/Demo/welcome.md"));

        var preview = await Run("restore --checkpoint before /Demo/arbeit");
        Assert.StartsWith(
            """
            restore /Demo/arbeit/ to checkpoint 'before' (2026-10-24 11:00 UTC, dev):
              moved back  /Demo/arbeit/plan2.md → /Demo/arbeit/plan.md
            Links follow the notes moved back: 1 link rewritten in /Demo/welcome.md.
            """,
            preview.Output);

        var applied = await Run($"restore --checkpoint before --plan {Plan(preview)} /Demo/arbeit");

        Assert.Equal("restored /Demo/arbeit/ to checkpoint 'before': 0 changed, 0 back, 1 moved back, 0 to the trash; rewrote 1 link in /Demo/welcome.md", applied.Output);
        Assert.Equal("hi [[plan]]\n", await Content("/Demo/welcome.md"));
        Assert.Null(await Content("/Demo/arbeit/plan2.md"));
        // The note itself moved back, with its history, rather than being created anew.
        Assert.Equal(3L, (await _store.GetAsync(P("/Demo/arbeit/plan.md"), CancellationToken.None))!.Revision);
        Assert.Contains("(no changes in /Demo/arbeit/", (await Run("diff --checkpoint before /Demo/arbeit")).Output);
    }

    [Fact]
    public async Task DiffOfAFolderShowsAMoveAsOneRow()
    {
        await Run("mkcp /Demo/arbeit before");
        await Run("mv /Demo/arbeit/sub/todo.md /Demo/arbeit/todo.md");

        Assert.Contains("moved    /Demo/arbeit/sub/todo.md → /Demo/arbeit/todo.md", (await Run("diff --checkpoint before /Demo/arbeit")).Output);
    }

    [Fact]
    public async Task APreviewNamesLinksOutsideThatWouldLeadNowhere()
    {
        await Run("mkcp /Demo/arbeit before");
        await Write("/Demo/arbeit/new.md", "new\n");
        await Write("/Demo/welcome.md", "hi [[plan]] and [[new]]\n");

        var preview = await Run("restore --checkpoint before /Demo/arbeit");

        Assert.Contains(
            """
            After the restore, 1 link outside /Demo/arbeit/ would lead nowhere:
              /Demo/welcome.md:1: [[new]] (now /Demo/arbeit/new.md, going to the trash)
            """,
            preview.Output);
    }

    [Fact]
    public async Task SmallMessagesAboutCheckpoints()
    {
        await Run("mkcp /Ideas base");
        await Run("mkcp /Demo/arbeit/plan.md only-plan");
        await Write("/Demo/arbeit/new.md", "new\n");
        await Run("mkcp /Demo/arbeit cp");

        Assert.Equal(ShellResult.Error("rg: --checkpoint needs a path in the checkpoint's vault, e.g. rg --checkpoint base -n 'two words' /Ideas"), await Run("rg --checkpoint base -n 'two words'"));
        Assert.Equal(ShellResult.Error("cat: /Demo/arbeit/new.md is not in checkpoint 'only-plan' (it covers /Demo/arbeit/plan.md)"), await Run("cat --checkpoint only-plan /Demo/arbeit/new.md"));
        await Write("/Demo/arbeit/newer.md", "newer\n");
        Assert.Equal(ShellResult.Error("cat: /Demo/arbeit/newer.md is not in checkpoint 'cp' (created since)"), await Run("cat --checkpoint cp /Demo/arbeit/newer.md"));
        Assert.Equal(
            ShellResult.Error("rmcp: checkpoint 'only-plan' covers /Demo/arbeit/plan.md, not /Demo/welcome.md; give its path or the vault (rmcp /Demo only-plan)"),
            await Run("rmcp /Demo/welcome.md only-plan"));
        Assert.False((await Run("rmcp /Demo/arbeit/plan.md only-plan")).IsError);
    }

    [Fact]
    public async Task FolderHistoryShowsWhatWentToTheTrash()
    {
        await Run("mkcp /Demo/arbeit before");
        await Write("/Demo/arbeit/new.md", "new\n");
        var plan = Plan(await Run("restore --checkpoint before /Demo/arbeit"));
        await Run($"restore --checkpoint before --plan {plan} /Demo/arbeit");
        await _writer.RemoveAsync(P("/Demo/welcome.md"), CancellationToken.None);

        var rows = (await Run("history --since 1h /Demo")).Output.Split('\n');

        Assert.Contains("2026-10-24 11:00 UTC  dev (restore)  /Demo/arbeit/new.md → trash", rows);
        Assert.Contains("2026-10-24 11:00 UTC  dev            /Demo/welcome.md → trash", rows);
    }

    [Fact]
    public async Task A_checkpoint_brings_back_a_removed_empty_folder()
    {
        await Run("mkdir /Demo/Drafts");
        await Run("mkcp /Demo before");
        await Run("rm /Demo/Drafts");

        var diff = (await Run("diff /Demo --checkpoint before")).Output;
        Assert.Contains("removed  /Demo/Drafts/", diff.Split('\n'));
        Assert.DoesNotContain(".vaultkeep", diff);

        var preview = await Run("restore --checkpoint before /Demo");
        Assert.Contains("  back        /Demo/Drafts/", preview.Output.Split('\n'));
        Assert.DoesNotContain(".vaultkeep", preview.Output);
        Assert.False((await Run($"restore --checkpoint before --plan {Plan(preview)} /Demo")).IsError);

        Assert.Contains("Drafts/", (await Run("ls /Demo")).Output.Split('\n'));
    }

    [Fact]
    public async Task A_folder_made_after_a_checkpoint_goes_with_its_restore()
    {
        await Run("mkcp /Demo before");
        await Run("mkdir /Demo/Drafts");

        var preview = await Run("restore --checkpoint before /Demo");
        Assert.Contains("  to trash    /Demo/Drafts/  (created since)", preview.Output.Split('\n'));
        Assert.False((await Run($"restore --checkpoint before --plan {Plan(preview)} /Demo")).IsError);

        Assert.DoesNotContain("Drafts/", (await Run("ls /Demo")).Output.Split('\n'));
    }

    [Fact]
    public async Task Lscp_counts_no_marker()
    {
        await Run("mkdir /Demo/arbeit/sub/Drafts");
        var made = await Run("mkcp /Demo/arbeit/sub one");

        Assert.Contains("(1 note)", made.Output);
        Assert.Contains("  1 note", (await Run("lscp /Demo")).Output);
    }
}

