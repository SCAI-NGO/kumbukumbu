// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Attachments;

public class AttachmentWriterTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly InMemoryNoteStore _store = new(("/Work/a/x.md", "a note\n"), ("/Work/d.png/n.md", "inside\n"));
    private readonly FakeTimeProvider _time = new(InMemoryNoteStore.Seeded.AddDays(1));

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    /// <summary>A PNG as far as the content check goes: the signature, then <paramref name="n"/>.</summary>
    private static byte[] Png(int n) => [.. PngSignature, .. BitConverter.GetBytes(n)];

    private AttachmentWriter Writer(ICaller? caller = null) => new(_store, _store, _time, caller ?? new FixedCaller(Writers.Dev));

    private async Task<StoredAttachment> Get(string path) => (await _store.GetAttachmentAsync(P(path), Ct))!;

    private static async Task<string> Write(AttachmentWriter writer, string path, byte[] bytes, string? version)
    {
        var result = await writer.WriteAsync(path, bytes, (10, 20), version, Ct);
        Assert.False(result.Result.IsError, result.Result.Output);
        return result.Result.Version!.Token;
    }

    [Fact]
    public async Task Writes_by_one_person_within_two_minutes_are_one_version()
    {
        var person = Writer(new FixedCaller("chris", Human: true));
        var v1 = await Write(person, "/Work/x.png", Png(1), null);
        _time.Advance(TimeSpan.FromSeconds(110));
        var v2 = await Write(person, "/Work/x.png", Png(2), v1);
        _time.Advance(TimeSpan.FromSeconds(110));
        var v3 = await Write(person, "/Work/x.png", Png(3), v2);
        _time.Advance(TimeSpan.FromMinutes(2));
        await Write(person, "/Work/x.png", Png(4), v3);

        // The created state is a version of its own; 2 joined the changeset of 3, which a pause of two minutes closed.
        var attachment = await Get("/Work/x.png");
        Assert.Equal(VersionToken.Of(Png(4)), attachment.Version);
        Assert.Equal([v3, v1], attachment.History.Select(h => h.State.Version));
        Assert.Equal("chris", attachment.LastWriter);
    }

    [Fact]
    public async Task An_agent_gets_ten_minutes()
    {
        var agent = Writer();
        var v1 = await Write(agent, "/Work/x.png", Png(1), null);
        _time.Advance(TimeSpan.FromMinutes(9));
        var v2 = await Write(agent, "/Work/x.png", Png(2), v1);
        _time.Advance(TimeSpan.FromMinutes(9));
        var v3 = await Write(agent, "/Work/x.png", Png(3), v2);
        _time.Advance(TimeSpan.FromMinutes(10));
        await Write(agent, "/Work/x.png", Png(4), v3);

        Assert.Equal([v3, v1], (await Get("/Work/x.png")).History.Select(h => h.State.Version));
    }

    [Fact]
    public async Task Notes_and_attachments_share_the_writers_changeset()
    {
        var notes = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));
        await notes.WriteAsync("/Work/plan.md", "plan\n", null, Ct);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Write(Writer(), "/Work/x.png", Png(1), null);

        Assert.Equal((await _store.GetAsync(P("/Work/plan.md"), Ct))!.Changeset, (await Get("/Work/x.png")).Changeset);
    }

    [Fact]
    public async Task The_eleventh_version_drops_the_oldest()
    {
        var writer = Writer();
        string? version = null;
        for (var i = 0; i <= 11; i++)
        {
            version = await Write(writer, "/Work/x.png", Png(i), version);
            _time.Advance(TimeSpan.FromMinutes(11));
        }

        var history = (await Get("/Work/x.png")).History;
        Assert.Equal(NoteWriter.HistoryLimit, history.Count);
        Assert.Equal(Enumerable.Range(1, 10).Reverse().Select(i => VersionToken.Of(Png(i))), history.Select(h => h.State.Version));
    }

    [Fact]
    public async Task A_stale_version_is_a_conflict_with_the_current_state()
    {
        var writer = Writer();
        var v1 = await Write(writer, "/Work/x.png", Png(1), null);
        var v2 = await Write(writer, "/Work/x.png", Png(2), v1);

        var stale = await writer.WriteAsync("/Work/x.png", Png(3), (10, 20), v1, Ct);
        var exists = await writer.WriteAsync("/Work/x.png", Png(3), (10, 20), null, Ct);

        foreach (var result in new[] { stale, exists })
        {
            Assert.True(result.Result.IsError);
            Assert.Equal(ShellErrorKind.Conflict, result.Result.Kind);
            Assert.Equal(new AttachmentState("image/png", Png(2).Length, 10, 20, v2, (await Get("/Work/x.png")).Current.BlobId), result.Attachment!.Current);
        }

        Assert.Equal("upload: /Work/x.png has changed since version " + v1 + " (it is now " + v2 + "); fetch it again and redo the change", stale.Result.Output);
        Assert.Equal(v2, (await Get("/Work/x.png")).Version);
    }

    [Fact]
    public async Task A_note_and_an_attachment_cannot_share_a_path_without_extension()
    {
        var writer = Writer();
        await Write(writer, "/Work/a/x.png", Png(1), null);

        // A note's path ends in .md, an attachment's in its format: writing over a note is refused before anything else.
        var overNote = await writer.WriteAsync("/Work/a/x.md", Png(2), null, null, Ct);

        Assert.True(overNote.Result.IsError);
        Assert.Equal(ShellErrorKind.Invalid, overNote.Result.Kind);
        Assert.Equal("a note\n", await _store.ReadAsync(P("/Work/a/x.md"), Ct));
        Assert.NotNull(await _store.GetAttachmentAsync(P("/Work/a/x.png"), Ct));
        Assert.Null(await _store.GetAttachmentAsync(P("/Work/a/x.md"), Ct));
    }

    [Fact]
    public async Task Content_that_does_not_match_its_name_is_refused()
    {
        var writer = Writer();

        var pdf = await writer.WriteAsync("/Work/x.pdf", Png(1), null, null, Ct);
        var docx = await writer.WriteAsync("/Work/x.docx", Png(1), null, null, Ct);

        Assert.Equal("upload: x.pdf isn't a PDF document (its content doesn't match its name)", pdf.Result.Output);
        Assert.Equal($"upload: x.docx: {AttachmentFormat.UnsupportedMessage}", docx.Result.Output);
        Assert.Empty(await _store.ListAttachmentsUnderAsync(VaultPath.Root, Ct));
    }

    [Fact]
    public async Task A_file_over_15_MB_is_refused()
    {
        var bytes = new byte[AttachmentFormat.MaxBytes + 1];
        PngSignature.CopyTo(bytes, 0);

        var result = await Writer().WriteAsync("/Work/x.png", bytes, (10, 20), null, Ct);

        Assert.Equal("upload: x.png is larger than 15 MB, the limit for a file", result.Result.Output);
        Assert.Equal(ShellErrorKind.Invalid, result.Result.Kind);
    }

    [Fact]
    public async Task A_new_attachment_needs_an_existing_vault_and_folder_room()
    {
        var writer = Writer();

        Assert.Contains("there is no vault 'Nope'", (await writer.WriteAsync("/Nope/x.png", Png(1), null, null, Ct)).Result.Output);
        Assert.Equal("upload: /Work/a/x.md: Not a directory", (await writer.WriteAsync("/Work/a/x.md/y.png", Png(1), null, null, Ct)).Result.Output);
        Assert.Equal("upload: /Work/d.png: Is a directory", (await writer.WriteAsync("/Work/d.png", Png(1), null, null, Ct)).Result.Output);
    }

    [Fact]
    public async Task A_key_that_can_only_read_is_refused()
    {
        var result = await Writer(new FixedCaller("reader", _ => false)).WriteAsync("/Work/x.png", Png(1), null, null, Ct);

        Assert.Equal(ShellErrorKind.Refused, result.Result.Kind);
        Assert.Empty(await _store.ListAttachmentsUnderAsync(VaultPath.Root, Ct));
    }

    [Fact]
    public async Task Text_tools_refuse_attachment_paths()
    {
        var writer = Writer();
        var version = await Write(writer, "/Work/a/x.png", Png(1), null);
        var notes = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));
        var replacer = new Replacer(_store, _time, new Plans(), new FixedCaller(Writers.Dev));

        var results = new[]
        {
            await notes.WriteAsync("/Work/a/x.png", "text", version, Ct),
            await notes.WriteAsync("/Work/a/new.png", "text", null, Ct),
            await notes.EditAsync("/Work/a/x.png", version, null, "text", Ct),
            await replacer.RunAsync("/Work/a/x.png", "a", "b", false, null, Ct),
        };

        Assert.Equal(
            ["write: x.png is an attachment; text tools can't change it", "write: new.png is an attachment; text tools can't change it",
             "edit: x.png is an attachment; text tools can't change it", "replace: x.png is an attachment; text tools can't change it"],
            results.Select(r => r.Output));
        Assert.All(results, r => Assert.True(r.IsError));
        Assert.Equal(version, (await Get("/Work/a/x.png")).Version);
    }

    [Fact]
    public async Task Replace_works_in_a_folder_named_like_an_attachment()
    {
        var replacer = new Replacer(_store, _time, new Plans(), new FixedCaller(Writers.Dev));

        var preview = await replacer.RunAsync("/Work/d.png", "inside", "outside", false, null, Ct);

        Assert.False(preview.IsError, preview.Output);
        Assert.Contains("/Work/d.png/n.md", preview.Output);
    }

    [Fact]
    public async Task Remove_moves_an_attachment_to_the_trash_in_the_writers_changeset()
    {
        var writer = Writer();
        var version = await Write(writer, "/Work/x.png", Png(1), null);
        var changeset = (await Get("/Work/x.png")).Changeset;

        var stale = await writer.RemoveAsync("/Work/x.png", Ct, "000000000000");
        var removed = await writer.RemoveAsync("/Work/x.png", Ct, version);
        var again = await writer.RemoveAsync("/Work/x.png", Ct);

        Assert.Equal($"rm: /Work/x.png has changed since version 000000000000 (it is now {version})", stale.Output);
        Assert.Equal("removed /Work/x.png; it stays in the trash for 30 days ('restore /Work/x.png' brings it back)", removed.Output);
        Assert.Equal("rm: /Work/x.png: No such file or directory", again.Output);
        Assert.Null(await _store.GetAttachmentAsync(P("/Work/x.png"), Ct));
        var trashed = Assert.Single(await _store.ListAttachmentTrashAsync(P("/Work/x.png"), Ct));
        Assert.Equal(changeset, trashed.Changeset);
        Assert.Equal(Writers.Dev, trashed.DeletedBy);
    }

    [Fact]
    public async Task Restore_from_trash_brings_back_the_bytes()
    {
        var writer = Writer();
        var version = await Write(writer, "/Work/x.png", Png(1), null);
        await writer.RemoveAsync("/Work/x.png", Ct);

        var restored = await writer.RestoreAsync("/Work/x.png", null, Ct);

        Assert.Equal("restored /Work/x.png from the trash", restored.Output);
        Assert.Equal(version, restored.Version!.Token);
        var attachment = await Get("/Work/x.png");
        Assert.Equal(version, attachment.Version);
        Assert.Null(attachment.Changeset);
        Assert.Empty(await _store.ListAttachmentTrashAsync(P("/Work/x.png"), Ct));
        await using var blob = await _store.OpenBlobAsync("Work", attachment.Current.BlobId, Ct);
        using var read = new MemoryStream();
        await blob!.CopyToAsync(read, Ct);
        Assert.Equal(Png(1), read.ToArray());

        Assert.Equal("restore: /Work/x.png exists; to go back to an earlier version, give one from 'history /Work/x.png'",
            (await writer.RestoreAsync("/Work/x.png", null, Ct)).Output);
    }

    [Fact]
    public async Task Restore_from_history_makes_an_earlier_version_current_and_keeps_the_replaced_one()
    {
        var writer = Writer();
        var v1 = await Write(writer, "/Work/x.png", Png(1), null);
        var v2 = await Write(writer, "/Work/x.png", Png(2), v1);

        var restored = await writer.RestoreAsync("/Work/x.png", v1, Ct);
        var unknown = await writer.RestoreAsync("/Work/x.png", "000000000000", Ct);

        Assert.Equal($"restored /Work/x.png to version {v1}", restored.Output);
        Assert.Equal("restore: /Work/x.png has no version 000000000000 ('history /Work/x.png' lists them)", unknown.Output);
        var attachment = await Get("/Work/x.png");
        Assert.Equal(v1, attachment.Version);
        Assert.Equal("dev (restore)", attachment.LastWriter);
        Assert.Null(attachment.Changeset);
        Assert.Equal([v2, v1], attachment.History.Select(h => h.State.Version));
    }

    [Fact]
    public async Task A_marker_is_written_like_a_file_in_the_writers_changeset()
    {
        var notes = new NoteWriter(_store, _time, new FixedCaller(Writers.Dev));
        await notes.WriteAsync("/Work/plan.md", "plan\n", null, Ct);
        _time.Advance(TimeSpan.FromMinutes(1));

        var result = await Writer().WriteAsync("/Work/Drafts/.vaultkeep", Array.Empty<byte>(), null, null, Ct);

        Assert.False(result.Result.IsError, result.Result.Output);
        Assert.Equal("created /Work/Drafts/", result.Result.Output);
        var marker = await Get("/Work/Drafts/.vaultkeep");
        Assert.Equal(0, marker.Current.Size);
        Assert.Equal("application/octet-stream", marker.Current.MediaType);
        Assert.Equal("e3b0c44298fc", marker.Version);
        Assert.Equal((await _store.GetAsync(P("/Work/plan.md"), Ct))!.Changeset, marker.Changeset);
    }

    [Fact]
    public async Task A_marker_directly_in_a_vault_is_refused()
    {
        var result = await Writer().WriteAsync("/Work/.vaultkeep", Array.Empty<byte>(), null, null, Ct);

        Assert.Equal(ShellErrorKind.Invalid, result.Result.Kind);
        Assert.Equal("mkdir: /Work/: A vault exists on its own; it needs no .vaultkeep", result.Result.Output);
    }

    [Fact]
    public async Task A_marker_below_a_note_is_not_a_directory()
    {
        await new NoteWriter(_store, _time, new FixedCaller(Writers.Dev)).WriteAsync("/Work/plan.md", "plan\n", null, Ct);

        var result = await Writer().WriteAsync("/Work/plan.md/.vaultkeep", Array.Empty<byte>(), null, null, Ct);

        Assert.Equal(ShellErrorKind.Conflict, result.Result.Kind);
        Assert.Equal("mkdir: /Work/plan.md: Not a directory", result.Result.Output);
    }

    [Fact]
    public async Task Removing_a_marker_names_its_folder()
    {
        await Write(Writer(), "/Work/Drafts/.vaultkeep", [], null);

        var result = await Writer().RemoveAsync("/Work/Drafts/.vaultkeep", Ct);

        Assert.Equal("removed folder /Work/Drafts/; it stays in the trash for 30 days ('restore /Work/Drafts/' brings it back)", result.Output);
    }
}
