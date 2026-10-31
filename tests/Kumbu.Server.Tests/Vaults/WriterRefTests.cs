// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Server.Vaults;
using MongoDB.Driver;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>Writer references and the payload version <c>v</c> as sealed (collab design §8.1).</summary>
[Collection("mongo")]
public sealed class WriterRefTests(MongoFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly WriterRef Key = WriterRef.ForOwner(WriterRef.Agent, "key-1");

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1];

    private sealed record World(VaultWorld Vaults, string VaultId, byte[] VaultKey, EncryptedNoteStore Store, Shell Shell, NoteWriter Writer);

    private async Task<World> WorldAsync(ICaller? caller = null)
    {
        var world = new VaultWorld(fixture.NewDatabase());
        var catalog = world.Catalog();
        var vault = await catalog.CreateAsync("Work", VaultWorld.Token, null, Ct);
        var store = Store(catalog);
        caller ??= new FixedCaller("Claude") { Reference = Key };
        var writer = new NoteWriter(store, TimeProvider.System, caller);
        var shell = new Shell(store, writer, new NoteMover(store, TimeProvider.System, caller));
        return new(world, vault.Id, await SealedPayloads.KeyOfTokenVaultAsync(world.Db, vault.Id, VaultWorld.Token, Ct), store, shell, writer);
    }

    private static async Task<JsonObject> SingleAsync(World w, string collection) =>
        Assert.Single(await SealedPayloads.OpenAllAsync(w.Vaults.Db, w.VaultId, w.VaultKey, collection, Ct));

    /// <summary>The note's document with its payload replaced by what <paramref name="change"/> makes of it.</summary>
    private static async Task RewriteNoteAsync(World w, Action<JsonObject> change)
    {
        var notes = w.Vaults.Db.GetCollection<SealedDocument>("notes");
        var doc = await notes.Find(d => d.Vault == w.VaultId).SingleAsync(Ct);
        var payload = SealedPayloads.Open(w.VaultId, w.VaultKey, "notes", doc);
        change(payload);
        await notes.UpdateOneAsync(d => d.Id == doc.Id, Builders<SealedDocument>.Update.Set(d => d.Blob, SealedPayloads.Seal(w.VaultId, w.VaultKey, "notes", doc, payload)), cancellationToken: Ct);
    }

    [Fact]
    public async Task A_written_note_payload_carries_v_2_and_the_reference()
    {
        var w = await WorldAsync();

        Assert.False((await w.Writer.WriteAsync("/Work/a.md", "a\n", null, Ct)).IsError);

        var payload = await SingleAsync(w, "notes");
        Assert.Equal(2, (int)payload["v"]!);
        var reference = payload["ref"]!.AsObject();
        Assert.True((bool)reference["owner"]!);
        Assert.Equal("agent", (string)reference["kind"]!);
        Assert.Equal("key-1", (string)reference["id"]!);
        Assert.False(reference.ContainsKey("writer"));
    }

    [Fact]
    public async Task A_payload_without_v_reads_as_the_owners()
    {
        var w = await WorldAsync();
        await w.Writer.WriteAsync("/Work/a.md", "a\n", null, Ct);
        await RewriteNoteAsync(w, p => { p.Remove("v"); p.Remove("ref"); });

        var note = (await Store(w.Vaults.Catalog()).GetAsync(P("/Work/a.md"), Ct))!;
        Assert.Same(WriterRef.Legacy, note.Ref);
        Assert.True(note.Ref!.Owner);

        // A later write keeps the legacy state's entry without v, so it still reads as the owner's.
        var later = Store(w.Vaults.Catalog());
        var writer = new NoteWriter(later, TimeProvider.System, new FixedCaller("Claude") { Reference = Key });
        Assert.False((await writer.WriteAsync("/Work/a.md", "b\n", note.Version, Ct)).IsError);
        var history = (await SingleAsync(w, "notes"))["History"]!.AsArray();
        Assert.False(history[0]!.AsObject().ContainsKey("v"));
        Assert.True((await Store(w.Vaults.Catalog()).GetAsync(P("/Work/a.md"), Ct))!.History[0].Ref!.IsLegacy);
    }

    [Fact]
    public async Task A_v2_payload_without_a_reference_reads_as_unknown()
    {
        var w = await WorldAsync();
        await w.Writer.WriteAsync("/Work/a.md", "a\n", null, Ct);
        await RewriteNoteAsync(w, p => p.Remove("ref"));

        var note = (await Store(w.Vaults.Catalog()).GetAsync(P("/Work/a.md"), Ct))!;
        Assert.True(note.Ref!.IsUnknown);
        Assert.False(note.Ref.Owner);
    }

    public static TheoryData<string> Entries => ["history", "trash", "move", "attachment", "attachment history", "attachment trash", "checkpoint"];

    [Theory]
    [MemberData(nameof(Entries))]
    public async Task Every_entry_with_a_writer_reads_back_its_reference(string entry)
    {
        var w = await WorldAsync();
        Assert.False((await w.Writer.WriteAsync("/Work/a.md", "a\n", null, Ct)).IsError);
        var files = w.Writer.ForAttachments()!;
        Assert.False((await files.WriteAsync("/Work/x.png", Png, (1, 1), null, Ct)).Result.IsError);
        var command = entry switch
        {
            "trash" => "rm /Work/a.md",
            "move" => "mv /Work/a.md /Work/b.md",
            "attachment trash" => "rm /Work/x.png",
            "checkpoint" => "mkcp /Work before",
            _ => null,
        };
        if (entry == "history") await w.Writer.WriteAsync("/Work/a.md", "b\n", VersionToken.Of("a\n"), Ct);
        if (entry == "attachment history") await files.WriteAsync("/Work/x.png", (byte[])[.. Png, 2], (1, 1), VersionToken.Of(Png), Ct);
        if (command is not null) Assert.False((await w.Shell.RunAsync(command, Ct)).IsError);

        // Read back by a second process, from what MongoDB holds.
        var store = Store(w.Vaults.Catalog());
        var reference = entry switch
        {
            "history" => (await store.GetAsync(P("/Work/a.md"), Ct))!.History[0].Ref,
            "trash" => (await store.ListTrashAsync(P("/Work"), Ct)).Single().Ref,
            "move" => (await store.FindMoveAsync(P("/Work/a.md"), Ct))!.Ref,
            "attachment" => (await store.GetAttachmentAsync(P("/Work/x.png"), Ct))!.Ref,
            "attachment history" => (await store.GetAttachmentAsync(P("/Work/x.png"), Ct))!.History[0].Ref,
            "attachment trash" => (await store.ListAttachmentTrashAsync(null, Ct)).Single().Ref,
            _ => (await store.ListCheckpointsAsync(VaultPath.Root, Ct)).Single().Ref,
        };
        Assert.Equal(Key, reference);
    }
}
