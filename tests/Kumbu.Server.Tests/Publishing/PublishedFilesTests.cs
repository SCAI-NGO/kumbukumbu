// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Accounts;
using Kumbu.Server.Publishing;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Attachments;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>
/// Publishing leaves files out (files spec, decision 6): a link or an embed of a file shows as its name without a link,
/// as one to a note left out does; the copy holds no file for it, so neither the reader nor an AI link serves one.
/// </summary>
[Collection("app")]
public sealed class PublishedFilesTests(KumbuAppFactory app)
{
    private const string Plan = "/Work/Beds/Plan.md";

    private const string PlanText = """
        # Plan

        See [[data.xlsx]], ![[data.xlsx]] and [x](data.csv).
        ![[flow.jpg]]
        """;

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

    /// <summary>Alice's Work with <c>Beds/</c> holding a spreadsheet's bytes, a CSV, a JPEG and the notes given.</summary>
    private async Task<RestClient> WorkAsync(params (string Name, string Content)[] notes)
    {
        var alice = await RestClient.NewAsync(Api, vault: "Work");
        await alice.CreateFileAsync("Beds/data.xlsx", FileDownloadTests.Bin);
        await alice.CreateFileAsync("Beds/data.csv", "bed,plant\n1,leek\n"u8.ToArray());
        await alice.CreateFileAsync("Beds/flow.jpg", ImageMetadataTests.JpegWithGps(6));
        foreach (var (name, content) in notes) await alice.CreateAsync(name, content);
        return alice;
    }

    private async Task<PublishedNote> PublishAsync(RestClient alice, string note, int depth = 0)
    {
        var (notes, access) = await AliceAsync(alice);
        return await Publications.PublishAsync(access, notes, P(note), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct, depth);
    }

    private static string Formatted(PublishedNote published) => published.Link[(published.Link.IndexOf('#') + 1)..];

    /// <summary>Alice's vault named Skills that isn't marked (hers is marked under the name Tools), with <c>x/plan.svg</c>,
    /// <c>x/Drawn.md</c> embedding it and <c>x/Plan.md</c> linking to that.</summary>
    private async Task<(RestClient Alice, EncryptedNoteStore Notes, VaultAccess Access)> UnmarkedSkillsAsync()
    {
        var alice = await RestClient.MarkedUnderAnotherNameAsync(Api);
        using (var put = await alice.SendContentAsync(HttpMethod.Put, "/api/files/Skills/x/plan.svg", new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(ImageMetadataTests.InkscapeSvg))))
        {
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        }

        foreach (var (name, content) in new[] { ("Drawn.md", "# Drawn\n\n![[plan.svg]]\n"), ("Plan.md", "# Plan\n\nSee [[Drawn]].\n") })
        {
            using var note = await alice.SendAsync(HttpMethod.Put, $"/api/notes/Skills/x/{name}", new { content });
            Assert.Equal(HttpStatusCode.Created, note.StatusCode);
        }

