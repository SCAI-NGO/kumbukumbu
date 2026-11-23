// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;
using Kumbu.Core.Sh;
using Kumbu.Server.Accounts;
using Kumbu.Server.Attachments;
using Kumbu.Server.Publishing;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Attachments;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>
/// Publish 2.0 (spec 2026-11-18): a note with the notes it links to, to a depth; marks and the publisher's exclusions as
/// a hard blacklist, walked around and never through; what was seen is what goes; a note that is marked, moved under a
/// mark or removed leaves every copy at once, with the notes reached only through it.
/// </summary>
[Collection("app")]
public sealed class BundleTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Publications Publications => Api.Services.GetRequiredService<Publications>();

    private PublicationStore Store => Api.Services.GetRequiredService<PublicationStore>();

    private static readonly WriterRef BySession = WriterRef.ForOwner(WriterRef.Session, null);

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private async Task<(EncryptedNoteStore Notes, VaultAccess Access)> AliceAsync(RestClient alice, string vault = "Work")
    {
        var notes = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(alice.User.Id, alice.UserKey));
        return (notes, (await notes.UnlockedVaultsAsync(Ct)).Single(a => a.Name == vault));
    }

    /// <summary>Alice's Work with <paramref name="notes"/> (name, content).</summary>
    private async Task<RestClient> WorkAsync(params (string Name, string Content)[] notes)
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        foreach (var (name, content) in notes) await alice.CreateAsync(name, content);
        return alice;
    }

    private static async Task EditAsync(RestClient alice, string note, string content)
    {
        var version = (await alice.ReadAsync(note))["version"]!.GetValue<string>();
        using var put = await alice.SendAsync(HttpMethod.Put, alice.Url(note), new { content, version });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    private async Task<BundlePreview> PreviewAsync(RestClient alice, string note, int depth, params string[] without)
    {
        var (notes, access) = await AliceAsync(alice);
        return await Publications.PreviewAsync(access, notes, P(note), depth, [.. without.Select(P)], Ct);
    }

    private async Task<PublishedNote> PublishAsync(RestClient alice, string note, int depth, string[]? without = null, string[]? expected = null, Publications? with = null)
    {
        var (notes, access) = await AliceAsync(alice);
        return await (with ?? Publications).PublishAsync(
            access, notes, P(note), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct, depth, without?.Select(P).ToList(), expected);
    }

    private async Task<PublishedNote> UpdateAsync(RestClient alice, string note, int? depth = null, string[]? without = null, string[]? expected = null)
    {
        var (notes, access) = await AliceAsync(alice);
        return await Publications.UpdateAsync(access, notes, P(note), alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct, depth, without?.Select(P).ToList(), expected);
    }

    private async Task MarkAsync(RestClient alice, string path)
    {
        var (notes, access) = await AliceAsync(alice);
        await Publications.SetUnpublishableAsync(access, notes, P(path), true, alice.Username, BySession, Ct);
    }

    private static string CodeOf(PublishedNote published) => ShareCode.Parse(published.Link[(published.Link.IndexOf('#') + 1)..])!;

    private async Task<PublishedCopy> CopyAsync(PublishedNote published) => (await Store.OpenCopyAsync(CodeOf(published), Ct))!.Value.Copy;

    private static string[] Paths(BundlePreview preview) => [.. preview.Notes.Select(n => n.Path.Value)];

    private Publications Limited(Action<Publications>? _ = null, long? copyLimit = null, int? noteLimit = null)
    {
        var s = Api.Services;
        var made = new Publications(
            s.GetRequiredService<VaultCatalog>(), Store, s.GetRequiredService<IMongoDatabase>(), s.GetRequiredService<IOptions<KumbuOptions>>(), s.GetRequiredService<TimeProvider>())
        {
            CopyLimit = copyLimit ?? 64L * 1024 * 1024,
            NoteLimit = noteLimit ?? 500,
        };
        return made;
    }

    [Fact]
    public async Task Depth_0_is_the_note_alone()
    {
        var alice = await WorkAsync(("A.md", "[[B]]"), ("B.md", "b"));

        var preview = await PreviewAsync(alice, "/Work/A.md", 0);

        Assert.Equal(["/Work/A.md"], Paths(preview));
        Assert.Empty(preview.LeftOut);
    }

    [Fact]
    public async Task Depth_2_takes_links_of_links_in_walk_order()
    {
        var alice = await WorkAsync(("A.md", "[[B]] and [[E]]"), ("B.md", "[[C]]"), ("C.md", "[[D]]"), ("D.md", "d"), ("E.md", "e"));

        var preview = await PreviewAsync(alice, "/Work/A.md", 2);

        Assert.Equal(["/Work/A.md", "/Work/B.md", "/Work/E.md", "/Work/C.md"], Paths(preview));
        Assert.Equal([0, 1, 1, 2], preview.Notes.Select(n => n.Depth));
    }

    [Fact]
    public async Task A_marked_note_is_neither_taken_nor_walked_through()
    {
        var alice = await WorkAsync(("A.md", "[[M]]"), ("M.md", "[[X]]"), ("X.md", "x"));
        await MarkAsync(alice, "/Work/M.md");

        var preview = await PreviewAsync(alice, "/Work/A.md", 2);

        Assert.Equal(["/Work/A.md"], Paths(preview));
        Assert.Equal(
            [new BundleLeftOut(P("/Work/M.md"), BundleReasons.Unpublishable), new BundleLeftOut(P("/Work/X.md"), BundleReasons.Behind)],
            preview.LeftOut);
    }

    [Fact]
    public async Task A_note_reached_another_way_is_taken()
    {
        var alice = await WorkAsync(("A.md", "[[M]] [[X]]"), ("M.md", "[[X]]"), ("X.md", "x"));
        await MarkAsync(alice, "/Work/M.md");

        var preview = await PreviewAsync(alice, "/Work/A.md", 2);

        Assert.Equal(["/Work/A.md", "/Work/X.md"], Paths(preview));
        Assert.Equal(1, preview.Notes[1].Depth);
    }

    [Fact]
    public async Task A_marked_folder_blocks_its_notes()
    {
        var alice = await WorkAsync(("A.md", "[[Secret/S]] [[B]]"), ("Secret/S.md", "s"), ("B.md", "b"));
        await MarkAsync(alice, "/Work/Secret");

        var preview = await PreviewAsync(alice, "/Work/A.md", 1);

        Assert.Equal(["/Work/A.md", "/Work/B.md"], Paths(preview));
        Assert.Equal([new BundleLeftOut(P("/Work/Secret/S.md"), BundleReasons.Unpublishable)], preview.LeftOut);
    }

    [Fact]
    public async Task Without_blocks_like_a_mark()
    {
        var alice = await WorkAsync(("A.md", "[[B]]"), ("B.md", "[[C]]"), ("C.md", "c"));

        var preview = await PreviewAsync(alice, "/Work/A.md", 2, "/Work/B.md");

        Assert.Equal(["/Work/A.md"], Paths(preview));
        Assert.Equal([new BundleLeftOut(P("/Work/B.md"), BundleReasons.Without), new BundleLeftOut(P("/Work/C.md"), BundleReasons.Behind)], preview.LeftOut);
    }

    [Fact]
    public async Task A_pdf_embed_is_listed()
    {
        var alice = await WorkAsync(("A.md", "[[B]]"));
        await alice.CreateFileAsync("manual.pdf", "%PDF-1.4\n%%EOF\n"u8.ToArray());
        await alice.CreateAsync("B.md", "![[manual.pdf]]");

        var preview = await PreviewAsync(alice, "/Work/A.md", 1);

        Assert.Equal(["manual.pdf", PublishOutcome.PdfsNotYet], preview.Plain);
    }

    [Fact]
    public async Task Depth_outside_0_to_5_is_invalid()
    {
        var alice = await WorkAsync(("A.md", "a"));

        var e = await Assert.ThrowsAsync<PublishRefusedException>(() => PreviewAsync(alice, "/Work/A.md", 6));
        Assert.Equal(ShellErrorKind.Invalid, e.Kind);
        await Assert.ThrowsAsync<PublishRefusedException>(() => PublishAsync(alice, "/Work/A.md", -1));
    }

    [Fact]
    public async Task More_than_the_note_limit_is_refused()
    {
        var alice = await WorkAsync(("A.md", "[[B]] [[C]]"), ("B.md", "b"), ("C.md", "c"));

        Assert.True((await PreviewAsync(alice, "/Work/A.md", 1)).TooMany is false);
        var e = await Assert.ThrowsAsync<PublishRefusedException>(() => PublishAsync(alice, "/Work/A.md", 1, with: Limited(noteLimit: 2)));
        Assert.Equal(ShellErrorKind.Invalid, e.Kind);
    }

    [Fact]
    public async Task A_member_never_previews()
    {
        var alice = await WorkAsync(("A.md", "a"));
        var bob = await RestClient.NewAsync(Api, vault: "Garten");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var notes = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(bob.User.Id, bob.UserKey));
        var access = (await notes.UnlockedVaultsAsync(Ct)).Single(a => a.Name == "alices-work");

        var e = await Assert.ThrowsAsync<PublishRefusedException>(() => Publications.PreviewAsync(access, notes, P("/alices-work/A.md"), 0, [], Ct));
        Assert.Equal(Refusals.NotOwner, e.Code);
    }

    [Fact]
    public async Task Publishing_depth_1_carries_the_linked_notes_with_links_inside_the_copy()
    {
        var alice = await WorkAsync(("A.md", "See [[B#Big idea|the idea]] and [[Elsewhere]]."), ("B.md", "# Bee\n\n## Big idea\n\nb"));

        var published = await PublishAsync(alice, "/Work/A.md", 1);

        var copy = await CopyAsync(published);
        Assert.Equal("See [the idea](#kn-1/Big%20idea) and Elsewhere.", copy.Markdown.TrimEnd());
        Assert.NotNull(copy.Notes);
        Assert.Equal([0, 1], copy.Notes!.Select(n => n.Id));
        Assert.Equal("Bee", copy.Notes[1].Title);
        Assert.Equal([[0, 1]], copy.Edges!);
        Assert.Equal(["/Work/A.md", "/Work/B.md"], published.Notes!.Select(n => n.Path.Value));
        var (_, access) = await AliceAsync(alice);
        var entry = Assert.Single((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications);
        Assert.Equal(1, entry.Depth);
        Assert.Equal("/Work/B.md", Assert.Single(entry.Members!).Note);
    }

    [Fact]
    public async Task Images_are_numbered_across_the_bundle_and_copied_once()
    {
        var alice = await WorkAsync();
        await alice.CreateFileAsync("a.jpg", ImageMetadataTests.JpegWithGps(4));
        await alice.CreateFileAsync("b.jpg", ImageMetadataTests.JpegWithGps(5));
        await alice.CreateAsync("A.md", "![[a.jpg]] [[B]]");
        await alice.CreateAsync("B.md", "![[b.jpg]] ![[a.jpg]]");

        var copy = await CopyAsync(await PublishAsync(alice, "/Work/A.md", 1));

        Assert.Equal(2, copy.Files.Count);
        Assert.Equal([0], copy.Notes![0].Files);
        Assert.Equal([1, 0], copy.Notes[1].Files);
        Assert.Contains("](f/0)", copy.Notes[1].Markdown);
    }

    [Fact]
    public async Task An_expected_set_that_differs_publishes_nothing_and_gives_the_new_preview()
    {
        var alice = await WorkAsync(("A.md", "[[B]] [[C]]"), ("B.md", "b"), ("C.md", "c"));

        var e = await Assert.ThrowsAsync<BundleChangedException>(() => PublishAsync(alice, "/Work/A.md", 1, expected: ["/Work/A.md", "/Work/B.md"]));

        Assert.Equal(["/Work/A.md", "/Work/B.md", "/Work/C.md"], e.Now.Set);
        var (_, access) = await AliceAsync(alice);
        Assert.Empty((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications);
        var published = await PublishAsync(alice, "/Work/A.md", 1, expected: ["/Work/C.md", "/Work/A.md", "/Work/B.md"]);
        Assert.Equal(3, published.Notes!.Count);
    }

    [Fact]
    public async Task Update_keeps_the_depth_and_without_and_reports_added_and_removed()
    {
        var alice = await WorkAsync(("A.md", "[[B]] [[C]]"), ("B.md", "b"), ("C.md", "c"), ("D.md", "d"));
        await PublishAsync(alice, "/Work/A.md", 1, without: ["/Work/C.md"]);
        await EditAsync(alice, "A.md", "[[C]] [[D]]");

        var updated = await UpdateAsync(alice, "/Work/A.md");

        Assert.Equal(["/Work/A.md", "/Work/D.md"], updated.Notes!.Select(n => n.Path.Value));
        Assert.Equal([P("/Work/D.md")], updated.Added);
        Assert.Equal([P("/Work/B.md")], updated.Removed);
        var again = await UpdateAsync(alice, "/Work/A.md", depth: 0);
        Assert.Equal(["/Work/A.md"], again.Notes!.Select(n => n.Path.Value));
        Assert.Null((await CopyAsync(again)).Notes);
    }

    [Fact]
    public async Task The_activity_says_how_many_notes()
    {
        var alice = await WorkAsync(("A.md", "[[B]]"), ("B.md", "b"), ("C.md", "c"));
        await PublishAsync(alice, "/Work/A.md", 1);
        await EditAsync(alice, "A.md", "[[B]] [[C]]");
        await UpdateAsync(alice, "/Work/A.md");

        var (_, access) = await AliceAsync(alice);
        var log = (await Store.ReadAsync(null, access.Vault, Ct)).Record.Log;
        Assert.Equal("You published /Work/A.md with 2 notes.", VaultEvents.TextFor(VaultEvents.NotePublished, true, "You", "Work", null, null, note: "/Work/A.md", notes: log[0].Notes));
        Assert.Equal((3, 1, 0), (log[1].Notes, log[1].Added, log[1].Removed));
        Assert.Equal(
            "You updated the published copy of /Work/A.md: +1, −0 notes.",
            VaultEvents.TextFor(VaultEvents.PublicationUpdated, true, "You", "Work", null, null, note: "/Work/A.md", notes: 3, added: 1, removed: 0));
    }

    /// <summary>A record and a copy as a server before Publish 2.0 wrote them: no depth, notes left out or members, no
    /// notes or edges in the copy; the keys absent, not null.</summary>
    private async Task<string> PublishedBeforeBundlesAsync(RestClient alice, string note, string markdown)
    {
        var (notes, access) = await AliceAsync(alice);
        var (code, key) = (ShareCode.New(), VaultCrypto.NewKey());
        var version = (await notes.GetAsync(P(note), Ct))!.Version;
        var record = new PublishingRecord(
            [new PublicationEntry(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), note, code, key, DateTime.UtcNow, version, [], alice.Username, new WriterRefPayload(true, "session"))],
            [], []);
        var json = JsonSerializer.SerializeToNode(record)!;
        foreach (var entry in json["Publications"]!.AsArray()) foreach (var later in new[] { "Depth", "Without", "Members" }) entry!.AsObject().Remove(later);
        var seal = typeof(UnlockedVault).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Single(m => m.Name == "Seal" && m.IsGenericMethodDefinition).MakeGenericMethod(typeof(JsonNode));
        var catalog = Api.Services.GetRequiredService<VaultCatalog>();
        var id = ObjectId.GenerateNewId();
        await catalog.Publishing.InsertOneAsync(
            new SealedDocument { Id = id, Vault = access.Id, PathId = access.Vault.PublishingId(), Revision = 1, Blob = (byte[])seal.Invoke(access.Vault, [$"publishing:{access.Id}:{id}:1", json])! },
            cancellationToken: Ct);

        using (var session = await catalog.StartSessionAsync(Ct))
        {
            session.StartTransaction();
            await Store.InsertCopyAsync(session, access.Id, code, key, alice.User.Id, new PublishedCopy("A", markdown, DateTime.UtcNow, []), [], Ct);
            await session.CommitTransactionAsync(Ct);
        }

        var lookup = ShareCode.Lookup(code);
        var copy = Encoding.UTF8.GetBytes($$"""{"Title":"A","Markdown":{{JsonSerializer.Serialize(markdown)}},"SnapshotAt":"2026-01-02T03:04:05Z","Files":[]}""");
        await catalog.Publications.UpdateOneAsync(
            d => d.Lookup == lookup,
            Builders<PublicationDocument>.Update.Set(d => d.Blob, VaultCrypto.Seal(key, copy, $"publication:{Convert.ToHexStringLower(lookup)}:copy")),
            cancellationToken: Ct);
        return code;
    }

    [Fact]
    public async Task A_copy_stored_before_bundles_still_opens_and_updates_as_depth_0()
    {
        var alice = await WorkAsync(("A.md", "[[B]]"), ("B.md", "b"));
        var code = await PublishedBeforeBundlesAsync(alice, "/Work/A.md", "B\n");
        using var reader = new ReaderApiTests.Reader(Api, "203.0.113.72");

        using (var read = await reader.PostAsync(code))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var json = await RestClient.JsonAsync(read);
            Assert.Equal("B", json["markdown"]!.GetValue<string>().TrimEnd());
            Assert.Null(json["notes"]);
        }

        var (notes, access) = await AliceAsync(alice);
        var state = (await Publications.StateAsync(access, notes, P("/Work/A.md"), null, Ct))!;
        Assert.Equal((1, 0), (state.Notes, state.Depth));
        Assert.Empty(await Publications.BundledAsync(access, VaultPath.Root.Child("Work"), Ct));
        var updated = await UpdateAsync(alice, "/Work/A.md");
        Assert.Equal(["/Work/A.md"], updated.Notes!.Select(n => n.Path.Value));
        Assert.Null((await CopyAsync(updated)).Notes);
        var entry = Assert.Single((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications);
        Assert.Equal((null, null), (entry.Depth, entry.Members));
    }

    [Fact]
    public async Task Older_is_true_when_any_note_of_the_bundle_changed()
    {
        var alice = await WorkAsync(("A.md", "[[B]]"), ("B.md", "b"));
        await PublishAsync(alice, "/Work/A.md", 1);
        var (notes, access) = await AliceAsync(alice);
        Assert.False((await Publications.StateAsync(access, notes, P("/Work/A.md"), null, Ct))!.Older);

        await EditAsync(alice, "B.md", "b changed");

        Assert.True((await Publications.StateAsync(access, notes, P("/Work/A.md"), null, Ct))!.Older);
    }

    [Fact]
    public async Task A_bundle_over_the_image_limit_is_refused()
    {
        var alice = await WorkAsync();
        await alice.CreateFileAsync("a.jpg", ImageMetadataTests.JpegWithGps(6));
        await alice.CreateAsync("A.md", "![[a.jpg]] [[B]]");
        await alice.CreateAsync("B.md", "b");

        var e = await Assert.ThrowsAsync<PublishRefusedException>(() => PublishAsync(alice, "/Work/A.md", 1, with: Limited(copyLimit: 10)));
        Assert.Equal(ShellErrorKind.Invalid, e.Kind);
    }

    // Task 4: notes leave the copy at once.

    [Fact]
    public async Task Marking_a_bundle_note_takes_it_out_and_its_links_become_text()
    {
        var alice = await WorkAsync(("A.md", "See [[B]]."), ("B.md", "b"));
        var published = await PublishAsync(alice, "/Work/A.md", 1);

        await MarkAsync(alice, "/Work/B.md");

        // The note alone reads as a copy of one note: no list of notes, as a fresh copy of it has none.
        var copy = await CopyAsync(published);
        Assert.Null(copy.Notes);
        Assert.Null(copy.Edges);
        Assert.Equal("See B.", copy.Markdown.TrimEnd());
        var (_, access) = await AliceAsync(alice);
        var record = (await Store.ReadAsync(null, access.Vault, Ct)).Record;
        Assert.Null(Assert.Single(record.Publications).Members);
        Assert.Equal((PublishingKinds.Updated, WithdrawalReasons.Unpublishable, 1), (record.Log[^1].Kind, record.Log[^1].Reason, record.Log[^1].Removed));
    }

    [Fact]
    public async Task After_a_note_leaves_the_others_stand_as_a_fresh_walk_numbers_them()
    {
        // D reaches Q before N does; without D a walk meets P first, by N's links in their order.
        var alice = await WorkAsync(("A.md", "[[D]] [[N]]"), ("D.md", "[[Q]]"), ("N.md", "[[P]] [[Q]]"), ("P.md", "p"), ("Q.md", "q"));
        var published = await PublishAsync(alice, "/Work/A.md", 2);

        await MarkAsync(alice, "/Work/D.md");

        var pruned = await CopyAsync(published);
        var fresh = await CopyAsync(await UpdateAsync(alice, "/Work/A.md"));
        Assert.Equal(fresh.Notes!.Select(n => (n.Id, n.Title, n.Markdown)), pruned.Notes!.Select(n => (n.Id, n.Title, n.Markdown)));
        Assert.Equal(fresh.Edges!.Select(e => (e[0], e[1])), pruned.Edges!.Select(e => (e[0], e[1])));
    }

    [Fact]
    public async Task Notes_reached_only_through_it_leave_too()
    {
        var alice = await WorkAsync(("A.md", "[[B]] [[D]]"), ("B.md", "[[C]]"), ("C.md", "c"), ("D.md", "[[C]] [[E]]"), ("E.md", "e"));
        var published = await PublishAsync(alice, "/Work/A.md", 2);

        await MarkAsync(alice, "/Work/D.md");

        // C stays: B still reaches it; E was reached only through D.
        Assert.Equal(["A", "B", "C"], (await CopyAsync(published)).Notes!.Select(n => n.Title));
    }

    [Fact]
    public async Task After_a_prune_the_notes_and_files_are_numbered_without_gaps()
    {
        var alice = await WorkAsync();
        await alice.CreateFileAsync("a.jpg", ImageMetadataTests.JpegWithGps(4));
        await alice.CreateFileAsync("b.jpg", ImageMetadataTests.JpegWithGps(5));
        await alice.CreateFileAsync("c.jpg", ImageMetadataTests.JpegWithGps(6));
        await alice.CreateAsync("A.md", "![[a.jpg]] [[B]] [[C]]");
        await alice.CreateAsync("B.md", "![[b.jpg]]");
        await alice.CreateAsync("C.md", "![[c.jpg]] back to [[A]]");
        var published = await PublishAsync(alice, "/Work/A.md", 1);
        var c = (await Store.OpenFileAsync(CodeOf(published), 2, Ct))!.Value.Bytes;

        await MarkAsync(alice, "/Work/B.md");

        // The reader can't tell a note or an image left: what stays is numbered as if it had never been there.
        var copy = await CopyAsync(published);
        Assert.Equal([(0, "A"), (1, "C")], copy.Notes!.Select(n => (n.Id, n.Title)));
        Assert.Equal("![a.jpg](f/0) B [C](#kn-1)", copy.Markdown.TrimEnd());
        Assert.Equal("![c.jpg](f/0) back to [A](#kn-0)", copy.Notes![1].Markdown.TrimEnd());
        Assert.Equal([1], copy.Notes[1].Files);
        Assert.Equal([0, 1], copy.Files.Select(f => f.Index));
        Assert.Equal([[0, 1], [1, 0]], copy.Edges!);
        Assert.Equal(c, (await Store.OpenFileAsync(CodeOf(published), 1, Ct))!.Value.Bytes);
        Assert.Null(await Store.OpenFileAsync(CodeOf(published), 2, Ct));
        var (_, access) = await AliceAsync(alice);
        var entry = Assert.Single((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications);
        Assert.Equal([(1, "/Work/C.md")], entry.Members!.Select(m => (m.Id, m.Note)));
        Assert.Equal(["/Work/a.jpg", "/Work/c.jpg"], entry.Files.Select(f => f.Path));
        using var reader = new ReaderApiTests.Reader(Api, "203.0.113.73");
        using var read = await reader.PostAsync(CodeOf(published));
        var json = await RestClient.JsonAsync(read);
        Assert.Equal([0, 1], json["notes"]!.AsArray().Select(n => n!["id"]!.GetValue<int>()));
        Assert.Equal([0, 1], json["files"]!.AsArray().Select(f => f!["index"]!.GetValue<int>()));
    }

    [Fact]
    public async Task After_a_prune_the_depths_are_a_fresh_walks_and_a_note_now_too_deep_leaves()
    {
        // At depth 3: A → B → D → F, and A → C → E → D. Without B, D is three links away and F four.
        var alice = await WorkAsync(("A.md", "[[B]] [[C]]"), ("B.md", "[[D]]"), ("C.md", "[[E]]"), ("D.md", "[[F]]"), ("E.md", "[[D]]"), ("F.md", "f"));
        var published = await PublishAsync(alice, "/Work/A.md", 3);
        Assert.Equal(6, (await CopyAsync(published)).Notes!.Count);

        await MarkAsync(alice, "/Work/B.md");

        var copy = await CopyAsync(published);
        Assert.Equal([(0, "A", 0), (1, "C", 1), (2, "E", 2), (3, "D", 3)], copy.Notes!.Select(n => (n.Id, n.Title, n.Depth)));
        Assert.Equal(["/Work/A.md", "/Work/C.md", "/Work/E.md", "/Work/D.md"], Paths(await PreviewAsync(alice, "/Work/A.md", 3)));
        Assert.Equal("[E](#kn-2)", copy.Notes![1].Markdown.TrimEnd());
        Assert.Equal("F", copy.Notes[3].Markdown.TrimEnd());
        var (_, access) = await AliceAsync(alice);
        Assert.Equal(
            [(1, "/Work/C.md"), (2, "/Work/E.md"), (3, "/Work/D.md")],
            Assert.Single((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications).Members!.Select(m => (m.Id, m.Note)).OrderBy(m => m.Id));
    }

    [Fact]
    public async Task A_cycle_takes_each_note_once_and_survives_a_prune()
    {
        var alice = await WorkAsync(("A.md", "[[B]]"), ("B.md", "[[C]] [[A]]"), ("C.md", "[[A]] [[B]]"));
        var published = await PublishAsync(alice, "/Work/A.md", 5);

        var copy = await CopyAsync(published);
        Assert.Equal([(0, "A", 0), (1, "B", 1), (2, "C", 2)], copy.Notes!.Select(n => (n.Id, n.Title, n.Depth)));
        Assert.Equal([[0, 1], [1, 0], [1, 2], [2, 0], [2, 1]], copy.Edges!);

        await MarkAsync(alice, "/Work/C.md");

        copy = await CopyAsync(published);
        Assert.Equal(["A", "B"], copy.Notes!.Select(n => n.Title));
        Assert.Equal([[0, 1], [1, 0]], copy.Edges!);
        Assert.Equal("C [A](#kn-0)", copy.Notes![1].Markdown.TrimEnd());
    }

    [Fact]
    public async Task The_walk_stays_in_its_vault()
    {
        var alice = await WorkAsync(("A.md", "[[B]] [[Garden/B]] [x](/Garden/B.md) [y](../Garden/B.md)"));
        using (var created = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Garden" })) Assert.True(created.IsSuccessStatusCode);
        using (var put = await alice.SendAsync(HttpMethod.Put, "/api/notes/Garden/B.md", new { content = "garden" })) Assert.Equal(HttpStatusCode.Created, put.StatusCode);

        var preview = await PreviewAsync(alice, "/Work/A.md", 2);

        Assert.Equal(["/Work/A.md"], Paths(preview));
        Assert.Empty(preview.LeftOut);
    }

    [Fact]
    public async Task A_member_note_moved_into_a_skills_place_leaves_the_copy()
    {
        var alice = await WorkAsync(("A.md", "[[B]] [[C]]"), ("B.md", "b"), ("C.md", "c"));
        var published = await PublishAsync(alice, "/Work/A.md", 1);

        using (var moved = await alice.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Work/B.md", to = "/Work/_skills/tidy/B.md" })) Assert.True(moved.IsSuccessStatusCode);

        var copy = await CopyAsync(published);
        Assert.Equal([(0, "A"), (1, "C")], copy.Notes!.Select(n => (n.Id, n.Title)));
        Assert.Equal("B [C](#kn-1)", copy.Markdown.TrimEnd());
        var (_, access) = await AliceAsync(alice);
        Assert.Equal([(1, "/Work/C.md")], Assert.Single((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications).Members!.Select(m => (m.Id, m.Note)));
    }

    [Fact]
    public async Task Removing_a_bundle_note_takes_it_out()
    {
        var alice = await WorkAsync(("A.md", "[[B]]"), ("B.md", "b"));
        var published = await PublishAsync(alice, "/Work/A.md", 1);

        using (var deleted = await alice.SendAsync(HttpMethod.Delete, alice.Url("B.md"))) Assert.True(deleted.IsSuccessStatusCode);

        Assert.Null((await CopyAsync(published)).Notes);
    }

    [Fact]
    public async Task Moving_a_bundle_note_into_a_marked_folder_takes_it_out_and_a_rename_keeps_it()
    {
        var alice = await WorkAsync(("A.md", "[[B]] [[C]]"), ("B.md", "b"), ("C.md", "c"), ("Secret/s.md", "s"));
        var published = await PublishAsync(alice, "/Work/A.md", 1);
        await MarkAsync(alice, "/Work/Secret");

        using (var moved = await alice.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Work/B.md", to = "/Work/Secret/B.md" })) Assert.True(moved.IsSuccessStatusCode);
        using (var renamed = await alice.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Work/C.md", to = "/Work/See.md" })) Assert.True(renamed.IsSuccessStatusCode);

        Assert.Equal(["A", "C"], (await CopyAsync(published)).Notes!.Select(n => n.Title));
        var (_, access) = await AliceAsync(alice);
        Assert.Equal(["/Work/See.md"], Assert.Single((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications).Members!.Select(m => m.Note));
    }

    [Fact]
    public async Task Its_images_go_with_it_unless_another_note_uses_them()
    {
        var alice = await WorkAsync();
        await alice.CreateFileAsync("a.jpg", ImageMetadataTests.JpegWithGps(4));
        await alice.CreateFileAsync("b.jpg", ImageMetadataTests.JpegWithGps(5));
        await alice.CreateAsync("A.md", "![[a.jpg]] [[B]]");
        await alice.CreateAsync("B.md", "![[a.jpg]] ![[b.jpg]]");
        var published = await PublishAsync(alice, "/Work/A.md", 1);

        await MarkAsync(alice, "/Work/B.md");

        var copy = await CopyAsync(published);
        Assert.Equal([0], copy.Files.Select(f => f.Index));
        Assert.NotNull(await Store.OpenFileAsync(CodeOf(published), 0, Ct));
        Assert.Null(await Store.OpenFileAsync(CodeOf(published), 1, Ct));
    }

    [Fact]
    public async Task Marking_the_start_note_still_withdraws_everything()
    {
        var alice = await WorkAsync(("A.md", "[[B]]"), ("B.md", "b"));
        var published = await PublishAsync(alice, "/Work/A.md", 1);

        await MarkAsync(alice, "/Work/A.md");

        Assert.Null(await Store.OpenCopyAsync(CodeOf(published), Ct));
    }

    // Task 5: over REST, the bar, the reader and the AI link.

    [Fact]
    public async Task The_api_previews_publishes_with_expected_and_answers_409_with_the_new_list()
    {
        var alice = await WorkAsync(("A.md", "[[B]] [[C]]"), ("B.md", "b"), ("C.md", "c"));

        using (var preview = await alice.GetAsync("/api/publication-previews/Work/A.md?depth=1&without=C.md"))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            var json = await RestClient.JsonAsync(preview);
            Assert.Equal(["/Work/A.md", "/Work/B.md"], json["notes"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()));
            Assert.Equal("without", json["leftOut"]![0]!["reason"]!.GetValue<string>());
        }

        using (var changed = await alice.SendAsync(HttpMethod.Post, "/api/publications/Work/A.md", new { depth = 1, expected = new[] { "A.md" } }))
        {
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Equal(3, (await RestClient.JsonAsync(changed))["preview"]!["notes"]!.AsArray().Count);
        }

        using (var published = await alice.SendAsync(HttpMethod.Post, "/api/publications/Work/A.md", new { depth = 1, without = new[] { "C.md" }, expected = new[] { "A.md", "B.md" } }))
        {
            Assert.Equal(HttpStatusCode.Created, published.StatusCode);
            Assert.Equal(2, (await RestClient.JsonAsync(published))["notes"]!.AsArray().Count);
        }

        using (var list = await alice.GetAsync("/api/vaults/Work/publications"))
        {
            var json = await RestClient.JsonAsync(list);
            Assert.Equal("/Work/B.md", json["bundled"]![0]!["path"]!.GetValue<string>());
            Assert.Equal("/Work/A.md", json["bundled"]![0]!["start"]!.GetValue<string>());
            Assert.Equal(2, json["publications"]![0]!["notes"]!.GetValue<int>());
            Assert.Equal(1, json["publications"]![0]!["depth"]!.GetValue<int>());
            Assert.Equal("/Work/C.md", json["publications"]![0]!["without"]![0]!.GetValue<string>());
        }

        // The overview carries the walk too, so that Update copy can show the bundle's list from there; never the link.
        using (var overview = await alice.GetAsync("/api/publications"))
        {
            var json = (await RestClient.JsonAsync(overview))[0]!;
            Assert.Equal(2, json["notes"]!.GetValue<int>());
            Assert.Equal(1, json["depth"]!.GetValue<int>());
            Assert.Equal(["/Work/C.md"], json["without"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.True(json["link"] is null, "the overview holds a link");
            // The versions the copy was made from, the start first, for Compare.
            var copied = json["copied"]!.AsArray();
            Assert.Equal(["/Work/A.md", "/Work/B.md"], copied.Select(c => c!["path"]!.GetValue<string>()));
            var a = await RestClient.JsonAsync(await alice.GetAsync("/api/notes/Work/A.md"));
            Assert.Equal(a["version"]!.GetValue<string>(), copied[0]!["version"]!.GetValue<string>());
        }

        using (var updated = await alice.SendAsync(HttpMethod.Put, "/api/publications/Work/A.md", new { depth = 0, expected = new[] { "A.md" } }))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var json = await RestClient.JsonAsync(updated);
            Assert.Equal(["/Work/A.md"], json["notes"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Equal(["/Work/B.md"], json["removed"]!.AsArray().Select(n => n!.GetValue<string>()));
        }

        using var bad = await alice.GetAsync("/api/publication-previews/Work/A.md?depth=9");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Over_http_a_bundle_goes_out_only_with_the_list_that_was_seen()
    {
        var alice = await WorkAsync(("A.md", "[[B]]"), ("B.md", "b"), ("C.md", "c"));

        using (var unseen = await alice.SendAsync(HttpMethod.Post, "/api/publications/Work/A.md", new { depth = 1 }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unseen.StatusCode);
        }

        var (_, access) = await AliceAsync(alice);
        Assert.Empty((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications);
        using (var seen = await alice.SendAsync(HttpMethod.Post, "/api/publications/Work/A.md", new { depth = 1, expected = new[] { "A.md", "B.md" } }))
        {
            Assert.Equal(HttpStatusCode.Created, seen.StatusCode);
        }

        await EditAsync(alice, "A.md", "[[B]] [[C]]");
        using (var none = await alice.SendAsync(HttpMethod.Put, "/api/publications/Work/A.md")) Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        using (var alone = await alice.SendAsync(HttpMethod.Put, "/api/publications/Work/A.md", new { depth = 0 })) Assert.Equal(HttpStatusCode.BadRequest, alone.StatusCode);
        using (var stale = await alice.SendAsync(HttpMethod.Put, "/api/publications/Work/A.md", new { expected = new[] { "A.md", "B.md" } }))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal(["/Work/A.md", "/Work/B.md", "/Work/C.md"], (await RestClient.JsonAsync(stale))["preview"]!["notes"]!.AsArray().Select(n => n!["path"]!.GetValue<string>()));
        }

        Assert.Equal(["/Work/B.md"], Assert.Single((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications).Members!.Select(m => m.Note));
        using var current = await alice.SendAsync(HttpMethod.Put, "/api/publications/Work/A.md", new { expected = new[] { "A.md", "B.md", "C.md" } });
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [Fact]
    public async Task Over_http_without_names_notes_of_the_bundle_only()
    {
        var alice = await WorkAsync(("A.md", "[[B]] [[C]]"), ("B.md", "[[D]]"), ("C.md", "c"), ("D.md", "d"), ("E.md", "e"));
        await MarkAsync(alice, "/Work/C.md");

        foreach (var without in new[] { "A.md", "E.md", "C.md", "D.md" })
        {
            using var preview = await alice.GetAsync($"/api/publication-previews/Work/A.md?depth=1&without={without}");
            Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
            using var publish = await alice.SendAsync(HttpMethod.Post, "/api/publications/Work/A.md", new { depth = 1, without = new[] { without }, expected = new[] { "A.md" } });
            Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
        }

        using (var published = await alice.SendAsync(HttpMethod.Post, "/api/publications/Work/A.md", new { depth = 2, without = new[] { "B.md", "D.md" }, expected = new[] { "A.md" } }))
        {
            Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        }

        using (var update = await alice.SendAsync(HttpMethod.Put, "/api/publications/Work/A.md", new { without = new[] { "E.md" }, expected = new[] { "A.md", "B.md", "D.md" } }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        }
    }

    [Fact]
    public async Task A_member_learns_a_note_is_bundled_but_not_its_start()
    {
        var alice = await WorkAsync(("A.md", "[[B]]"), ("B.md", "b"));
        await PublishAsync(alice, "/Work/A.md", 1);
        var bob = await RestClient.NewAsync(Api, vault: "Garten");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);

        using var list = await bob.GetAsync("/api/vaults/alices-work/publications");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var bundled = Assert.Single((await RestClient.JsonAsync(list))["bundled"]!.AsArray())!;
        Assert.Equal("/alices-work/B.md", bundled["path"]!.GetValue<string>());
        Assert.Null(bundled["start"]);
        Assert.DoesNotContain("A.md", bundled.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reader_gets_every_note_of_the_bundle_in_walk_order_and_no_edges()
    {
        var alice = await WorkAsync(("A.md", "[[B]]"), ("B.md", "[[C]]"), ("C.md", "c"));
        var published = await PublishAsync(alice, "/Work/A.md", 2);
        using var reader = new ReaderApiTests.Reader(Api, "203.0.113.71");

        using var read = await reader.PostAsync(CodeOf(published));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var json = await RestClient.JsonAsync(read);
        Assert.Equal(["A", "B", "C"], json["notes"]!.AsArray().Select(n => n!["title"]!.GetValue<string>()));
        Assert.Equal([0, 1, 2], json["notes"]!.AsArray().Select(n => n!["depth"]!.GetValue<int>()));
        Assert.DoesNotContain("edges", json.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("[B](#kn-1)", json["markdown"]!.GetValue<string>().TrimEnd());
    }

    [Fact]
    public async Task The_ai_link_text_carries_the_whole_bundle()
    {
        var copy = new PublishedCopy("A", "See [B](#kn-1/Big%20idea) and [C](#kn-9).", DateTime.UnixEpoch, [new PublishedCopyFile(0, "image/png")],
            [new PublishedBundleNote(0, "A", "See [B](#kn-1/Big%20idea) and [C](#kn-9).", 0, []), new PublishedBundleNote(1, "B", "![x](f/0) back to [A](#kn-0)", 1, [0])], [[0, 1]]);

        var text = AiLinkApi.Markdown(copy, "https://k.example/s/t/T/files");

        Assert.Contains("See [B](#n1) and C.", text);
        Assert.Contains("\n\n---\n\n# B {#n1}\n\n![x](https://k.example/s/t/T/files/0) (image/png) back to [A](#n0)", text);
    }
}
