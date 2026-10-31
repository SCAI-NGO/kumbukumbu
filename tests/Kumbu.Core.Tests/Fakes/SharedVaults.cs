// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Tests.Fakes;

/// <summary>A shared vault as an owner and as a member see it, for the marks of collab design §10.3.</summary>
internal static class SharedVaults
{
    /// <summary>Alice's "Garten", shared: <c>plan.md</c> last written by Bob (a member, writer id w-bob), <c>beet.md</c> by
    /// Alice's agent "claude", <c>own.md</c> by Bob's agent; both members' writes store the empty label.</summary>
    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    internal static InMemoryNoteStore SharedGarten(bool asMember)
    {
        var store = new InMemoryNoteStore();
        store.Put(new StoredNote(P("/Garten/plan.md"), "Beete planen\n", 1, "", InMemoryNoteStore.Seeded, [], "Beete planen\n", Ref: WriterRef.ForMember("w-carol")));
        store.Put(new StoredNote(P("/Garten/beet.md"), "Beete gießen\n", 1, "claude", InMemoryNoteStore.Seeded, [], "Beete gießen\n", Ref: WriterRef.ForOwner(WriterRef.Agent, "k1")));
        store.Put(new StoredNote(P("/Garten/own.md"), "Beete jäten\n", 1, "", InMemoryNoteStore.Seeded, [], "Beete jäten\n", Ref: WriterRef.ForMember("w-bob")));
        store.Infos["Garten"] = asMember ? new VaultInfo("\"Alice\"", AccessLevel.Write, true) : new VaultInfo(null, AccessLevel.Owner, true);
        return store;
    }

    /// <summary>A shell as Bob, a member: his own writes are those under his writer id.</summary>
    internal static Shell AsBob(InMemoryNoteStore store)
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        return new Shell(
            store, new NoteWriter(store, time, new FixedCaller(Writers.Dev)), new NoteMover(store, time, new FixedCaller(Writers.Dev)), labels: new BobsLabels());
    }

    private sealed class BobsLabels : IWriterLabels
    {
        public ValueTask<(string Display, string? Kind)> RenderAsync(string vault, string label, WriterRef? reference, CancellationToken ct) =>
            ValueTask.FromResult<(string, string?)>((label, null));

        public Task<bool> IsOwnAsync(string vault, WriterRef? reference, CancellationToken ct) => Task.FromResult(reference is { Owner: false, Writer: "w-bob" });
    }
}
