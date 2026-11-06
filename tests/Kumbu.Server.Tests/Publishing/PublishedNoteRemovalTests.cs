// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text.RegularExpressions;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Publishing;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Attachments;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>
/// Removing and moving a published note (publish spec §3.6, §3.7): a removal withdraws the publication in the same
/// transaction, but only for the owner's session, her key with "May publish" or her device; anyone else gets a final
/// refusal, code <c>published</c>, and nothing moves or goes. A move keeps the link, and marks follow their folder;
/// moving it, or a file it holds, into an unpublishable folder withdraws it. Edits never touch a publication.
/// </summary>
[Collection("app")]
public sealed partial class PublishedNoteRemovalTests(KumbuAppFactory app)
{
    private const string Questions = "/Work/Routing/Questions.md";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Publications Publications => Api.Services.GetRequiredService<Publications>();

    private PublicationStore Store => Api.Services.GetRequiredService<PublicationStore>();

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    private static readonly WriterRef BySession = WriterRef.ForOwner(WriterRef.Session, null);

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    [GeneratedRegex("^To apply: (restore --checkpoint .+)$", RegexOptions.Multiline)]
    private static partial Regex ApplyLine();

    private sealed record World(RestClient Alice, string Code);

    private async Task<(EncryptedNoteStore Notes, VaultAccess Access)> OpenAsync(ICredential credential, string vault)
    {
        var notes = new EncryptedNoteStore(Catalog, credential);
        return (notes, (await notes.UnlockedVaultsAsync(Ct)).Single(a => a.Name == vault));
    }

    private Task<(EncryptedNoteStore Notes, VaultAccess Access)> AliceAsync(RestClient alice, string vault = "Work") =>
        OpenAsync(new UserCredential(alice.User.Id, alice.UserKey), vault);

    private async Task<string> PublishAsync(RestClient alice, string note = Questions)
    {
        var (notes, access) = await AliceAsync(alice, P(note).Vault!);
        var published = await Publications.PublishAsync(access, notes, P(note), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct);
        return ShareCode.Parse(published.Link[(published.Link.IndexOf('#') + 1)..])!;
    }

    private async Task MarkAsync(RestClient alice, string path)
    {
        var (notes, access) = await AliceAsync(alice);
        await Publications.SetUnpublishableAsync(access, notes, P(path), true, alice.Username, BySession, Ct);
    }

