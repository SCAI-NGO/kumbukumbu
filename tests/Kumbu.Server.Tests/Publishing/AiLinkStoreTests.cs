// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Publishing;
using Kumbu.Server.Sharing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>
/// The AI link's token and its documents (AI link spec §3.4): labels and a collection of its own, nothing in the
/// database that opens without the token, gone with every way its publication ends, nothing towards the owner's
/// storage, and a count and an hour kept in code, not left to the TTL monitor.
/// </summary>
[Collection("app")]
public sealed class AiLinkStoreTests(KumbuAppFactory app)
{
    private const string Plan = "/Work/Beds/Plan.md";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private static readonly WriterRef BySession = WriterRef.ForOwner(WriterRef.Session, null);

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private IMongoCollection<BsonDocument> AiLinks => app.Database.GetCollection<BsonDocument>("ai_links");

    private static async Task<(EncryptedNoteStore Notes, VaultAccess Access)> OpenAsync(WebApplicationFactory<Program> api, RestClient alice)
    {
        var notes = new EncryptedNoteStore(api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(alice.User.Id, alice.UserKey));
        return (notes, (await notes.UnlockedVaultsAsync(Ct)).Single(a => a.Name == "Work"));
    }

    private sealed record Linked(ReaderApiTests.Published Published, OpenedPublication Opened, string Token);

    /// <summary>A published note (<see cref="ReaderApiTests.PublishedAsync(WebApplicationFactory{Program}, int)"/>), opened
    /// with its code, and a token made for it.</summary>
    private static async Task<Linked> LinkedAsync(WebApplicationFactory<Program> api)
    {
        var published = await ReaderApiTests.PublishedAsync(api);
        var store = api.Services.GetRequiredService<PublicationStore>();
        var opened = (await store.OpenPublicationAsync(published.Code, Ct))!;
        var made = await store.CreateAiLinkAsync(opened, Ct);
        Assert.NotNull(made);
        return new Linked(published, opened, made.Value.Token);
    }

    private Task<Linked> LinkedAsync() => LinkedAsync(Api);

    private PublicationStore Store => Api.Services.GetRequiredService<PublicationStore>();

    private Task<long> LinksOfAsync(ObjectId publication) => AiLinks.CountDocumentsAsync(new BsonDocument("publication", publication), cancellationToken: Ct);

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;

    [Fact]
    public async Task A_token_opens_its_copy_and_a_share_code_opens_nothing()
    {
        var linked = await LinkedAsync();

        var opened = await Store.OpenAiLinkAsync(linked.Token, Ct);

        Assert.NotNull(opened);
        Assert.Equal(linked.Opened.Id, opened.Value.Opened.Id);
        Assert.Equal(linked.Published.Alice.User.Id, opened.Value.Opened.Owner);
        Assert.Equal("Plan for the beds", opened.Value.Opened.Copy.Title);
        Assert.Equal("image/jpeg", Assert.Single(opened.Value.Opened.Copy.Files).MediaType);
        Assert.Null(await Store.OpenAiLinkAsync(linked.Published.Code, Ct));
        Assert.Null(await Store.OpenPublicationAsync(linked.Token, Ct));
        // Labels of its own: the token's lookup is no share code's.
        Assert.False(AiLinkToken.Lookup(linked.Token).AsSpan().SequenceEqual(ShareCode.Lookup(linked.Token)));

        Assert.Equal(linked.Token, AiLinkToken.Parse(linked.Token));
        Assert.Equal(linked.Token, AiLinkToken.Parse(linked.Token.ToLowerInvariant()));
        Assert.Equal(AiLinkToken.Length, linked.Token.Length);
        Assert.Null(AiLinkToken.Parse(linked.Token[1..]));
        Assert.Null(AiLinkToken.Parse(linked.Token + "A"));
        Assert.Null(AiLinkToken.Parse(linked.Token[1..] + "U"));
        Assert.Null(AiLinkToken.Parse(""));
        Assert.Null(AiLinkToken.Parse(new string('A', 65)));
        Assert.NotEqual(AiLinkToken.New(), AiLinkToken.New());
        Assert.Equal(55, AiLinkToken.Ceiling(1));
    }

