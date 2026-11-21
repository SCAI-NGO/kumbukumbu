// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;
using Kumbu.Server.Publishing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>
/// A copy is made before the vault's gate is taken for its write. A move, a removal or a mark that lands in between
/// must leave the copy as a fresh one would be: each test lets one request land there (the clock is asked exactly
/// there, once the copy is made) and asserts what the copy holds.
/// </summary>
[Collection("app")]
public sealed class PublishingRaceTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Api => app.PublicApi;

    private Publications Publications => Api.Services.GetRequiredService<Publications>();

    private PublicationStore Store => Api.Services.GetRequiredService<PublicationStore>();

    private static readonly WriterRef BySession = WriterRef.ForOwner(WriterRef.Session, null);

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var p) ? p : throw new ArgumentException(path);

    private async Task<(EncryptedNoteStore Notes, VaultAccess Access)> AliceAsync(RestClient alice)
    {
        var notes = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(alice.User.Id, alice.UserKey));
        return (notes, (await notes.UnlockedVaultsAsync(Ct)).Single(a => a.Name == "Work"));
    }

    private sealed class ClockWithAStep(TimeProvider inner, Action once) : TimeProvider
    {
        private Action? _once = once;

        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Exchange(ref _once, null)?.Invoke();
            return inner.GetUtcNow();
        }
    }

    private Publications WithAStep(Action once)
    {
        var s = Api.Services;
        return new Publications(
            s.GetRequiredService<VaultCatalog>(), Store, s.GetRequiredService<IMongoDatabase>(), s.GetRequiredService<IOptions<KumbuOptions>>(),
            new ClockWithAStep(s.GetRequiredService<TimeProvider>(), once));
    }

    private static void Move(RestClient alice, string from, string to)
    {
        using var moved = Task.Run(() => alice.SendAsync(HttpMethod.Post, "/api/move", new { from, to })).GetAwaiter().GetResult();
        Assert.True(moved.IsSuccessStatusCode);
    }

    private static void Delete(RestClient alice, string note)
    {
        using var deleted = Task.Run(() => alice.SendAsync(HttpMethod.Delete, alice.Url(note))).GetAwaiter().GetResult();
        Assert.True(deleted.IsSuccessStatusCode);
    }

    private async Task<RestClient> WorkAsync(bool markedFolder, string a = "[[B]] [[C]]")
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateAsync("A.md", a);
        await alice.CreateAsync("B.md", "b");
        await alice.CreateAsync("C.md", "c");
        await alice.CreateAsync("Secret/s.md", "s");
        if (markedFolder)
        {
            var (notes, access) = await AliceAsync(alice);
            await Publications.SetUnpublishableAsync(access, notes, P("/Work/Secret"), true, alice.Username, BySession, Ct);
        }

        return alice;
    }

    private async Task<PublishedCopy> CopyAsync(PublishedNote published) =>
        (await Store.OpenCopyAsync(ShareCode.Parse(published.Link[(published.Link.IndexOf('#') + 1)..])!, Ct))!.Value.Copy;

    private async Task<string[]> TitlesAsync(PublishedNote published) => [.. (await CopyAsync(published)).Notes?.Select(n => n.Title) ?? []];

    private async Task<PublishedNote> PublishAsync(RestClient alice, Publications with, int depth = 1)
    {
        var (notes, access) = await AliceAsync(alice);
        return await with.PublishAsync(access, notes, P("/Work/A.md"), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct, depth);
    }

    [Fact]
    public async Task A_note_moved_under_a_mark_while_the_bundle_is_made_stays_out_of_the_copy()
    {
        var alice = await WorkAsync(markedFolder: true);

        var published = await PublishAsync(alice, WithAStep(() => Move(alice, "/Work/B.md", "/Work/Secret/B.md")));

        Assert.Equal(["A", "C"], await TitlesAsync(published));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_note_removed_while_the_bundle_is_made_stays_out_of_the_copy(bool markedFolder)
    {
        var alice = await WorkAsync(markedFolder);

        var published = await PublishAsync(alice, WithAStep(() => Delete(alice, "B.md")));

        Assert.Equal(["A", "C"], await TitlesAsync(published));
    }

    [Fact]
    public async Task A_note_moved_under_a_mark_while_an_update_first_takes_it_stays_out_of_the_copy()
    {
        var alice = await WorkAsync(markedFolder: true);
        var published = await PublishAsync(alice, Publications, depth: 0);
        var racing = WithAStep(() => Move(alice, "/Work/B.md", "/Work/Secret/B.md"));

        var (notes, access) = await AliceAsync(alice);
        await racing.UpdateAsync(access, notes, P("/Work/A.md"), alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct, depth: 1);

        Assert.Equal(["A", "C"], await TitlesAsync(published));
    }

    [Fact]
    public async Task A_single_note_moved_under_a_mark_while_its_copy_is_made_is_not_published()
    {
        var alice = await WorkAsync(markedFolder: true, a: "alone");
        var racing = WithAStep(() => Move(alice, "/Work/A.md", "/Work/Secret/A.md"));

        await Assert.ThrowsAnyAsync<Exception>(() => PublishAsync(alice, racing, depth: 0));

        var (_, access) = await AliceAsync(alice);
        Assert.Empty((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_image_moved_under_a_mark_while_the_copy_is_made_stays_out_of_it(bool moved)
    {
        var alice = await WorkAsync(markedFolder: true, a: "![[pic.jpg]]");
        await alice.CreateFileAsync("pic.jpg", Kumbu.Server.Tests.Attachments.ImageMetadataTests.JpegWithGps(6));

        var published = await PublishAsync(alice, WithAStep(() => { if (moved) Move(alice, "/Work/pic.jpg", "/Work/Secret/pic.jpg"); }), depth: 0);

        Assert.Equal(moved ? 0 : 1, (await CopyAsync(published)).Files.Count);
    }
}
