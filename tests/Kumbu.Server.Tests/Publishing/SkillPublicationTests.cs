// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;
using Kumbu.Core.Sh;
using Kumbu.Server.Accounts;
using Kumbu.Server.Publishing;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Attachments;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>A skill shared by link (skills design §3.12): a publication of its own kind holding every file of the folder
/// as it is, behind <c>/k#code</c>; only the owner, never while a mark covers any file, one link at a time, and kept out
/// of the note publications' listings and reader.</summary>
[Collection("app")]
public sealed class SkillPublicationTests(KumbuAppFactory app)
{
    private const string Manifest = "---\nname: write-as-alice\ndescription: Writes posts.\n---\n<!-- keep -->\nSee [style](references/style.md).\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Exif = ImageMetadataTests.JpegWithGps(6);

    private static readonly WriterRef BySession = WriterRef.ForOwner(WriterRef.Session, null);

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private Publications Publications => app.PublicApi.Services.GetRequiredService<Publications>();

    private PublicationStore Store => app.PublicApi.Services.GetRequiredService<PublicationStore>();

    private static async Task PutNoteAsync(RestClient user, string path, string content)
    {
        using var put = await user.SendAsync(HttpMethod.Put, "/api/notes" + path, new { content });
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
    }

    private static async Task PutFileAsync(RestClient user, string path, byte[] bytes)
    {
        using var put = await user.SendContentAsync(HttpMethod.Put, "/api/files" + path, new ByteArrayContent(bytes) { Headers = { ContentType = new("application/octet-stream") } });
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
    }

    private static EncryptedNoteStore StoreOf(IServiceProvider services, ICredential credential) =>
        new(services.GetRequiredService<VaultCatalog>(), credential, null, null, null, services.GetRequiredService<Publications>(), services.GetRequiredService<StorageQuota>());

    private static async Task<(VaultAccess Access, EncryptedNoteStore Store)> OpenAsync(RestClient user, string vault, IServiceProvider? services = null)
    {
        var store = StoreOf(services ?? user.Api.Services, new UserCredential(user.User.Id, user.UserKey));
        return ((await store.UnlockedVaultsAsync(Ct)).Single(a => a.Name == vault), store);
    }

