// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Board;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>archive: into the vault's archive under a name stamped in the server's time zone, links following.</summary>
public class ArchiveCommandTests
{
    private readonly InMemoryNoteStore _store = new(
        ("/Work/Overview.md", "Source: [report](/Inbox/report.pdf)\n"),
        ("/Work/Inbox/v1.2 notes.md", "notes\n"),
        ("/Work/Inbox/Manual/pump.md", "See [[v1.2 notes]]\n"),
        ("/Work/_archive/old.md", "old\n"),
        ("/Work/_skills/foo/SKILL.md", "---\nname: foo\ndescription: Foo.\n---\n"),
        ("/Skills/foo/SKILL.md", "---\nname: foo\ndescription: Foo.\n---\n")) { SkillsVault = "Skills" };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 20, 30, 5, TimeSpan.Zero));

    public ArchiveCommandTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));
        _store.AddAttachment("/Work/Inbox/report.pdf");
        _store.AddAttachment("/Work/Inbox/photo.jpeg");
        _store.AddAttachment("/Work/Inbox/Manual/pump.pdf");
    }

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path);

    private Task<ShellResult> Run(string line, ICaller? caller = null)
    {
        caller ??= new FixedCaller(Writers.Dev);
        return new Shell(_store, new NoteWriter(_store, _time, caller), new NoteMover(_store, _time, caller)).RunAsync(line, Ct);
    }

    [Fact]
    public async Task A_pdf_moves_into_the_archive_stamped_before_its_extension_and_links_follow()
    {
        var result = await Run("archive /Work/Inbox/report.pdf");

        Assert.False(result.IsError, result.Output);
        Assert.StartsWith("moved /Work/Inbox/report.pdf to /Work/_archive/Inbox/report 2026-11-08 213005.pdf", result.Output);
        Assert.Null(await _store.GetAttachmentAsync(P("/Work/Inbox/report.pdf"), Ct));
        Assert.NotNull(await _store.GetAttachmentAsync(P("/Work/_archive/Inbox/report 2026-11-08 213005.pdf"), Ct));
        Assert.Equal("Source: [report](/_archive/Inbox/report%202026-11-08%20213005.pdf)\n", await _store.ReadAsync(P("/Work/Overview.md"), Ct));
    }

    [Fact]
    public async Task A_note_keeps_md_at_the_end()
    {
        var result = await Run("archive \"/Work/Inbox/v1.2 notes.md\"");

        Assert.False(result.IsError, result.Output);
        Assert.Equal("notes\n", await _store.ReadAsync(P("/Work/_archive/Inbox/v1.2 notes 2026-11-08 213005.md"), Ct));
        Assert.Contains("rewrote 1 link in /Work/Inbox/Manual/pump.md", result.Output);
    }

    [Fact]
    public async Task A_jpeg_stays_jpeg()
    {
        Assert.False((await Run("archive /Work/Inbox/photo.jpeg")).IsError);
        Assert.NotNull(await _store.GetAttachmentAsync(P("/Work/_archive/Inbox/photo 2026-11-08 213005.jpeg"), Ct));
    }

    [Fact]
    public async Task A_folder_moves_with_its_contents()
    {
        var result = await Run("archive /Work/Inbox/Manual/");

        Assert.False(result.IsError, result.Output);
        Assert.StartsWith("moved /Work/Inbox/Manual/ to /Work/_archive/Inbox/Manual 2026-11-08 213005/", result.Output);
        Assert.Equal("See [[v1.2 notes]]\n", await _store.ReadAsync(P("/Work/_archive/Inbox/Manual 2026-11-08 213005/pump.md"), Ct));
        Assert.NotNull(await _store.GetAttachmentAsync(P("/Work/_archive/Inbox/Manual 2026-11-08 213005/pump.pdf"), Ct));
        Assert.Empty(await _store.ListNotesUnderAsync(P("/Work/Inbox/Manual"), Ct));
    }

    [Fact]
    public async Task A_note_at_the_vaults_root_goes_to_the_archives_root()
    {
        Assert.False((await Run("archive /Work/Overview.md")).IsError);
        Assert.NotNull(await _store.ReadAsync(P("/Work/_archive/Overview 2026-11-08 213005.md"), Ct));
    }

    [Fact]
    public async Task A_second_archive_in_the_same_second_gets_a_suffix_after_the_time()
    {
        Assert.False((await Run("archive /Work/Inbox/report.pdf")).IsError);
        _store.AddAttachment("/Work/Inbox/report.pdf");
        Assert.False((await Run("archive /Work/Inbox/report.pdf")).IsError);
        _store.AddAttachment("/Work/Inbox/report.pdf");
        Assert.False((await Run("archive /Work/Inbox/report.pdf")).IsError);

        Assert.NotNull(await _store.GetAttachmentAsync(P("/Work/_archive/Inbox/report 2026-11-08 213005.pdf"), Ct));
        Assert.NotNull(await _store.GetAttachmentAsync(P("/Work/_archive/Inbox/report 2026-11-08 213005-2.pdf"), Ct));
        Assert.NotNull(await _store.GetAttachmentAsync(P("/Work/_archive/Inbox/report 2026-11-08 213005-3.pdf"), Ct));
    }

    [Fact]
    public async Task An_upper_case_extension_keeps_its_spelling()
    {
        _store.AddAttachment("/Work/Inbox/photo.JPG");

        Assert.False((await Run("archive /Work/Inbox/photo.JPG")).IsError);
        Assert.NotNull(await _store.GetAttachmentAsync(P("/Work/_archive/Inbox/photo 2026-11-08 213005.JPG"), Ct));
    }

    [Fact]
    public async Task A_note_already_at_the_stamped_name_counts_as_a_clash()
    {
        await new NoteWriter(_store, _time, new FixedCaller(Writers.Dev)).WriteAsync("/Work/_archive/Overview 2026-11-08 213005.md", "earlier\n", null, Ct);

        var result = await Run("archive /Work/Overview.md");

        Assert.StartsWith("moved /Work/Overview.md to /Work/_archive/Overview 2026-11-08 213005-2.md", result.Output);
        Assert.Equal("earlier\n", await _store.ReadAsync(P("/Work/_archive/Overview 2026-11-08 213005.md"), Ct));
    }

    [Fact]
    public async Task Rewritten_links_are_marked_as_archives()
    {
        Assert.False((await Run("archive /Work/Inbox/report.pdf")).IsError);

        var history = (await Run("history /Work/Overview.md")).Output;
        Assert.Contains("dev (archive)", history);
        Assert.DoesNotContain("(mv)", history);
    }

    [Fact]
    public async Task A_folders_marker_is_not_there_as_everywhere_in_the_shell()
    {
        _store.AddAttachment("/Work/Empty/.vaultkeep");

        var result = await Run("archive /Work/Empty/.vaultkeep");

        Assert.Equal(ShellResult.Error("archive: /Work/Empty/.vaultkeep: No such file or directory"), result);
        Assert.NotNull(await _store.GetAttachmentAsync(P("/Work/Empty/.vaultkeep"), Ct));
    }

    [Fact]
    public async Task A_damaged_note_says_so()
    {
        _store.Damaged.Add(P("/Work/Inbox/broken.md"));

        Assert.Equal(ShellResult.Error(Damage.Message("archive", P("/Work/Inbox/broken.md"))), await Run("archive /Work/Inbox/broken.md"));
    }

    [Fact]
    public async Task A_moved_note_says_where_it_went()
    {
        Assert.False((await Run("mv /Work/Overview.md /Work/Start.md")).IsError);

        var result = await Run("archive /Work/Overview.md");

        Assert.True(result.IsError);
        Assert.StartsWith("archive: /Work/Overview.md: No such file or directory; it was moved to /Work/Start.md on ", result.Output);
    }

    [Theory]
    [InlineData("/Work/Board/Board.md")]
    [InlineData("/Work/Board/card.md")]
    public async Task A_writer_may_not_archive_the_board_or_a_card_it_links(string path)
    {
        var owner = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));
        await owner.WriteAsync("/Work/Board/Board.md", BoardTemplate.Text() + "See [[card]].\n", null, Ct);
        await owner.WriteAsync("/Work/Board/card.md", "a card\n", null, Ct);

        var result = await Run($"archive {path}", new FixedCaller("writer", LevelOf: _ => AccessLevel.Write));

        Assert.True(result.IsError, result.Output);
        Assert.Equal(Refusals.Structural, result.Code);
        Assert.StartsWith("archive: Only those who may maintain *Work* can", result.Output);
        Assert.NotNull(await _store.ReadAsync(P(path), Ct));
    }

    [Fact]
    public async Task A_folder_of_the_stamped_name_counts_as_a_clash()
    {
        await new NoteWriter(_store, _time, new FixedCaller(Writers.Dev)).WriteAsync("/Work/_archive/Inbox/Manual 2026-11-08 213005/x.md", "x\n", null, Ct);

        var result = await Run("archive /Work/Inbox/Manual/");

        Assert.StartsWith("moved /Work/Inbox/Manual/ to /Work/_archive/Inbox/Manual 2026-11-08 213005-2/", result.Output);
    }

    [Theory]
    [InlineData("/Work/_archive/old.md", "archive: /Work/_archive/old.md is already in the archive")]
    [InlineData("/Work/_archive/", "archive: /Work/_archive/ is already in the archive")]
    [InlineData("/Skills/foo/SKILL.md", "archive: /Skills/foo/SKILL.md is in /Skills/, where skills live; skills and their files can't be archived")]
    [InlineData("/Work/_skills/foo/SKILL.md", "archive: /Work/_skills/foo/SKILL.md is in /Work/_skills/, where skills live; skills and their files can't be archived")]
    [InlineData("/Work/_skills/foo/", "archive: /Work/_skills/foo/ is in /Work/_skills/, where skills live; skills and their files can't be archived")]
    public async Task Refuses_and_points_to_rm(string path, string expected)
    {
        var before = await _store.ListNotesUnderAsync(VaultPath.Root, Ct);

        var result = await Run($"archive {path}");

        Assert.True(result.IsError);
        Assert.StartsWith(expected, result.Output);
        Assert.EndsWith("(to remove it, use rm)", result.Output);
        Assert.Equal(before, await _store.ListNotesUnderAsync(VaultPath.Root, Ct));
    }

    [Theory]
    [InlineData("archive /Work/Inbox/missing.md", "archive: /Work/Inbox/missing.md: No such file or directory")]
    [InlineData("archive /Work/Inbox/Manual", "archive: /Work/Inbox/Manual: Is a directory (to archive the folder, write /Work/Inbox/Manual/)")]
    [InlineData("archive /Work/Inbox/missing/", "archive: /Work/Inbox/missing/: No such file or directory")]
    [InlineData("archive /Work/Overview.md/", "archive: /Work/Overview.md/: Not a directory (to archive the file, leave out the '/')")]
    [InlineData("archive /Work/Overview.md#Source", "archive: archives whole notes, not sections")]
    [InlineData("archive -f /Work/Overview.md", "archive: invalid option -- 'f'")]
    [InlineData("archive", "archive: missing operand (archive <path>)")]
    [InlineData("archive /Work/", "archive: /Work/ is a vault; a vault can't be archived, only what is in it")]
    [InlineData("archive /", "archive: / is not in a vault; a vault can't be archived, only what is in it")]
    public async Task Errors(string line, string expected) => Assert.Equal(ShellResult.Error(expected), await Run(line));

    [Fact]
    public async Task Two_paths_are_too_many() =>
        Assert.StartsWith("archive: too many arguments", (await Run("archive /Work/Overview.md /Work/Inbox/v1.2")).Output);

    [Fact]
    public async Task A_reader_is_refused_by_the_movers_check()
    {
        var result = await Run("archive /Work/Inbox/report.pdf", new FixedCaller("reader", vault => vault != "Work"));

        Assert.Equal(ShellResult.Error("archive: this key can only read vault Work") with { Code = Refusals.ReadOnly }, result);
        Assert.NotNull(await _store.GetAttachmentAsync(P("/Work/Inbox/report.pdf"), Ct));
    }

    [Fact]
    public async Task History_follows()
    {
        await new NoteWriter(_store, _time, new FixedCaller(Writers.Dev)).WriteAsync("/Work/Inbox/v1.2 notes.md", "two\n", VersionToken.Of("notes\n"), Ct);

        Assert.False((await Run("archive \"/Work/Inbox/v1.2 notes.md\"")).IsError);

        var history = (await Run("history \"/Work/_archive/Inbox/v1.2 notes 2026-11-08 213005.md\"")).Output;
        Assert.Contains("seed  1 line", history);
        Assert.Contains("dev  1 line", history);
    }

    [Fact]
    public async Task Help_says_how_to_restore_and_that_the_archive_is_not_access_control()
    {
        var help = (await Run("help archive")).Output;

        Assert.StartsWith("archive <path>", help);
        Assert.Contains("server's time zone", help);
        Assert.Contains("unless given --archive or a path", help);
        Assert.Contains("To restore, mv it back (mv never overwrites).", help);
        Assert.Contains("hiding the archive is about attention, not access", help);
    }
}
