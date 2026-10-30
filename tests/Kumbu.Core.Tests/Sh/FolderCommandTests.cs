// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary>Folders that stay when empty: made with mkdir, removed with rm, brought back with restore.</summary>
public class FolderCommandTests
{
    private readonly InMemoryNoteStore _store = new(("/Work/plan.md", "plan\n"), ("/Work/Sub/deep.md", "deep\n"));
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 10, 30, 11, 0, 0, DateTimeKind.Utc));

    private static CancellationToken Ct => CancellationToken.None;

    private Shell ShellFor(ICaller caller) =>
        new(_store, new NoteWriter(_store, _time, caller), new NoteMover(_store, _time, caller));

    private Task<ShellResult> Run(string line, ICaller? caller = null) => ShellFor(caller ?? new FixedCaller(Writers.Dev)).RunAsync(line, Ct);

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path);

    private Task<StoredAttachment?> Marker(string path) => _store.GetAttachmentAsync(P(path), Ct);

    [Fact]
    public async Task Mkdir_makes_a_kept_folder_that_ls_lists()
    {
        var result = await Run("mkdir /Work/Drafts");

        Assert.Equal("made folder /Work/Drafts/; an empty folder stays until you remove it (rm /Work/Drafts)", result.Output);
        Assert.Contains("Drafts/", (await Run("ls /Work")).Output.Split('\n'));
        var marker = await Marker("/Work/Drafts/.vaultkeep");
        Assert.NotNull(marker);
        Assert.Equal(0, marker.Current.Size);
    }

    [Fact]
    public async Task Mkdir_writes_only_the_deepest_marker()
    {
        Assert.False((await Run("mkdir /Work/A/B")).IsError);

        Assert.NotNull(await Marker("/Work/A/B/.vaultkeep"));
        Assert.Null(await Marker("/Work/A/.vaultkeep"));
    }

    [Theory]
    [InlineData("/Work/Drafts", "/Work/Drafts/ exists already; nothing was written")]
    [InlineData("/Work/Sub", "/Work/Sub/ exists already; nothing was written")]
    [InlineData("/Work", "/Work/ is a vault and exists already; nothing was written")]
    public async Task Mkdir_on_an_existing_folder_writes_nothing(string path, string expected)
    {
        await Run("mkdir /Work/Drafts");
        var before = (await Marker("/Work/Drafts/.vaultkeep"))!.Revision;

        var result = await Run($"mkdir {path}");

        Assert.Equal(ShellResult.Ok(expected), result);
        Assert.Equal(before, (await Marker("/Work/Drafts/.vaultkeep"))!.Revision);
        Assert.Null(await Marker("/Work/Sub/.vaultkeep"));
    }

    [Fact]
    public async Task Mkdir_in_no_vault_is_an_error() =>
        Assert.Equal(
            ShellResult.Error("mkdir: there is no vault 'Wrk' (ls lists the vaults; mkdir does not create vaults)"),
            await Run("mkdir /Wrk/Drafts"));

    [Fact]
    public async Task Mkdir_on_a_note_path_is_an_error() =>
        Assert.Equal(ShellResult.Error("mkdir: /Work/plan.md: File exists (it is a note)"), await Run("mkdir /Work/plan.md"));

    [Fact]
    public async Task Mkdir_on_a_file_path_is_an_error()
    {
        var files = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev)).ForAttachments()!;
        Assert.False((await files.WriteAsync("/Work/shot.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, (1, 1), null, Ct)).Result.IsError);

        Assert.Equal(ShellResult.Error("mkdir: /Work/shot.png: File exists (it is a file)"), await Run("mkdir /Work/shot.png"));
    }

    [Fact]
    public async Task Mkdir_on_a_read_only_vault_is_refused()
    {
        var result = await Run("mkdir /Work/Drafts", new FixedCaller(Writers.Dev, v => false));

        Assert.True(result.IsError);
        Assert.Contains("mkdir:", result.Output);
        Assert.Null(await Marker("/Work/Drafts/.vaultkeep"));
    }

    [Fact]
    public async Task Mkdir_has_no_p() =>
        Assert.Equal(ShellResult.Error("mkdir: there is no -p: the folders above are always made"), await Run("mkdir -p /Work/A/B"));

    [Fact]
    public async Task Mkdir_names_a_damaged_marker_as_its_folder()
    {
        _store.Damaged.Add(P("/Work/Drafts/.vaultkeep"));

        var result = await Run("mkdir /Work/Drafts");

        Assert.True(result.IsError);
        Assert.StartsWith("mkdir: /Work/Drafts/: damaged:", result.Output);
        Assert.DoesNotContain(".vaultkeep", result.Output);
    }

    [Fact]
    public async Task Restore_of_a_damaged_marker_names_its_folder()
    {
        _store.Damaged.Add(P("/Work/Drafts/.vaultkeep"));

        var result = await new NoteWriter(_store, _time, new FixedCaller(Writers.Dev)).ForAttachments()!
            .RestoreAsync("/Work/Drafts/.vaultkeep", null, Ct);

        Assert.StartsWith("restore: /Work/Drafts/: damaged:", result.Output);
    }

    [Fact]
    public async Task Rm_removes_a_kept_empty_folder_to_the_trash()
    {
        await Run("mkdir /Work/Drafts");

        var result = await Run("rm /Work/Drafts");

        Assert.Equal(
            ShellResult.Ok("removed folder /Work/Drafts/; it stays in the trash for 30 days ('restore /Work/Drafts/' brings it back)"),
            result with { Version = null });
        Assert.Null(await Marker("/Work/Drafts/.vaultkeep"));
        Assert.DoesNotContain("Drafts/", (await Run("ls /Work")).Output.Split('\n'));
    }

    [Fact]
    public async Task Rm_refuses_a_folder_with_content_and_counts_it()
    {
        Assert.Equal(
            ShellResult.Error("rm: /Work/Sub: Is a directory holding 1 entry (rm removes one note or file at a time, or an empty folder)"),
            await Run("rm /Work/Sub"));

        await Run("mkdir /Work/Drafts");
        var writer = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));
        await writer.WriteAsync("/Work/Drafts/a.md", "text\n", null, Ct);
        await writer.WriteAsync("/Work/Drafts/b.md", "text\n", null, Ct);
        Assert.Contains("rm: /Work/Drafts: Is a directory holding 2 entries", (await Run("rm /Work/Drafts")).Output);
    }

    [Fact]
    public async Task Rm_counts_a_kept_subfolder_as_an_entry()
    {
        await Run("mkdir /Work/A");
        await Run("mkdir /Work/A/B");

        Assert.Equal(
            ShellResult.Error("rm: /Work/A: Is a directory holding 1 entry (rm removes one note or file at a time, or an empty folder)"),
            await Run("rm /Work/A"));
    }

    [Fact]
    public async Task Rm_of_a_missing_path_is_as_before() =>
        Assert.StartsWith("rm: /Work/Nothing: No such file or directory", (await Run("rm /Work/Nothing")).Output);

    [Fact]
    public async Task Restore_brings_a_removed_folder_back()
    {
        await Run("mkdir /Work/Drafts");
        await Run("rm /Work/Drafts");

        var result = await Run("restore /Work/Drafts/");

        Assert.Equal("restored /Work/Drafts/ from the trash", result.Output);
        Assert.NotNull(await Marker("/Work/Drafts/.vaultkeep"));
    }

    [Fact]
    public async Task A_folder_named_like_a_file_is_removed_and_restored_as_a_folder()
    {
        Assert.False((await Run("mkdir /Work/v1.png")).IsError);

        Assert.Equal(
            ShellResult.Ok("removed folder /Work/v1.png/; it stays in the trash for 30 days ('restore /Work/v1.png/' brings it back)"),
            (await Run("rm /Work/v1.png")) with { Version = null });
        Assert.Null(await Marker("/Work/v1.png/.vaultkeep"));

        Assert.Equal("restored /Work/v1.png/ from the trash", (await Run("restore /Work/v1.png/")).Output);
        Assert.NotNull(await Marker("/Work/v1.png/.vaultkeep"));
    }

    [Fact]
    public async Task A_missing_file_path_is_still_no_such_file()
    {
        Assert.StartsWith("rm: /Work/nope.png: No such file or directory", (await Run("rm /Work/nope.png")).Output);
        Assert.Equal("restore: /Work/nope.png is not in the trash ('trash' lists what is)", (await Run("restore /Work/nope.png")).Output);
    }

    [Fact]
    public async Task A_folder_stays_when_its_last_note_goes()
    {
        var writer = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));
        await Run("mkdir /Work/Drafts");
        Assert.False((await writer.WriteAsync("/Work/Drafts/a.md", "text\n", null, Ct)).IsError);
        Assert.False((await Run("rm /Work/Drafts/a.md")).IsError);

        Assert.Contains("Drafts/", (await Run("ls /Work")).Output.Split('\n'));
    }
}
