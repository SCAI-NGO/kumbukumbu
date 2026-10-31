// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Tests.Fakes;

namespace Kumbu.Core.Tests.Attachments;

public class InMemoryAttachmentStoreTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

    private static VaultPath P(string value)
    {
        Assert.True(VaultPath.TryParse(value, out var path));
        return path;
    }

    private static async Task<StoredAttachment> Attachment(InMemoryNoteStore store, string path, byte[] bytes)
    {
        var blobId = await store.PutBlobAsync(P(path).Vault!, bytes, CancellationToken.None);
        var state = new AttachmentState("image/png", bytes.Length, 1, 1, VersionToken.Of(bytes), blobId);
        return new StoredAttachment(P(path), state, 1, "dev", Now, [], null, Ref: WriterRef.Server);
    }

    [Fact]
    public async Task Put_blob_twice_returns_the_same_id()
    {
        var store = new InMemoryNoteStore(("/work/a.md", "a"));
        byte[] bytes = [1, 2, 3];

        var first = await store.PutBlobAsync("work", bytes, CancellationToken.None);
        var second = await store.PutBlobAsync("work", bytes.ToArray(), CancellationToken.None);
        var other = await store.PutBlobAsync("work", new byte[] { 1, 2, 4 }, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        await using var blob = await store.OpenBlobAsync("work", first, CancellationToken.None);
        using var read = new MemoryStream();
        await blob!.CopyToAsync(read, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, read.ToArray());
        Assert.Null(await store.OpenBlobAsync("other", first, CancellationToken.None));
    }

    [Fact]
    public async Task A_batch_creating_a_note_and_an_attachment_is_atomic()
    {
        var store = new InMemoryNoteStore(("/work/a.md", "a"));
        var attachment = await Attachment(store, "/work/x.png", [1, 2, 3]);
        var batch = new NoteBatch
        {
            Creates = { new StoredNote(P("/work/a.md"), "again", 1, "dev", Now, [], Ref: WriterRef.Server) },
            AttachmentCreates = { attachment },
        };

        var conflicts = await store.TryCommitAsync(batch, "dev", WriterRef.Server, Now, CancellationToken.None);

        Assert.Equal([P("/work/a.md")], conflicts);
        Assert.Null(await store.GetAttachmentAsync(P("/work/x.png"), CancellationToken.None));
        Assert.Equal("a", await store.ReadAsync(P("/work/a.md"), CancellationToken.None));

        var fresh = new NoteBatch
        {
            Creates = { new StoredNote(P("/work/b.md"), "![[x.png]]", 1, "dev", Now, [], Ref: WriterRef.Server) },
            AttachmentCreates = { attachment },
        };
        Assert.Empty(await store.TryCommitAsync(fresh, "dev", WriterRef.Server, Now, CancellationToken.None));
        Assert.Equal(attachment, await store.GetAttachmentAsync(P("/work/x.png"), CancellationToken.None));
        Assert.Equal([attachment], await store.ListAttachmentsUnderAsync(P("/work"), CancellationToken.None));
    }

    [Fact]
    public async Task A_stale_attachment_replace_is_a_conflict()
    {
        var store = new InMemoryNoteStore(("/work/a.md", "a"));
        var attachment = await Attachment(store, "/work/x.png", [1, 2, 3]);
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentCreates = { attachment } }, "dev", WriterRef.Server, Now, CancellationToken.None));
        var next = attachment with { Revision = 2 };
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentReplaces = { (attachment, next) } }, "dev", WriterRef.Server, Now, CancellationToken.None));

        var stale = new NoteBatch { AttachmentReplaces = { (attachment, attachment with { Revision = 2 }) } };

        Assert.Equal([P("/work/x.png")], await store.TryCommitAsync(stale, "dev", WriterRef.Server, Now, CancellationToken.None));
        Assert.Equal(2, (await store.GetAttachmentAsync(P("/work/x.png"), CancellationToken.None))!.Revision);
    }

    [Fact]
    public async Task Trash_lists_attachments_with_their_deletion()
    {
        var store = new InMemoryNoteStore(("/work/a.md", "a"));
        var attachment = await Attachment(store, "/work/img/x.png", [1, 2, 3]);
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentCreates = { attachment } }, "dev", WriterRef.Server, Now, CancellationToken.None));
        var changeset = Changeset.New(Now);

        var removal = new NoteBatch { Changeset = changeset, AttachmentTrashes = { attachment } };
        Assert.Empty(await store.TryCommitAsync(removal, "dev (rm)", WriterRef.Server, Now.AddMinutes(1), CancellationToken.None));

        Assert.Null(await store.GetAttachmentAsync(P("/work/img/x.png"), CancellationToken.None));
        var trashed = Assert.Single(await store.ListAttachmentTrashAsync(P("/work/img"), CancellationToken.None));
        Assert.Equal(new TrashedAttachment(attachment, "dev (rm)", Now.AddMinutes(1), changeset, WriterRef.Server), trashed);
        Assert.Equal([trashed], await store.ListAttachmentTrashAsync(null, CancellationToken.None));
        Assert.Empty(await store.ListAttachmentTrashAsync(P("/other"), CancellationToken.None));
        Assert.Empty(await store.ListTrashAsync(P("/work"), CancellationToken.None));
    }

    [Fact]
    public async Task A_restore_brings_a_trashed_attachment_back_once()
    {
        var store = new InMemoryNoteStore(("/work/a.md", "a"));
        var attachment = await Attachment(store, "/work/x.png", [1, 2, 3]);
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentCreates = { attachment } }, "dev", WriterRef.Server, Now, CancellationToken.None));
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentTrashes = { attachment } }, "dev", WriterRef.Server, Now, CancellationToken.None));
        var trashed = Assert.Single(await store.ListAttachmentTrashAsync(null, CancellationToken.None));
        var restored = attachment with { Revision = 2, LastWriteAt = Now.AddMinutes(1) };

        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentRestores = { (trashed, restored) } }, "dev", WriterRef.Server, Now, CancellationToken.None));

        Assert.Equal(restored, await store.GetAttachmentAsync(P("/work/x.png"), CancellationToken.None));
        Assert.Empty(await store.ListAttachmentTrashAsync(null, CancellationToken.None));
        Assert.Equal(
            [P("/work/x.png")],
            await store.TryCommitAsync(new NoteBatch { AttachmentRestores = { (trashed, restored) } }, "dev", WriterRef.Server, Now, CancellationToken.None));
    }

    [Fact]
    public async Task Attachment_writes_and_removals_keep_the_writers_changeset_open()
    {
        var store = new InMemoryNoteStore(("/work/a.md", "a"));
        var changeset = Changeset.New(Now);
        var attachment = await Attachment(store, "/work/x.png", [1, 2, 3]) with { LastWriter = "lea (upload)", Changeset = changeset };
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentCreates = { attachment } }, "lea (upload)", WriterRef.Server, Now, CancellationToken.None));

        Assert.Equal((changeset, Now), await store.LatestChangesetAsync("work", "lea", CancellationToken.None));

        var removal = Changeset.New(Now.AddMinutes(5));
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentTrashes = { attachment }, Changeset = removal }, "lea (rm)", WriterRef.Server, Now.AddMinutes(5), CancellationToken.None));

        Assert.Equal((removal, Now.AddMinutes(5)), await store.LatestChangesetAsync("work", "lea", CancellationToken.None));
        Assert.Null(await store.LatestChangesetAsync("other", "lea", CancellationToken.None));
    }

    [Fact]
    public async Task A_restore_names_its_trash_entry_by_value_not_by_instance()
    {
        var store = new InMemoryNoteStore(("/work/a.md", "a"));
        var attachment = await Attachment(store, "/work/x.png", [1, 2, 3]);
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentCreates = { attachment } }, "dev", WriterRef.Server, Now, CancellationToken.None));
        Assert.Empty(await store.TryCommitAsync(new NoteBatch { AttachmentTrashes = { attachment } }, "dev", WriterRef.Server, Now, CancellationToken.None));
        var listed = Assert.Single(await store.ListAttachmentTrashAsync(null, CancellationToken.None));
        var copy = listed with { Attachment = listed.Attachment with { History = [.. listed.Attachment.History] } };

        Assert.Empty(await store.TryCommitAsync(
            new NoteBatch { AttachmentRestores = { (copy, copy.Attachment with { Revision = 2 }) } }, "dev", WriterRef.Server, Now, CancellationToken.None));

        Assert.Equal(2, (await store.GetAttachmentAsync(P("/work/x.png"), CancellationToken.None))!.Revision);
        Assert.Empty(await store.ListAttachmentTrashAsync(null, CancellationToken.None));
    }
}
