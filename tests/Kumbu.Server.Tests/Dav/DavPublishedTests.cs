// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Publishing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using static Kumbu.Server.Tests.Api.RestClient;

namespace Kumbu.Server.Tests.Dav;

/// <summary>What WebDAV does with a published note (WebDAV design §3.6 cases 6 and 7): a share never may withdraw one
/// (publish design §3.7), so its <c>DELETE</c> holds the note instead of trashing it, a file that follows within the
/// minute is a content write, and without one the note reappears and the owner's activity says the share tried; a
/// <c>MOVE</c> onto a backup name ends the same way, one into an unpublishable folder is refused, any other keeps the
/// publication.</summary>
[Collection("app")]
public sealed class DavPublishedTests(KumbuAppFactory app) : IAsyncDisposable
{
    private const string Root = "https://localhost/dav/Work/";

    private const string Attempt = "“Work PC (dav)” tried to remove /Work/Ideas/plan.md; it is published, so it stays.";

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

    private PublicationStore Publishing => Api.Services.GetRequiredService<PublicationStore>();

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private static readonly WriterRef BySession = WriterRef.ForOwner(WriterRef.Session, null);

    private static Dictionary<string, string> To(string path) => new() { ["Destination"] = Root + path };

    /// <summary>The owner's own store, as her session opens it, and her access to Work.</summary>
    private async Task<(EncryptedNoteStore Store, VaultAccess Access)> OwnerAsync(RestClient owner)
    {
        var store = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(owner.User.Id, owner.UserKey));
        return (store, (await store.UnlockedVaultsAsync(Ct)).Single(a => a.Name == "Work"));
    }

    /// <summary>A share of Alice's Work, with <c>Ideas/plan.md</c> published by her session; the publication's code.</summary>
    private async Task<(DavClient Dav, string Code)> PublishedAsync(params string[] others)
    {
        var dav = await DavClient.NewAsync(Api);
        await dav.Owner.CreateAsync("Ideas/plan.md", "# Plan\n");
        foreach (var other in others) await dav.Owner.CreateAsync(other, $"# {other}\n");
        var (store, access) = await OwnerAsync(dav.Owner);
        var published = await Api.Services.GetRequiredService<Publications>().PublishAsync(
            access, store, P("/Work/Ideas/plan.md"), dav.Owner.User.Id, dav.Owner.Username, BySession, KumbuAppFactory.PublicUrl, Ct);
        return (dav, ShareCode.Parse(published.Link[(published.Link.IndexOf('#') + 1)..])!);
    }

    private async Task<PublishingRecord> RecordAsync(RestClient owner) => (await Publishing.ReadAsync(null, (await OwnerAsync(owner)).Access.Vault, Ct)).Record;

    /// <summary>Still published at <paramref name="note"/>, under the same code, and nothing of it withdrawn.</summary>
    private async Task AssertPublishedAsync(RestClient owner, string code, string note = "/Work/Ideas/plan.md")
    {
        Assert.NotNull(await Publishing.OpenCopyAsync(code, Ct));
        var record = await RecordAsync(owner);
        Assert.Equal(note, Assert.Single(record.Publications).Note);
        Assert.DoesNotContain(record.Log, l => l.Kind == PublishingKinds.Withdrawn);
    }

    private static async Task<string?> NoteAsync(RestClient owner, string path)
    {
        using var response = await owner.GetAsync(owner.Url(path));
        return response.StatusCode == HttpStatusCode.NotFound ? null : (await JsonAsync(response))["content"]!.GetValue<string>();
    }

    private static async Task<List<string>> ListingAsync(DavClient dav, string folder = "/dav/Work/Ideas/") =>
        DavAssert.Hrefs(await DavAssert.MultistatusAsync(await dav.PropfindAsync(folder, depth: "1")));

    private static async Task<string[]> ActivityAsync(RestClient owner)
    {
        using var response = await owner.GetAsync($"/api/vaults/{Uri.EscapeDataString(owner.Vault)}/activity");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await JsonAsync(response))["events"]!.AsArray().Select(e => e!["text"]!.GetValue<string>())];
    }

    private static async Task<HttpStatusCode> SendAsync(DavClient dav, HttpMethod method, string path, HttpContent? content = null, Dictionary<string, string>? headers = null)
    {
        using var response = await dav.SendAsync(method, path, content, headers);
        return response.StatusCode;
    }

    /// <summary>Any request of the share: the one moment its holds whose minute is up end.</summary>
    private static async Task NextRequestAsync(DavClient dav)
    {
        using var response = await dav.PropfindAsync("/dav/", depth: "0");
    }

    [Fact]
    public async Task Delete_of_a_published_note_answers_204_and_hides_it()
    {
        var (dav, code) = await PublishedAsync("Ideas/other.md");

        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(dav, HttpMethod.Delete, "/dav/Work/Ideas/plan.md"));

        var listing = await ListingAsync(dav);
        Assert.DoesNotContain("/dav/Work/Ideas/plan.md", listing);
        Assert.Contains("/dav/Work/Ideas/other.md", listing);
        Assert.Equal(HttpStatusCode.NotFound, await SendAsync(dav, HttpMethod.Get, "/dav/Work/Ideas/plan.md"));
        Assert.Equal("# Plan\n", await NoteAsync(dav.Owner, "Ideas/plan.md"));
        Assert.Empty(await (await OwnerAsync(dav.Owner)).Store.ListTrashAsync(P("/Work"), Ct));
        await AssertPublishedAsync(dav.Owner, code);
    }

    [Fact]
    public async Task A_file_arriving_within_60_seconds_is_a_content_write_and_the_publication_stays()
    {
        var (dav, code) = await PublishedAsync();
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(dav, HttpMethod.Delete, "/dav/Work/Ideas/plan.md"));
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.Created, await SendAsync(dav, HttpMethod.Put, "/dav/Work/Ideas/plan.md", new ByteArrayContent(Encoding.UTF8.GetBytes("# Plan, edited\n"))));

        Assert.Equal("# Plan, edited\n", await NoteAsync(dav.Owner, "Ideas/plan.md"));
        var (store, _) = await OwnerAsync(dav.Owner);
        Assert.Equal("# Plan\n", Assert.Single((await store.GetAsync(P("/Work/Ideas/plan.md"), Ct))!.History).Content);
        Assert.Contains("/dav/Work/Ideas/plan.md", await ListingAsync(dav));
        // The minute passes: nothing more happens to it.
        _time.Advance(TimeSpan.FromSeconds(61));
        await NextRequestAsync(dav);
        Assert.Equal("# Plan, edited\n", await NoteAsync(dav.Owner, "Ideas/plan.md"));
        Assert.Empty(await store.ListTrashAsync(P("/Work"), Ct));
        await AssertPublishedAsync(dav.Owner, code);
        Assert.DoesNotContain(Attempt, await ActivityAsync(dav.Owner));
    }

    [Fact]
    public async Task Without_arrival_the_published_note_reappears_and_nothing_is_trashed()
    {
        var (dav, code) = await PublishedAsync();
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(dav, HttpMethod.Delete, "/dav/Work/Ideas/plan.md"));
        _time.Advance(TimeSpan.FromSeconds(59));
        await NextRequestAsync(dav);
        Assert.DoesNotContain("/dav/Work/Ideas/plan.md", await ListingAsync(dav));

        _time.Advance(TimeSpan.FromSeconds(2));
        await NextRequestAsync(dav);

        Assert.Contains("/dav/Work/Ideas/plan.md", await ListingAsync(dav));
        Assert.Equal("# Plan\n", await NoteAsync(dav.Owner, "Ideas/plan.md"));
        Assert.Empty(await (await OwnerAsync(dav.Owner)).Store.ListTrashAsync(P("/Work"), Ct));
        await AssertPublishedAsync(dav.Owner, code);
        // A file after the minute is an ordinary write of the note, which is there again.
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(dav, HttpMethod.Put, "/dav/Work/Ideas/plan.md", new ByteArrayContent(Encoding.UTF8.GetBytes("# Later\n"))));
        await AssertPublishedAsync(dav.Owner, code);
    }

    [Fact]
    public async Task The_owners_activity_records_the_attempt()
    {
        var (dav, _) = await PublishedAsync();
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(dav, HttpMethod.Delete, "/dav/Work/Ideas/plan.md"));
        // Recorded when the hold ends, not before: a file may still come.
        Assert.DoesNotContain(Attempt, await ActivityAsync(dav.Owner));

        _time.Advance(TimeSpan.FromSeconds(61));
        await NextRequestAsync(dav);

        var texts = await ActivityAsync(dav.Owner);
        Assert.Equal(Attempt, texts[0]);
        Assert.Single(texts, t => t == Attempt);
        var record = await RecordAsync(dav.Owner);
        Assert.Equal((PublishingKinds.Kept, "/Work/Ideas/plan.md", "Work PC (dav)"), (record.Log[^1].Kind, record.Log[^1].Note, record.Log[^1].By));
        // Another request ends nothing more.
        await NextRequestAsync(dav);
        Assert.Single(await ActivityAsync(dav.Owner), t => t == Attempt);
    }

    /// <summary>A hold its share doesn't come back for within the parking TTL is forgotten (<see cref="Kumbu.Server.Dav.DavHolds"/>):
    /// the note is simply there again, and no request ended it, so nothing is recorded.</summary>
    [Fact]
    public async Task A_forgotten_hold_reappears_and_records_nothing()
    {
        var (dav, code) = await PublishedAsync();
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(dav, HttpMethod.Delete, "/dav/Work/Ideas/plan.md"));

        _time.Advance(Kumbu.Server.Dav.DavRules.ParkingTtl + TimeSpan.FromSeconds(1));
        await NextRequestAsync(dav);

        Assert.Contains("/dav/Work/Ideas/plan.md", await ListingAsync(dav));
        await AssertPublishedAsync(dav.Owner, code);
        Assert.DoesNotContain(Attempt, await ActivityAsync(dav.Owner));
    }

    /// <summary>A member's share is held the same way, and the owner learns of it without a label, which members don't
    /// store.</summary>
    [Fact]
    public async Task A_members_share_is_held_too_and_the_owner_sees_the_attempt()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Ideas/plan.md", "# Plan\n");
        var (store, access) = await OwnerAsync(alice);
        var published = await Api.Services.GetRequiredService<Publications>().PublishAsync(
            access, store, P("/Work/Ideas/plan.md"), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct);
        var code = ShareCode.Parse(published.Link[(published.Link.IndexOf('#') + 1)..])!;
        var bob = await RestClient.NewAsync(Api, vault: "Garden");
        await alice.ShareWithAsync(bob, "alices-work");
        var dav = await DavClient.NewAsync(Api, bob, vaults: new Dictionary<string, string> { ["alices-work"] = "write" });

        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(dav, HttpMethod.Delete, "/dav/alices-work/Ideas/plan.md"));
        Assert.DoesNotContain("/dav/alices-work/Ideas/plan.md", await ListingAsync(dav, "/dav/alices-work/Ideas/"));
        _time.Advance(TimeSpan.FromSeconds(61));
        await NextRequestAsync(dav);

        Assert.Contains("/dav/alices-work/Ideas/plan.md", await ListingAsync(dav, "/dav/alices-work/Ideas/"));
        Assert.Equal("# Plan\n", await NoteAsync(alice, "Ideas/plan.md"));
        await AssertPublishedAsync(alice, code);
        Assert.Equal("A member's WebDAV share tried to remove /Work/Ideas/plan.md; it is published, so it stays.", (await ActivityAsync(alice))[0]);
    }

    [Fact]
    public async Task Delete_of_a_folder_removes_the_rest_and_keeps_published_notes_and_their_folders()
    {
        var dav = await DavClient.NewAsync(Api);
        foreach (var folder in new[] { "Ideas/", "Ideas/Deep/", "Ideas/Empty/" }) Assert.Equal(HttpStatusCode.Created, await SendAsync(dav, DavClient.Mkcol, "/dav/Work/" + folder));
        await dav.Owner.CreateAsync("Ideas/Deep/plan.md", "# Plan\n");
        await dav.Owner.CreateAsync("Ideas/Deep/b.md", "# B\n");
        await dav.Owner.CreateAsync("Ideas/other.md", "# Other\n");
        await dav.Owner.CreateAsync("Kept.md", "# Kept\n");
        var (store, access) = await OwnerAsync(dav.Owner);
        var published = await Api.Services.GetRequiredService<Publications>().PublishAsync(
            access, store, P("/Work/Ideas/Deep/plan.md"), dav.Owner.User.Id, dav.Owner.Username, BySession, KumbuAppFactory.PublicUrl, Ct);
        var code = ShareCode.Parse(published.Link[(published.Link.IndexOf('#') + 1)..])!;

        Assert.Equal(HttpStatusCode.NoContent, await SendAsync(dav, HttpMethod.Delete, "/dav/Work/Ideas/"));

        Assert.Equal("# Plan\n", await NoteAsync(dav.Owner, "Ideas/Deep/plan.md"));
        Assert.Null(await NoteAsync(dav.Owner, "Ideas/Deep/b.md"));
        Assert.Null(await NoteAsync(dav.Owner, "Ideas/other.md"));
        Assert.Equal("# Kept\n", await NoteAsync(dav.Owner, "Kept.md"));
        // The rest in one batch: one removal time.
        var trash = await store.ListTrashAsync(P("/Work"), Ct);
        Assert.Equal(["/Work/Ideas/Deep/b.md", "/Work/Ideas/other.md"], trash.Select(t => t.Note.Path.Value).Order(StringComparer.Ordinal));
        Assert.Single(trash.Select(t => t.DeletedAt).Distinct());
        // The folders above the published note keep their markers; the empty one goes.
        Assert.NotNull(await store.GetAttachmentAsync(FolderMarker.In(P("/Work/Ideas")), Ct));
        Assert.NotNull(await store.GetAttachmentAsync(FolderMarker.In(P("/Work/Ideas/Deep")), Ct));
        Assert.Null(await store.GetAttachmentAsync(FolderMarker.In(P("/Work/Ideas/Empty")), Ct));
        Assert.DoesNotContain("/dav/Work/Ideas/Deep/plan.md", await ListingAsync(dav, "/dav/Work/Ideas/Deep/"));
        await AssertPublishedAsync(dav.Owner, code, "/Work/Ideas/Deep/plan.md");

        _time.Advance(TimeSpan.FromSeconds(61));
        await NextRequestAsync(dav);

        Assert.Contains("/dav/Work/Ideas/Deep/plan.md", await ListingAsync(dav, "/dav/Work/Ideas/Deep/"));
        Assert.Equal(2, (await store.ListTrashAsync(P("/Work"), Ct)).Count);
        await AssertPublishedAsync(dav.Owner, code, "/Work/Ideas/Deep/plan.md");
    }

    [Fact]
    public async Task Move_of_a_published_note_onto_a_backup_name_ends_as_case_6()
    {
        var (dav, code) = await PublishedAsync();

        Assert.Equal(HttpStatusCode.Created, await SendAsync(dav, DavClient.Move, "/dav/Work/Ideas/plan.md", headers: To("Ideas/plan.md~")));
        var listing = await ListingAsync(dav);
        Assert.Contains("/dav/Work/Ideas/plan.md~", listing);
        Assert.DoesNotContain("/dav/Work/Ideas/plan.md", listing);

        _time.Advance(TimeSpan.FromSeconds(61));
        await NextRequestAsync(dav);

        listing = await ListingAsync(dav);
        Assert.Contains("/dav/Work/Ideas/plan.md", listing);
        Assert.DoesNotContain("/dav/Work/Ideas/plan.md~", listing);
        Assert.Equal("# Plan\n", await NoteAsync(dav.Owner, "Ideas/plan.md"));
        Assert.Empty(await (await OwnerAsync(dav.Owner)).Store.ListTrashAsync(P("/Work"), Ct));
        await AssertPublishedAsync(dav.Owner, code);
        Assert.Equal(Attempt, (await ActivityAsync(dav.Owner))[0]);
    }

    [Fact]
    public async Task Move_of_a_published_note_into_an_unpublishable_folder_is_403_with_the_reason()
    {
        var (dav, code) = await PublishedAsync("Done/old.md");
        var (store, access) = await OwnerAsync(dav.Owner);
        await Api.Services.GetRequiredService<Publications>().SetUnpublishableAsync(access, store, P("/Work/Done"), true, dav.Owner.Username, BySession, Ct);

        using var move = await dav.SendAsync(DavClient.Move, "/dav/Work/Ideas/plan.md", headers: To("Done/plan.md"));

        Assert.Equal(HttpStatusCode.Forbidden, move.StatusCode);
        Assert.Equal(
            "/Work/Ideas/plan.md is published, and this key may not publish in Work, so it can't withdraw it either; withdraw it first with a key that may publish (publish rm) or in the web app",
            await move.Content.ReadAsStringAsync(Ct));
        Assert.Equal("# Plan\n", await NoteAsync(dav.Owner, "Ideas/plan.md"));
        Assert.Null(await NoteAsync(dav.Owner, "Done/plan.md"));
        await AssertPublishedAsync(dav.Owner, code);
    }

    [Fact]
    public async Task Any_other_move_keeps_the_publication()
    {
        var (dav, code) = await PublishedAsync("Archive/old.md");

        Assert.Equal(HttpStatusCode.Created, await SendAsync(dav, DavClient.Move, "/dav/Work/Ideas/plan.md", headers: To("Archive/plan.md")));

        Assert.Null(await NoteAsync(dav.Owner, "Ideas/plan.md"));
        Assert.Equal("# Plan\n", await NoteAsync(dav.Owner, "Archive/plan.md"));
        await AssertPublishedAsync(dav.Owner, code, "/Work/Archive/plan.md");
        // A folder's move too.
        Assert.Equal(HttpStatusCode.Created, await SendAsync(dav, DavClient.Move, "/dav/Work/Archive/", headers: To("Old/")));
        await AssertPublishedAsync(dav.Owner, code, "/Work/Old/plan.md");
    }
}
