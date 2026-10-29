// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using Kumbu.Server.Accounts;
using Kumbu.Server.Attachments;
using Kumbu.Server.Search;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Tests.Attachments;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static Kumbu.Server.Tests.Attachments.PdfJobTests;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Search;

/// <summary><c>semsearch</c> over the pages of PDFs, end to end: an uploaded PDF, its analysis, its pages embedded under its path.</summary>
[Collection("app")]
public sealed class PdfSearchTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Four pages; page 2 holds what the PDF's own bytes say, the others filler.</summary>
    private static FakePdfSidecar Sidecar() => new()
    {
        Analysis = bytes =>
        {
            var said = Encoding.ASCII.GetString(bytes.Span).Split('\n')[1][2..];
            return FakePdfSidecar.Canned(4, n => n == 2 ? $"Findings\n{said}\nmore of it" : $"filler page {n} of plain words");
        },
    };

    private static UnlockedVault Current(WebApplicationFactory<Program> host, UnlockedVault vault) =>
        host.Services.GetRequiredService<VaultCatalog>().Current(vault.Id) ?? vault;

    private static async Task<(RestClient User, UnlockedVault Vault, string Key)> UploadAsync(WebApplicationFactory<Program> host, string file, string said)
    {
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync(file, Pdf(said));
        var vault = await VaultAsync(host, user);
        var key = await user.KeyAsync("search", KeyRight.Write);
        var path = P($"/{user.Vault}/{file}");
        await EventuallyAsync(() => Task.FromResult(Current(host, vault).Embeddings.ContainsKey(path)), "the PDF's pages are embedded");
        return (user, vault, key);
    }

    [Fact]
    public async Task A_page_of_a_pdf_is_found_by_meaning_and_named_with_its_number()
    {
        await using var host = Host(app, Sidecar());
        var (user, vault, key) = await UploadAsync(host, "Papers/x.pdf", "zebras migrate across the savannah");
        var path = $"/{user.Vault}/Papers/x.pdf";

        var (isError, output) = await user.ShAsync($"semsearch zebras migrate savannah /{user.Vault}", key);
        var words = await user.ShAsync($"semsearch --words zebras migrate savannah /{user.Vault}", key);

        Assert.False(isError, output);
        Assert.StartsWith($"1. {path} p2  score ", output);
        Assert.Contains("\n   Findings zebras migrate across the savannah more of it\n", output);
        var embeddings = Current(host, vault).Embeddings[P(path)];
        Assert.Equal([1, 2, 3, 4], embeddings.Chunks.Select(c => c.StartLine));
        Assert.Equal((await StateAsync(host, user, "Papers/x.pdf")).Version, embeddings.Version);
        Assert.DoesNotContain("x.pdf", words.Text);
    }

    [Fact]
    public async Task A_trashed_pdf_loses_its_embeddings()
    {
        await using var host = Host(app, Sidecar());
        var (user, vault, key) = await UploadAsync(host, "gone.pdf", "okapis hide in rainforests");
        var path = P($"/{user.Vault}/gone.pdf");

        var removed = await user.ShAsync($"rm {path.Value}", key);
        await host.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.False(removed.IsError, removed.Text);
        Assert.False(Current(host, vault).Embeddings.ContainsKey(path));
        Assert.Equal(0, await host.Services.GetRequiredService<VaultCatalog>().Embeddings.CountDocumentsAsync(MongoDB.Driver.Builders<SealedDocument>.Filter.Eq(d => d.Vault, vault.Id), cancellationToken: Ct));
        Assert.DoesNotContain("gone.pdf", (await user.ShAsync($"semsearch okapis rainforests /{user.Vault}", key)).Text);
    }

    [Fact]
    public async Task A_newer_version_of_a_pdf_is_embedded_again()
    {
        await using var host = Host(app, Sidecar());
        var (user, vault, key) = await UploadAsync(host, "draft.pdf", "pelicans nest on cliffs");
        var path = P($"/{user.Vault}/draft.pdf");
        var first = Current(host, vault).Embeddings[path].Version;

        using (var put = await user.PutFileAsync("draft.pdf", Pdf("narwhals swim under ice"), first)) Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var second = (await StateAsync(host, user, "draft.pdf")).Version;
        await EventuallyAsync(() => Task.FromResult(Current(host, vault).Embeddings.GetValueOrDefault(path)?.Version == second), "the new version is embedded");
        var (_, output) = await user.ShAsync($"semsearch -n 1 narwhals swim under ice /{user.Vault}", key);

        Assert.NotEqual(first, second);
        Assert.StartsWith($"1. {path.Value} p2  score ", output);
        Assert.Contains("narwhals swim under ice", output);
        Assert.DoesNotContain("pelicans", output);
    }

    [Fact]
    public async Task A_switch_of_the_vaults_provider_embeds_its_pdfs_again()
    {
        var remote = new FakeEmbedder(model: "remote:qwen3@64");
        await using var host = Host(app, Sidecar()).WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<EmbeddingProviders>();
            services.AddSingleton(sp => EmbeddingProviders.Of(
                sp.GetRequiredService<IEmbeddingDriver>(), new EmbeddingProvider("remote", "Remote AI", "EU, zero data retention", remote)));
        }));
        var (user, vault, key) = await UploadAsync(host, "switch.pdf", "lemurs leap between trees");
        var path = P($"/{user.Vault}/switch.pdf");

        using (var chosen = await user.SendAsync(HttpMethod.Put, $"/api/vaults/{Uri.EscapeDataString(user.Vault)}/embeddings", new { provider = "remote" }))
        {
            Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        }

        await EventuallyAsync(() => Task.FromResult(Current(host, vault).Embeddings[path].Model == "remote:qwen3@64"), "the PDF is embedded by the remote model");
        var (_, output) = await user.ShAsync($"semsearch -n 1 lemurs leap trees /{user.Vault}", key);

        Assert.Contains(remote.Seen, t => !t.Query && t.Text.Contains("lemurs leap between trees"));
        Assert.StartsWith($"1. {path.Value} p2  score ", output);
    }

    [Fact]
    public async Task An_unlock_keeps_the_pages_of_pdfs_and_embeds_a_scan_as_having_none()
    {
        var sidecar = Sidecar();
        await using var host = Host(app, sidecar);
        var (user, vault, key) = await UploadAsync(host, "kept.pdf", "tapirs graze at dusk");
        sidecar.Analysis = _ => FakePdfSidecar.Canned(2, _ => "  ");
        await user.CreateFileAsync("scan.pdf", Pdf("a scan without text"));
        var (kept, scan) = (P($"/{user.Vault}/kept.pdf"), P($"/{user.Vault}/scan.pdf"));
        await EventuallyAsync(() => Task.FromResult(Current(host, vault).Embeddings.ContainsKey(scan)), "the scan is indexed");

        var embedded = Embedded("tapirs graze at dusk");

        // Another server on the same database: the vault is loaded and caught up afresh.
        await using var again = Host(app, sidecar);
        var reopened = await VaultAsync(again, user);
        await again.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Empty(Current(host, vault).Embeddings[scan].Chunks);
        Assert.Equal([1, 2, 3, 4], Current(again, reopened).Embeddings[kept].Chunks.Select(c => c.StartLine));
        Assert.Empty(Current(again, reopened).Embeddings[scan].Chunks);
        Assert.Equal(embedded, Embedded("tapirs graze at dusk"));
        Assert.StartsWith($"1. {kept.Value} p2  score ", (await user.ShAsync($"semsearch -n 1 tapirs graze dusk /{user.Vault}", key)).Text);
    }

    /// <summary>How many texts with <paramref name="phrase"/> the app's embedder embedded as documents (not queries).</summary>
    private int Embedded(string phrase)
    {
        lock (app.Embedder.Seen) return app.Embedder.Seen.Count(t => !t.Query && t.Text.Contains(phrase, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rekeyed_vault_keeps_the_pages_of_its_pdfs_without_embedding_them_again()
    {
        await using var host = Host(app, Sidecar());
        var (user, vault, _) = await UploadAsync(host, "Papers/keys.pdf", "albatrosses glide for days");
        var path = P($"/{user.Vault}/Papers/keys.pdf");
        var embedded = Embedded("albatrosses glide for days");

        using (var rekeyed = await user.SendAsync(HttpMethod.Post, $"/api/vaults/{Uri.EscapeDataString(user.Vault)}/rekey", new { password = RestClient.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
        }

        var after = await VaultAsync(host, user);
        await host.Services.GetRequiredService<SearchIndex>().IdleAsync(TimeSpan.FromSeconds(10), Ct);
        var key = await user.KeyAsync("after the re-keying", KeyRight.Read);

        Assert.NotSame(vault, after);
        var pages = Current(host, after).Embeddings[path];
        Assert.Equal([1, 2, 3, 4], pages.Chunks.Select(c => c.StartLine));
        Assert.Equal((await StateAsync(host, user, "Papers/keys.pdf")).Version, pages.Version);
        Assert.Equal(embedded, Embedded("albatrosses glide for days"));
        Assert.StartsWith($"1. {path.Value} p2  score ", (await user.ShAsync($"semsearch -n 1 albatrosses glide days /{user.Vault}", key)).Text);
    }

    [Fact]
    public async Task A_moved_pdf_is_found_under_its_new_path_only()
    {
        await using var host = Host(app, Sidecar());
        var (user, vault, key) = await UploadAsync(host, "inbox.pdf", "puffins dive for sand eels");
        var (from, to) = (P($"/{user.Vault}/inbox.pdf"), P($"/{user.Vault}/Papers/puffins.pdf"));

        var moved = await user.ShAsync($"mv {from.Value} {to.Value}", key);
        await EventuallyAsync(() => Task.FromResult(Current(host, vault).Embeddings.ContainsKey(to) && !Current(host, vault).Embeddings.ContainsKey(from)), "the pages move along");
        var (_, output) = await user.ShAsync($"semsearch puffins dive sand eels /{user.Vault}", key);

        Assert.False(moved.IsError, moved.Text);
        Assert.StartsWith($"1. {to.Value} p2  score ", output);
        Assert.DoesNotContain(from.Value + " ", output);
    }
}
