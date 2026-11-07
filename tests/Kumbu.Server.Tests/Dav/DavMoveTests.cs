// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using Kumbu.Core.Paths;
using Kumbu.Server.Dav;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Dav;

/// <summary><c>MOVE</c> on the store and editors' atomic saves (WebDAV design §3.6 cases 1–5, §6 steps 4–5): a rename
/// that rewrites links in one batch, a temp file moved over its note as one content write, an original deleted or renamed
/// to a backup name first and brought back by the file that follows within the minute, and <c>Overwrite</c>.</summary>
[Collection("app")]
public sealed class DavMoveTests(KumbuAppFactory app) : IAsyncDisposable
{
    private const string Root = "https://localhost/dav/Work/";

    // Whole seconds, as the other timed tests: MongoDB keeps milliseconds.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private WebApplicationFactory<Program>? _timed;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A server of its own whose clock the test moves.</summary>
    private WebApplicationFactory<Program> Api => _timed ??= app.PublicApi.WithWebHostBuilder(b => b.ConfigureServices(s =>
    {
        s.RemoveAll<TimeProvider>();
        s.AddSingleton<TimeProvider>(_time);
    }));

    public async ValueTask DisposeAsync()
    {
        if (_timed is not null) await _timed.DisposeAsync();
    }

    private static HttpContent Bytes(byte[] bytes) => new ByteArrayContent(bytes);

    private static HttpContent Text(string text) => Bytes(Encoding.UTF8.GetBytes(text));

    private static Dictionary<string, string> To(string path, string? overwrite = null)
    {
        var headers = new Dictionary<string, string> { ["Destination"] = Root + path };
        if (overwrite is not null) headers["Overwrite"] = overwrite;
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

    private static async Task<List<string>> ListingAsync(DavClient dav, string folder = "/dav/Work/") =>
        DavAssert.Hrefs(await DavAssert.MultistatusAsync(await dav.PropfindAsync(folder, depth: "1")));

    private static async Task<HttpStatusCode> StatusAsync(DavClient dav, HttpMethod method, string path)
    {
        using var response = await dav.SendAsync(method, path);
        return response.StatusCode;
    }

    private async Task ParkAsync(DavClient dav, string name, HttpContent content)
    {
        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/" + name, content);
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
    }

    /// <summary>Any request of the share: the one moment its holds whose minute is up end.</summary>
    private static async Task NextRequestAsync(DavClient dav)
    {
        using var response = await dav.PropfindAsync("/dav/", depth: "0");
    }

    /// <summary>Every call of the clock moves it on a millisecond, so that two batches never share a time.</summary>
    private IDisposable Ticking()
    {
        _time.AutoAdvanceAmount = TimeSpan.FromMilliseconds(1);
        return new Reset(() => _time.AutoAdvanceAmount = TimeSpan.Zero);
    }

    private sealed class Reset(Action reset) : IDisposable
    {
        public void Dispose() => reset();
    }

    /// <summary>Spec §6 step 5: Explorer renames <c>Questions.md</c>; the link follows, and the move and the rewrite are one batch.</summary>
    [Fact]
    public async Task Case1_rename_rewrites_links_in_one_batch()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/Questions.md", "# Questions\n");
        await dav.Owner.CreateAsync("Ideas/Routing.md", "# Routing\n\nAsk [[Questions]]\n");

        HttpResponseMessage move;
        using (Ticking()) move = await dav.SendAsync(DavClient.Move, "/dav/Work/Ideas/Questions.md", headers: To("Ideas/Open%20questions.md"));
        using var _ = move;

        Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        Assert.Null(await NoteAsync(dav.Owner, "Ideas/Questions.md"));
        Assert.Equal("# Questions\n", await NoteAsync(dav.Owner, "Ideas/Open questions.md"));
        Assert.Equal("# Routing\n\nAsk [[Open questions]]\n", await NoteAsync(dav.Owner, "Ideas/Routing.md"));
        var store = Store(dav.Owner);
        var record = await store.FindMoveAsync(P("/Work/Ideas/Questions.md"), Ct);
        var routing = await store.GetAsync(P("/Work/Ideas/Routing.md"), Ct);
        Assert.Equal("/Work/Ideas/Open questions.md", record!.To.Value);
        Assert.Equal(record.MovedAt, routing!.LastWriteAt);
        Assert.Equal(record.Changeset?.Id, routing.Changeset?.Id);
        Assert.Equal("Work PC (dav)", record.MovedBy);
    }

