// SPDX-License-Identifier: AGPL-3.0-or-later
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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>
/// Unpublishable marks (publish spec §3.6): a hard block on what they cover, the owner included; setting one withdraws
/// every publication whose note or file it covers; lifting removes only that mark. The owner's session's alone, also
/// while publishing is switched off.
/// </summary>
[Collection("app")]
public sealed class UnpublishableTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Publications Publications => Api.Services.GetRequiredService<Publications>();

    private PublicationStore Store => Api.Services.GetRequiredService<PublicationStore>();

    private static readonly WriterRef BySession = WriterRef.ForOwner(WriterRef.Session, null);

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private async Task<(EncryptedNoteStore Notes, VaultAccess Access)> OpenAsync(RestClient user, string vault = "Work", IServiceProvider? services = null)
    {
        var notes = new EncryptedNoteStore((services ?? Api.Services).GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey));
        return (notes, (await notes.UnlockedVaultsAsync(Ct)).Single(a => a.Name == vault));
    }

    private async Task<PublishedNote> PublishAsync(RestClient alice, string note)
    {
        var (notes, access) = await OpenAsync(alice);
        return await Publications.PublishAsync(access, notes, P(note), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct);
    }

    private async Task MarkAsync(RestClient alice, string path, bool marked = true, IServiceProvider? services = null)
    {
        var (notes, access) = await OpenAsync(alice, services: services);
        await (services?.GetRequiredService<Publications>() ?? Publications).SetUnpublishableAsync(access, notes, P(path), marked, alice.Username, BySession, Ct);
    }

    private async Task<IReadOnlyList<VaultPath>> MarksAsync(RestClient user, string vault = "Work") =>
        await Publications.MarksAsync((await OpenAsync(user, vault)).Access, Ct);

    private async Task<PublishingRecord> RecordAsync(RestClient alice) => (await Store.ReadAsync(null, (await OpenAsync(alice)).Access.Vault, Ct)).Record;

    private static string CodeOf(PublishedNote published) => ShareCode.Parse(published.Link[(published.Link.IndexOf('#') + 1)..])!;

    private Task<long> CountAsync(string collection, string vaultId) =>
        app.Database.GetCollection<BsonDocument>(collection).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("vault", vaultId), cancellationToken: Ct);

    private static async Task RefusedAsync(Func<Task> act, ShellErrorKind kind, string? code, string? sentence = null)
    {
        var e = await Assert.ThrowsAsync<PublishRefusedException>(act);
        Assert.Equal((kind, code), (e.Kind, e.Code));
        if (sentence is not null) Assert.Equal(sentence, e.Message);
    }

    /// <summary>The publication of <paramref name="note"/> is gone, copy and files, and its last log entry says a mark withdrew it.</summary>
    private async Task AssertWithdrawnByMarkAsync(RestClient alice, PublishedNote published, string note)
    {
        Assert.Null(await Store.OpenCopyAsync(CodeOf(published), Ct));
        Assert.Null(await Store.OpenFileAsync(CodeOf(published), 0, Ct));
        var record = await RecordAsync(alice);
        Assert.DoesNotContain(record.Publications, p => p.Note == note);
        var last = record.Log.Last(l => l.Note == note);
        Assert.Equal((PublishingKinds.Withdrawn, WithdrawalReasons.Unpublishable), (last.Kind, last.Reason));
        Assert.Equal((PublishingKinds.Withdrawn, WithdrawalReasons.Unpublishable), (record.Log[^1].Kind, record.Log[^1].Reason));
    }

    [Fact]
    public async Task An_unpublishable_note_or_folder_blocks_the_owner_too()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Private/Plan.md", "a");
        await alice.CreateAsync("Private/Deep/Plan.md", "b");
        await alice.CreateAsync("Notes/x.md", "c");
        await alice.CreateAsync("Notes/y.md", "d");

        await MarkAsync(alice, "/Work/Private");
        await MarkAsync(alice, "/Work/Notes/x.md");

        foreach (var note in new[] { "/Work/Private/Plan.md", "/Work/Private/Deep/Plan.md", "/Work/Notes/x.md" })
        {
            await RefusedAsync(
                () => PublishAsync(alice, note), ShellErrorKind.Refused, Refusals.Unpublishable,
                $"{note} is marked unpublishable; only the vault's owner can lift the mark, in the web app");
        }

        Assert.Equal([P("/Work/Notes/x.md"), P("/Work/Private")], (await MarksAsync(alice)).OrderBy(p => p.Value, StringComparer.Ordinal));
        Assert.Equal(0, await CountAsync("publications", (await OpenAsync(alice)).Access.Id));
        await PublishAsync(alice, "/Work/Notes/y.md");

        // A mark goes on what is there: a note, a file or a folder with something below it.
        await RefusedAsync(() => MarkAsync(alice, "/Work/Nothing"), ShellErrorKind.NotFound, null, "/Work/Nothing: no such note, file or folder");
        Assert.Equal(2, (await MarksAsync(alice)).Count);
    }

    [Fact]
    public async Task Marking_a_published_note_or_its_folder_withdraws_it()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateFileAsync("Private/flow.jpg", ImageMetadataTests.JpegWithGps(2));
        await alice.CreateAsync("Notes/a.md", "a");
        await alice.CreateAsync("Private/b.md", "b ![[flow.jpg]]");
        await alice.CreateAsync("Public/c.md", "c");
        var a = await PublishAsync(alice, "/Work/Notes/a.md");
        var b = await PublishAsync(alice, "/Work/Private/b.md");
        var c = await PublishAsync(alice, "/Work/Public/c.md");
        var vaultId = (await OpenAsync(alice)).Access.Id;
        Assert.Equal(1, await CountAsync("publication_files", vaultId));

        await MarkAsync(alice, "/Work/Notes/a.md");
        await AssertWithdrawnByMarkAsync(alice, a, "/Work/Notes/a.md");
        Assert.NotNull(await Store.OpenCopyAsync(CodeOf(b), Ct));

        await MarkAsync(alice, "/Work/Private");
        await AssertWithdrawnByMarkAsync(alice, b, "/Work/Private/b.md");

        Assert.Equal(0, await CountAsync("publication_files", vaultId));
        Assert.Equal(1, await CountAsync("publications", vaultId));
        Assert.NotNull(await Store.OpenCopyAsync(CodeOf(c), Ct));
        Assert.Equal("/Work/Public/c.md", Assert.Single((await RecordAsync(alice)).Publications).Note);
        Assert.Equal(2, (await RecordAsync(alice)).Log.Count(l => l.Reason == WithdrawalReasons.Unpublishable));
    }

    [Fact]
    public async Task Marking_an_attachment_a_copy_holds_withdraws_the_whole_publication()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateFileAsync("Routing/flow.jpg", ImageMetadataTests.JpegWithGps(3));
        await alice.CreateFileAsync("Routing/other.jpg", ImageMetadataTests.JpegWithGps(4));
        await alice.CreateAsync("Routing/Questions.md", "Questions ![[flow.jpg]]");
        await alice.CreateAsync("Routing/Other.md", "Other ![[other.jpg]]");
        var published = await PublishAsync(alice, "/Work/Routing/Questions.md");
        var other = await PublishAsync(alice, "/Work/Routing/Other.md");

        await MarkAsync(alice, "/Work/Routing/flow.jpg");

        await AssertWithdrawnByMarkAsync(alice, published, "/Work/Routing/Questions.md");
        Assert.NotNull(await Store.OpenFileAsync(CodeOf(other), 0, Ct));
        Assert.Equal(1, await CountAsync("publication_files", (await OpenAsync(alice)).Access.Id));

        // The note itself isn't covered: published again, the marked image is plain text.
        var again = await PublishAsync(alice, "/Work/Routing/Questions.md");
        Assert.Equal(["flow.jpg"], again.Plain);
    }

    [Fact]
    public async Task Lifting_a_mark_allows_publishing_again()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Private/x.md", "x");
        await MarkAsync(alice, "/Work/Private");
        await MarkAsync(alice, "/Work/Private/x.md");

        // Its own mark lifted, the note is still in a marked folder.
        await MarkAsync(alice, "/Work/Private/x.md", marked: false);
        Assert.Equal([P("/Work/Private")], await MarksAsync(alice));
        await RefusedAsync(() => PublishAsync(alice, "/Work/Private/x.md"), ShellErrorKind.Refused, Refusals.Unpublishable);

        await MarkAsync(alice, "/Work/Private", marked: false);
        Assert.Empty(await MarksAsync(alice));
        await PublishAsync(alice, "/Work/Private/x.md");
    }

    [Fact]
    public async Task Marks_work_while_switched_off()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Notes/a.md", "a");
        var published = await PublishAsync(alice, "/Work/Notes/a.md");
        await using var off = Api.WithWebHostBuilder(b => b.UseSetting("Kumbu:Publish:Enabled", "false"));
        Assert.False(off.Services.GetRequiredService<Publications>().Enabled);

        await MarkAsync(alice, "/Work/Notes", services: off.Services);
        await AssertWithdrawnByMarkAsync(alice, published, "/Work/Notes/a.md");
        Assert.Equal([P("/Work/Notes")], await MarksAsync(alice));

        await MarkAsync(alice, "/Work/Notes", marked: false, services: off.Services);
        Assert.Empty(await MarksAsync(alice));
    }

    [Fact]
    public async Task Marks_are_the_owners_alone()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Notes/a.md", "a");
        await MarkAsync(alice, "/Work/Notes/a.md");
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var (bobsNotes, bobs) = await OpenAsync(bob, "alices-work");

        await RefusedAsync(
            () => Publications.SetUnpublishableAsync(bobs, bobsNotes, P("/alices-work/Notes"), true, bob.Username, BySession, Ct),
            ShellErrorKind.Refused, Refusals.NotOwner, "only the owner of alices-work can publish its notes");
        await RefusedAsync(
            () => Publications.SetUnpublishableAsync(bobs, bobsNotes, P("/alices-work/Notes/a.md"), false, bob.Username, BySession, Ct), ShellErrorKind.Refused, Refusals.NotOwner);

        Assert.Empty(await MarksAsync(bob, "alices-work"));
        Assert.Equal([P("/Work/Notes/a.md")], await MarksAsync(alice));

        // The owner's own key without "May publish" neither sets nor lifts one.
        var vaultId = await alice.VaultIdAsync();
        var (key, info) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            alice.User, alice.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [vaultId] = KeyRight.Write }, Ct);
        var keyNotes = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new ApiKeyCredential(info.Id, key));
        var keys = (await keyNotes.UnlockedVaultsAsync(Ct)).Single(a => a.Name == "Work");
        Assert.True(keys.IsOwner);
        var by = WriterRef.ForOwner(WriterRef.Agent, info.Id);
        await RefusedAsync(
            () => Publications.SetUnpublishableAsync(keys, keyNotes, P("/Work/Notes"), true, "Claude", by, Ct), ShellErrorKind.Refused, Refusals.NoPublishRight);
        await RefusedAsync(
            () => Publications.SetUnpublishableAsync(keys, keyNotes, P("/Work/Notes/a.md"), false, "Claude", by, Ct), ShellErrorKind.Refused, Refusals.NoPublishRight);
        Assert.Equal([P("/Work/Notes/a.md")], await MarksAsync(alice));
    }
}
