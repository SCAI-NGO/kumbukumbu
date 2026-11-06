// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;
using Kumbu.Core.Sh;
using Kumbu.Server.Accounts;
using Kumbu.Server.Tests.Accounts;
using Kumbu.Server.Attachments;
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
/// Publishing, updating and withdrawing one note (publish spec §3.1–3.5, §3.10): only the vault's owner, through her
/// session or a credential given "May publish"; never a member, a device or the dev token; never what a mark covers;
/// the copy filtered, with its images stripped; "older" following the note and its attachments.
/// </summary>
[Collection("app")]
public sealed partial class PublicationsTests(KumbuAppFactory app)
{
    private const string Questions = "/Work/Routing/Questions.md";

    // Spec §6's note, its embed naming a JPEG, so that the name and the media type agree.
    private const string Spec6 = """
        ---
        status: draft
        ---
        # Questions on routing #routing

        1. Should the chatbot move the user between screens? See [[Routing master]].
        2. Is there a list of routes? [Router docs](https://example.org/router)
        %% ask about deep links later %%
        ![[flow.jpg]]
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Publications Publications => Api.Services.GetRequiredService<Publications>();

    private PublicationStore Store => Api.Services.GetRequiredService<PublicationStore>();

    private VaultCatalog Catalog => Api.Services.GetRequiredService<VaultCatalog>();

    private static readonly WriterRef BySession = WriterRef.ForOwner(WriterRef.Session, null);

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    [GeneratedRegex("^https?://[^/]+/s#([0-9A-HJKMNP-TV-Z]{4}-){7}[0-9A-HJKMNP-TV-Z]{4}$")]
    private static partial Regex LinkPattern();

    private sealed record Opened(EncryptedNoteStore Notes, VaultAccess Access);

    /// <summary>A store for <paramref name="credential"/>, as one request has it, and its access to <paramref name="vault"/>.</summary>
    private async Task<Opened> OpenAsync(ICredential credential, string vault, IServiceProvider? services = null)
    {
        var notes = new EncryptedNoteStore((services ?? Api.Services).GetRequiredService<VaultCatalog>(), credential);
        return new Opened(notes, (await notes.UnlockedVaultsAsync(Ct)).Single(a => a.Name == vault));
    }

    private Task<Opened> AliceAsync(RestClient alice, string vault = "Work", IServiceProvider? services = null) =>
        OpenAsync(new UserCredential(alice.User.Id, alice.UserKey), vault, services);

    /// <summary>Alice with <c>Work</c> holding spec §6's note and <c>Routing/flow.jpg</c>, a camera's JPEG with GPS data.</summary>
    private async Task<RestClient> AliceWithQuestionsAsync()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateFileAsync("Routing/flow.jpg", ImageMetadataTests.JpegWithGps(6));
        await alice.CreateAsync("Routing/Questions.md", Spec6);
        return alice;
    }

    private async Task<PublishedNote> PublishAsync(RestClient alice, string note = Questions, IServiceProvider? services = null)
    {
        var (notes, access) = await AliceAsync(alice, P(note).Vault!, services);
        return await (services?.GetRequiredService<Publications>() ?? Publications)
            .PublishAsync(access, notes, P(note), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct);
    }

    private async Task<PublishedNote> UpdateAsync(RestClient alice, string note = Questions)
    {
        var (notes, access) = await AliceAsync(alice, P(note).Vault!);
        return await Publications.UpdateAsync(access, notes, P(note), alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct);
    }

    private async Task<PublicationState?> StateAsync(RestClient alice, string note = Questions)
    {
        var (notes, access) = await AliceAsync(alice, P(note).Vault!);
        return await Publications.StateAsync(access, notes, P(note), null, Ct);
    }

    private static string CodeOf(PublishedNote published) => ShareCode.Parse(published.Link[(published.Link.IndexOf('#') + 1)..])!;

    private static async Task<PublishRefusedException> RefusedAsync(Func<Task> act, ShellErrorKind kind, string? code, string? sentence = null)
    {
        var e = await Assert.ThrowsAsync<PublishRefusedException>(act);
        Assert.Equal(kind, e.Kind);
        Assert.Equal(code, e.Code);
        if (sentence is not null) Assert.Equal(sentence, e.Message);
        return e;
    }

    private async Task<(string Key, string Id)> KeyAsync(
        RestClient user, Dictionary<string, KeyRight> vaults, ApiKeyKind kind = ApiKeyKind.Agent, params string[] publish)
    {
        var (key, info) = await Api.Services.GetRequiredService<ApiKeys>().CreateAsync(
            user.User, user.UserKey, DateTime.UtcNow, "Claude " + Guid.NewGuid().ToString("N")[..6], vaults, Ct, kind: kind, publish: publish.Length == 0 ? null : new HashSet<string>(publish));
        return (key, info.Id);
    }

    private async Task PlantMarksAsync(UnlockedVault vault, params string[] marks)
    {
        var (doc, record) = await Store.ReadAsync(null, vault, Ct);
        using var session = await Catalog.StartSessionAsync(Ct);
        await Store.WriteAsync(session, vault, doc, record with { Unpublishable = [.. record.Unpublishable, .. marks] }, Ct);
    }

    private Task<long> CountAsync(string collection, string vaultId) =>
        app.Database.GetCollection<BsonDocument>(collection).CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("vault", vaultId), cancellationToken: Ct);

    [Fact]
    public async Task The_owner_publishes_and_gets_a_link_once()
    {
        var alice = await AliceWithQuestionsAsync();

        var published = await PublishAsync(alice);

        Assert.Matches(LinkPattern(), published.Link);
        Assert.StartsWith(KumbuAppFactory.PublicUrl + "/s#", published.Link, StringComparison.Ordinal);
        var (notes, access) = await AliceAsync(alice);
        var record = (await Store.ReadAsync(null, access.Vault, Ct)).Record;
        var entry = Assert.Single(record.Publications);
        Assert.Equal(Questions, entry.Note);
        var logged = Assert.Single(record.Log);
        Assert.Equal(PublishingKinds.Published, logged.Kind);
        Assert.Equal(Questions, logged.Note);
        Assert.Null(logged.Reason);

        var opened = await Store.OpenCopyAsync(CodeOf(published), Ct);
        Assert.NotNull(opened);
        Assert.Equal(alice.User.Id, opened.Value.Owner);
        Assert.Equal(published.SnapshotAt, opened.Value.Copy.SnapshotAt);

        var listed = Assert.Single(await Publications.ListAsync(access, notes, P("/Work"), null, Ct));
        Assert.Equal(P(Questions), listed.Note);
        Assert.Null(listed.Link);
        Assert.Equal(published.SnapshotAt, listed.SnapshotAt);
        Assert.False(listed.Older);
        // Only a link base gives the link again, the same one.
        Assert.Equal(published.Link, Assert.Single(await Publications.ListAsync(access, notes, P("/Work"), KumbuAppFactory.PublicUrl, Ct)).Link);
        Assert.Empty(await Publications.ListAsync(access, notes, P("/Work/Other"), null, Ct));

        await RefusedAsync(
            () => PublishAsync(alice), ShellErrorKind.Conflict, null, $"{Questions} is already published; publish update makes a new copy under the same link");
        Assert.Single((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications);
        Assert.Equal(1, await CountAsync("publications", access.Id));
    }

    [Fact]
    public async Task A_member_never_publishes()
    {
        var alice = await AliceWithQuestionsAsync();
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var vaultId = await alice.VaultIdAsync();
        var (key, keyId) = await KeyAsync(bob, new() { [vaultId] = KeyRight.Write });
        const string note = "/alices-work/Routing/Questions.md";

        foreach (var credential in new ICredential[] { new UserCredential(bob.User.Id, bob.UserKey), new ApiKeyCredential(keyId, key) })
        {
            var (notes, access) = await OpenAsync(credential, "alices-work");
            Assert.Equal(AccessLevel.Write, access.Level);
            await RefusedAsync(
                () => Publications.PublishAsync(access, notes, P(note), bob.User.Id, bob.Username, BySession, KumbuAppFactory.PublicUrl, Ct),
                ShellErrorKind.Refused, Refusals.NotOwner, "only the owner of alices-work can publish its notes");
            await RefusedAsync(
                () => Publications.UpdateAsync(access, notes, P(note), bob.Username, BySession, KumbuAppFactory.PublicUrl, Ct), ShellErrorKind.Refused, Refusals.NotOwner);
            await RefusedAsync(() => Publications.WithdrawAsync(access, notes, P(note), bob.Username, BySession, Ct), ShellErrorKind.Refused, Refusals.NotOwner);
        }

        Assert.Equal(0, await CountAsync("publications", vaultId));
        Assert.Equal(0, await CountAsync("publishing", vaultId));
    }

    [Fact]
    public async Task A_key_without_may_publish_is_refused()
    {
        var alice = await AliceWithQuestionsAsync();
        var vaultId = await alice.VaultIdAsync();
        var (key, keyId) = await KeyAsync(alice, new() { [vaultId] = KeyRight.Write });
        var (notes, access) = await OpenAsync(new ApiKeyCredential(keyId, key), "Work");

        await RefusedAsync(
            () => Publications.PublishAsync(access, notes, P(Questions), alice.User.Id, "Claude", WriterRef.ForOwner(WriterRef.Agent, keyId), KumbuAppFactory.PublicUrl, Ct),
            ShellErrorKind.Refused, Refusals.NoPublishRight,
            "this key may not publish in Work; its owner can allow it (\"May publish\") under API keys or Connections in the web app");
        await RefusedAsync(() => Publications.WithdrawAsync(access, notes, P(Questions), "Claude", WriterRef.ForOwner(WriterRef.Agent, keyId), Ct), ShellErrorKind.Refused, Refusals.NoPublishRight);
        Assert.Equal(0, await CountAsync("publications", vaultId));

        // The same key's sibling with the right publishes.
        var (allowed, allowedId) = await KeyAsync(alice, new() { [vaultId] = KeyRight.Write }, publish: vaultId);
        var (n2, a2) = await OpenAsync(new ApiKeyCredential(allowedId, allowed), "Work");
        var published = await Publications.PublishAsync(a2, n2, P(Questions), alice.User.Id, "Claude", WriterRef.ForOwner(WriterRef.Agent, allowedId), KumbuAppFactory.PublicUrl, Ct);
        Assert.Matches(LinkPattern(), published.Link);
    }

    [Fact]
    public async Task A_key_with_may_publish_on_one_vault_is_refused_on_another()
    {
        var alice = await AliceWithQuestionsAsync();
        using (var created = await alice.SendAsync(HttpMethod.Post, "/api/vaults", new { name = "Gartenarbeit" })) Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using (var put = await alice.SendAsync(HttpMethod.Put, "/api/notes/Gartenarbeit/Beete.md", new { content = "Beete planen" })) Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        var work = await alice.VaultIdAsync();
        var garten = (await Catalog.OpenForUserAsync(alice.User.Id, alice.UserKey, Ct)).Single(v => v.Name == "Gartenarbeit").Id;
        var (key, keyId) = await KeyAsync(alice, new() { [work] = KeyRight.Write, [garten] = KeyRight.Write }, publish: work);
        var credential = new ApiKeyCredential(keyId, key);
        var by = WriterRef.ForOwner(WriterRef.Agent, keyId);

        var (gNotes, gAccess) = await OpenAsync(credential, "Gartenarbeit");
        Assert.True(gAccess.IsOwner);
        await RefusedAsync(
            () => Publications.PublishAsync(gAccess, gNotes, P("/Gartenarbeit/Beete.md"), alice.User.Id, "Claude", by, KumbuAppFactory.PublicUrl, Ct),
            ShellErrorKind.Refused, Refusals.NoPublishRight,
            "this key may not publish in Gartenarbeit; its owner can allow it (\"May publish\") under API keys or Connections in the web app");
        Assert.Equal(0, await CountAsync("publications", garten));

        var (wNotes, wAccess) = await OpenAsync(credential, "Work");
        await Publications.PublishAsync(wAccess, wNotes, P(Questions), alice.User.Id, "Claude", by, KumbuAppFactory.PublicUrl, Ct);
        Assert.Equal(1, await CountAsync("publications", work));
    }

    [Fact]
    public async Task A_device_key_is_refused()
    {
        var alice = await AliceWithQuestionsAsync();
        var vaultId = await alice.VaultIdAsync();
        var (key, keyId) = await KeyAsync(alice, new() { [vaultId] = KeyRight.Write }, ApiKeyKind.Device);
        var by = WriterRef.ForOwner(WriterRef.Device, keyId);
        var (notes, access) = await OpenAsync(new ApiKeyCredential(keyId, key, device: true), "Work");

        await RefusedAsync(
            () => Publications.PublishAsync(access, notes, P(Questions), alice.User.Id, "Laptop", by, KumbuAppFactory.PublicUrl, Ct), ShellErrorKind.Refused, Refusals.NoPublishRight);

        // Even with the flag planted into its slot, a device never publishes.
        var slots = app.Database.GetCollection<BsonDocument>("slots");
        var filter = Builders<BsonDocument>.Filter.Eq("apiKey", keyId) & Builders<BsonDocument>.Filter.Eq("vault", vaultId);
        var payload = LegacySlot.Open(key, vaultId, keyId, (await slots.Find(filter).SingleAsync(Ct))["wrapped"].AsByteArray);
        await slots.UpdateOneAsync(
            filter, Builders<BsonDocument>.Update.Set("wrapped", LegacySlot.SealV2(key, vaultId, keyId, payload.VaultKey, payload.Rights, null, null, publish: true)), cancellationToken: Ct);
        var (n2, a2) = await OpenAsync(new ApiKeyCredential(keyId, key, device: true), "Work");
        await RefusedAsync(
            () => Publications.PublishAsync(a2, n2, P(Questions), alice.User.Id, "Laptop", by, KumbuAppFactory.PublicUrl, Ct), ShellErrorKind.Refused, Refusals.NoPublishRight);
        Assert.Equal(0, await CountAsync("publications", vaultId));
    }

    [Fact]
    public async Task The_dev_token_is_refused()
    {
        var (notes, access) = await OpenAsync(new FixedCredential(KumbuAppFactory.Token), (await Catalog.OpenAsync(KumbuAppFactory.Token, Ct)).First().Name);
        Assert.True(access.IsOwner);
        var note = VaultPath.Root.Child(access.Name).Child("Anything.md");

        await RefusedAsync(
            () => Publications.PublishAsync(access, notes, note, "dev", "dev", WriterRef.Server, KumbuAppFactory.PublicUrl, Ct), ShellErrorKind.Refused, Refusals.NoPublishRight);
        await RefusedAsync(() => Publications.WithdrawAsync(access, notes, note, "dev", WriterRef.Server, Ct), ShellErrorKind.Refused, Refusals.NoPublishRight);
    }

    [Fact]
    public async Task Switched_off_refuses_publishing_and_allows_withdrawing()
    {
        var alice = await AliceWithQuestionsAsync();
        var published = await PublishAsync(alice);
        await using var off = Api.WithWebHostBuilder(b => b.UseSetting("Kumbu:Publish:Enabled", "false"));
        var publications = off.Services.GetRequiredService<Publications>();
        Assert.False(publications.Enabled);
        Assert.True(Publications.Enabled);
        var (notes, access) = await AliceAsync(alice, services: off.Services);

        await RefusedAsync(
            () => publications.PublishAsync(access, notes, P("/Work/Routing/Other.md"), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct),
            ShellErrorKind.Refused, Refusals.PublishingOff, "publishing is switched off on this server");
        await RefusedAsync(
            () => publications.UpdateAsync(access, notes, P(Questions), alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct),
            ShellErrorKind.Refused, Refusals.PublishingOff, "publishing is switched off on this server");
        Assert.Single(await publications.ListAsync(access, notes, P("/Work"), null, Ct));

        await publications.WithdrawAsync(access, notes, P(Questions), alice.Username, BySession, Ct);

        Assert.Null(await Store.OpenCopyAsync(CodeOf(published), Ct));
        Assert.Empty(await publications.ListAsync(access, notes, P("/Work"), null, Ct));
    }

    [Fact]
    public async Task A_planted_mark_blocks_the_owner_too()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Private/Plan.md", "a");
        await alice.CreateAsync("Private/Deep/Plan.md", "b");
        await alice.CreateAsync("Notes/x.md", "c");
        await alice.CreateAsync("Notes/y.md", "d");
        var (_, access) = await AliceAsync(alice);
        await PlantMarksAsync(access.Vault, "/Work/Private", "/Work/Notes/x.md");

        foreach (var note in new[] { "/Work/Private/Plan.md", "/Work/Private/Deep/Plan.md", "/Work/Notes/x.md" })
        {
            await RefusedAsync(
                () => PublishAsync(alice, note), ShellErrorKind.Refused, Refusals.Unpublishable,
                $"{note} is marked unpublishable; only the vault's owner can lift the mark, in the web app");
        }

        Assert.Equal(0, await CountAsync("publications", access.Id));
        await PublishAsync(alice, "/Work/Notes/y.md");

        // A mark set after publishing blocks the update too.
        await PlantMarksAsync(access.Vault, "/Work/Notes/y.md");
        await RefusedAsync(() => UpdateAsync(alice, "/Work/Notes/y.md"), ShellErrorKind.Refused, Refusals.Unpublishable);
    }

    [Fact]
    public async Task A_mark_covers_only_its_own_folder()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateAsync("Private/x.md", "a");
        var (_, access) = await AliceAsync(alice);
        await PlantMarksAsync(access.Vault, "/Work/Priv", "/Work/Private/x");

        await PublishAsync(alice, "/Work/Private/x.md");

        Assert.False(Publications.Covered(["/Work/Priv"], "/Work/Private/x.md"));
        Assert.True(Publications.Covered(["/Work/Private"], "/Work/Private/x.md"));
        Assert.True(Publications.Covered(["/Work/Private/x.md"], "/Work/Private/x.md"));
    }

    [Fact]
    public async Task The_copy_is_filtered_and_holds_the_stripped_image()
    {
        var alice = await AliceWithQuestionsAsync();

        var published = await PublishAsync(alice);

        Assert.Empty(published.Plain);
        var copy = (await Store.OpenCopyAsync(CodeOf(published), Ct))!.Value.Copy;
        Assert.Equal("Questions on routing", copy.Title);
        Assert.Equal(
            "1. Should the chatbot move the user between screens? See Routing master.\n2. Is there a list of routes? [Router docs](https://example.org/router)\n![flow.jpg](f/0)",
            copy.Markdown);
        var file = Assert.Single(copy.Files);
        Assert.Equal((0, "flow.jpg", "image/jpeg"), (file.Index, file.Name, file.MediaType));

        var (bytes, mediaType) = (await Store.OpenFileAsync(CodeOf(published), 0, Ct))!.Value;
        Assert.Equal("image/jpeg", mediaType);
        Assert.Equal(ImageMetadata.Strip(ImageMetadataTests.JpegWithGps(6), "image/jpeg"), bytes);
        Assert.Equal(-1, bytes.AsSpan().IndexOf("GPS48N"u8));

        var (_, access) = await AliceAsync(alice);
        var entry = Assert.Single((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications);
        Assert.Equal("/Work/Routing/flow.jpg", Assert.Single(entry.Files).Path);
    }

    [Fact]
    public async Task An_embed_it_cannot_copy_is_plain_text_and_stores_no_bytes()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        var png = ImageMetadataTests.Png(ImageMetadataTests.Ihdr, ImageMetadataTests.PngChunk("IDAT", [0x78, 0x9C, 1, 2]), ImageMetadataTests.PngChunk("IEND", []));
        await alice.CreateFileAsync("Private/secret.png", png);
        await alice.CreateFileAsync("Routing/report.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"));
        // A PNG signature with a chunk Strip doesn't know: not copyable.
        await alice.CreateFileAsync(
            "Routing/odd.png",
            ImageMetadataTests.Png(ImageMetadataTests.Ihdr, ImageMetadataTests.PngChunk("ABCD", [1, 2, 3]), ImageMetadataTests.PngChunk("IDAT", [0x78, 0x9C]), ImageMetadataTests.PngChunk("IEND", [])));
        Assert.Null(ImageMetadata.Strip(ImageMetadataTests.Png(
            ImageMetadataTests.Ihdr, ImageMetadataTests.PngChunk("ABCD", [1, 2, 3]), ImageMetadataTests.PngChunk("IDAT", [0x78, 0x9C]), ImageMetadataTests.PngChunk("IEND", [])), "image/png"));
        await alice.CreateAsync("Routing/Questions.md", "a ![[secret.png]] b\n![[gone.png]]\n![[report.pdf]]\n![[odd.png]]");
        var (_, access) = await AliceAsync(alice);
        await PlantMarksAsync(access.Vault, "/Work/Private");

        var published = await PublishAsync(alice);

        var copy = (await Store.OpenCopyAsync(CodeOf(published), Ct))!.Value.Copy;
        Assert.Equal("a secret.png b\ngone.png\nreport.pdf\nodd.png", copy.Markdown);
        Assert.Empty(copy.Files);
        Assert.Equal(0, await CountAsync("publication_files", access.Id));
        Assert.Equal(["secret.png", "gone.png", "report.pdf", "odd.png", "PDFs are not published yet"], published.Plain);
        Assert.Empty(Assert.Single((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications).Files);
    }

    [Fact]
    public async Task Older_follows_the_note_and_its_attachments()
    {
        var alice = await AliceWithQuestionsAsync();
        var published = await PublishAsync(alice);
        var state = await StateAsync(alice);
        Assert.NotNull(state);
        Assert.False(state.Older);
        Assert.Null(state.ChangedAt);
        Assert.Equal(published.SnapshotAt, state.SnapshotAt);
        Assert.Equal(alice.Username, state.PublishedBy);

        var version = (await alice.ReadAsync("Routing/Questions.md"))["version"]!.GetValue<string>();
        using (var put = await alice.SendAsync(HttpMethod.Put, alice.Url("Routing/Questions.md"), new { content = Spec6 + "\n3. One more", version })) Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var edited = (await (await AliceAsync(alice)).Notes.GetAsync(P(Questions), Ct))!;
        state = await StateAsync(alice);
        Assert.True(state!.Older);
        Assert.Equal(edited.LastWriteAt, state.ChangedAt);

        await UpdateAsync(alice);
        state = await StateAsync(alice);
        Assert.False(state!.Older);
        Assert.Null(state.ChangedAt);

        var image = (await (await AliceAsync(alice)).Notes.GetAttachmentAsync(P("/Work/Routing/flow.jpg"), Ct))!;
        using (var put = await alice.PutFileAsync("Routing/flow.jpg", ImageMetadataTests.JpegWithGps(1), image.Version)) Assert.True(put.IsSuccessStatusCode);
        var replaced = (await (await AliceAsync(alice)).Notes.GetAttachmentAsync(P("/Work/Routing/flow.jpg"), Ct))!;
        state = await StateAsync(alice);
        Assert.True(state!.Older);
        Assert.Equal(replaced.LastWriteAt, state.ChangedAt);

        await UpdateAsync(alice);
        Assert.False((await StateAsync(alice))!.Older);
        using (var removed = await alice.SendAsync(HttpMethod.Delete, alice.Url("Routing/flow.jpg", "files"))) Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        state = await StateAsync(alice);
        Assert.True(state!.Older);

        // Nothing is withdrawn: the copy holds its own bytes (decision 14).
        Assert.NotNull(await Store.OpenFileAsync(CodeOf(published), 0, Ct));
    }

    [Fact]
    public async Task Update_keeps_the_link()
    {
        var alice = await AliceWithQuestionsAsync();
        var first = await PublishAsync(alice);
        var at = (await Store.OpenCopyAsync(CodeOf(first), Ct))!.Value.Id;
        var version = (await alice.ReadAsync("Routing/Questions.md"))["version"]!.GetValue<string>();
        using (var put = await alice.SendAsync(HttpMethod.Put, alice.Url("Routing/Questions.md"), new { content = "# Questions on routing\n\nNew text", version })) Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var second = await UpdateAsync(alice);

        Assert.Equal(first.Link, second.Link);
        Assert.True(second.SnapshotAt > first.SnapshotAt);
        var opened = (await Store.OpenCopyAsync(CodeOf(second), Ct))!.Value;
        Assert.Equal(at, opened.Id);
        Assert.Equal("New text", opened.Copy.Markdown);
        Assert.Equal(second.SnapshotAt, opened.Copy.SnapshotAt);
        Assert.Empty(opened.Copy.Files);
        var (_, access) = await AliceAsync(alice);
        var record = (await Store.ReadAsync(null, access.Vault, Ct)).Record;
        Assert.Equal(second.SnapshotAt, Assert.Single(record.Publications).SnapshotAt);
        Assert.Equal([PublishingKinds.Published, PublishingKinds.Updated], record.Log.Select(l => l.Kind));
        Assert.Equal(0, await CountAsync("publication_files", access.Id));

        await RefusedAsync(() => UpdateAsync(alice, "/Work/Routing/Nothing.md"), ShellErrorKind.NotFound, null, "/Work/Routing/Nothing.md: no such note");
        await alice.CreateAsync("Routing/Other.md", "x");
        await RefusedAsync(() => UpdateAsync(alice, "/Work/Routing/Other.md"), ShellErrorKind.NotFound, null, "/Work/Routing/Other.md is not published");
    }

    [Fact]
    public async Task Withdraw_kills_the_link()
    {
        var alice = await AliceWithQuestionsAsync();
        var published = await PublishAsync(alice);
        var (notes, access) = await AliceAsync(alice);
        Assert.Equal(1, await CountAsync("publication_files", access.Id));

        await Publications.WithdrawAsync(access, notes, P(Questions), alice.Username, BySession, Ct);

        Assert.Null(await Store.OpenCopyAsync(CodeOf(published), Ct));
        Assert.Equal(0, await CountAsync("publications", access.Id));
        Assert.Equal(0, await CountAsync("publication_files", access.Id));
        Assert.Null(await StateAsync(alice));
        var record = (await Store.ReadAsync(null, access.Vault, Ct)).Record;
        Assert.Empty(record.Publications);
        var withdrawn = record.Log[^1];
        Assert.Equal((PublishingKinds.Withdrawn, Questions, WithdrawalReasons.ByRight), (withdrawn.Kind, withdrawn.Note, withdrawn.Reason));
        await RefusedAsync(
            () => Publications.WithdrawAsync(access, notes, P(Questions), alice.Username, BySession, Ct), ShellErrorKind.NotFound, null, $"{Questions} is not published");

        // Publishing again makes a new link.
        Assert.NotEqual(published.Link, (await PublishAsync(alice)).Link);
    }

    [Fact]
    public async Task A_member_sees_the_path_alone()
    {
        var alice = await AliceWithQuestionsAsync();
        await PublishAsync(alice);
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        var vaultId = await alice.VaultIdAsync();
        var (key, keyId) = await KeyAsync(bob, new() { [vaultId] = KeyRight.Write });
        var expected = new PublicationState(P("/alices-work/Routing/Questions.md"));

        foreach (var credential in new ICredential[] { new UserCredential(bob.User.Id, bob.UserKey), new ApiKeyCredential(keyId, key) })
        {
            var (notes, access) = await OpenAsync(credential, "alices-work");
            Assert.Equal(expected, Assert.Single(await Publications.ListAsync(access, notes, P("/alices-work"), KumbuAppFactory.PublicUrl, Ct)));
            Assert.Equal(expected, await Publications.StateAsync(access, notes, P("/alices-work/Routing/Questions.md"), KumbuAppFactory.PublicUrl, Ct));
            Assert.Null(await Publications.StateAsync(access, notes, P("/alices-work/Routing/flow.jpg"), null, Ct));
        }
    }

    [Fact]
    public async Task No_record_prints_a_code_a_link_or_a_key()
    {
        var alice = await AliceWithQuestionsAsync();
        var published = await PublishAsync(alice);
        var (notes, access) = await AliceAsync(alice);
        var record = (await Store.ReadAsync(null, access.Vault, Ct)).Record;
        var entry = Assert.Single(record.Publications);
        var state = (await Publications.StateAsync(access, notes, P(Questions), KumbuAppFactory.PublicUrl, Ct))!;
        Assert.Equal(published.Link, state.Link);
        var code = CodeOf(published);
        string[] secrets = [code, published.Link[(published.Link.IndexOf('#') + 1)..], published.Link, Convert.ToBase64String(entry.Key), Convert.ToHexString(entry.Key)];

        foreach (var text in new[] { published.ToString(), state.ToString(), entry.ToString(), record.ToString(), new ReaderSlot(entry.Key, alice.User.Id).ToString(),
            new PublishOutcome(published.Link, published.SnapshotAt, published.Plain, false).ToString() })
        {
            Assert.DoesNotContain(secrets, s => text.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        // What isn't secret still shows.
        Assert.Contains(Questions, entry.ToString(), StringComparison.Ordinal);
        Assert.Contains(Questions, state.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_the_root_or_the_vault_lists_everything()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateAsync("a.md", "a");
        await alice.CreateAsync("Deep/Down/b.md", "b");
        await PublishAsync(alice, "/Work/a.md");
        await PublishAsync(alice, "/Work/Deep/Down/b.md");
        var (notes, access) = await AliceAsync(alice);

        foreach (var under in new[] { "/", "/Work" })
        {
            Assert.Equal([P("/Work/Deep/Down/b.md"), P("/Work/a.md")], (await Publications.ListAsync(access, notes, P(under), null, Ct)).Select(s => s.Note));
        }

        Assert.Equal([P("/Work/Deep/Down/b.md")], (await Publications.ListAsync(access, notes, P("/Work/Deep"), null, Ct)).Select(s => s.Note));
    }

    [Fact]
    public async Task Withdrawing_what_is_neither_published_nor_a_note_is_no_such_note()
    {
        var alice = await AliceWithQuestionsAsync();
        await alice.CreateAsync("Routing/Other.md", "x");
        var (notes, access) = await AliceAsync(alice);

        await RefusedAsync(
            () => Publications.WithdrawAsync(access, notes, P("/Work/Routing/Nothing.md"), alice.Username, BySession, Ct), ShellErrorKind.NotFound, null, "/Work/Routing/Nothing.md: no such note");
        await RefusedAsync(
            () => Publications.WithdrawAsync(access, notes, P("/Work/Routing/Other.md"), alice.Username, BySession, Ct), ShellErrorKind.NotFound, null, "/Work/Routing/Other.md is not published");

        // An entry whose note is gone is still withdrawn by its entry.
        var published = await PublishAsync(alice);
        var (doc, record) = await Store.ReadAsync(null, access.Vault, Ct);
        using (var session = await Catalog.StartSessionAsync(Ct))
        {
            await Store.WriteAsync(session, access.Vault, doc, record with { Publications = [record.Publications[0] with { Note = "/Work/Routing/Gone.md" }] }, Ct);
        }

        await Publications.WithdrawAsync(access, notes, P("/Work/Routing/Gone.md"), alice.Username, BySession, Ct);
        Assert.Null(await Store.OpenCopyAsync(CodeOf(published), Ct));
    }

    [Fact]
    public async Task A_member_is_told_first_that_publishing_is_switched_off()
    {
        var alice = await AliceWithQuestionsAsync();
        var bob = await RestClient.NewAsync(Api, vault: "Gartenarbeit");
        await alice.ShareWithAsync(bob, "alices-work", MemberRole.Write);
        await using var off = Api.WithWebHostBuilder(b => b.UseSetting("Kumbu:Publish:Enabled", "false"));
        var publications = off.Services.GetRequiredService<Publications>();
        var (notes, access) = await OpenAsync(new UserCredential(bob.User.Id, bob.UserKey), "alices-work", off.Services);
        const string note = "/alices-work/Routing/Questions.md";

        await RefusedAsync(
            () => publications.PublishAsync(access, notes, P(note), bob.User.Id, bob.Username, BySession, KumbuAppFactory.PublicUrl, Ct), ShellErrorKind.Refused, Refusals.PublishingOff);
        await RefusedAsync(
            () => publications.UpdateAsync(access, notes, P(note), bob.Username, BySession, KumbuAppFactory.PublicUrl, Ct), ShellErrorKind.Refused, Refusals.PublishingOff);
        // Withdrawing isn't switched off, so the member hears why he may not.
        await RefusedAsync(() => publications.WithdrawAsync(access, notes, P(note), bob.Username, BySession, Ct), ShellErrorKind.Refused, Refusals.NotOwner);
    }

    [Fact]
    public async Task A_copy_takes_images_up_to_its_limit()
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        var jpeg = ImageMetadataTests.JpegWithGps(6);
        var size = ImageMetadata.Strip(jpeg, "image/jpeg")!.Length;
        await alice.CreateFileAsync("a.jpg", jpeg);
        await alice.CreateFileAsync("b.jpg", jpeg);
        await alice.CreateAsync("Two.md", "![[a.jpg]]\n![[b.jpg]]");
        await alice.CreateAsync("Big.md", "![[a.jpg]]");
        var s = Api.Services;
        Publications Limited(long limit) => new(Catalog, Store, s.GetRequiredService<IMongoDatabase>(), s.GetRequiredService<Microsoft.Extensions.Options.IOptions<KumbuOptions>>(), s.GetRequiredService<TimeProvider>())
        {
            CopyLimit = limit,
        };
        var (notes, access) = await AliceAsync(alice);

        var two = await Limited(size + size / 2).PublishAsync(access, notes, P("/Work/Two.md"), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct);
        var big = await Limited(size - 1).PublishAsync(access, notes, P("/Work/Big.md"), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct);

        Assert.Equal(["b.jpg"], two.Plain);
        var copy = (await Store.OpenCopyAsync(CodeOf(two), Ct))!.Value.Copy;
        Assert.Equal("![a.jpg](f/0)\nb.jpg", copy.Markdown);
        Assert.Equal(["a.jpg"], big.Plain);
        Assert.Empty((await Store.OpenCopyAsync(CodeOf(big), Ct))!.Value.Copy.Files);
        Assert.Equal(64L * 1024 * 1024, Publications.CopyLimit);
    }

    [Fact]
    public async Task Parallel_publishes_of_one_note_give_one_copy()
    {
        var alice = await AliceWithQuestionsAsync();
        // The record exists, so each publish replaces it rather than inserting the first one.
        await alice.CreateAsync("Other.md", "x");
        await PublishAsync(alice, "/Work/Other.md");
        var (_, access) = await AliceAsync(alice);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try
            {
                return (Published: await PublishAsync(alice), Refused: (PublishRefusedException?)null);
            }
            catch (PublishRefusedException e)
            {
                return (Published: (PublishedNote?)null, Refused: e);
            }
        }));

        Assert.Single(attempts, a => a.Published is not null);
        Assert.All(
            attempts.Where(a => a.Refused is not null),
            a => Assert.Equal((ShellErrorKind.Conflict, $"{Questions} is already published; publish update makes a new copy under the same link"), (a.Refused!.Kind, a.Refused.Message)));
        Assert.Equal(2, await CountAsync("publications", access.Id));
        Assert.Single((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications, p => p.Note == Questions);
    }

    [Fact]
    public async Task A_lookup_taken_twice_is_a_conflict_without_the_drivers_words()
    {
        var alice = await AliceWithQuestionsAsync();
        var code = CodeOf(await PublishAsync(alice));
        var (_, access) = await AliceAsync(alice);
        using var session = await Catalog.StartSessionAsync(Ct);
        session.StartTransaction();

        var e = await Assert.ThrowsAsync<PublishingConflictException>(
            () => Store.InsertCopyAsync(session, access.Id, code, Kumbu.Core.Crypto.VaultCrypto.NewKey(), alice.User.Id, new PublishedCopy("t", "m", DateTime.UtcNow, []), [], Ct));

        Assert.Null(e.InnerException);
        Assert.Equal("The vault's publications changed meanwhile; try again.", e.Message);
        await session.AbortTransactionAsync(Ct);
    }
}
