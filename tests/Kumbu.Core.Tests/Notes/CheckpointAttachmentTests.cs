// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Notes;

/// <summary>Attachments in checkpoints (made, browsed, compared and restored as notes are) and in their history.</summary>
public class CheckpointAttachmentTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly InMemoryNoteStore _store = new(("/Work/plan.md", "see ![](/shot.png)\n"), ("/Ideas/x.md", "x\n"));
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 10, 27, 11, 0, 0, DateTimeKind.Utc));
    private readonly NoteWriter _writer;
    private readonly AttachmentWriter _files;
    private readonly Shell _sh;

    public CheckpointAttachmentTests()
    {
        var caller = new FixedCaller(Writers.Dev);
        _writer = new NoteWriter(_store, _time, caller);
        _files = new AttachmentWriter(_store, _store, _time, caller);
        _sh = new Shell(_store, _writer, new NoteMover(_store, _time, caller), new Checkpoints(_store, _writer, new Plans()));
    }

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static byte[] Png(int n) => [.. PngSignature, .. BitConverter.GetBytes(n)];

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, Ct);

    private Task<StoredAttachment?> Get(string path) => _store.GetAttachmentAsync(P(path), Ct);

    private async Task<string> Upload(string path, byte[] bytes)
    {
        var result = await _files.WriteAsync(path, bytes, (10, 20), (await Get(path))?.Version, Ct);
        Assert.False(result.Result.IsError, result.Result.Output);
        return result.Result.Version!.Token;
    }

    private async Task<byte[]> Bytes(StoredAttachment attachment)
    {
        await using var stream = await _store.OpenBlobAsync(attachment.Path.Vault!, attachment.Current.BlobId, Ct);
        using var read = new MemoryStream();
        await stream!.CopyToAsync(read, Ct);
        return read.ToArray();
    }

    private static string Plan(ShellResult preview) =>
        preview.Output.Split('\n').Single(l => l.StartsWith("plan: ", StringComparison.Ordinal))["plan: ".Length..];

    [Fact]
    public async Task A_checkpoint_keeps_an_attachment_version_after_it_changes()
    {
        var v1 = await Upload("/Work/shot.png", Png(1));
        var original = (await Get("/Work/shot.png"))!;

        Assert.Equal(
            "checkpoint 'before' of /Work/ (1 note, 1 attachment); read from it with --checkpoint before, e.g. ls --checkpoint before /Work",
            (await Run("mkcp /Work before")).Output);
        // Pushed out of the history entirely: the checkpoint still holds its own reference to the bytes.
        for (var i = 2; i <= NoteWriter.HistoryLimit + 3; i++)
        {
            _time.Advance(TimeSpan.FromHours(1));
            await Upload("/Work/shot.png", Png(i));
        }

        Assert.DoesNotContain((await Get("/Work/shot.png"))!.History, h => h.State.Version == v1);
        var checkpoint = Assert.Single(await _store.ListCheckpointsAsync(P("/Work"), Ct));
        var held = Assert.Single(checkpoint.Attachments);
        Assert.Equal((P("/Work/shot.png"), v1, original.Current.BlobId), (held.Path, held.Version, held.BlobId));
        Assert.Contains("1 note, 1 attachment", (await Run("lscp /Work")).Output);

        // Browsing the checkpoint shows the attachment as it was, with its bytes.
        var (view, _) = await new Checkpoints(_store, _writer, new Plans()).OpenAsync("ls", "Work", "before", Ct);
        var then = (await StoreAttachments.GetAsync(view!, P("/Work/shot.png"), Ct))!;
        Assert.Equal(v1, then.Version);
        Assert.Equal(Png(1), await Bytes(then));
        Assert.Equal([P("/Work/shot.png")], await StoreAttachments.PathsUnderAsync(view!, P("/Work"), Ct));
        Assert.Contains("changed  /Work/shot.png", (await Run("diff --checkpoint before /Work")).Output);
    }

    [Fact]
    public async Task Restoring_a_checkpoint_restores_attachments()
    {
        var shot = await Upload("/Work/shot.png", Png(1));
        var report = await Upload("/Work/docs/old.png", Png(2));
        await Run("mkcp /Work before");
        _time.Advance(TimeSpan.FromHours(1));
        await Upload("/Work/shot.png", Png(3));
        Assert.False((await _files.RemoveAsync("/Work/docs/old.png", Ct)).IsError);
        await Upload("/Work/new.png", Png(4));

        var preview = await Run("restore --checkpoint before /Work");
        Assert.StartsWith(
            """
            restore /Work/ to checkpoint 'before' (2026-10-27 11:00 UTC, dev):
              back        /Work/docs/old.png
              to trash    /Work/new.png  (created since)
              changed     /Work/shot.png

            """,
            preview.Output);
        var applied = await Run($"restore --checkpoint before --plan {Plan(preview)} /Work");

        Assert.Equal("restored /Work/ to checkpoint 'before': 1 changed, 1 back, 0 moved back, 1 to the trash", applied.Output);
        var restored = (await Get("/Work/shot.png"))!;
        Assert.Equal(shot, restored.Version);
        Assert.Equal(Png(1), await Bytes(restored));
        Assert.Equal("dev (restore)", restored.LastWriter);
        // The state it replaced stays in the history, as for a note.
        Assert.Equal(VersionToken.Of(Png(3)), restored.History[0].State.Version);
        Assert.Equal(report, (await Get("/Work/docs/old.png"))!.Version);
        Assert.Null(await Get("/Work/new.png"));
        Assert.Contains(await _store.ListAttachmentTrashAsync(P("/Work/new.png"), Ct), t => t.Attachment.Path == P("/Work/new.png"));
        Assert.Contains("(no changes in /Work/", (await Run("diff --checkpoint before /Work")).Output);
    }

    [Fact]
    public async Task An_attachment_and_a_checkpoint_of_it_are_restored_at_once()
    {
        var v1 = await Upload("/Work/shot.png", Png(1));
        Assert.Equal(
            "checkpoint 'one' of /Work/shot.png (1 attachment); read from it with --checkpoint one, e.g. cat --checkpoint one /Work/shot.png",
            (await Run("mkcp /Work/shot.png one")).Output);
        await Run("mkcp /Work all");
        await Upload("/Work/shot.png", Png(2));

        var result = await Run("restore --checkpoint one /Work/shot.png");
        Assert.Equal(("restored /Work/shot.png to checkpoint 'one'", v1), (result.Output, result.Version!.Token));
        Assert.Equal(v1, (await Get("/Work/shot.png"))!.Version);

        // From a folder checkpoint too, and back from the trash.
        Assert.False((await _files.RemoveAsync("/Work/shot.png", Ct)).IsError);
        Assert.Equal("restored /Work/shot.png from checkpoint 'all' (it had been removed)", (await Run("restore --checkpoint all /Work/shot.png")).Output);
        Assert.Equal(Png(1), await Bytes((await Get("/Work/shot.png"))!));
        Assert.Equal("/Work/shot.png is already as in checkpoint 'all'", (await Run("restore --checkpoint all /Work/shot.png")).Output);
    }

    [Fact]
    public async Task A_folder_restore_moves_a_moved_attachment_back()
    {
        await Upload("/Work/shot.png", Png(1));
        await Run("mkcp /Work all");
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False((await Run("mv /Work/shot.png /Work/pics/shot.png")).IsError);
        Assert.Equal("see ![](/pics/shot.png)\n", await _store.ReadAsync(P("/Work/plan.md"), Ct));

        var preview = await Run("restore --checkpoint all /Work");
        Assert.Contains("  moved back  /Work/pics/shot.png → /Work/shot.png", preview.Output);
        var applied = await Run($"restore --checkpoint all --plan {Plan(preview)} /Work");

        Assert.StartsWith("restored /Work/ to checkpoint 'all': 1 changed, 0 back, 1 moved back, 0 to the trash", applied.Output);
        Assert.Equal("see ![](/shot.png)\n", await _store.ReadAsync(P("/Work/plan.md"), Ct));
        var back = (await Get("/Work/shot.png"))!;
        // Moved back with its history, not created anew.
        Assert.Equal(3L, back.Revision);
        Assert.Null(await Get("/Work/pics/shot.png"));
    }

    [Fact]
    public async Task Links_outside_follow_an_attachment_moved_back()
    {
        await Upload("/Work/pics/a.png", Png(1));
        await _writer.WriteAsync("/Work/plan.md", "see ![](/pics/a.png)\n", VersionToken.Of("see ![](/shot.png)\n"), Ct);
        await Run("mkcp /Work/pics pics");
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False((await Run("mv /Work/pics/a.png /Work/a.png")).IsError);
        Assert.Equal("see ![](/a.png)\n", await _store.ReadAsync(P("/Work/plan.md"), Ct));

        var preview = await Run("restore --checkpoint pics /Work/pics");
        Assert.StartsWith(
            """
            restore /Work/pics/ to checkpoint 'pics' (2026-10-27 11:00 UTC, dev):
              moved back  /Work/a.png → /Work/pics/a.png
            Links follow the notes moved back: 1 link rewritten in /Work/plan.md.
            """,
            preview.Output);
        await Run($"restore --checkpoint pics --plan {Plan(preview)} /Work/pics");

        Assert.Equal("see ![](/pics/a.png)\n", await _store.ReadAsync(P("/Work/plan.md"), Ct));
        Assert.NotNull(await Get("/Work/pics/a.png"));
    }

    [Fact]
    public async Task A_preview_names_links_to_an_attachment_going_to_the_trash()
    {
        await Upload("/Work/pics/a.png", Png(1));
        await Run("mkcp /Work/pics before");
        await Upload("/Work/pics/new.png", Png(2));
        await _writer.WriteAsync("/Work/plan.md", "see ![[new.png]]\n", VersionToken.Of("see ![](/shot.png)\n"), Ct);

        var preview = await Run("restore --checkpoint before /Work/pics");

        Assert.Contains(
            """
            After the restore, 1 link outside /Work/pics/ would lead nowhere:
              /Work/plan.md:1: ![[new.png]] (now /Work/pics/new.png, going to the trash)
            """,
            preview.Output);
    }

    [Fact]
    public async Task A_checkpoint_of_a_folder_with_only_attachments_is_one()
    {
        await Upload("/Work/pics/a.png", Png(1));

        Assert.StartsWith("checkpoint 'pics' of /Work/pics/ (1 attachment)", (await Run("mkcp /Work/pics pics")).Output);
        // A checkpoint without attachments reads as before.
        Assert.StartsWith("checkpoint 'ideas' of /Ideas/ (1 note);", (await Run("mkcp /Ideas ideas")).Output);
    }

    [Fact]
    public async Task History_lists_attachment_versions_newest_first()
    {
        var v1 = await Upload("/Work/shot.png", Png(1));
        _time.Advance(TimeSpan.FromHours(1));
        var v2 = await Upload("/Work/shot.png", Png(2));
        _time.Advance(TimeSpan.FromHours(1));
        var v3 = await Upload("/Work/shot.png", Png(3));

        var versions = NoteHistory.Versions((await Get("/Work/shot.png"))!);

        Assert.Equal([v3, v2, v1], versions.Select(v => v.Version));
        Assert.Equal([true, false, false], versions.Select(v => v.Current));
        Assert.Equal(
            [_time.GetUtcNow().UtcDateTime, _time.GetUtcNow().UtcDateTime.AddHours(-1), _time.GetUtcNow().UtcDateTime.AddHours(-2)],
            versions.Select(v => v.WrittenAt));
        Assert.All(versions, v => Assert.Equal((Writers.Dev, "image/png", 12L, v.Version), (v.Writer, v.State.MediaType, v.State.Size, v.State.Version)));
        Assert.NotNull(versions[0].Changeset);
    }
}