    [Fact]
    public async Task The_database_holds_no_token_and_nothing_that_opens_without_it()
    {
        var linked = await LinkedAsync();
        var token = linked.Token;

        var doc = await AiLinks.Find(new BsonDocument("publication", linked.Opened.Id)).SingleAsync(Ct);

        Assert.Equal(["_id", "vault", "publication", "lookup", "slot", "expiresAt", "count"], doc.Names);
        var bytes = doc.ToBson();
        Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(token)), "the token");
        Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(token)))), "the token's hex");
        Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(Convert.ToHexString(Encoding.UTF8.GetBytes(token)))), "the token's hex");
        Assert.False(Contains(bytes, linked.Opened.Key), "the publication key");
        Assert.False(Contains(bytes, Encoding.UTF8.GetBytes(linked.Published.Alice.User.Id)), "the owner's id");
        Assert.False(Contains(bytes, ShareCode.Lookup(linked.Published.Code)), "the share code's lookup");

        var lookup = doc["lookup"].AsByteArray;
        Assert.Equal(VaultCrypto.Derive(token, "kumbu/v1/ai-link-lookup", []), lookup);
        Assert.NotEqual(ShareCode.Lookup(token), lookup);
        var slot = doc["slot"].AsByteArray;
        var context = $"ai-link:{Convert.ToHexStringLower(lookup)}:{linked.Opened.Id}";
        // The token's key opens it; nothing the database or the share code gives does.
        Assert.NotEmpty(VaultCrypto.Open(AiLinkToken.Key(token), slot, context));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(ShareCode.ReaderKey(linked.Published.Code), slot, context));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(ShareCode.ReaderKey(token), slot, context));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(lookup, slot, context));
    }

    [Fact]
    public async Task Every_way_a_publication_ends_takes_its_tokens()
    {
        async Task AssertGoneAsync(Linked linked, string how)
        {
            Assert.True(await Store.OpenAiLinkAsync(linked.Token, Ct) is null, $"{how}: the token still opens");
            Assert.True(await LinksOfAsync(linked.Opened.Id) == 0, $"{how}: its document is left");
        }

        async Task<Linked> ShownAsync()
        {
            var linked = await LinkedAsync();
            Assert.NotNull(await Store.OpenAiLinkAsync(linked.Token, Ct));
            Assert.Equal(1, await LinksOfAsync(linked.Opened.Id));
            return linked;
        }

        var publications = Api.Services.GetRequiredService<Publications>();

        var withdrawn = await ShownAsync();
        var (notes, access) = await OpenAsync(Api, withdrawn.Published.Alice);
        await publications.WithdrawAsync(access, notes, P(Plan), withdrawn.Published.Alice.Username, BySession, Ct);
        await AssertGoneAsync(withdrawn, "withdrawn");

        var marked = await ShownAsync();
        (notes, access) = await OpenAsync(Api, marked.Published.Alice);
        await publications.SetUnpublishableAsync(access, notes, P(Plan), true, marked.Published.Alice.Username, BySession, Ct);
        await AssertGoneAsync(marked, "marked unpublishable");

        var noteDeleted = await ShownAsync();
        using (var deleted = await noteDeleted.Published.Alice.SendAsync(HttpMethod.Delete, noteDeleted.Published.Alice.Url("Beds/Plan.md")))
        {
            Assert.True(deleted.IsSuccessStatusCode, deleted.StatusCode.ToString());
        }

        await AssertGoneAsync(noteDeleted, "note deleted");

        var vaultDeleted = await ShownAsync();
        var alice = vaultDeleted.Published.Alice;
        Assert.NotNull(await Api.Services.GetRequiredService<Deletion>().DeleteVaultAsync(alice.User, alice.UserKey, "Work", RestClient.Password, "Work", null, Ct));
        await AssertGoneAsync(vaultDeleted, "vault deleted");

        var accountDeleted = await ShownAsync();
        await DeleteAccountAsync(accountDeleted.Published.Alice);
        await AssertGoneAsync(accountDeleted, "account deleted");

        // Her shared vault stays for its members, closing; her publications go at once.
        var closing = await ShownAsync();
        var bob = await RestClient.NewAsync(Api, vault: "Work");
        await closing.Published.Alice.ShareWithAsync(bob, "alices-work", MemberRole.Read);
        var vaultId = await closing.Published.Alice.VaultIdAsync();
        await DeleteAccountAsync(closing.Published.Alice);
        Assert.NotNull(await app.Database.GetCollection<VaultDocument>("vaults").Find(v => v.Id == vaultId && v.ClosingAt != null).FirstOrDefaultAsync(Ct));
        await AssertGoneAsync(closing, "account deleted, its shared vault closing");
    }

    private static async Task DeleteAccountAsync(RestClient user)
    {
        using var response = await user.SendAsync(HttpMethod.Post, "/api/account/delete", new { password = RestClient.Password, confirm = user.Username });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Tokens_count_nothing_towards_storage()
    {
        var linked = await LinkedAsync();
        var vaultId = await linked.Published.Alice.VaultIdAsync();
        var admin = Api.Services.GetRequiredService<Admin>();
        var before = await admin.BytesByVaultAsync([vaultId], Ct);

        for (var i = 1; i < 10; i++) Assert.NotNull(await Store.CreateAiLinkAsync(linked.Opened, Ct));

        Assert.Equal(10, await AiLinks.CountDocumentsAsync(new BsonDocument("vault", vaultId), cancellationToken: Ct));
        Assert.Equal(before, await admin.BytesByVaultAsync([vaultId], Ct));
    }

    [Fact]
    public async Task The_count_stops_at_the_ceiling_and_the_hour_at_expiry()
    {
        // At the real time, else MongoDB's TTL monitor deletes the documents mid-test.
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        await using var api = Api.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(time);
        }));
        var store = api.Services.GetRequiredService<PublicationStore>();
        var linked = await LinkedAsync(api);
        var ceiling = AiLinkToken.Ceiling(linked.Opened.Copy.Files.Count);
        var id = (await store.OpenAiLinkAsync(linked.Token, Ct))!.Value.Id;

        for (var i = 0; i < ceiling; i++) Assert.True(await store.CountAiLinkAsync(id, ceiling, Ct), $"request {i + 1}");

        Assert.False(await store.CountAiLinkAsync(id, ceiling, Ct));
        Assert.Equal(ceiling, (await AiLinks.Find(new BsonDocument("_id", id)).SingleAsync(Ct))["count"].ToInt32());

        var second = (await store.CreateAiLinkAsync(linked.Opened, Ct))!.Value;
        Assert.Equal(time.GetUtcNow().UtcDateTime + AiLinkToken.Lifetime, second.ExpiresAt, TimeSpan.FromMilliseconds(1));
        var secondId = (await store.OpenAiLinkAsync(second.Token, Ct))!.Value.Id;
        time.Advance(AiLinkToken.Lifetime - TimeSpan.FromSeconds(1));
        Assert.NotNull(await store.OpenAiLinkAsync(second.Token, Ct));
        time.Advance(TimeSpan.FromSeconds(1));

        // The document is still there: the TTL monitor only tidies up, the code keeps the hour.
        Assert.Equal(1, await AiLinks.CountDocumentsAsync(new BsonDocument("_id", secondId), cancellationToken: Ct));
        Assert.Null(await store.OpenAiLinkAsync(second.Token, Ct));
        Assert.False(await store.CountAiLinkAsync(secondId, ceiling, Ct));
    }
}
