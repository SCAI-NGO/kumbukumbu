// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Core.Notes;
using Kumbu.Server.Vaults;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>Who wrote which line (blame spec §5.3, §5.4): computed by the store in the write's transaction, sealed in the
/// note's own payload, and kept by everything that carries the payload.</summary>
[Collection("mongo")]
public sealed class AuthorshipStoreTests(MongoFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime T0 = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly WriterRef BritaRef = WriterRef.ForOwner(WriterRef.Session, null);
    private static readonly WriterRef VeyaltRef = WriterRef.ForMember("w-7f3a", WriterRef.Agent);

    private sealed record World(VaultWorld Vaults, string VaultId, byte[] VaultKey, EncryptedNoteStore Store);

    private async Task<World> WorldAsync()
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        var vault = await catalog.CreateAsync("Work", Token, null, Ct);
        return new(world, vault.Id, await SealedPayloads.KeyOfTokenVaultAsync(world.Db, vault.Id, Token, Ct), Store(catalog));
    }

    /// <summary>Another server process on the same database.</summary>
    private static EncryptedNoteStore Later(World w) => Store(w.Vaults.Catalog());

    private static async Task<StoredNote> CreatedAsync(World w, string path, string content)
    {
        Assert.True(await w.Store.TryCreateAsync(new StoredNote(P(path), content, 1, "Brita", T0, [], Ref: BritaRef), Ct));
        return (await w.Store.GetAsync(P(path), Ct))!;
    }

    private static async Task<StoredNote> WrittenAsync(EncryptedNoteStore store, StoredNote current, string content, string writer, WriterRef reference, DateTime at)
    {
        Assert.True(await store.TryReplaceAsync(current, NoteWriter.Next(current, content, writer, reference, at), Ct));
        return (await store.GetAsync(current.Path, Ct))!;
    }

    /// <summary>The stored record's writer and hour of each line; the record must describe the stored content.</summary>
    private static string[] Lines(StoredNote note)
    {
        Assert.NotNull(note.Authorship);
        Assert.Equal(VersionToken.Hash(note.Content), note.Authorship.Hash);
        return [.. note.Authorship.ByLine().Select(w => w is null ? "-" : $"{w.Label}@{(w.At - T0).TotalHours}")];
    }

    private static async Task<string[]> LinesAsync(EncryptedNoteStore store, string path) => Lines((await store.GetAsync(P(path), Ct))!);

    [Fact]
    public async Task The_record_is_sealed_in_the_note_compact_and_reads_back_in_a_second_process()
    {
        var w = await WorldAsync();
        var note = await CreatedAsync(w, "/Work/a.md", "one\ntwo\nthree\n");
        await WrittenAsync(w.Store, note, "one\n2\nthree\n", "", VeyaltRef, T0.AddHours(1));

        var read = (await Later(w).GetAsync(P("/Work/a.md"), Ct))!;
        Assert.Equal(["Brita@0", "@1", "Brita@0"], Lines(read));
        Assert.Equal([BritaRef, VeyaltRef], read.Authorship!.Writes.Select(x => x.Ref));
        Assert.All(read.Authorship.Writes, x => Assert.Equal(DateTimeKind.Utc, x.At.Kind));

        var payload = Assert.Single(await SealedPayloads.OpenAllAsync(w.Vaults.Db, w.VaultId, w.VaultKey, "notes", Ct));
        var by = payload["by"]!.AsObject();
        Assert.Equal(VersionToken.Hash("one\n2\nthree\n"), (string?)by["h"]);
        Assert.Equal([1, 0, 1, 1, 1, 0], by["r"]!.AsArray().Select(n => (int)n!));
        Assert.Equal("""["","2026-11-18T10:00:00Z"]""", new JsonArray([by["w"]![1]![0]!.DeepClone(), by["w"]![1]![2]!.DeepClone()]).ToJsonString());
        Assert.Equal("w-7f3a", (string?)by["w"]![1]![1]!["writer"]);
        Assert.False(by.ContainsKey("e"));
        Assert.False(by.ContainsKey("d"));
    }

    [Fact]
    public void A_payload_without_a_record_reads_with_none_and_a_legacy_or_unknown_writer_keeps_its_kind()
    {
        var json = """{"Path":"/Work/a.md","Content":"a","LastWriter":"dev","LastWriteAt":"2026-10-23T12:00:00Z","History":[],"SeedContent":null}""";
        Assert.Null(JsonSerializer.Deserialize<NotePayload>(json)!.ToNote(1).Authorship);
        var plain = new StoredNote(P("/Work/a.md"), "a", 1, "dev", T0, [], Ref: WriterRef.Server);
        Assert.DoesNotContain("\"by\"", Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(Payload.From(plain))), StringComparison.Ordinal);

        // Backfilled from a history written before references, or sealed without one; dropped once.
        var record = new Authorship(VersionToken.Hash("a\nb\nc"), T0.AddDays(-1), true,
            [new("old", WriterRef.Legacy, T0.AddDays(-2)), new("lost", WriterRef.Unknown, T0.AddDays(-1)), new("dev", WriterRef.Server, T0)], [new(1, 0), new(1, 1), new(1, 2), new(1, -1)]);
        var back = JsonSerializer.Deserialize<NotePayload>(JsonSerializer.SerializeToUtf8Bytes(Payload.From(plain with { Content = "a\nb\nc", Authorship = record })))!.ToNote(1).Authorship!;

        Assert.Equal((record.Hash, record.Earlier, record.Dropped), (back.Hash, back.Earlier, back.Dropped));
        Assert.Equal(record.Writes, back.Writes);
        Assert.Equal(record.Runs, back.Runs);
        Assert.True(back.Writes[0].Ref!.IsLegacy);
        Assert.True(back.Writes[1].Ref!.IsUnknown);
    }

    [Fact]
    public async Task A_move_keeps_the_record()
    {
        var w = await WorldAsync();
        var note = await WrittenAsync(w.Store, await CreatedAsync(w, "/Work/a.md", "one\ntwo\n"), "one\n2\n", "Veyalt", VeyaltRef, T0.AddHours(1));

        Assert.Empty(await w.Store.TryCommitAsync(new NoteBatch { Moves = { (note, note with { Path = P("/Work/b.md"), Revision = note.Revision + 1 }) } }, "Brita", BritaRef, T0.AddHours(2), Ct));

        Assert.Equal(["Brita@0", "Veyalt@1"], await LinesAsync(Later(w), "/Work/b.md"));
    }

    [Fact]
    public async Task The_trash_keeps_the_record_and_a_restore_brings_it_back()
    {
        var w = await WorldAsync();
        var note = await WrittenAsync(w.Store, await CreatedAsync(w, "/Work/a.md", "one\ntwo\n"), "one\n2\n", "Veyalt", VeyaltRef, T0.AddHours(1));
        // A record the caller puts on the state it trashes is not the one the trash keeps.
        var forged = new Authorship(VersionToken.Hash(note.Content), null, false, [new("Brita", BritaRef, T0)], [new(2, 0)]);

        Assert.True(await w.Store.TryTrashAsync(note with { Authorship = forged }, "Brita", BritaRef, T0.AddHours(2), Ct));
        Assert.Equal(["Brita@0", "Veyalt@1"], Lines(Assert.Single(await Later(w).ListTrashAsync(P("/Work"), Ct)).Note));

        var restored = await Later(w).TryRestoreFromTrashAsync(P("/Work/a.md"), null, "Brita", BritaRef, T0.AddHours(3), new Changeset("c1", T0.AddHours(3)), Ct);
        Assert.NotNull(restored);
        Assert.Equal(["Brita@0", "Veyalt@1"], await LinesAsync(Later(w), "/Work/a.md"));
    }

    [Fact]
    public async Task A_stale_record_reads_from_the_history_and_the_next_write_stores_a_true_one()
    {
        var w = await WorldAsync();
        await WrittenAsync(w.Store, await CreatedAsync(w, "/Work/a.md", "one\ntwo\n"), "one\n2\n", "Veyalt", VeyaltRef, T0.AddHours(1));
        // As if written by a server that kept the record but changed the content (a downgrade, a bug).
        await SealedPayloads.RewriteAsync(w.Vaults.Db, w.VaultId, w.VaultKey, "notes", _ => true, p => p["Content"] = "one\n2\nthree\n", Ct);

        var later = Later(w);
        var read = (await later.GetAsync(P("/Work/a.md"), Ct))!;
        Assert.NotEqual(VersionToken.Hash(read.Content), read.Authorship!.Hash);
        Assert.Equal(["Brita@0", "Veyalt@1", "Veyalt@1"], Authorship.Of(read).ByLine().Select(x => $"{x!.Label}@{(x.At - T0).TotalHours}"));

        await WrittenAsync(later, read, "one\n2\nthree\nfour\n", "Brita", BritaRef, T0.AddHours(2));
        Assert.Equal(["Brita@0", "Veyalt@1", "Veyalt@1", "Brita@2"], await LinesAsync(Later(w), "/Work/a.md"));
    }

    [Fact]
    public async Task Two_writers_racing_leave_a_record_of_the_stored_content()
    {
        var w = await WorldAsync();
        var created = await CreatedAsync(w, "/Work/a.md", "one\ntwo\nthree\n");
        var second = Later(w);
        var seenBySecond = (await second.GetAsync(P("/Work/a.md"), Ct))!;

        await WrittenAsync(w.Store, created, "ONE\ntwo\nthree\n", "Veyalt", VeyaltRef, T0.AddHours(1));
        // The second process's swap fails on the revision; its retry computes from the winner's state.
        Assert.False(await second.TryReplaceAsync(seenBySecond, NoteWriter.Next(seenBySecond, "one\ntwo\nTHREE\n", "Brita", BritaRef, T0.AddHours(2)), Ct));
        var winner = (await second.GetAsync(P("/Work/a.md"), Ct))!;
        await WrittenAsync(second, winner, "ONE\ntwo\nTHREE\n", "Brita", BritaRef, T0.AddHours(2));

        Assert.Equal(["Veyalt@1", "Brita@0", "Brita@2"], await LinesAsync(Later(w), "/Work/a.md"));

        // Both at once, many times: whoever wins, the record is of what is stored.
        var stores = new[] { w.Store, second };
        await Task.WhenAll(stores.Select((store, i) => Task.Run(async () =>
        {
            for (var round = 0; round < 10; round++)
            {
                while (true)
                {
                    var current = (await store.GetAsync(P("/Work/a.md"), Ct))!;
                    var lines = current.Content.Split('\n');
                    lines[i] = $"{(i == 0 ? "Veyalt" : "Brita")} {round}";
                    var next = NoteWriter.Next(current, string.Join('\n', lines), i == 0 ? "Veyalt" : "Brita", i == 0 ? VeyaltRef : BritaRef, T0.AddHours(3 + round));
                    if (await store.TryReplaceAsync(current, next, Ct)) break;
                }
            }
        }, Ct)));

        var final = (await Later(w).GetAsync(P("/Work/a.md"), Ct))!;
        Assert.Equal(["Veyalt 9", "Brita 9", "THREE", ""], final.Content.Split('\n'));
        Assert.Equal(["Veyalt@12", "Brita@12", "Brita@2"], Lines(final));
    }
}
