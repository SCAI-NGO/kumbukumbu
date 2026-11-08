// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Dav;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Dav;

/// <summary><c>DELETE</c> and <c>COPY</c> on the store (WebDAV design §3.5): to the trash, a folder in one batch; a copy
/// with the source's content and no link rewritten, a folder recursively; and the parking area's files copied onto real
/// paths as a <c>PUT</c> of their bytes there.</summary>
[Collection("app")]
public sealed class DavDeleteCopyTests(KumbuAppFactory app)
{
    private const string Root = "https://localhost/dav/Work/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static HttpContent Bytes(byte[] bytes) => new ByteArrayContent(bytes);

    private static HttpContent Text(string text) => Bytes(Encoding.UTF8.GetBytes(text));

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers) => headers.ToDictionary(h => h.Name, h => h.Value);

    private static Dictionary<string, string> To(string path, string? overwrite = null, string? depth = null)
    {
        var headers = Headers(("Destination", Root + path));
        if (overwrite is not null) headers["Overwrite"] = overwrite;
        if (depth is not null) headers["Depth"] = depth;
        return headers;
    }

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    /// <summary>The owner's own store, as her session opens it.</summary>
    private EncryptedNoteStore Store(RestClient owner) =>
        new(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(owner.User.Id, owner.UserKey));

    private static async Task<string?> NoteAsync(RestClient owner, string path)
    {
        using var response = await owner.GetAsync(owner.Url(path));
        return response.StatusCode == HttpStatusCode.NotFound ? null : (await JsonAsync(response))["content"]!.GetValue<string>();
    }

    private static async Task<HttpStatusCode> FileStatusAsync(RestClient owner, string path)
    {
        using var response = await owner.GetAsync(owner.Url(path, "files"));
        return response.StatusCode;
    }

    [Fact]
    public async Task Delete_of_a_note_moves_it_to_the_trash_and_answers_204()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# Plan");

        using var delete = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/plan.md");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Null(await NoteAsync(dav.Owner, "plan.md"));
        var trashed = Assert.Single(await Store(dav.Owner).ListTrashAsync(P("/Work/plan.md"), Ct));
        Assert.Equal("# Plan", trashed.Note.Content);
        Assert.Equal("Work PC (dav)", trashed.DeletedBy);
        using var again = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/plan.md");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Delete_of_an_attachment_moves_it_to_the_trash()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateFileAsync("shot.png", FileApiTests.Png(2, 2));

        using var delete = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/shot.png");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await FileStatusAsync(dav.Owner, "shot.png"));
        Assert.Single(await Store(dav.Owner).ListAttachmentTrashAsync(P("/Work/shot.png"), Ct));
    }

    /// <summary>Everything below the folder goes in one commit: one removal time and one changeset for all of it.</summary>
    [Fact]
    public async Task Delete_of_a_folder_is_one_batch()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/a.md", "# A");
        await dav.Owner.CreateAsync("Ideas/Deep/b.md", "# B");
        await dav.Owner.CreateAsync("Ideas/Deep/c.md", "# C");
        await dav.Owner.CreateFileAsync("Ideas/shot.png", FileApiTests.Png(2, 2));
        await dav.Owner.CreateAsync("Other.md", "# Other");

        using var delete = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/Ideas/");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var store = Store(dav.Owner);
        var notes = await store.ListTrashAsync(P("/Work/Ideas"), Ct);
        var files = await store.ListAttachmentTrashAsync(P("/Work/Ideas"), Ct);
        Assert.Equal(["/Work/Ideas/Deep/b.md", "/Work/Ideas/Deep/c.md", "/Work/Ideas/a.md"], notes.Select(t => t.Note.Path.Value).Order(StringComparer.Ordinal));
        Assert.Single(files);
        Assert.Single(notes.Select(t => t.DeletedAt).Concat(files.Select(f => f.DeletedAt)).Distinct());
        Assert.Single(notes.Select(t => t.Changeset?.Id).Concat(files.Select(f => f.Changeset?.Id)).Distinct());
        Assert.Empty(await store.ListNotesUnderAsync(P("/Work/Ideas"), Ct));
        Assert.Equal("# Other", await NoteAsync(dav.Owner, "Other.md"));
        using var gone = await dav.PropfindAsync("/dav/Work/Ideas/", depth: "0");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Delete_of_an_empty_folder_trashes_its_marker()
    {
        var dav = await DavClient.NewAsync(Api);
        using (var mkcol = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/Empty")) Assert.Equal(HttpStatusCode.Created, mkcol.StatusCode);

        using var delete = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/Empty/");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var gone = await dav.PropfindAsync("/dav/Work/Empty/", depth: "0");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Theory]
    [InlineData("/dav/")]
    [InlineData("/dav/Work/")]
    public async Task Delete_on_a_vault_or_root_is_403(string path)
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# Plan");

        using var delete = await dav.SendAsync(HttpMethod.Delete, path);

        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal("# Plan", await NoteAsync(dav.Owner, "plan.md"));
    }

    [Fact]
    public async Task Delete_with_read_is_403_and_a_missing_path_404()
    {
        var reader = await DavClient.NewAsync(Api, right: "read");
        await reader.Owner.CreateAsync("plan.md", "# Plan");

        using var refused = await reader.SendAsync(HttpMethod.Delete, "/dav/Work/plan.md");
        var writer = await DavClient.NewAsync(Api, reader.Owner, label: "Desk");
        using var missing = await writer.SendAsync(HttpMethod.Delete, "/dav/Work/none.md");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("# Plan", await NoteAsync(reader.Owner, "plan.md"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>A folder that holds a structural file takes it along: only a share that may maintain removes it.</summary>
    [Fact]
    public async Task Delete_of_a_folder_with_the_board_needs_maintain()
    {
        var writer = await DavClient.NewAsync(Api);
        await writer.Owner.CreateAsync("Board/Board.md", "# Board\n");

        using var refused = await writer.SendAsync(HttpMethod.Delete, "/dav/Work/Board/");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("# Board\n", await NoteAsync(writer.Owner, "Board/Board.md"));
        var maintainer = await DavClient.NewAsync(Api, writer.Owner, label: "Desk", right: "maintain");
        using var deleted = await maintainer.SendAsync(HttpMethod.Delete, "/dav/Work/Board/");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task Delete_of_UPPER_MD_trashes_the_md_note()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Plan.md", "# Plan");

        using var delete = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/Plan.MD");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Null(await NoteAsync(dav.Owner, "Plan.md"));
    }

    /// <summary>What a share removed is recorded for it, by vault and path, with the version it had (§3.6 case 3 asks).</summary>
    [Fact]
    public async Task Delete_records_the_removal_for_the_share()
    {
        var dav = await DavClient.NewAsync(Api);
        var version = await dav.Owner.CreateAsync("Ideas/plan.md", "# Plan");
        await dav.Owner.CreateAsync("Ideas/other.md", "# Other");
        var vault = await dav.Owner.VaultIdAsync();
        var holds = Api.Services.GetRequiredService<DavHolds>();

        using (var delete = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/Ideas/plan.md")) Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(version, holds.RecentRemoval(dav.Id, vault, "Ideas/plan.md"));
        Assert.Null(holds.RecentRemoval("ZZZZ", vault, "Ideas/plan.md"));
        Assert.Null(holds.RecentRemoval(dav.Id, vault, "Ideas/other.md"));

        // A folder's removal records each note in it.
        using (var folder = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/Ideas/")) Assert.Equal(HttpStatusCode.NoContent, folder.StatusCode);
        Assert.NotNull(holds.RecentRemoval(dav.Id, vault, "Ideas/other.md"));
    }

    [Fact]
    public async Task Copy_of_a_note_leaves_links_alone()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/Questions.md", "# Questions\n\nSee [[Routing]] and [r](Routing.md)\n");
        await dav.Owner.CreateAsync("Ideas/Routing.md", "# Routing\n\nAsk [[Questions]]\n");

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/Ideas/Questions.md", headers: To("Archive.md"));

        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        Assert.Equal("# Questions\n\nSee [[Routing]] and [r](Routing.md)\n", await NoteAsync(dav.Owner, "Archive.md"));
        Assert.Equal("# Questions\n\nSee [[Routing]] and [r](Routing.md)\n", await NoteAsync(dav.Owner, "Ideas/Questions.md"));
        Assert.Equal("# Routing\n\nAsk [[Questions]]\n", await NoteAsync(dav.Owner, "Ideas/Routing.md"));
    }

    [Fact]
    public async Task Copy_of_a_folder_is_recursive()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/a.md", "# A\n\n[[b]]\n");
        await dav.Owner.CreateAsync("Ideas/Deep/b.md", "# B");
        var png = FileApiTests.Png(2, 2);
        await dav.Owner.CreateFileAsync("Ideas/Deep/shot.png", png);
        using (var mkcol = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/Ideas/Empty")) Assert.Equal(HttpStatusCode.Created, mkcol.StatusCode);

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/Ideas/", headers: To("Kept/"));

        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        Assert.Equal("# A\n\n[[b]]\n", await NoteAsync(dav.Owner, "Kept/a.md"));
        Assert.Equal("# B", await NoteAsync(dav.Owner, "Kept/Deep/b.md"));
        using (var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/Kept/Deep/shot.png")) Assert.Equal(png, await get.Content.ReadAsByteArrayAsync(Ct));
        var listing = DavAssert.Hrefs(await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/Kept/", depth: "1")));
        Assert.Contains("/dav/Work/Kept/Empty/", listing);
        Assert.Equal("# A\n\n[[b]]\n", await NoteAsync(dav.Owner, "Ideas/a.md"));
    }

    [Fact]
    public async Task Copy_of_a_folder_with_depth_0_copies_the_folder_only_and_depth_1_is_400()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/a.md", "# A");

        using var shallow = await dav.SendAsync(DavClient.Copy, "/dav/Work/Ideas/", headers: To("Kept/", depth: "0"));
        using var one = await dav.SendAsync(DavClient.Copy, "/dav/Work/Ideas/", headers: To("Other/", depth: "1"));

        Assert.Equal(HttpStatusCode.Created, shallow.StatusCode);
        var listing = DavAssert.Hrefs(await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/Kept/", depth: "1")));
        Assert.Equal(["/dav/Work/Kept/"], listing);
        Assert.Equal(HttpStatusCode.BadRequest, one.StatusCode);
    }

    [Fact]
    public async Task Copy_of_an_attachment_keeps_its_bytes()
    {
        var dav = await DavClient.NewAsync(Api);
        var png = FileApiTests.Png(3, 2);
        await dav.Owner.CreateFileAsync("shot.png", png);

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/shot.png", headers: To("copy.png"));
        using var renamed = await dav.SendAsync(DavClient.Copy, "/dav/Work/shot.png", headers: To("copy.pdf"));

        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        using (var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/copy.png")) Assert.Equal(png, await get.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
    }

    [Fact]
    public async Task Copy_with_overwrite_f_onto_an_existing_target_is_412()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("a.md", "# A");
        await dav.Owner.CreateAsync("b.md", "# B");

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/a.md", headers: To("b.md", overwrite: "F"));

        Assert.Equal(HttpStatusCode.PreconditionFailed, copy.StatusCode);
        Assert.Equal("# B", await NoteAsync(dav.Owner, "b.md"));
    }

    /// <summary>With <c>Overwrite: T</c> the target goes to the trash and the copy takes its place, in one batch.</summary>
    [Fact]
    public async Task Copy_with_overwrite_t_trashes_the_target_in_the_same_batch()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/a.md", "# A");
        await dav.Owner.CreateAsync("Kept/a.md", "# Old A");
        await dav.Owner.CreateAsync("Kept/gone.md", "# Gone");

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/Ideas/", headers: To("Kept/", overwrite: "T"));

        Assert.Equal(HttpStatusCode.NoContent, copy.StatusCode);
        Assert.Equal("# A", await NoteAsync(dav.Owner, "Kept/a.md"));
        Assert.Null(await NoteAsync(dav.Owner, "Kept/gone.md"));
        var trash = await Store(dav.Owner).ListTrashAsync(P("/Work/Kept"), Ct);
        Assert.Equal(["/Work/Kept/a.md", "/Work/Kept/gone.md"], trash.Select(t => t.Note.Path.Value).Order(StringComparer.Ordinal));
        var copied = await Store(dav.Owner).GetAsync(P("/Work/Kept/a.md"), Ct);
        Assert.Single(trash.Select(t => t.Changeset?.Id).Append(copied!.Changeset?.Id).Distinct());
    }

    /// <summary>A refused copy leaves the target as it was: every check runs before the target goes.</summary>
    [Fact]
    public async Task A_refused_copy_with_overwrite_t_keeps_the_target()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateFileAsync("shot.png", FileApiTests.Png(2, 2));
        await dav.Owner.CreateAsync("Board/Board.md", "# Board\n");
        await dav.Owner.CreateAsync("plan.md", "# Plan");
        using (var parked = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.latin.md", Bytes([0x43, 0x61, 0x66, 0xE9]))) Assert.Equal(HttpStatusCode.Created, parked.StatusCode);

        using var format = await dav.SendAsync(DavClient.Copy, "/dav/Work/shot.png", headers: To("plan.md", overwrite: "T"));
        using var structural = await dav.SendAsync(DavClient.Copy, "/dav/Work/plan.md", headers: To("Board/Board.md", overwrite: "T"));
        using var latin = await dav.SendAsync(DavClient.Copy, "/dav/Work/.latin.md", headers: To("plan.md", overwrite: "T"));

        Assert.Equal(HttpStatusCode.BadRequest, format.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, structural.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, latin.StatusCode);
        Assert.Equal("# Plan", await NoteAsync(dav.Owner, "plan.md"));
        Assert.Equal("# Board\n", await NoteAsync(dav.Owner, "Board/Board.md"));
        Assert.Empty(await Store(dav.Owner).ListTrashAsync(P("/Work"), Ct));
    }

    [Fact]
    public async Task Copy_into_itself_is_403()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/a.md", "# A");

        using var into = await dav.SendAsync(DavClient.Copy, "/dav/Work/Ideas/", headers: To("Ideas/Inner/"));
        using var onto = await dav.SendAsync(DavClient.Copy, "/dav/Work/Ideas/a.md", headers: To("Ideas/", overwrite: "T"));

        Assert.Equal(HttpStatusCode.Forbidden, into.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, onto.StatusCode);
        Assert.Equal("# A", await NoteAsync(dav.Owner, "Ideas/a.md"));
    }

    [Fact]
    public async Task Copy_with_read_is_403()
    {
        var dav = await DavClient.NewAsync(Api, right: "read");
        await dav.Owner.CreateAsync("a.md", "# A");

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/a.md", headers: To("b.md"));

        Assert.Equal(HttpStatusCode.Forbidden, copy.StatusCode);
        Assert.Null(await NoteAsync(dav.Owner, "b.md"));
    }

    [Fact]
    public async Task Copy_across_vaults_is_502()
    {
        var owner = await RestClient.NewAsync(Api, vault: "Work");
        using (var personal = await owner.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Personal" })) Assert.Equal(HttpStatusCode.Created, personal.StatusCode);
        var dav = await DavClient.NewAsync(Api, owner, vaults: new Dictionary<string, string> { ["Work"] = "write", ["Personal"] = "write" });
        await owner.CreateAsync("a.md", "# A");

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/a.md", headers: Headers(("Destination", "https://localhost/dav/Personal/a.md")));

        Assert.Equal(HttpStatusCode.BadGateway, copy.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example/dav/Work/b.md")]
    [InlineData("http://localhost/dav/Work/b.md")]
    [InlineData("https://localhost/api/x")]
    public async Task A_destination_on_another_host_scheme_or_outside_dav_is_502(string destination)
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("a.md", "# A");

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/a.md", headers: Headers(("Destination", destination)));

        Assert.Equal(HttpStatusCode.BadGateway, copy.StatusCode);
        Assert.Null(await NoteAsync(dav.Owner, "b.md"));
    }

    [Fact]
    public async Task Copy_of_UPPER_MD_copies_the_md_note_and_onto_UPPER_MD_writes_md()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Plan.md", "# Plan");

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/Plan.MD", headers: To("Copy.MD"));

        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        Assert.Equal("# Plan", await NoteAsync(dav.Owner, "Copy.md"));
    }

    [Fact]
    public async Task Copy_of_a_parked_file_onto_a_real_md_path_stores_a_note()
    {
        var dav = await DavClient.NewAsync(Api);
        using (var parked = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.plan.md.tmp", Text("# Plan\r\n"))) Assert.Equal(HttpStatusCode.Created, parked.StatusCode);

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/.plan.md.tmp", headers: To("plan.md"));

        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        Assert.Equal("# Plan\n", await NoteAsync(dav.Owner, "plan.md"));
        Assert.Equal($"\"{(await dav.Owner.ReadAsync("plan.md"))["version"]!.GetValue<string>()}\"", copy.Headers.ETag?.Tag);
        using (var source = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.plan.md.tmp")) Assert.Equal(HttpStatusCode.OK, source.StatusCode);

        // Over a note, a content write of it, as a PUT would be: one more version in its history.
        using (var again = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.plan.md.tmp", Text("# Plan, two\n"))) Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        using var over = await dav.SendAsync(DavClient.Copy, "/dav/Work/.plan.md.tmp", headers: To("plan.md"));
        Assert.Equal(HttpStatusCode.NoContent, over.StatusCode);
        Assert.Equal("# Plan, two\n", await NoteAsync(dav.Owner, "plan.md"));
        Assert.Empty(await Store(dav.Owner).ListTrashAsync(P("/Work/plan.md"), Ct));
    }

    [Fact]
    public async Task Copy_of_a_parked_non_utf8_file_onto_a_md_path_is_415()
    {
        var dav = await DavClient.NewAsync(Api);
        using (var parked = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.latin.tmp", Bytes([0x43, 0x61, 0x66, 0xE9, 0x0A]))) Assert.Equal(HttpStatusCode.Created, parked.StatusCode);

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/.latin.tmp", headers: To("latin.md"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, copy.StatusCode);
        Assert.Null(await NoteAsync(dav.Owner, "latin.md"));
    }

    [Fact]
    public async Task Copy_of_a_parked_file_onto_an_unsupported_name_is_415_and_onto_an_image_name_checks_the_bytes()
    {
        var dav = await DavClient.NewAsync(Api);
        using (var parked = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.x.tmp", Text("not a picture"))) Assert.Equal(HttpStatusCode.Created, parked.StatusCode);
        using (var png = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.y.tmp", Bytes(FileApiTests.Png(2, 2)))) Assert.Equal(HttpStatusCode.Created, png.StatusCode);

        using var sheet = await dav.SendAsync(DavClient.Copy, "/dav/Work/.x.tmp", headers: To("budget.xlsx"));
        using var picture = await dav.SendAsync(DavClient.Copy, "/dav/Work/.x.tmp", headers: To("shot.png"));
        using var real = await dav.SendAsync(DavClient.Copy, "/dav/Work/.y.tmp", headers: To("shot.png"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, sheet.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, picture.StatusCode);
        Assert.Equal(HttpStatusCode.Created, real.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await FileStatusAsync(dav.Owner, "shot.png"));
    }

    [Fact]
    public async Task Copy_of_a_real_file_onto_a_parked_name_is_a_parked_copy()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# Plan");

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/plan.md", headers: To("plan.md~"));

        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        using (var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/plan.md~")) Assert.Equal("# Plan", await get.Content.ReadAsStringAsync(Ct));
        Assert.Equal(["/Work/plan.md"], (await Store(dav.Owner).ListNotesUnderAsync(P("/Work"), Ct)).Select(p => p.Value));
    }

    [Fact]
    public async Task Copy_of_a_real_folder_into_a_dot_folder_is_parked()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/a.md", "# A");
        await dav.Owner.CreateAsync("Ideas/Deep/b.md", "# B");

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/Ideas/", headers: To(".backup/"));

        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        using (var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.backup/Deep/b.md")) Assert.Equal("# B", await get.Content.ReadAsStringAsync(Ct));
        var listing = DavAssert.Hrefs(await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/.backup/", depth: "1")));
        Assert.Contains("/dav/Work/.backup/Deep/", listing);
        Assert.Contains("/dav/Work/.backup/a.md", listing);
        Assert.Equal(2, (await Store(dav.Owner).ListNotesUnderAsync(P("/Work"), Ct)).Count);
    }

    /// <summary>A stored folder copied onto a parked name that wouldn't fit the share's parking cap by itself is a 507
    /// before a byte of it is read (security review finding 3): nothing of it is parked, and what the share had parked
    /// stays, rather than the server reading gigabytes into memory and then evicting.</summary>
    [Fact]
    public async Task Copy_of_a_stored_folder_larger_than_the_parking_cap_is_507_and_parks_nothing()
    {
        var dav = await DavClient.NewAsync(Api);
        var pdf = new byte[14_000_000];
        "%PDF-1.4\n"u8.CopyTo(pdf);
        // Five of them: 70 MB, beyond the 64 MiB a share may park.
        for (var i = 0; i < 5; i++) await dav.Owner.CreateFileAsync($"Archive/{i}.pdf", pdf);
        Assert.True(5L * pdf.Length > Kumbu.Server.Dav.DavRules.ParkingCap);
        using (var kept = await dav.SendAsync(HttpMethod.Put, "/dav/Work/.keep", Bytes([1, 2, 3]))) Assert.Equal(HttpStatusCode.Created, kept.StatusCode);

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/Archive/", headers: To(".backup/"));

        Assert.Equal(HttpStatusCode.InsufficientStorage, copy.StatusCode);
        using (var backup = await dav.PropfindAsync("/dav/Work/.backup/", depth: "0")) Assert.Equal(HttpStatusCode.NotFound, backup.StatusCode);
        using (var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.keep")) Assert.Equal([1, 2, 3], await get.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>A name that holds only an empty placeholder is no parked name: a stored file copied onto it is copied in
    /// the store, not into the share's memory.</summary>
    [Fact]
    public async Task A_copy_onto_a_placeholder_copies_the_file_in_the_store()
    {
        var dav = await DavClient.NewAsync(Api);
        var png = FileApiTests.Png(2, 2);
        await dav.Owner.CreateFileAsync("shot.png", png);
        using (var empty = await dav.SendAsync(HttpMethod.Put, "/dav/Work/copy.png", Bytes([]))) Assert.Equal(HttpStatusCode.Created, empty.StatusCode);

        using var copy = await dav.SendAsync(DavClient.Copy, "/dav/Work/shot.png", headers: To("copy.png"));

        Assert.Equal(HttpStatusCode.NoContent, copy.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await FileStatusAsync(dav.Owner, "shot.png"));
        using (var file = await dav.Owner.GetAsync(dav.Owner.Url("copy.png", "files"))) Assert.Equal(png, await file.Content.ReadAsByteArrayAsync(Ct));
        Assert.Empty(await Store(dav.Owner).ListTrashAsync(P("/Work"), Ct));

        // The placeholder went with the write: the copy removed from the store leaves nothing at its name.
        using (var removed = await dav.Owner.SendAsync(HttpMethod.Delete, dav.Owner.Url("copy.png", "files"))) Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.DoesNotContain("/dav/Work/copy.png", DavAssert.Hrefs(await DavAssert.MultistatusAsync(await dav.PropfindAsync("/dav/Work/"))));
    }
}
