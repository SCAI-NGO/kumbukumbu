// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using Kumbu.Core.Board;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Vaults;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Sharing;

/// <summary>
/// HARD RULE 4: members' writes store no names (collab design §8.1). On a vault shared with her, every entry a
/// member writes, through every write path, as a session, a key or a device, has the label <c>""</c> and the reference
/// <c>{writer, kind}</c>, the writer id being her membership's for that session or credential, and nothing in it names
/// her: no username, user id, key id, key or device name. The owner's own writes keep their label. Never weakened for
/// another test.
/// </summary>
[Collection("mongo")]
public sealed class NoNamesTests(MongoFixture fixture)
{
    private const string Password = "correct horse battery staple";
    private const string KeyName = "Bobs-Agent-7x";
    private const string DeviceName = "Bobs-Laptop-9q";
    private const string DeviceKeyName = "garten – Obsidian " + DeviceName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1];

    private static readonly string[] Labels = ["LastWriter", "WrittenBy", "DeletedBy", "MovedBy", "CreatedBy"];

    private readonly IMongoDatabase _db = fixture.NewDatabase();
    private readonly Argon2Kdf _kdf = new(new Argon2Parameters(MemoryKiB: 1024, Iterations: 1, Parallelism: 1));
    private VaultCatalog? _catalog;

    private VaultCatalog Catalog => _catalog ??= new(_db, Options.Create(new KumbuOptions()), TimeProvider.System, NullLogger<VaultCatalog>.Instance);

    private ApiKeys Keys => new(_db, Catalog, TimeProvider.System, NullLogger<ApiKeys>.Instance);

    private UserCatalog Users =>
        new(_db, _kdf, Keys, new Connections(_db, Catalog, TimeProvider.System, NullLogger<Connections>.Instance), TimeProvider.System, NullLogger<UserCatalog>.Instance);

    private Memberships Memberships => new(_db, Catalog, Options.Create(new KumbuOptions()), TimeProvider.System, NullLogger<Memberships>.Instance);

    private sealed record Account(User User, byte[] Key, DateTime ReadAt);

    private async Task<Account> AccountAsync(string? vault)
    {
        var username = "u" + Guid.NewGuid().ToString("N")[..12];
        await Users.CreateAsync(username, Password, Roles.User, Ct);
        var (user, key, readAt) = (await Users.TrySignInAsync(username, Password, Ct))!.Value;
        if (vault is not null) await Catalog.CreateForUserAsync(vault, user.Id, key, Ct);
        return new(user, key, readAt);
    }

    /// <summary>Alice's empty "Gartenarbeit", shared with Bob as "garten" to maintain.</summary>
    private sealed record World(Account Alice, Account Bob, string VaultId, string MembershipId, byte[] VaultKey);

    private async Task<World> WorldAsync()
    {
        var alice = await AccountAsync("Gartenarbeit");
        var bob = await AccountAsync("Work");
        var vaultId = (await Catalog.FindForUserAsync(alice.User.Id, alice.Key, "Gartenarbeit", Ct))!;
        var membershipId = await Memberships.GrantAsync(alice.User.Id, alice.Key, vaultId, bob.User.Id, bob.Key, "garten", "Bob", MemberRole.Maintain, Ct);
        return new(alice, bob, vaultId, membershipId, await Catalog.OwnerVaultKeyAsync(alice.User.Id, alice.Key, vaultId, Ct));
    }

    /// <summary>Bob as the caller of a request: his session, his agent's key or his device's key; with the credential's id
    /// (null for the session), which is what the membership's <c>writerIds</c> maps his writer id to.</summary>
    private async Task<(RequestCaller Caller, string? CredentialId)> BobAsync(World w, string kind)
    {
        if (kind == "session") return (RequestCaller.ForSession(w.Bob.User, w.Bob.Key, w.Bob.ReadAt, windowOpen: true, Catalog), null);
        var device = kind == "device";
        var (secret, info) = await Keys.CreateAsync(
            w.Bob.User, w.Bob.Key, w.Bob.ReadAt, device ? DeviceKeyName : KeyName, new Dictionary<string, KeyRight> { [w.VaultId] = KeyRight.Maintain }, Ct,
            kind: device ? ApiKeyKind.Device : ApiKeyKind.Agent, writerName: device ? DeviceName : null);
        return (await RequestCaller.ForApiKeyAsync((await Keys.ResolveAsync(secret, Ct))!, secret, Catalog, Ct), info.Id);
    }

    private sealed record Tools(EncryptedNoteStore Store, NoteWriter Writer, NoteMover Mover, Shell Shell, BoardService Board);

    private async Task<Tools> ToolsAsync(RequestCaller caller)
    {
        // As every endpoint does, the vaults are opened before a write: the level, and the writer id, come from there.
        await caller.OpenVaultsAsync(Ct);
        var store = new EncryptedNoteStore(Catalog, caller.Credential);
        var writer = new NoteWriter(store, TimeProvider.System, caller);
        var mover = new NoteMover(store, TimeProvider.System, caller);
        return new(store, writer, mover, new Shell(store, writer, mover), new BoardService(store, writer, mover, TimeProvider.System, caller));
    }

    public static TheoryData<string, string> PathsAndKinds()
    {
        var data = new TheoryData<string, string>();
        string[] paths =
        [
            "write", "edit", "replace", "mv", "mv folder", "rm", "restore from trash", "restore version", "restore --checkpoint",
            "board add", "board move", "board rename_column", "mkcp", "upload", "upload replace", "rm attachment", "restore attachment", "mkdir",
        ];
        foreach (var kind in new[] { "session", "key", "device" })
        {
            foreach (var path in paths) data.Add(path, kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PathsAndKinds))]
    public async Task A_members_write_stores_no_label_and_no_identity(string path, string kind)
    {
        var w = await WorldAsync();
        var (caller, credentialId) = await BobAsync(w, kind);
        var t = await ToolsAsync(caller);
        var files = t.Writer.ForAttachments()!;
        Assert.False((await t.Writer.WriteAsync("/garten/a.md", "a foo\n", null, Ct)).IsError);
        Assert.False((await t.Writer.WriteAsync("/garten/sub/b.md", "b foo\n", null, Ct)).IsError);
        Assert.False((await t.Writer.WriteAsync("/garten/gone.md", "gone\n", null, Ct)).IsError);
        Assert.False((await files.WriteAsync("/garten/x.png", Png, (1, 1), null, Ct)).Result.IsError);
        Assert.False((await files.WriteAsync("/garten/y.png", (byte[])[.. Png, 3], (1, 1), null, Ct)).Result.IsError);
        Assert.False((await t.Board.CreateAsync("garten", Ct)).IsError);
        Assert.False((await t.Board.AddAsync("garten", "Card", "Ideas", null, null, null, null, Ct)).IsError);
        Assert.False((await t.Shell.RunAsync("mkcp /garten before", Ct)).IsError);
        Assert.False((await t.Shell.RunAsync("rm /garten/gone.md", Ct)).IsError);
        Assert.False((await t.Shell.RunAsync("rm /garten/y.png", Ct)).IsError);
        Assert.False((await t.Writer.WriteAsync("/garten/a.md", "a foo again\n", VersionToken.Of("a foo\n"), Ct)).IsError);
        var before = await DocumentsAsync(w);

        var result = path switch
        {
            "write" => await t.Writer.WriteAsync("/garten/new.md", "new\n", null, Ct),
            "edit" => await t.Writer.EditAsync("/garten/sub/b.md", VersionToken.Of("b foo\n"), null, "more\n", Ct),
            "replace" => await ReplaceAsync(t.Store, caller),
            "mv" => await t.Shell.RunAsync("mv /garten/a.md /garten/c.md", Ct),
            "mv folder" => await t.Shell.RunAsync("mv /garten/sub/ /garten/other/", Ct),
            "rm" => await t.Shell.RunAsync("rm /garten/a.md", Ct),
            "restore from trash" => await t.Shell.RunAsync("restore /garten/gone.md", Ct),
            "restore version" => await t.Shell.RunAsync("restore /garten/a.md " + VersionToken.Of("a foo\n"), Ct),
            "restore --checkpoint" => await t.Shell.RunAsync("restore --checkpoint before /garten/a.md", Ct),
            "board add" => await t.Board.AddAsync("garten", "Second", "Backlog", null, null, null, null, Ct),
            "board move" => await t.Board.MoveAsync("garten", "Card", "Today", null, null, Ct),
            "board rename_column" => await t.Board.RenameColumnAsync("garten", "Ideas", "Thoughts", Ct),
            "mkcp" => await t.Shell.RunAsync("mkcp /garten after", Ct),
            "upload" => (await files.WriteAsync("/garten/z.png", (byte[])[.. Png, 4], (1, 1), null, Ct)).Result,
            "upload replace" => (await files.WriteAsync("/garten/x.png", (byte[])[.. Png, 5], (1, 1), VersionToken.Of(Png), Ct)).Result,
            "rm attachment" => await t.Shell.RunAsync("rm /garten/x.png", Ct),
            "restore attachment" => await t.Shell.RunAsync("restore /garten/y.png", Ct),
            _ => await t.Shell.RunAsync("mkdir /garten/empty", Ct),
        };

        Assert.False(result.IsError, result.Output);
        var after = await DocumentsAsync(w);
        Assert.NotEmpty(after.Keys.Except(before.Keys));

        var writerId = Memberships.WriterIdOf(await Share.MembershipAsync(_db, w.MembershipId), w.VaultKey, credentialId ?? Memberships.SessionWriter);
        Assert.NotNull(writerId);
        var expectedKind = kind switch { "session" => WriterRef.Session, "device" => WriterRef.Device, _ => WriterRef.Agent };
        string[] forbidden = [w.Bob.User.Username, w.Bob.User.Id, KeyName, DeviceName, "Bobs", .. credentialId is null ? Array.Empty<string>() : [credentialId]];
        // What the caller hands to the store, and what the vault in memory now holds, is that reference too: no
        // credential id that a later rendering, or a sealing that kept it, could pass on.
        Assert.Equal(WriterRef.ForMember(writerId!, expectedKind), caller.ReferenceIn("garten"));
        Assert.Equal("", caller.WriterIn("garten"));
        foreach (var notePath in await t.Store.ListNotesUnderAsync(VaultPath.Root.Child("garten"), Ct))
        {
            var note = (await t.Store.GetAsync(notePath, Ct))!;
            Assert.Equal(WriterRef.ForMember(writerId!, expectedKind), note.Ref);
            Assert.All(note.History, h => Assert.Equal(WriterRef.ForMember(writerId!, expectedKind), h.Ref));
        }

        foreach (var (where, payload) in after)
        {
            var json = payload.ToJsonString();
            foreach (var name in forbidden) Assert.False(json.Contains(name, StringComparison.OrdinalIgnoreCase), $"{where} names Bob ({name}): {json}");
            AssertNoName(where, payload, writerId, expectedKind);
        }
    }

    [Fact]
    public async Task The_owners_write_still_stores_her_label()
    {
        var w = await WorldAsync();
        var t = await ToolsAsync(RequestCaller.ForSession(w.Alice.User, w.Alice.Key, w.Alice.ReadAt, windowOpen: true, Catalog));

        Assert.False((await t.Writer.WriteAsync("/Gartenarbeit/a.md", "a\n", null, Ct)).IsError);
        Assert.False((await t.Writer.WriteAsync("/Gartenarbeit/c.md", "see [[a]]\n", null, Ct)).IsError);
        Assert.False((await t.Shell.RunAsync("mv /Gartenarbeit/a.md /Gartenarbeit/b.md", Ct)).IsError);

        var notes = await SealedPayloads.OpenAllAsync(_db, w.VaultId, w.VaultKey, "notes", Ct);
        var note = notes.Single(n => (string)n["Content"]! == "a\n");
        Assert.Equal(w.Alice.User.Username, (string)note["LastWriter"]!);
        // The note whose link the move rewrote: her label, marked with the tool.
        Assert.Equal($"{w.Alice.User.Username} (mv)", (string)notes.Single(n => n != note)["LastWriter"]!);
        var reference = note["ref"]!.AsObject();
        Assert.True((bool)reference["owner"]!);
        Assert.Equal(WriterRef.Session, (string)reference["kind"]!);
        Assert.False(reference.ContainsKey("writer"));
        Assert.Equal(w.Alice.User.Username, (string)Assert.Single(await SealedPayloads.OpenAllAsync(_db, w.VaultId, w.VaultKey, "moves", Ct))["MovedBy"]!);
    }

    [Fact]
    public async Task Two_members_writer_ids_differ_and_change_with_a_new_membership()
    {
        var w = await WorldAsync();
        var carol = await AccountAsync(null);
        await Memberships.GrantAsync(w.Alice.User.Id, w.Alice.Key, w.VaultId, carol.User.Id, carol.Key, "Beete", "Carol", MemberRole.Write, Ct);

        var bobs = await WriterOfAsync(w, RequestCaller.ForSession(w.Bob.User, w.Bob.Key, w.Bob.ReadAt, windowOpen: true, Catalog), "/garten/b.md");
        var carols = await WriterOfAsync(w, RequestCaller.ForSession(carol.User, carol.Key, carol.ReadAt, windowOpen: true, Catalog), "/Beete/c.md");
        Assert.NotEqual(bobs, carols);
        // One writer id for all his sessions on the vault: a second one writes under the same.
        var (_, again, readAgain) = (await Users.TrySignInAsync(w.Bob.User.Username, Password, Ct))!.Value;
        Assert.Equal(bobs, await WriterOfAsync(w, RequestCaller.ForSession(w.Bob.User, again, readAgain, windowOpen: true, Catalog), "/garten/d.md"));
        Assert.NotEqual(w.Bob.User.Id, bobs);

        // His membership ends and goes (as his account's deletion takes it); a new one gets a new writer id.
        await Share.SetStatusAsync(_db, w.MembershipId, MembershipStatus.Left);
        await _db.GetCollection<MembershipDocument>(Memberships.CollectionName).DeleteOneAsync(m => m.Id == w.MembershipId, Ct);
        await _db.GetCollection<SlotDocument>("slots").DeleteOneAsync(s => s.Membership == w.MembershipId, Ct);
        await Memberships.GrantAsync(w.Alice.User.Id, w.Alice.Key, w.VaultId, w.Bob.User.Id, w.Bob.Key, "garten", "Bob", MemberRole.Write, Ct);
        var later = await WriterOfAsync(w, RequestCaller.ForSession(w.Bob.User, w.Bob.Key, w.Bob.ReadAt, windowOpen: true, Catalog), "/garten/e.md");
        Assert.NotEqual(bobs, later);
        Assert.NotEqual(carols, later);
    }

    /// <summary>The writer id the note <paramref name="path"/>, written now by <paramref name="caller"/>, stores.</summary>
    private async Task<string> WriterOfAsync(World w, RequestCaller caller, string path)
    {
        var t = await ToolsAsync(caller);
        Assert.False((await t.Writer.WriteAsync(path, "x\n", null, Ct)).IsError);
        var notes = await SealedPayloads.OpenAllAsync(_db, w.VaultId, w.VaultKey, "notes", Ct);
        var written = Assert.Single(notes, n => (string)n["Content"]! == "x\n" && (string?)n["ref"]?["writer"] is not null && !Seen.Contains(n.ToJsonString()));
        Seen.Add(written.ToJsonString());
        Assert.Equal("", (string)written["LastWriter"]!);
        return (string)written["ref"]!["writer"]!;
    }

    private HashSet<string> Seen { get; } = [];

    private static async Task<ShellResult> ReplaceAsync(EncryptedNoteStore store, ICaller caller)
    {
        var replacer = new Replacer(store, TimeProvider.System, new Plans(), caller);
        var preview = await replacer.RunAsync("/garten", "foo", "bar", false, null, Ct);
        var plan = preview.Output.Split('\n').Single(l => l.StartsWith("plan: ", StringComparison.Ordinal))["plan: ".Length..];
        return await replacer.RunAsync("/garten", "foo", "bar", false, plan, Ct);
    }

    /// <summary>Every payload that carries a writer in the shared vault, opened with its key.</summary>
    private async Task<Dictionary<string, JsonObject>> DocumentsAsync(World w)
    {
        var all = new Dictionary<string, JsonObject>();
        foreach (var collection in SealedPayloads.WithWriter)
        {
            foreach (var doc in await _db.GetCollection<SealedDocument>(collection).Find(d => d.Vault == w.VaultId).ToListAsync(Ct))
            {
                all[$"{collection}/{doc.Id}/{doc.Revision}"] = SealedPayloads.Open(w.VaultId, w.VaultKey, collection, doc);
            }
        }

        return all;
    }

    /// <summary>Every entry in <paramref name="node"/> that has a label field: each label is <c>""</c>, and its reference
    /// is <c>{writer, kind}</c> with Bob's writer id and kind, and nothing else.</summary>
    private static void AssertNoName(string where, JsonNode? node, string writerId, string kind)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array) AssertNoName(where, item, writerId, kind);
            return;
        }

        if (node is not JsonObject obj) return;
        if (Labels.Any(obj.ContainsKey))
        {
            foreach (var label in Labels.Where(obj.ContainsKey))
            {
                if (obj[label] is JsonValue value) Assert.True((string?)value == "", $"{where}: {label} is '{value}'");
            }

            var reference = Assert.IsType<JsonObject>(obj["ref"]);
            Assert.Equal(["kind", "writer"], reference.Select(p => p.Key).Order(StringComparer.Ordinal));
            Assert.Equal(writerId, (string)reference["writer"]!);
            Assert.Equal(kind, (string)reference["kind"]!);
        }

        foreach (var (_, child) in obj) AssertNoName(where, child, writerId, kind);
    }
}