    /// <summary>Alice's <c>Work</c> with <c>Routing/Questions.md</c>, which embeds <c>Routing/flow.jpg</c>, published by her session.</summary>
    private async Task<World> PublishedAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateFileAsync("Routing/flow.jpg", ImageMetadataTests.JpegWithGps(5));
        await alice.CreateAsync("Routing/Questions.md", "# Questions on routing\n\n![[flow.jpg]]\n");
        return new World(alice, await PublishAsync(alice));
    }

    private async Task<PublishingRecord> RecordAsync(RestClient alice) => (await Store.ReadAsync(null, (await AliceAsync(alice)).Access.Vault, Ct)).Record;

    private async Task<bool> OpensAsync(string code) => await Store.OpenCopyAsync(code, Ct) is not null;

    private async Task<bool> ExistsAsync(RestClient alice, string note = "Routing/Questions.md")
    {
        using var read = await alice.GetAsync(alice.Url(note));
        return read.StatusCode == HttpStatusCode.OK;
    }

    private Task<long> CountAsync(string collection, string vaultId) =>
        app.Database.GetCollection<BsonDocument>(collection).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("vault", vaultId), cancellationToken: Ct);

    /// <summary>The publication of <paramref name="note"/> is gone, and its last log entry says why.</summary>
    private async Task AssertWithdrawnAsync(World w, string reason, string note = Questions)
    {
        Assert.False(await OpensAsync(w.Code));
        var record = await RecordAsync(w.Alice);
        Assert.Empty(record.Publications);
        Assert.Equal((PublishingKinds.Withdrawn, reason, note), (record.Log[^1].Kind, record.Log[^1].Reason, record.Log[^1].Note));
        Assert.Equal(0, await CountAsync("publication_files", (await AliceAsync(w.Alice)).Access.Id));
    }

    /// <summary>Still published, under the same code, and nothing of it withdrawn.</summary>
    private async Task AssertPublishedAsync(World w, string note = Questions)
    {
        Assert.True(await OpensAsync(w.Code));
        var record = await RecordAsync(w.Alice);
        Assert.Equal(note, Assert.Single(record.Publications).Note);
        Assert.DoesNotContain(record.Log, l => l.Kind == PublishingKinds.Withdrawn);
    }

    private static async Task AssertFinalAsync(HttpResponseMessage response, string sentence)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await RestClient.JsonAsync(response);
        Assert.Equal(Refusals.Published, body["code"]?.GetValue<string>());
        Assert.Equal(sentence, body["error"]!.GetValue<string>());
    }

    private const string KeySentence =
        "/Work/Routing/Questions.md is published, and this key may not publish in Work, so it can't withdraw it either; withdraw it first with a key that may publish (publish rm) or in the web app";

    [Fact]
    public async Task The_owner_removes_a_published_note_and_the_link_dies()
    {
        var w = await PublishedAsync();

        using var deleted = await w.Alice.SendAsync(HttpMethod.Delete, w.Alice.Url("Routing/Questions.md"));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(await ExistsAsync(w.Alice));
        await AssertWithdrawnAsync(w, WithdrawalReasons.Removed);
        Assert.Equal(1, (await RecordAsync(w.Alice)).Log.Count(l => l.Kind == PublishingKinds.Withdrawn));
    }

    [Fact]
    public async Task A_key_with_may_publish_removes_it()
    {
        var w = await PublishedAsync();
        var key = await w.Alice.KeyAsync("Claude", KeyRight.Write, mayPublish: true);

        var (isError, text) = await w.Alice.ShAsync($"rm {Questions}", key);

        Assert.False(isError, text);
        Assert.False(await ExistsAsync(w.Alice));
        await AssertWithdrawnAsync(w, WithdrawalReasons.Removed);
        Assert.Equal("Claude", (await RecordAsync(w.Alice)).Log[^1].By);
    }

    [Fact]
    public async Task The_owners_device_removes_it()
    {
        var w = await PublishedAsync();
        var device = await w.Alice.KeyAsync("Laptop", KeyRight.Write, ApiKeyKind.Device);

        using var deleted = await w.Alice.SendAsync(HttpMethod.Delete, w.Alice.Url("Routing/Questions.md"), bearer: device);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await AssertWithdrawnAsync(w, WithdrawalReasons.Removed);
    }

    [Fact]
    public async Task A_key_without_may_publish_is_refused_naming_the_publication()
    {
        var w = await PublishedAsync();
        var key = await w.Alice.KeyAsync("Claude", KeyRight.Write);

        var (isError, text) = await w.Alice.ShAsync($"rm {Questions}", key);
        using var deleted = await w.Alice.SendAsync(HttpMethod.Delete, w.Alice.Url("Routing/Questions.md"), bearer: key);

        Assert.True(isError);
        Assert.Contains("is published", text, StringComparison.Ordinal);
        Assert.Contains("may not publish", text, StringComparison.Ordinal);
        Assert.Equal(KeySentence, text);
        await AssertFinalAsync(deleted, KeySentence);
        Assert.True(await ExistsAsync(w.Alice));
        await AssertPublishedAsync(w);
    }

    [Fact]
    public async Task A_member_is_refused()
    {
        var w = await PublishedAsync();
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await w.Alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);

        using var deleted = await bob.SendAsync(HttpMethod.Delete, "/api/notes/alices-work/Routing/Questions.md");

        await AssertFinalAsync(
            deleted,
            "/alices-work/Routing/Questions.md is published by the vault's owner; only the owner can remove it or move it into an unpublishable folder");
        Assert.True(await ExistsAsync(w.Alice));
        await AssertPublishedAsync(w);
    }

    [Fact]
    public async Task A_members_device_gets_a_final_refusal()
    {
        var w = await PublishedAsync();
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await w.Alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var (device, _) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            bob.User, bob.UserKey, DateTime.UtcNow, "Phone", new Dictionary<string, KeyRight> { [await w.Alice.VaultIdAsync()] = KeyRight.Write }, Ct, kind: ApiKeyKind.Device);

        foreach (var _ in Enumerable.Range(0, 2))
        {
            using var deleted = await bob.SendAsync(HttpMethod.Delete, "/api/notes/alices-work/Routing/Questions.md", bearer: device);
            Assert.NotEqual(HttpStatusCode.Conflict, deleted.StatusCode);
            Assert.NotEqual(HttpStatusCode.ServiceUnavailable, deleted.StatusCode);
            Assert.Null(deleted.Headers.RetryAfter);
            await AssertFinalAsync(
                deleted,
                "/alices-work/Routing/Questions.md is published by the vault's owner; only the owner can remove it or move it into an unpublishable folder");
        }

        Assert.True(await ExistsAsync(w.Alice));
        await AssertPublishedAsync(w);
    }

    [Fact]
    public async Task The_dev_token_is_refused()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        var (_, access) = await OpenAsync(new FixedCredential(KumbuAppFactory.Token), (await Catalog.OpenAsync(KumbuAppFactory.Token, Ct)).First().Name);
        var name = $"published-{Guid.NewGuid():N}.md";
        var note = VaultPath.Root.Child(access.Name).Child(name);
        using (var put = await alice.SendAsync(HttpMethod.Put, $"/api/notes/{access.Name}/{name}", new { content = "x" }, bearer: KumbuAppFactory.Token))
        {
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        }

        // Nobody can publish in the dev token's vaults: the entry is planted.
        var (doc, record) = await Store.ReadAsync(null, access.Vault, Ct);
        var entry = new PublicationEntry(
            Guid.NewGuid().ToString("N"), access.ToVault(note).Value, ShareCode.New(), VaultCrypto.NewKey(), DateTime.UtcNow, "v", [], "dev",
            WriterRefPayload.From(BySession)!);
        using (var session = await Catalog.StartSessionAsync(Ct))
        {
            await Store.WriteAsync(session, access.Vault, doc, record with { Publications = [.. record.Publications, entry] }, Ct);
        }

        try
        {
            var (isError, text) = await alice.ShAsync($"rm {note}", KumbuAppFactory.Token);

            Assert.True(isError);
            Assert.Contains($"{note} is published, and this key may not publish in {access.Name}", text, StringComparison.Ordinal);
            Assert.Contains((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications, p => p.Id == entry.Id);
        }
        finally
        {
            var (now, planted) = await Store.ReadAsync(null, access.Vault, Ct);
            using var session = await Catalog.StartSessionAsync(Ct);
            await Store.WriteAsync(session, access.Vault, now, planted with { Publications = [.. planted.Publications.Where(p => p.Id != entry.Id)] }, Ct);
        }

        Assert.False((await alice.ShAsync($"rm {note}", KumbuAppFactory.Token)).IsError);
    }

    [Fact]
    public async Task A_single_trash_meets_the_rule_too()
    {
        var w = await PublishedAsync();
        var (key, info) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            w.Alice.User, w.Alice.UserKey, DateTime.UtcNow, "Claude", new Dictionary<string, KeyRight> { [await w.Alice.VaultIdAsync()] = KeyRight.Write }, Ct);
        var keyId = info.Id;

        // As DI makes the request's store; TryTrashAsync has no caller in the server yet, so it is called here.
        EncryptedNoteStore StoreOf(ICredential credential) => new(Catalog, credential, null, null, null, Publications);
        var byKey = StoreOf(new ApiKeyCredential(keyId, key));
        var refused = await Assert.ThrowsAsync<PublishedNoteException>(async () =>
            await byKey.TryTrashAsync((await byKey.GetAsync(P(Questions), Ct))!, "Claude", WriterRef.ForOwner(WriterRef.Agent, keyId), DateTime.UtcNow, Ct));
        Assert.Equal((KeySentence, Refusals.Published), (refused.Message, refused.Code));
        Assert.True(await ExistsAsync(w.Alice));
        await AssertPublishedAsync(w);

        var bySession = StoreOf(new UserCredential(w.Alice.User.Id, w.Alice.UserKey));
        Assert.True(await bySession.TryTrashAsync((await bySession.GetAsync(P(Questions), Ct))!, w.Alice.Username, BySession, DateTime.UtcNow, Ct));
        await AssertWithdrawnAsync(w, WithdrawalReasons.Removed);
    }

    [Fact]
    public async Task Restore_does_not_bring_it_back()
    {
        var w = await PublishedAsync();
        using (var deleted = await w.Alice.SendAsync(HttpMethod.Delete, w.Alice.Url("Routing/Questions.md"))) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using var restored = await w.Alice.SendAsync(HttpMethod.Post, "/api/restore/Work/Routing/Questions.md", new { });

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.True(await ExistsAsync(w.Alice));
        var (notes, access) = await AliceAsync(w.Alice);
        Assert.Null(await Publications.StateAsync(access, notes, P(Questions), null, Ct));
        Assert.False(await OpensAsync(w.Code));
    }

    [Fact]
    public async Task A_checkpoint_restore_meets_the_rule()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Routing/Old.md", "old");
        var maintain = await alice.KeyAsync("Claude", KeyRight.Maintain);
        Assert.False((await alice.ShAsync("mkcp /Work/Routing before", maintain)).IsError);
        await alice.CreateAsync("Routing/Questions.md", "# Questions on routing\n");
        var w = new World(alice, await PublishAsync(alice));

        var (_, preview) = await alice.ShAsync("restore --checkpoint before /Work/Routing/", maintain);
        var apply = ApplyLine().Match(preview);
        Assert.True(apply.Success, preview);
        var (isError, text) = await alice.ShAsync(apply.Groups[1].Value, maintain);

        Assert.True(isError);
        Assert.Contains("is published", text, StringComparison.Ordinal);
        Assert.True(await ExistsAsync(alice));
        await AssertPublishedAsync(w);

        using var planned = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Work/checkpoints/before/preview");
        Assert.Equal(HttpStatusCode.OK, planned.StatusCode);
        var plan = (await RestClient.JsonAsync(planned))["plan"]!.GetValue<string>();
        using var restored = await alice.SendAsync(HttpMethod.Post, "/api/vaults/Work/checkpoints/before/restore", new { plan });

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.False(await ExistsAsync(alice));
        await AssertWithdrawnAsync(w, WithdrawalReasons.Removed);
    }

    [Fact]
    public async Task A_move_keeps_the_link()
    {
        var w = await PublishedAsync();
        var key = await w.Alice.KeyAsync("Claude", KeyRight.Write);

        var (isError, text) = await w.Alice.ShAsync($"mv {Questions} /Work/Done/Questions.md", key);

        Assert.False(isError, text);
        var (notes, access) = await AliceAsync(w.Alice);
        Assert.Equal(P("/Work/Done/Questions.md"), Assert.Single(await Publications.ListAsync(access, notes, P("/Work"), null, Ct)).Note);
        await AssertPublishedAsync(w, "/Work/Done/Questions.md");
    }

    [Fact]
    public async Task A_folder_move_keeps_the_link_and_the_mark()
    {
        var w = await PublishedAsync();
        await w.Alice.CreateAsync("Routing/Private/Plan.md", "plan");
        await MarkAsync(w.Alice, "/Work/Routing/Private");
        var key = await w.Alice.KeyAsync("Claude", KeyRight.Write);

        var (isError, text) = await w.Alice.ShAsync("mv /Work/Routing/ /Work/Done/", key);

        Assert.False(isError, text);
        await AssertPublishedAsync(w, "/Work/Done/Questions.md");
        Assert.Equal("/Work/Done/flow.jpg", Assert.Single(Assert.Single((await RecordAsync(w.Alice)).Publications).Files).Path);
        var (_, access) = await AliceAsync(w.Alice);
        Assert.Equal([P("/Work/Done/Private")], await Publications.MarksAsync(access, Ct));

        (isError, text) = await w.Alice.ShAsync("mv /Work/Done/Questions.md /Work/Done/Private/Questions.md", key);

        Assert.True(isError);
        Assert.Contains("/Work/Done/Questions.md is published, and this key may not publish in Work", text, StringComparison.Ordinal);
        Assert.True(await ExistsAsync(w.Alice, "Done/Questions.md"));
        Assert.False(await ExistsAsync(w.Alice, "Done/Private/Questions.md"));
        await AssertPublishedAsync(w, "/Work/Done/Questions.md");
    }

    [Fact]
    public async Task A_folder_mark_stays_when_something_stays_under_it()
    {
        var w = await PublishedAsync();
        await w.Alice.CreateAsync("Private/a.md", "a");
        await w.Alice.CreateAsync("Private/b.md", "b");
        await MarkAsync(w.Alice, "/Work/Private");
        var key = await w.Alice.KeyAsync("Claude", KeyRight.Write);

        Assert.False((await w.Alice.ShAsync("mv /Work/Private/a.md /Work/Open/a.md", key)).IsError);

        var (_, access) = await AliceAsync(w.Alice);
        Assert.Equal([P("/Work/Private")], await Publications.MarksAsync(access, Ct));
        await AssertPublishedAsync(w);
    }

    [Fact]
    public async Task Moving_it_into_an_unpublishable_folder_withdraws_it_for_the_owner()
    {
        var w = await PublishedAsync();
        await w.Alice.CreateAsync("Private/Plan.md", "plan");
        await MarkAsync(w.Alice, "/Work/Private");

        using var moved = await w.Alice.SendAsync(HttpMethod.Post, "/api/move", new { from = Questions, to = "/Work/Private/Questions.md" });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.True(await ExistsAsync(w.Alice, "Private/Questions.md"));
        await AssertWithdrawnAsync(w, WithdrawalReasons.Unpublishable);
    }

    [Fact]
    public async Task Moving_an_attachment_it_holds_into_an_unpublishable_folder_withdraws_it()
    {
        var w = await PublishedAsync();
        await w.Alice.CreateAsync("Private/Plan.md", "plan");
        await MarkAsync(w.Alice, "/Work/Private");
        var key = await w.Alice.KeyAsync("Claude", KeyRight.Write);

        // Refused for a key without the right, as for the note itself.
        var (isError, text) = await w.Alice.ShAsync("mv /Work/Routing/flow.jpg /Work/Private/flow.jpg", key);
        Assert.True(isError);
        Assert.Equal(KeySentence, text);
        await AssertPublishedAsync(w);

        using var moved = await w.Alice.SendAsync(HttpMethod.Post, "/api/move", new { from = "/Work/Routing/flow.jpg", to = "/Work/Private/flow.jpg" });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.True(await ExistsAsync(w.Alice));
        await AssertWithdrawnAsync(w, WithdrawalReasons.Unpublishable);
    }

    [Fact]
    public async Task Editing_stays_free()
    {
        var w = await PublishedAsync();
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await w.Alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var before = (await Store.OpenCopyAsync(w.Code, Ct))!.Value.Copy;
        var version = (await w.Alice.ReadAsync("Routing/Questions.md"))["version"]!.GetValue<string>();

        using var written = await bob.SendAsync(HttpMethod.Put, "/api/notes/alices-work/Routing/Questions.md", new { content = "# Questions on routing\n\nBob's.\n", version });

        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        Assert.Equal(before.Markdown, (await Store.OpenCopyAsync(w.Code, Ct))!.Value.Copy.Markdown);
        await AssertPublishedAsync(w);
        var (notes, access) = await AliceAsync(w.Alice);
        Assert.True((await Publications.StateAsync(access, notes, P(Questions), null, Ct))!.Older);
    }

    [Fact]
    public async Task Deleting_the_account_withdraws_its_publications_at_once()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Routing/Questions.md", "# Questions on routing\n");
        using (var garten = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Gartenarbeit" })) Assert.Equal(HttpStatusCode.Created, garten.StatusCode);
        using (var put = await alice.SendAsync(HttpMethod.Put, "/api/notes/Gartenarbeit/Beete.md", new { content = "Beete planen" })) Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        var bob = await RestClient.NewAsync(Api, vault: "Arbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var work = await alice.VaultIdAsync();
        var gartenId = (await AliceAsync(alice, "Gartenarbeit")).Access.Id;
        var codes = new[] { await PublishAsync(alice), await PublishAsync(alice, "/Gartenarbeit/Beete.md") };
        var (bobsNotes, bobs) = await OpenAsync(new UserCredential(bob.User.Id, bob.UserKey), "alices-work");
        Assert.Single(await Publications.ListAsync(bobs, bobsNotes, P("/alices-work"), null, Ct));

        using (var deleted = await alice.SendAsync(HttpMethod.Post, "/api/account/delete", new { password = RestClient.Password, confirm = alice.Username }))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        Assert.NotNull((await app.Database.GetCollection<VaultDocument>("vaults").Find(v => v.Id == work).SingleAsync(Ct)).ClosingAt);
        foreach (var code in codes) Assert.False(await OpensAsync(code));
        foreach (var vaultId in new[] { work, gartenId })
        {
            foreach (var collection in new[] { "publications", "publication_files", "publishing" }) Assert.Equal(0, await CountAsync(collection, vaultId));
        }

        (bobsNotes, bobs) = await OpenAsync(new UserCredential(bob.User.Id, bob.UserKey), "alices-work");
        Assert.Empty(await Publications.ListAsync(bobs, bobsNotes, P("/alices-work"), null, Ct));
    }
}