    private async Task<(RestClient Alice, VaultAccess Access, EncryptedNoteStore Store)> SkillAsync()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await PutNoteAsync(alice, "/Skills/write-as-alice/SKILL.md", Manifest);
        await PutNoteAsync(alice, "/Skills/write-as-alice/examples/post.md", "# Post\n");
        await PutFileAsync(alice, "/Skills/write-as-alice/scripts/run.py", "print(1)\n"u8.ToArray());
        await PutFileAsync(alice, "/Skills/write-as-alice/photo.jpg", Exif);
        var (access, store) = await OpenAsync(alice, "Skills");
        return (alice, access, store);
    }

    private Task<PublishedSkill> ShareAsync(VaultAccess access, EncryptedNoteStore store, RestClient alice, Publications? publications = null) =>
        (publications ?? Publications).PublishSkillAsync(access, store, P("/Skills/write-as-alice"), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct);

    private static string CodeOf(PublishedSkill shared) => ShareCode.Parse(shared.Link[(shared.Link.IndexOf('#') + 1)..])!;

    private Task<long> CountAsync(string collection, string vaultId) =>
        app.Database.GetCollection<BsonDocument>(collection).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("vault", vaultId), cancellationToken: Ct);

    [Fact]
    public async Task A_skill_publication_holds_every_file_verbatim()
    {
        var (alice, access, store) = await SkillAsync();
        var shared = await ShareAsync(access, store, alice);
        var opened = (await Store.OpenSkillAsync(CodeOf(shared), Ct))!;
        Assert.Equal(["SKILL.md", "examples/post.md", "photo.jpg", "scripts/run.py"], opened.Copy.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
        var manifest = opened.Copy.Files.Single(f => f.Path == "SKILL.md");
        Assert.Equal(Manifest, Encoding.UTF8.GetString((await Store.OpenSkillFileAsync(opened, manifest.Index, Ct))!.Value.Bytes));
        Assert.Equal(Exif, (await Store.OpenSkillFileAsync(opened, opened.Copy.Files.Single(f => f.Path == "photo.jpg").Index, Ct))!.Value.Bytes);
        Assert.Equal("print(1)\n"u8.ToArray(), (await Store.OpenSkillFileAsync(opened, opened.Copy.Files.Single(f => f.Path == "scripts/run.py").Index, Ct))!.Value.Bytes);

        Assert.Equal("write-as-alice", opened.Copy.Name);
        Assert.Equal("Writes posts.", opened.Copy.Description);
        Assert.Equal(Manifest, opened.Copy.Manifest);
        Assert.Equal(shared.SnapshotAt, opened.Copy.SnapshotAt);
        Assert.Equal(alice.User.Id, opened.Owner);
        Assert.Equal(Exif.Length, opened.Copy.Files.Single(f => f.Path == "photo.jpg").Size);
        Assert.StartsWith("sha256:", opened.Copy.Hash, StringComparison.Ordinal);

        var record = (await Store.ReadAsync(null, access.Vault, Ct)).Record;
        var entry = Assert.Single(record.Publications);
        Assert.Equal("skill", entry.Kind);
        Assert.Equal("/Skills/write-as-alice", entry.Note);
        var state = Assert.Single(await Publications.SkillStatesAsync(access, store, Ct));
        Assert.Equal(new SkillPublicationState(entry.Id, P("/Skills/write-as-alice"), shared.SnapshotAt, false), state);
    }

    [Fact]
    public async Task The_link_is_k_with_the_code_in_the_fragment()
    {
        var (alice, access, store) = await SkillAsync();
        Assert.Matches(@"^https://kumbu\.test/k#[0-9A-Z]{4}(-[0-9A-Z]{4}){7}$", (await ShareAsync(access, store, alice)).Link);
    }

    [Fact]
    public async Task The_reader_endpoint_doesnt_open_a_skill()
    {
        var (alice, access, store) = await SkillAsync();
        var code = CodeOf(await ShareAsync(access, store, alice));
        Assert.Null(await Store.OpenPublicationAsync(code, Ct));
        Assert.Null(await Store.OpenCopyAsync(code, Ct));
        using var reader = new ReaderApiTests.Reader(app.PublicApi, "203.0.113.160");
        using var read = await reader.PostAsync(code);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact]
    public async Task A_second_link_isnt_made_while_one_exists()
    {
        var (alice, access, store) = await SkillAsync();
        await ShareAsync(access, store, alice);
        var refused = await Assert.ThrowsAsync<PublishRefusedException>(() => ShareAsync(access, store, alice));
        Assert.Equal(ShellErrorKind.Conflict, refused.Kind);
        Assert.Equal("/Skills/write-as-alice is already shared; withdraw its link first", refused.Message);
        Assert.Equal(1, await CountAsync("publications", access.Id));
    }

    [Fact]
    public async Task A_skill_with_an_unpublishable_file_cant_be_shared()
    {
        var (alice, access, store) = await SkillAsync();
        await Publications.SetUnpublishableAsync(access, store, P("/Skills/write-as-alice/examples"), true, alice.Username, BySession, Ct);
        var refused = await Assert.ThrowsAsync<PublishRefusedException>(() => ShareAsync(access, store, alice));
        Assert.Equal(Refusals.Unpublishable, refused.Code);
        Assert.Equal(0, await CountAsync("publications", access.Id));
    }

    [Fact]
    public async Task A_skill_past_the_limits_isnt_shared()
    {
        var (alice, access, store) = await SkillAsync();
        var services = app.PublicApi.Services;
        Publications Limited(int files, long bytes) => new(
            services.GetRequiredService<VaultCatalog>(), Store, services.GetRequiredService<IMongoDatabase>(), services.GetRequiredService<IOptions<KumbuOptions>>(),
            services.GetRequiredService<TimeProvider>()) { SkillFileLimit = files, SkillByteLimit = bytes };

        var many = await Assert.ThrowsAsync<PublishRefusedException>(() => ShareAsync(access, store, alice, Limited(3, 10L * 1024 * 1024)));
        Assert.Equal("/Skills/write-as-alice has more than 3 files", many.Message);
        var large = await Assert.ThrowsAsync<PublishRefusedException>(() => ShareAsync(access, store, alice, Limited(200, 100)));
        Assert.StartsWith("/Skills/write-as-alice is larger than ", large.Message, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync("publications", access.Id));
    }

    [Fact]
    public async Task Update_replaces_the_files_under_the_same_link()
    {
        var (alice, access, store) = await SkillAsync();
        var shared = await ShareAsync(access, store, alice);
        var code = CodeOf(shared);
        await PutNoteAsync(alice, "/Skills/write-as-alice/examples/second.md", "# Second\n");
        Assert.True(Assert.Single(await Publications.SkillStatesAsync(access, store, Ct)).Changed);

        var at = await Publications.UpdateSkillAsync(access, store, P("/Skills/write-as-alice"), alice.Username, BySession, Ct);

        Assert.True(at >= shared.SnapshotAt);
        var opened = (await Store.OpenSkillAsync(code, Ct))!;
        Assert.Equal(at, opened.Copy.SnapshotAt);
        Assert.Contains(opened.Copy.Files, f => f.Path == "examples/second.md");
        Assert.Equal("# Second\n", Encoding.UTF8.GetString((await Store.OpenSkillFileAsync(opened, opened.Copy.Files.Single(f => f.Path == "examples/second.md").Index, Ct))!.Value.Bytes));
        Assert.Equal(5, opened.Copy.Files.Count);
        var state = Assert.Single(await Publications.SkillStatesAsync(access, store, Ct));
        Assert.False(state.Changed);
        Assert.Equal(at, state.SnapshotAt);
        Assert.Equal(1, await CountAsync("publications", access.Id));
        Assert.Equal(5, await CountAsync("publication_files", access.Id));
    }

    [Fact]
    public async Task A_member_cant_share()
    {
        var alice = await RestClient.NewAsync(app.PublicApi, vault: "Work");
        await PutNoteAsync(alice, "/Work/_skills/x/SKILL.md", "---\nname: x\ndescription: y\n---\n");
        var bob = await RestClient.NewAsync(app.PublicApi, vault: "Garden");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var vaultId = await alice.VaultIdAsync();
        var (access, store) = await OpenAsync(bob, "alices-work");

        var refused = await Assert.ThrowsAsync<PublishRefusedException>(() =>
            Publications.PublishSkillAsync(access, store, P("/alices-work/_skills/x"), bob.User.Id, bob.Username, BySession, KumbuAppFactory.PublicUrl, Ct));

        Assert.Equal(Refusals.NotOwner, refused.Code);
        Assert.Empty(await Publications.SkillStatesAsync(access, store, Ct));
        Assert.Equal(0, await CountAsync("publications", vaultId));
        Assert.Equal(0, await CountAsync("publishing", vaultId));
    }

    [Fact]
    public async Task Publishing_off_refuses_and_withdraw_still_works()
    {
        var (alice, access, store) = await SkillAsync();
        var code = CodeOf(await ShareAsync(access, store, alice));
        await using var off = app.PublicApi.WithWebHostBuilder(b => b.UseSetting("Kumbu:Publish:Enabled", "false"));
        var publications = off.Services.GetRequiredService<Publications>();
        var (offAccess, offStore) = await OpenAsync(alice, "Skills", off.Services);

        var publish = await Assert.ThrowsAsync<PublishRefusedException>(() => ShareAsync(offAccess, offStore, alice, publications));
        Assert.Equal(Refusals.PublishingOff, publish.Code);
        var update = await Assert.ThrowsAsync<PublishRefusedException>(() =>
            publications.UpdateSkillAsync(offAccess, offStore, P("/Skills/write-as-alice"), alice.Username, BySession, Ct));
        Assert.Equal(Refusals.PublishingOff, update.Code);

        var id = Assert.Single(await publications.SkillStatesAsync(offAccess, offStore, Ct)).Id;
        await publications.WithdrawSkillAsync(offAccess, offStore, id, alice.Username, BySession, Ct);

        Assert.Null(await Store.OpenSkillAsync(code, Ct));
        Assert.Empty(await publications.SkillStatesAsync(offAccess, offStore, Ct));
        Assert.Equal(0, await CountAsync("publications", access.Id));
        Assert.Equal(0, await CountAsync("publication_files", access.Id));
        var logged = (await Store.ReadAsync(null, access.Vault, Ct)).Record.Log;
        Assert.Equal([PublishingKinds.Published, PublishingKinds.Withdrawn], logged.Select(l => l.Kind));
    }

    [Fact]
    public async Task Note_listings_leave_skill_publications_out()
    {
        var (alice, access, store) = await SkillAsync();
        await ShareAsync(access, store, alice);

        Assert.Empty(await Publications.ListAsync(access, store, P("/Skills"), null, Ct));
        Assert.Empty(await Publications.ListAsync(access, store, VaultPath.Root, null, Ct));
        Assert.Null(await Publications.StateAsync(access, store, P("/Skills/write-as-alice"), null, Ct));
        Assert.Empty(await Publications.PublishedUnderAsync(access, P("/Skills"), Ct));
        var withdraw = await Assert.ThrowsAsync<PublishRefusedException>(() =>
            Publications.WithdrawAsync(access, store, P("/Skills/write-as-alice"), alice.Username, BySession, Ct));
        Assert.Equal(ShellErrorKind.NotFound, withdraw.Kind);

        using var overview = await alice.GetAsync("/api/publications");
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        Assert.Empty((await RestClient.JsonAsync(overview)).AsArray());
        using var vault = await alice.GetAsync("/api/vaults/Skills/publications");
        Assert.Equal(HttpStatusCode.OK, vault.StatusCode);
        Assert.Empty((await RestClient.JsonAsync(vault))["publications"]!.AsArray());

        var (key, _) = await app.PublicApi.Services.GetRequiredService<ApiKeys>().CreateAsync(
            alice.User, alice.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [access.Id] = KeyRight.Read }, Ct);
        var (isError, text) = await alice.ShAsync("publish ls /Skills", key);
        Assert.False(isError, text);
        Assert.DoesNotContain("write-as-alice", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Neither_the_code_nor_the_link_is_logged()
    {
        var log = new CapturedLog();
        await using var api = app.PublicApi.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(log)));
        var alice = await RestClient.NewAsync(api, vault: "Work");
        await PutNoteAsync(alice, "/Skills/x/SKILL.md", "---\nname: x\ndescription: y\n---\n");
        var (access, store) = await OpenAsync(alice, "Skills");
        var link = (await api.Services.GetRequiredService<Publications>().PublishSkillAsync(access, store, P("/Skills/x"), alice.User.Id, alice.Username,
            BySession, KumbuAppFactory.PublicUrl, Ct)).Link;
        await api.Services.GetRequiredService<Publications>().UpdateSkillAsync(access, store, P("/Skills/x"), alice.Username, BySession, Ct);
        var code = ShareCode.Parse(link[(link.IndexOf('#') + 1)..])!;
        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains(code) || l.Contains(ShareCode.Format(code)) || l.Contains(link));
    }
}