    [Fact]
    public async Task Case1_folder_move_rewrites_links()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/a.md", "# A\n");
        await dav.Owner.CreateAsync("Ideas/Deep/b.md", "# B\n");
        var png = FileApiTests.Png(2, 2);
        await dav.Owner.CreateFileAsync("Ideas/shot.png", png);
        await dav.Owner.CreateAsync("Other.md", "# Other\n\n[[Ideas/a]] and ![[Ideas/shot.png]]\n");

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/Ideas/", headers: To("Archive/"));

        Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        Assert.Equal("# A\n", await NoteAsync(dav.Owner, "Archive/a.md"));
        Assert.Equal("# B\n", await NoteAsync(dav.Owner, "Archive/Deep/b.md"));
        Assert.Equal("# Other\n\n[[Archive/a]] and ![[Archive/shot.png]]\n", await NoteAsync(dav.Owner, "Other.md"));
        Assert.Empty(await Store(dav.Owner).ListNotesUnderAsync(P("/Work/Ideas"), Ct));
        using (var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/Archive/shot.png")) Assert.Equal(png, await get.Content.ReadAsByteArrayAsync(Ct));
        Assert.Empty(await Store(dav.Owner).ListTrashAsync(P("/Work"), Ct));
    }

    [Fact]
    public async Task Case1_attachment_move_keeps_its_bytes_and_its_links()
    {
        var dav = await DavClient.NewAsync(Api);
        var png = FileApiTests.Png(3, 2);
        await dav.Owner.CreateFileAsync("shot.png", png);
        await dav.Owner.CreateAsync("Other.md", "![[shot.png]]\n");

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/shot.png", headers: To("diagram.png"));

        Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        using (var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/diagram.png")) Assert.Equal(png, await get.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(dav, HttpMethod.Get, "/dav/Work/shot.png"));
        Assert.Equal("![[diagram.png]]\n", await NoteAsync(dav.Owner, "Other.md"));
    }

    /// <summary>Spec §6 step 4: the editor writes <c>.Questions.md.tmp</c> and moves it over <c>Questions.md</c>.</summary>
    [Fact]
    public async Task Case2_editor_save_via_tmp_file_is_one_version_and_leaves_links_alone()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/Questions.md", "# Questions\n\nold\n");
        await dav.Owner.CreateAsync("Ideas/Routing.md", "# Routing\n\nAsk [[Questions]]\n");
        await ParkAsync(dav, "Ideas/.Questions.md.tmp", Text("# Questions\r\n\r\nnew\r\n"));

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/Ideas/.Questions.md.tmp", headers: To("Ideas/Questions.md"));

        Assert.Equal(HttpStatusCode.NoContent, move.StatusCode);
        var store = Store(dav.Owner);
        var note = await store.GetAsync(P("/Work/Ideas/Questions.md"), Ct);
        Assert.Equal("# Questions\n\nnew\n", note!.Content);
        Assert.Equal("# Questions\n\nold\n", Assert.Single(note.History).Content);
        Assert.Equal($"\"{note.Version}\"", move.Headers.ETag?.Tag);
        Assert.Equal("# Routing\n\nAsk [[Questions]]\n", await NoteAsync(dav.Owner, "Ideas/Routing.md"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(dav, HttpMethod.Get, "/dav/Work/Ideas/.Questions.md.tmp"));
        Assert.Empty(await store.ListTrashAsync(P("/Work"), Ct));
        Assert.Null(await store.FindMoveAsync(P("/Work/Ideas/Questions.md"), Ct));
    }

    [Fact]
    public async Task Case3_parked_onto_a_free_md_path_is_a_new_note()
    {
        var dav = await DavClient.NewAsync(Api);
        await ParkAsync(dav, ".new.md.tmp", Text("# New\n"));

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/.new.md.tmp", headers: To("new.md"));

        Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        Assert.Equal("# New\n", await NoteAsync(dav.Owner, "new.md"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(dav, HttpMethod.Get, "/dav/Work/.new.md.tmp"));
    }

    /// <summary>An editor that deletes the original before it moves its temp file into place.</summary>
    private async Task<(DavClient Dav, HttpStatusCode Status)> DeleteThenMoveAsync(TimeSpan pause, bool otherShare = false)
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# v1\n");
        using (var delete = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/plan.md")) Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        _time.Advance(pause);
        var saver = otherShare ? await DavClient.NewAsync(Api, dav.Owner, label: "Desk") : dav;
        await ParkAsync(saver, ".plan.md.tmp", Text("# v2\n"));
        using var move = await saver.SendAsync(DavClient.Move, "/dav/Work/.plan.md.tmp", headers: To("plan.md"));
        return (dav, move.StatusCode);
    }

    [Fact]
    public async Task Case3_after_the_share_deleted_the_original_restores_it_with_history()
    {
        var (dav, status) = await DeleteThenMoveAsync(TimeSpan.FromSeconds(59));

        Assert.Equal(HttpStatusCode.Created, status);
        var store = Store(dav.Owner);
        var note = await store.GetAsync(P("/Work/plan.md"), Ct);
        Assert.Equal("# v2\n", note!.Content);
        Assert.Contains(note.History, h => h.Content == "# v1\n");
        Assert.Empty(await store.ListTrashAsync(P("/Work/plan.md"), Ct));
    }

    [Fact]
    public async Task Case3_after_61_seconds_is_a_new_note()
    {
        var (dav, status) = await DeleteThenMoveAsync(TimeSpan.FromSeconds(61));

        Assert.Equal(HttpStatusCode.Created, status);
        var store = Store(dav.Owner);
        var note = await store.GetAsync(P("/Work/plan.md"), Ct);
        Assert.Equal("# v2\n", note!.Content);
        Assert.Empty(note.History);
        Assert.Equal("# v1\n", Assert.Single(await store.ListTrashAsync(P("/Work/plan.md"), Ct)).Note.Content);
    }

    [Fact]
    public async Task Case3_does_not_restore_another_shares_removal()
    {
        var (dav, status) = await DeleteThenMoveAsync(TimeSpan.FromSeconds(5), otherShare: true);

        Assert.Equal(HttpStatusCode.Created, status);
        var store = Store(dav.Owner);
        var note = await store.GetAsync(P("/Work/plan.md"), Ct);
        Assert.Equal("# v2\n", note!.Content);
        Assert.Empty(note.History);
        Assert.Single(await store.ListTrashAsync(P("/Work/plan.md"), Ct));
    }

    [Fact]
    public async Task Case4_note_onto_a_backup_name_then_new_file_is_one_content_write()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# v1\n");
        await dav.Owner.CreateAsync("Routing.md", "Ask [[plan]]\n");

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("plan.md~"));
        Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        _time.Advance(TimeSpan.FromSeconds(30));
        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("# v2\n"));

        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        var store = Store(dav.Owner);
        var note = await store.GetAsync(P("/Work/plan.md"), Ct);
        Assert.Equal("# v2\n", note!.Content);
        Assert.Equal("# v1\n", Assert.Single(note.History).Content);
        Assert.Equal("Ask [[plan]]\n", await NoteAsync(dav.Owner, "Routing.md"));
        var listing = await ListingAsync(dav);
        Assert.Contains("/dav/Work/plan.md", listing);
        Assert.DoesNotContain("/dav/Work/plan.md~", listing);
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(dav, HttpMethod.Get, "/dav/Work/plan.md~"));

        // The hold is over: the minute passing takes nothing to the trash.
        _time.Advance(TimeSpan.FromSeconds(61));
        await NextRequestAsync(dav);
        Assert.Equal("# v2\n", await NoteAsync(dav.Owner, "plan.md"));
        Assert.Empty(await store.ListTrashAsync(P("/Work"), Ct));
    }

    /// <summary>The file may arrive by a <c>MOVE</c> of a temp file too: the same content write.</summary>
    [Fact]
    public async Task Case4_a_temp_file_moved_onto_the_original_is_the_content_write()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# v1\n");
        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("plan.md~"))) Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        await ParkAsync(dav, ".plan.md.tmp", Text("# v2\n"));

        using var save = await dav.SendAsync(DavClient.Move, "/dav/Work/.plan.md.tmp", headers: To("plan.md", overwrite: "F"));

        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        var note = await Store(dav.Owner).GetAsync(P("/Work/plan.md"), Ct);
        Assert.Equal("# v2\n", note!.Content);
        Assert.Equal("# v1\n", Assert.Single(note.History).Content);
    }

    [Fact]
    public async Task Case4_without_arrival_goes_to_the_trash_after_60_seconds()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# v1\n");
        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("plan.md~"))) Assert.Equal(HttpStatusCode.Created, move.StatusCode);

        _time.Advance(TimeSpan.FromSeconds(59));
        await NextRequestAsync(dav);
        Assert.Equal("# v1\n", await NoteAsync(dav.Owner, "plan.md"));

        // Nothing ends it but a request of the share.
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("# v1\n", await NoteAsync(dav.Owner, "plan.md"));
        await NextRequestAsync(dav);
        Assert.Null(await NoteAsync(dav.Owner, "plan.md"));
        var trashed = Assert.Single(await Store(dav.Owner).ListTrashAsync(P("/Work/plan.md"), Ct));
        Assert.Equal("# v1\n", trashed.Note.Content);
        Assert.Equal("Work PC (dav)", trashed.DeletedBy);
        var listing = await ListingAsync(dav);
        Assert.DoesNotContain("/dav/Work/plan.md", listing);
        Assert.DoesNotContain("/dav/Work/plan.md~", listing);
    }

    /// <summary>A request of the share ends its expired holds before it is answered: a file that comes after the minute
    /// is a new note, and the held one is in the trash, not overwritten and then trashed.</summary>
    [Fact]
    public async Task Case4_a_file_after_60_seconds_is_a_new_note_and_the_old_one_is_in_the_trash()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# v1\n");
        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("plan.md~"))) Assert.Equal(HttpStatusCode.Created, move.StatusCode);

        _time.Advance(TimeSpan.FromSeconds(61));
        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("# v2\n"));
        await NextRequestAsync(dav);

        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        var store = Store(dav.Owner);
        var note = await store.GetAsync(P("/Work/plan.md"), Ct);
        Assert.Equal("# v2\n", note!.Content);
        Assert.Empty(note.History);
        Assert.Equal("# v1\n", Assert.Single(await store.ListTrashAsync(P("/Work/plan.md"), Ct)).Note.Content);
    }

    [Fact]
    public async Task Case4_delete_of_the_held_name_does_nothing_more()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# v1\n");
        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("plan.md~"))) Assert.Equal(HttpStatusCode.Created, move.StatusCode);

        using var delete = await dav.SendAsync(HttpMethod.Delete, "/dav/Work/plan.md~");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var store = Store(dav.Owner);
        Assert.Equal("# v1\n", await NoteAsync(dav.Owner, "plan.md"));
        Assert.Empty(await store.ListTrashAsync(P("/Work"), Ct));
        var listing = await ListingAsync(dav);
        Assert.DoesNotContain("/dav/Work/plan.md~", listing);
        Assert.DoesNotContain("/dav/Work/plan.md", listing);

        _time.Advance(TimeSpan.FromSeconds(61));
        await NextRequestAsync(dav);
        Assert.Null(await NoteAsync(dav.Owner, "plan.md"));
        Assert.Single(await store.ListTrashAsync(P("/Work"), Ct));
    }

    [Fact]
    public async Task Case4_held_note_is_hidden_from_this_share_only()
    {
        var dav = await DavClient.NewAsync(Api);
        var other = await DavClient.NewAsync(Api, dav.Owner, label: "Desk");
        await dav.Owner.CreateAsync("plan.md", "# Plan\n");

        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("plan.md~"))) Assert.Equal(HttpStatusCode.Created, move.StatusCode);

        var mine = await ListingAsync(dav);
        Assert.Contains("/dav/Work/plan.md~", mine);
        Assert.DoesNotContain("/dav/Work/plan.md", mine);
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(dav, HttpMethod.Get, "/dav/Work/plan.md"));
        using (var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/plan.md~")) Assert.Equal("# Plan\n", await get.Content.ReadAsStringAsync(Ct));
        var theirs = await ListingAsync(other);
        Assert.Contains("/dav/Work/plan.md", theirs);
        Assert.DoesNotContain("/dav/Work/plan.md~", theirs);
        Assert.Equal("# Plan\n", await NoteAsync(dav.Owner, "plan.md"));
        Assert.Equal(["/Work/plan.md"], (await Store(dav.Owner).ListNotesUnderAsync(P("/Work"), Ct)).Select(p => p.Value));
    }

    /// <summary>An editor that gives up its save renames the backup back: the note is the share's again, nothing written.</summary>
    [Fact]
    public async Task Case4_moving_the_held_name_back_ends_the_hold()
    {
        var dav = await DavClient.NewAsync(Api);
        var version = await dav.Owner.CreateAsync("plan.md", "# Plan\n");
        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("plan.md~"))) Assert.Equal(HttpStatusCode.Created, move.StatusCode);

        using var back = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md~", headers: To("plan.md"));

        Assert.Equal(HttpStatusCode.Created, back.StatusCode);
        Assert.Contains("/dav/Work/plan.md", await ListingAsync(dav));
        _time.Advance(TimeSpan.FromSeconds(61));
        await NextRequestAsync(dav);
        Assert.Equal(version, (await dav.Owner.ReadAsync("plan.md"))["version"]!.GetValue<string>());
        Assert.Empty(await Store(dav.Owner).ListTrashAsync(P("/Work"), Ct));
    }

    /// <summary>Another share's request ends nothing of this one's: only the share's own request, with its own rights.</summary>
    [Fact]
    public async Task Case4_another_shares_request_ends_nothing()
    {
        var dav = await DavClient.NewAsync(Api);
        var other = await DavClient.NewAsync(Api, dav.Owner, label: "Desk");
        await dav.Owner.CreateAsync("plan.md", "# v1\n");
        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("plan.md~"))) Assert.Equal(HttpStatusCode.Created, move.StatusCode);

        _time.Advance(TimeSpan.FromSeconds(61));
        await NextRequestAsync(other);

        Assert.Equal("# v1\n", await NoteAsync(dav.Owner, "plan.md"));
        await NextRequestAsync(dav);
        Assert.Null(await NoteAsync(dav.Owner, "plan.md"));
    }

    /// <summary>A hold its share doesn't come back for within the parking TTL is forgotten: the note stays where it is.</summary>
    [Fact]
    public async Task Case4_a_hold_past_the_parking_ttl_is_forgotten()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# v1\n");
        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("plan.md~"))) Assert.Equal(HttpStatusCode.Created, move.StatusCode);

        _time.Advance(DavRules.ParkingTtl + TimeSpan.FromSeconds(1));
        var listing = await ListingAsync(dav);

        Assert.Contains("/dav/Work/plan.md", listing);
        Assert.DoesNotContain("/dav/Work/plan.md~", listing);
        Assert.Equal("# v1\n", await NoteAsync(dav.Owner, "plan.md"));
        Assert.Empty(await Store(dav.Owner).ListTrashAsync(P("/Work"), Ct));
    }

    [Fact]
    public async Task Case4_a_revoked_share_ends_nothing()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# v1\n");
        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("plan.md~"))) Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        using (var revoke = await dav.Owner.SendAsync(HttpMethod.Delete, $"/api/dav-shares/{dav.Id}")) Assert.True(revoke.IsSuccessStatusCode, revoke.StatusCode.ToString());

        _time.Advance(TimeSpan.FromSeconds(61));
        using (var refused = await dav.PropfindAsync("/dav/", depth: "0")) DavAssert.Challenged(refused);
        _time.Advance(DavRules.ParkingTtl);
        using (var again = await dav.PropfindAsync("/dav/", depth: "0")) DavAssert.Challenged(again);

        Assert.Equal("# v1\n", await NoteAsync(dav.Owner, "plan.md"));
        Assert.Empty(await Store(dav.Owner).ListTrashAsync(P("/Work"), Ct));
    }

    /// <summary>A member's share of a vault shared with her (alias "Work"), and the membership's id.</summary>
    private async Task<(DavClient Dav, RestClient Alice, string Membership)> MembersShareAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Garten");
        var bob = await RestClient.NewAsync(Api, vault: "Own");
        var membership = await alice.ShareWithAsync(bob, "Work");
        var dav = await DavClient.NewAsync(Api, bob, vaults: new Dictionary<string, string> { ["Work"] = "write" });
        await alice.CreateAsync("plan.md", "# v1\n");
        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("plan.md~"))) Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        return (dav, alice, membership);
    }

    [Fact]
    public async Task Case4_an_ended_membership_ends_nothing()
    {
        var (dav, alice, membership) = await MembersShareAsync();
        await Api.Services.GetRequiredService<Memberships>().RevokeAsync(await alice.VaultIdAsync(), membership, Ct);

        _time.Advance(TimeSpan.FromSeconds(61));
        await NextRequestAsync(dav);

        Assert.Equal("# v1\n", await NoteAsync(alice, "plan.md"));
        Assert.Empty(await Store(alice).ListTrashAsync(P("/Garten"), Ct));
    }

    [Fact]
    public async Task Case4_a_role_lowered_to_read_ends_nothing()
    {
        var (dav, alice, membership) = await MembersShareAsync();
        await Api.Services.GetRequiredService<Memberships>().ChangeRoleAsync(alice.User.Id, alice.UserKey, await alice.VaultIdAsync(), membership, MemberRole.Read, () => { }, Ct);

        _time.Advance(TimeSpan.FromSeconds(61));
        var listing = await ListingAsync(dav);

        Assert.Equal("# v1\n", await NoteAsync(alice, "plan.md"));
        Assert.Empty(await Store(alice).ListTrashAsync(P("/Garten"), Ct));
        Assert.Contains("/dav/Work/plan.md", listing);
    }

    /// <summary>The ending trashes only the version that was held: a note changed meanwhile by another hand stays.</summary>
    [Fact]
    public async Task Case4_a_note_changed_meanwhile_is_not_trashed()
    {
        var dav = await DavClient.NewAsync(Api);
        var version = await dav.Owner.CreateAsync("plan.md", "# v1\n");
        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("plan.md~"))) Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        using (var edit = await dav.Owner.SendAsync(HttpMethod.Put, dav.Owner.Url("plan.md"), new { content = "# Owner's\n", version })) Assert.True(edit.IsSuccessStatusCode, edit.StatusCode.ToString());

        _time.Advance(TimeSpan.FromSeconds(61));
        var listing = await ListingAsync(dav);

        Assert.Equal("# Owner's\n", await NoteAsync(dav.Owner, "plan.md"));
        Assert.Empty(await Store(dav.Owner).ListTrashAsync(P("/Work"), Ct));
        Assert.Contains("/dav/Work/plan.md", listing);
    }

    /// <summary>An arrival the write refuses has taken the hold: the note is the share's again where it was, and stays.</summary>
    [Fact]
    public async Task Case4_a_refused_arrival_lets_the_note_reappear()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# v1\n");
        using (var move = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("plan.md~"))) Assert.Equal(HttpStatusCode.Created, move.StatusCode);

        using var put = await dav.SendAsync(HttpMethod.Put, "/dav/Work/plan.md", Text("# v2\n"), new Dictionary<string, string> { ["If-Match"] = "\"nope\"" });

        Assert.Equal(HttpStatusCode.PreconditionFailed, put.StatusCode);
        var listing = await ListingAsync(dav);
        Assert.Contains("/dav/Work/plan.md", listing);
        Assert.DoesNotContain("/dav/Work/plan.md~", listing);
        _time.Advance(TimeSpan.FromSeconds(61));
        await NextRequestAsync(dav);
        Assert.Equal("# v1\n", await NoteAsync(dav.Owner, "plan.md"));
        Assert.Empty(await Store(dav.Owner).ListTrashAsync(P("/Work"), Ct));
    }

    /// <summary>A hold whose ending fails is put back, for the share's next request; another share's holds aren't touched.</summary>
    [Fact]
    public async Task An_ending_that_fails_puts_its_holds_back()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var holds = new DavHolds(time);
        holds.Hold("aaaa", "v", "a.md~", P("/Work/a.md"), "1");
        holds.Hold("aaaa", "v", "b.md~", P("/Work/b.md"), "2");
        holds.Hold("bbbb", "v", "c.md~", P("/Work/c.md"), "3");
        time.Advance(TimeSpan.FromSeconds(61));

        await Assert.ThrowsAsync<InvalidOperationException>(() => holds.ExpireAsync("aaaa", _ => throw new InvalidOperationException()));
        var ended = new List<string>();
        await holds.ExpireAsync("aaaa", h =>
        {
            ended.Add(h.HeldName);
            return Task.CompletedTask;
        });

        Assert.Equal(["a.md~", "b.md~"], ended.Order(StringComparer.Ordinal));
        Assert.Equal(["c.md"], holds.Hidden("bbbb", "v"));
    }

    [Fact]
    public async Task Case5_overwrite_t_onto_a_folder_trashes_what_was_in_it()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/a.md", "# A");
        await dav.Owner.CreateAsync("Ideas/b.md", "# B");
        await dav.Owner.CreateAsync("Kept/a.md", "# Old A");
        await dav.Owner.CreateAsync("Kept/gone.md", "# Gone");

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/Ideas/", headers: To("Kept/", overwrite: "T"));

        Assert.Equal(HttpStatusCode.NoContent, move.StatusCode);
        Assert.Equal("# A", await NoteAsync(dav.Owner, "Kept/a.md"));
        Assert.Equal("# B", await NoteAsync(dav.Owner, "Kept/b.md"));
        Assert.Null(await NoteAsync(dav.Owner, "Kept/gone.md"));
        var store = Store(dav.Owner);
        Assert.Empty(await store.ListNotesUnderAsync(P("/Work/Ideas"), Ct));
        Assert.Equal(["/Work/Kept/a.md", "/Work/Kept/gone.md"], (await store.ListTrashAsync(P("/Work/Kept"), Ct)).Select(t => t.Note.Path.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Case5_overwrite_f_is_412()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("a.md", "# A");
        await dav.Owner.CreateAsync("b.md", "# B");

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/a.md", headers: To("b.md", overwrite: "F"));

        Assert.Equal(HttpStatusCode.PreconditionFailed, move.StatusCode);
        Assert.Equal("# A", await NoteAsync(dav.Owner, "a.md"));
        Assert.Equal("# B", await NoteAsync(dav.Owner, "b.md"));
    }

    [Fact]
    public async Task Case5_overwrite_t_trashes_the_target_and_moves_in_one_batch()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("a.md", "# A\n");
        await dav.Owner.CreateAsync("b.md", "# B\n");
        await dav.Owner.CreateAsync("Other.md", "See [[a]]\n");

        HttpResponseMessage move;
        using (Ticking()) move = await dav.SendAsync(DavClient.Move, "/dav/Work/a.md", headers: To("b.md", overwrite: "T"));
        using var _ = move;

        Assert.Equal(HttpStatusCode.NoContent, move.StatusCode);
        Assert.Null(await NoteAsync(dav.Owner, "a.md"));
        Assert.Equal("# A\n", await NoteAsync(dav.Owner, "b.md"));
        Assert.Equal("See [[b]]\n", await NoteAsync(dav.Owner, "Other.md"));
        var store = Store(dav.Owner);
        var trashed = Assert.Single(await store.ListTrashAsync(P("/Work"), Ct));
        Assert.Equal("# B\n", trashed.Note.Content);
        var record = await store.FindMoveAsync(P("/Work/a.md"), Ct);
        Assert.Equal(trashed.DeletedAt, record!.MovedAt);
        Assert.Equal(trashed.Changeset?.Id, record.Changeset?.Id);
    }

    /// <summary>Every check of the move runs before the target goes: a refused move costs nothing.</summary>
    [Fact]
    public async Task A_refused_move_with_overwrite_t_keeps_the_target()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateFileAsync("shot.png", FileApiTests.Png(2, 2));
        await dav.Owner.CreateAsync("Board/Board.md", "# Board\n");
        await dav.Owner.CreateAsync("plan.md", "# Plan");

        using var format = await dav.SendAsync(DavClient.Move, "/dav/Work/shot.png", headers: To("plan.md", overwrite: "T"));
        using var structural = await dav.SendAsync(DavClient.Move, "/dav/Work/plan.md", headers: To("Board/Board.md", overwrite: "T"));

        Assert.Equal(HttpStatusCode.BadRequest, format.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, structural.StatusCode);
        Assert.Equal("# Plan", await NoteAsync(dav.Owner, "plan.md"));
        Assert.Equal("# Board\n", await NoteAsync(dav.Owner, "Board/Board.md"));
        Assert.Empty(await Store(dav.Owner).ListTrashAsync(P("/Work"), Ct));
    }

    [Fact]
    public async Task A_folder_onto_a_parking_name_is_403()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/a.md", "# A");

        using var tilde = await dav.SendAsync(DavClient.Move, "/dav/Work/Ideas/", headers: To("Ideas~/"));
        using var dot = await dav.SendAsync(DavClient.Move, "/dav/Work/Ideas/", headers: To(".Ideas/"));

        Assert.Equal(HttpStatusCode.Forbidden, tilde.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, dot.StatusCode);
        Assert.Equal("# A", await NoteAsync(dav.Owner, "Ideas/a.md"));
        Assert.Contains("/dav/Work/Ideas/", await ListingAsync(dav));
    }

    [Fact]
    public async Task A_parked_folder_onto_a_real_path_is_403()
    {
        var dav = await DavClient.NewAsync(Api);
        using (var mkcol = await dav.SendAsync(DavClient.Mkcol, "/dav/Work/.stuff")) Assert.Equal(HttpStatusCode.Created, mkcol.StatusCode);
        await ParkAsync(dav, ".stuff/a.md", Text("# A"));

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/.stuff/", headers: To("Stuff/"));

        Assert.Equal(HttpStatusCode.Forbidden, move.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(dav, HttpMethod.Get, "/dav/Work/.stuff/a.md"));
        Assert.Empty(await Store(dav.Owner).ListNotesUnderAsync(P("/Work"), Ct));
    }

    [Fact]
    public async Task A_parked_non_utf8_file_onto_a_md_path_is_415_and_stays_parked()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("plan.md", "# Plan");
        await ParkAsync(dav, ".latin.tmp", Bytes([0x43, 0x61, 0x66, 0xE9, 0x0A]));

        using var free = await dav.SendAsync(DavClient.Move, "/dav/Work/.latin.tmp", headers: To("latin.md"));
        using var over = await dav.SendAsync(DavClient.Move, "/dav/Work/.latin.tmp", headers: To("plan.md"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, free.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, over.StatusCode);
        Assert.Null(await NoteAsync(dav.Owner, "latin.md"));
        Assert.Equal("# Plan", await NoteAsync(dav.Owner, "plan.md"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(dav, HttpMethod.Get, "/dav/Work/.latin.tmp"));
    }

    [Fact]
    public async Task Move_across_vaults_is_502()
    {
        var owner = await RestClient.NewAsync(Api, vault: "Work");
        using (var personal = await owner.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Personal" })) Assert.Equal(HttpStatusCode.Created, personal.StatusCode);
        var dav = await DavClient.NewAsync(Api, owner, vaults: new Dictionary<string, string> { ["Work"] = "write", ["Personal"] = "write" });
        await owner.CreateAsync("a.md", "# A");

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/a.md", headers: new Dictionary<string, string> { ["Destination"] = "https://localhost/dav/Personal/a.md" });

        Assert.Equal(HttpStatusCode.BadGateway, move.StatusCode);
        Assert.Equal("# A", await NoteAsync(owner, "a.md"));
    }

    [Theory]
    [InlineData("https://evil.example/dav/Work/b.md")]
    [InlineData("https://localhost/api/x")]
    public async Task Move_with_a_foreign_destination_is_502(string destination)
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("a.md", "# A");

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/a.md", headers: new Dictionary<string, string> { ["Destination"] = destination });

        Assert.Equal(HttpStatusCode.BadGateway, move.StatusCode);
        Assert.Equal("# A", await NoteAsync(dav.Owner, "a.md"));
    }

    [Theory]
    [InlineData("/dav/Work/", "https://localhost/dav/Work/Inner/")]
    [InlineData("/dav/", "https://localhost/dav/Work/Inner/")]
    public async Task Move_of_a_vault_is_403(string source, string destination)
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("a.md", "# A");

        using var move = await dav.SendAsync(DavClient.Move, source, headers: new Dictionary<string, string> { ["Destination"] = destination });

        Assert.Equal(HttpStatusCode.Forbidden, move.StatusCode);
        Assert.Equal("# A", await NoteAsync(dav.Owner, "a.md"));
    }

    [Fact]
    public async Task Move_with_read_is_403()
    {
        var dav = await DavClient.NewAsync(Api, right: "read");
        await dav.Owner.CreateAsync("a.md", "# A");

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/a.md", headers: To("b.md"));

        Assert.Equal(HttpStatusCode.Forbidden, move.StatusCode);
        Assert.Equal("# A", await NoteAsync(dav.Owner, "a.md"));
    }

    [Fact]
    public async Task Parked_onto_parked_is_a_parking_move()
    {
        var dav = await DavClient.NewAsync(Api);
        await ParkAsync(dav, ".a.tmp", Text("junk"));

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/.a.tmp", headers: To(".b.tmp"));

        Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        using (var get = await dav.SendAsync(HttpMethod.Get, "/dav/Work/.b.tmp")) Assert.Equal("junk", await get.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(dav, HttpMethod.Get, "/dav/Work/.a.tmp"));
        Assert.Empty(await Store(dav.Owner).ListNotesUnderAsync(P("/Work"), Ct));
    }

    [Fact]
    public async Task Move_of_UPPER_MD_moves_the_md_note_and_onto_UPPER_MD_writes_md()
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Plan.md", "# Plan");
        await ParkAsync(dav, ".notes.tmp", Text("# Notes"));

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/Plan.MD", headers: To("Done.MD"));
        using var parked = await dav.SendAsync(DavClient.Move, "/dav/Work/.notes.tmp", headers: To("Notes.MD"));

        Assert.Equal(HttpStatusCode.Created, move.StatusCode);
        Assert.Null(await NoteAsync(dav.Owner, "Plan.md"));
        Assert.Equal("# Plan", await NoteAsync(dav.Owner, "Done.md"));
        Assert.Equal(HttpStatusCode.Created, parked.StatusCode);
        Assert.Equal("# Notes", await NoteAsync(dav.Owner, "Notes.md"));
    }
}
