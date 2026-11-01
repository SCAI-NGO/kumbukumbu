// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using Kumbu.Core.Board;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Server.Notes;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
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

    public static TheoryData<string> WritePaths =>
    [
        "write", "edit", "replace", "mv", "mv folder", "rm", "restore from trash", "restore version", "restore --checkpoint",
        "board add", "board move", "board rename_column", "mkcp", "upload", "upload replace", "rm attachment", "restore attachment",
        "mkdir", "import",
    ];

    /// <summary>
    /// Every write path stores a reference (collab design §8.1, fail closed): after the write, every sealed payload
    /// that carries a writer, in every vault, has <c>v: 2</c> and a reference, and so has every entry inside it.
    /// </summary>
    [Theory]
    [MemberData(nameof(WritePaths))]
    public async Task Every_write_path_stores_a_reference(string path)
    {
        var w = await WorldAsync();
        var caller = new FixedCaller("Claude") { Reference = Key };
        var files = w.Writer.ForAttachments()!;
        var mover = new NoteMover(w.Store, TimeProvider.System, caller);
        var board = new BoardService(w.Store, w.Writer, mover, TimeProvider.System, caller);
        Assert.False((await w.Writer.WriteAsync("/Work/a.md", "a foo\n", null, Ct)).IsError);
        Assert.False((await w.Writer.WriteAsync("/Work/sub/b.md", "b foo\n", null, Ct)).IsError);
        Assert.False((await w.Writer.WriteAsync("/Work/gone.md", "gone\n", null, Ct)).IsError);
        Assert.False((await files.WriteAsync("/Work/x.png", Png, (1, 1), null, Ct)).Result.IsError);
        Assert.False((await files.WriteAsync("/Work/y.png", (byte[])[.. Png, 3], (1, 1), null, Ct)).Result.IsError);
        Assert.False((await board.CreateAsync("Work", Ct)).IsError);
        Assert.False((await board.AddAsync("Work", "Card", "Ideas", null, null, null, null, Ct)).IsError);
        Assert.False((await w.Shell.RunAsync("mkcp /Work before", Ct)).IsError);
        Assert.False((await w.Shell.RunAsync("rm /Work/gone.md", Ct)).IsError);
        Assert.False((await w.Shell.RunAsync("rm /Work/y.png", Ct)).IsError);
        Assert.False((await w.Writer.WriteAsync("/Work/a.md", "a foo again\n", VersionToken.Of("a foo\n"), Ct)).IsError);
        var before = await DocumentsAsync(w);

        var result = path switch
        {
            "write" => await w.Writer.WriteAsync("/Work/new.md", "new\n", null, Ct),
            "edit" => await w.Writer.EditAsync("/Work/sub/b.md", VersionToken.Of("b foo\n"), null, "more\n", Ct),
            "replace" => await ReplaceAsync(w.Store, caller),
            "mv" => await w.Shell.RunAsync("mv /Work/a.md /Work/c.md", Ct),
            "mv folder" => await w.Shell.RunAsync("mv /Work/sub/ /Work/other/", Ct),
            "rm" => await w.Shell.RunAsync("rm /Work/a.md", Ct),
            "restore from trash" => await w.Shell.RunAsync("restore /Work/gone.md", Ct),
            "restore version" => await w.Shell.RunAsync("restore /Work/a.md " + VersionToken.Of("a foo\n"), Ct),
            "restore --checkpoint" => await w.Shell.RunAsync("restore --checkpoint before /Work/a.md", Ct),
            "board add" => await board.AddAsync("Work", "Second", "Backlog", null, null, null, null, Ct),
            "board move" => await board.MoveAsync("Work", "Card", "Today", null, null, Ct),
            "board rename_column" => await board.RenameColumnAsync("Work", "Ideas", "Thoughts", Ct),
            "mkcp" => await w.Shell.RunAsync("mkcp /Work after", Ct),
            "upload" => (await files.WriteAsync("/Work/z.png", (byte[])[.. Png, 4], (1, 1), null, Ct)).Result,
            "upload replace" => (await files.WriteAsync("/Work/x.png", (byte[])[.. Png, 5], (1, 1), VersionToken.Of(Png), Ct)).Result,
            "rm attachment" => await w.Shell.RunAsync("rm /Work/x.png", Ct),
            "restore attachment" => await w.Shell.RunAsync("restore /Work/y.png", Ct),
            "mkdir" => await w.Shell.RunAsync("mkdir /Work/empty", Ct),
            _ => await ImportAsync(w),
        };

        Assert.False(result.IsError, result.Output);
        var after = await DocumentsAsync(w);
        Assert.NotEmpty(after.Keys.Except(before.Keys));
        foreach (var (where, payload) in after) AssertReferenced(where, payload);
    }

    [Fact]
    public async Task A_caller_without_a_reference_is_refused()
    {
        var w = await WorldAsync(new FixedCaller("Claude") { Reference = null! });

        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Writer.WriteAsync("/Work/a.md", "a\n", null, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => w.Writer.ForAttachments()!.WriteAsync("/Work/x.png", Png, (1, 1), null, Ct));

        Assert.Empty(await DocumentsAsync(w));
        Assert.Empty(await Store(w.Vaults.Catalog()).ListNotesUnderAsync(VaultPath.Root, Ct));
    }

    private static async Task<ShellResult> ReplaceAsync(EncryptedNoteStore store, ICaller caller)
    {
        var replacer = new Replacer(store, TimeProvider.System, new Plans(), caller);
        var preview = await replacer.RunAsync("/Work", "foo", "bar", false, null, Ct);
        var plan = preview.Output.Split('\n').Single(l => l.StartsWith("plan: ", StringComparison.Ordinal))["plan: ".Length..];
        return await replacer.RunAsync("/Work", "foo", "bar", false, plan, Ct);
    }

    /// <summary>A seed folder with a note and a kept empty folder, imported into a vault the seeder creates.</summary>
    private static async Task<ShellResult> ImportAsync(World w)
    {
        var root = Directory.CreateTempSubdirectory("kumbu-seed-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Demo", "kept"));
            await File.WriteAllTextAsync(Path.Combine(root, "Demo", "hello.md"), "hello\n", Ct);
            var options = Options.Create(new KumbuOptions { DevToken = VaultWorld.Token, Seed = new SeedOptions { Path = root } });
            await new Seeder(options, w.Vaults.Catalog(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<Seeder>.Instance).StartAsync(Ct);
            return ShellResult.Ok("imported");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Every payload that carries a writer, in every vault of the world, by collection, document and revision.</summary>
    private static async Task<Dictionary<string, JsonObject>> DocumentsAsync(World w)
    {
        var all = new Dictionary<string, JsonObject>();
        foreach (var vault in await w.Vaults.Db.GetCollection<BsonDocument>("vaults").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(Ct))
        {
            var id = vault["_id"].AsString;
            var key = await SealedPayloads.KeyOfTokenVaultAsync(w.Vaults.Db, id, VaultWorld.Token, Ct);
            foreach (var collection in SealedPayloads.WithWriter)
            {
                foreach (var doc in await w.Vaults.Db.GetCollection<SealedDocument>(collection).Find(d => d.Vault == id).ToListAsync(Ct))
                {
                    all[$"{collection}/{doc.Id}/{doc.Revision}"] = SealedPayloads.Open(id, key, collection, doc);
                }
            }
        }

        return all;
    }

    private static readonly string[] Writers = ["LastWriter", "WrittenBy", "DeletedBy", "MovedBy", "CreatedBy"];

    /// <summary>The payload, and every entry inside it that names a writer, has <c>v: 2</c> and a reference.</summary>
    private static void AssertReferenced(string where, JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array) AssertReferenced(where, item);
            return;
        }

        if (node is not JsonObject obj) return;
        if (Writers.Any(obj.ContainsKey))
        {
            Assert.True(obj["v"] is JsonValue v && (int)v == 2, $"{where}: no v in {obj.ToJsonString()[..Math.Min(200, obj.ToJsonString().Length)]}");
            Assert.True(obj["ref"] is JsonObject, $"{where}: no ref");
        }

        foreach (var (_, child) in obj) AssertReferenced(where, child);
    }

    public static TheoryData<string> Markers => ["legacy", "unknown"];

    private static WriterRef Marker(string name) => name == "legacy" ? WriterRef.Legacy : WriterRef.Unknown;

    /// <summary>The markers are only ever read: a write that brings one as its own reference is refused, as one without
    /// any, since a legacy write would read back as the owner's.</summary>
    [Theory]
    [MemberData(nameof(Markers))]
    public async Task A_write_with_a_marker_that_is_only_read_is_refused(string marker)
    {
        var w = await WorldAsync();
        Assert.False((await w.Writer.WriteAsync("/Work/a.md", "a\n", null, Ct)).IsError);
        var before = await DocumentsAsync(w);
        var caller = new FixedCaller("Claude") { Reference = Marker(marker) };
        var writer = new NoteWriter(w.Store, TimeProvider.System, caller);
        var shell = new Shell(w.Store, writer, new NoteMover(w.Store, TimeProvider.System, caller));

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync("/Work/b.md", "b\n", null, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync("/Work/a.md", "a2\n", VersionToken.Of("a\n"), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => shell.RunAsync("rm /Work/a.md", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => shell.RunAsync("mv /Work/a.md /Work/c.md", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => shell.RunAsync("mkcp /Work cp", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ForAttachments()!.WriteAsync("/Work/x.png", Png, (1, 1), null, Ct));

        Assert.Equal(before.Keys.Order(), (await DocumentsAsync(w)).Keys.Order());
    }

    [Fact]
    public async Task A_moved_legacy_note_keeps_its_reference_and_the_move_gets_the_movers()
    {
        var w = await WorldAsync();
        await w.Writer.WriteAsync("/Work/a.md", "a\n", null, Ct);
        await RewriteNoteAsync(w, p => { p.Remove("v"); p.Remove("ref"); });
        var store = Store(w.Vaults.Catalog());
        var caller = new FixedCaller("Claude") { Reference = Key };

        Assert.False((await new Shell(store, new NoteWriter(store, TimeProvider.System, caller), new NoteMover(store, TimeProvider.System, caller))
            .RunAsync("mv /Work/a.md /Work/b.md", Ct)).IsError);

        var later = Store(w.Vaults.Catalog());
        Assert.True((await later.GetAsync(P("/Work/b.md"), Ct))!.Ref!.IsLegacy);
        Assert.Equal(Key, (await later.FindMoveAsync(P("/Work/a.md"), Ct))!.Ref);
    }
}