        var notes = new EncryptedNoteStore(Api.Services.GetRequiredService<VaultCatalog>(), new UserCredential(alice.User.Id, alice.UserKey));
        return (alice, notes, (await notes.UnlockedVaultsAsync(Ct)).Single(a => a.Name == "Skills"));
    }

    [Fact]
    public async Task An_svg_in_a_vault_named_Skills_that_isnt_marked_goes_with_its_note()
    {
        var (alice, notes, access) = await UnmarkedSkillsAsync();

        var published = await Publications.PublishAsync(access, notes, P("/Skills/x/Drawn.md"), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct);

        var copy = (await Store.OpenCopyAsync(ShareCode.Parse(Formatted(published))!, Ct))!.Value.Copy;
        Assert.Equal(new PublishedCopyFile(0, "image/svg+xml"), Assert.Single(copy.Files));
    }

    [Fact]
    public async Task An_svg_in_a_vault_named_Skills_that_isnt_marked_goes_with_its_note_in_a_publication_of_several()
    {
        var (alice, notes, access) = await UnmarkedSkillsAsync();

        var published = await Publications.PublishAsync(access, notes, P("/Skills/x/Plan.md"), alice.User.Id, alice.Username, BySession, KumbuAppFactory.PublicUrl, Ct, 1);

        var copy = (await Store.OpenCopyAsync(ShareCode.Parse(Formatted(published))!, Ct))!.Value.Copy;
        Assert.Equal(new PublishedCopyFile(0, "image/svg+xml"), Assert.Single(copy.Files));
        Assert.Equal([0], copy.Notes!.Single(n => n.Title == "Drawn").Files);
    }

    [Fact]
    public async Task A_note_is_published_without_its_files_and_shows_their_names()
    {
        var alice = await WorkAsync(("Beds/Plan.md", PlanText));

        var published = await PublishAsync(alice, Plan);

        var copy = (await Store.OpenCopyAsync(ShareCode.Parse(Formatted(published))!, Ct))!.Value.Copy;
        Assert.Equal("See data.xlsx, data.xlsx and x.\n![flow.jpg](f/0)", copy.Markdown);
        // The image goes as before; the files don't.
        Assert.Equal(new PublishedCopyFile(0, "image/jpeg"), Assert.Single(copy.Files));
        var (_, access) = await AliceAsync(alice);
        Assert.Equal("/Work/Beds/flow.jpg", Assert.Single(Assert.Single((await Store.ReadAsync(null, access.Vault, Ct)).Record.Publications).Files).Path);
        Assert.Equal(["data.xlsx"], published.Plain);

        // The reader serves the image and nothing else.
        using var reader = new ReaderApiTests.Reader(Api, "203.0.113.120");
        using (var read = await reader.PostAsync(Formatted(published))) Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using (var image = await reader.FileAsync(0)) Assert.Equal("image/jpeg", image.Content.Headers.ContentType!.MediaType);
        using (var none = await reader.FileAsync(1)) Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        // An AI link likewise: the names, no link, and no file past the image.
        using var client = Api.CreateDefaultClient(new Uri(KumbuAppFactory.PublicUrl), new CookieContainerHandler(reader.Jar));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ReaderApi.CookiePath}/ai-links");
        request.Headers.Add("Origin", RestClient.Origin);
        request.Headers.Add(ReaderApi.CopyHeader, reader.Copy);
        request.Headers.Add("X-Forwarded-For", reader.Address);
        using var made = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var path = (await RestClient.JsonAsync(made))["link"]!.GetValue<string>()[KumbuAppFactory.PublicUrl.Length..];
        using var plain = Api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri(KumbuAppFactory.PublicUrl) });
        var markdown = await plain.GetStringAsync(path, Ct);
        Assert.Contains("See data.xlsx, data.xlsx and x.", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("data.csv", markdown, StringComparison.Ordinal);
        using var file = await plain.GetAsync($"{path}/files/1", Ct);
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
    }

    [Fact]
    public async Task A_bundle_is_published_without_its_files()
    {
        var alice = await WorkAsync(("Beds/Plan.md", "# Plan\n\nSee [[Soil]].\n"), ("Beds/Soil.md", "# Soil\n\n![[data.xlsx]] and [the table](data.csv)\n"));
        var (notes, access) = await AliceAsync(alice);
        var preview = await Publications.PreviewAsync(access, notes, P(Plan), 1, [], Ct);
        Assert.Equal(["data.xlsx"], preview.Plain);
        Assert.Equal(0, preview.Bytes);

        var published = await PublishAsync(alice, Plan, depth: 1);

        var copy = (await Store.OpenCopyAsync(ShareCode.Parse(Formatted(published))!, Ct))!.Value.Copy;
        Assert.Empty(copy.Files);
        var soil = copy.Notes!.Single(n => n.Title == "Soil");
        Assert.Equal("data.xlsx and the table\n", soil.Markdown);
        Assert.Empty(soil.Files);
        Assert.Equal(["data.xlsx"], published.Plain);
    }
}
