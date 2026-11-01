// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Notes;

/// <summary>Every entry that stores a writer's label stores the caller's reference beside it (collab design §8.1).</summary>
public class WriterRefTests
{
    private static readonly WriterRef Key = WriterRef.ForOwner(WriterRef.Agent, "key-1");

    private readonly InMemoryNoteStore _store = new(("/Work/a.md", "one\n"), ("/Work/b.md", "b\n"));
    private readonly FakeTimeProvider _time = new(InMemoryNoteStore.Seeded.AddDays(1));

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    [Fact]
    public async Task A_write_stores_the_callers_reference_beside_its_label()
    {
        var caller = new FixedCaller("Claude") { Reference = Key };
        var writer = new NoteWriter(_store, _time, caller);
        var sh = new Shell(_store, writer, new NoteMover(_store, _time, caller));

        Assert.False((await writer.WriteAsync("/Work/a.md", "two\n", VersionToken.Of("one\n"), Ct)).IsError);
        var note = (await _store.GetAsync(P("/Work/a.md"), Ct))!;
        Assert.Equal(("Claude", Key), (note.LastWriter, note.Ref));

        _time.Advance(TimeSpan.FromHours(1));
        Assert.False((await writer.WriteAsync("/Work/a.md", "three\n", VersionToken.Of("two\n"), Ct)).IsError);
        var history = (await _store.GetAsync(P("/Work/a.md"), Ct))!.History;
        Assert.Equal(("Claude", Key), (history[0].WrittenBy, history[0].Ref));
        Assert.Equal(WriterRef.Server, history[1].Ref);

        Assert.False((await sh.RunAsync("mv /Work/a.md /Work/c.md", Ct)).IsError);
        var move = (await _store.FindMoveAsync(P("/Work/a.md"), Ct))!;
        Assert.Equal(("Claude", Key), (move.MovedBy, move.Ref));

        Assert.False((await sh.RunAsync("rm /Work/b.md", Ct)).IsError);
        var trashed = (await _store.ListTrashAsync(P("/Work/b.md"), Ct)).Single();
        Assert.Equal(("Claude", Key), (trashed.DeletedBy, trashed.Ref));

        Assert.False((await sh.RunAsync("mkcp /Work before", Ct)).IsError);
        var checkpoint = (await _store.ListCheckpointsAsync(VaultPath.Root, Ct)).Single();
        Assert.Equal(("Claude", Key), (checkpoint.CreatedBy, checkpoint.Ref));

        var written = await writer.ForAttachments()!.WriteAsync("/Work/x.png", TestPng, (1, 1), null, Ct);
        Assert.False(written.Result.IsError);
        var attachment = (await _store.GetAttachmentAsync(P("/Work/x.png"), Ct))!;
        Assert.Equal(("Claude", Key), (attachment.LastWriter, attachment.Ref));
    }

    private static readonly byte[] TestPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
}
