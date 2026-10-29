// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;
using Kumbu.Server.Attachments;
using Kumbu.Server.Tests.Api;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static Kumbu.Server.Tests.Attachments.PdfJobTests;
using static Kumbu.Server.Tests.Vaults.VaultWorld;

namespace Kumbu.Server.Tests.Attachments;

/// <summary>The server's <see cref="IAttachmentPixels"/>: blobs opened here, pixels and analyses made in the sidecar.</summary>
[Collection("app")]
public sealed class AttachmentPixelsTests(KumbuAppFactory app)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AttachmentPixels Pixels(WebApplicationFactory<Program> host, RestClient user) => new(
        new EncryptedNoteStore(host.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey)),
        host.Services.GetRequiredService<VaultCatalog>(),
        host.Services.GetRequiredService<IPdfSidecar>(),
        host.Services.GetRequiredService<PdfAnalyses>(),
        host.Services.GetRequiredService<PdfAnalysisRunner>());

    private static VaultPath At(RestClient user, string path) => P($"/{user.Vault}/{path}");

    [Fact]
    public void The_app_gives_the_shell_these_pixels()
    {
        // Resolved per request (it needs the request's caller); the shell's optional parameter gets it.
        Assert.True(app.Services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(IAttachmentPixels)));
    }

    [Fact]
    public async Task A_page_and_an_image_are_rendered_by_the_sidecar_at_the_step_asked_for()
    {
        var sidecar = new FakePdfSidecar();
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("paper.pdf", Pdf("a paper to look at"));
        await user.CreateFileAsync("shot.png", FakePdfSidecar.SolidPng(1440, 3120));
        var pixels = Pixels(host, user);

        var page = await pixels.PageAsync(At(user, "paper.pdf"), await StateAsync(host, user, "paper.pdf"), 4, SizeStep.Large, new Crop(72, 300, 451, 260), Ct);
        var image = await pixels.ImageAsync(At(user, "shot.png"), await StateAsync(host, user, "shot.png"), SizeStep.Small, null, Ct);

        Assert.Equal((4, SizeSteps.Of(SizeStep.Large), new Crop(72, 300, 451, 260)), Assert.Single(sidecar.PageRequests));
        Assert.Equal(("image/png", 595d, 842d), (page.MediaType, page.OriginalWidth, page.OriginalHeight));
        Assert.Equal((SizeSteps.Of(SizeStep.Small), (Crop?)null), Assert.Single(sidecar.ImageRequests));
        Assert.Equal((1440d, 3120d), (image.OriginalWidth, image.OriginalHeight));
        Assert.True(Math.Max(image.Width, image.Height) <= 768);
    }

    [Fact]
    public async Task An_image_over_the_header_limits_never_reaches_the_sidecar()
    {
        var sidecar = new FakePdfSidecar();
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        // Stored behind the upload's back: the upload itself refuses it.
        var bytes = FileApiTests.Png(10_000, 10_000);
        var store = new EncryptedNoteStore(host.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey));
        var blobId = await store.PutBlobAsync(user.Vault, bytes, Ct);
        var state = new AttachmentState("image/png", bytes.Length, 10_000, 10_000, Kumbu.Core.Notes.VersionToken.Of(bytes), blobId);

        var e = await Assert.ThrowsAsync<PixelsUnavailableException>(() => Pixels(host, user).ImageAsync(At(user, "huge.png"), state, SizeStep.Medium, null, Ct));

        Assert.Equal($"/{user.Vault}/huge.png is too large to look at (100 megapixels; the limit is 50)", e.Message);
        Assert.Equal(0, sidecar.Renders);
    }

    [Fact]
    public async Task With_the_sidecar_down_rendering_says_it_is_unavailable()
    {
        await using var host = Host(app, new FakePdfSidecar { Down = true });
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("paper.pdf", Pdf("nobody renders this"));
        await user.CreateFileAsync("shot.png", FakePdfSidecar.SolidPng(20, 20));
        var (pdf, png) = (await StateAsync(host, user, "paper.pdf"), await StateAsync(host, user, "shot.png"));
        var pixels = Pixels(host, user);

        var page = await Assert.ThrowsAsync<PixelsUnavailableException>(() => pixels.PageAsync(At(user, "paper.pdf"), pdf, 1, SizeStep.Medium, null, Ct));
        var image = await Assert.ThrowsAsync<PixelsUnavailableException>(() => pixels.ImageAsync(At(user, "shot.png"), png, SizeStep.Medium, null, Ct));

        Assert.Equal("PDF rendering is unavailable right now; try again later", page.Message);
        Assert.Equal("image rendering is unavailable right now; try again later", image.Message);
    }

    [Fact]
    public async Task The_sidecars_refusals_become_sentences()
    {
        var sidecar = new RefusingSidecar();
        await using var host = Host(app, new FakePdfSidecar());
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("paper.pdf", Pdf("a short paper"));
        var state = await StateAsync(host, user, "paper.pdf");
        var pixels = new AttachmentPixels(
            new EncryptedNoteStore(host.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey)),
            host.Services.GetRequiredService<VaultCatalog>(), sidecar,
            host.Services.GetRequiredService<PdfAnalyses>(), host.Services.GetRequiredService<PdfAnalysisRunner>());

        async Task<string> Says(string code, Crop? crop = null)
        {
            sidecar.Code = code;
            return (await Assert.ThrowsAsync<PixelsUnavailableException>(() => pixels.PageAsync(At(user, "paper.pdf"), state, 40, SizeStep.Medium, crop, Ct))).Message;
        }

        Assert.Equal($"/{user.Vault}/paper.pdf has no page 40", await Says("no_such_page"));
        Assert.Equal("the crop 10,10,5000,5 lies outside page 40", await Says("bad_crop", new Crop(10, 10, 5000, 5)));
        Assert.Equal("this PDF is password-protected; Kumbu² can't read it", await Says("encrypted"));
        Assert.Equal("PDF rendering is unavailable right now; try again later", await Says("busy"));
        Assert.Equal($"rendering /{user.Vault}/paper.pdf took too long; try a smaller --size or a --crop", await Says("timeout"));
        Assert.Equal($"/{user.Vault}/paper.pdf couldn't be rendered", await Says("failed"));
    }

    private sealed class RefusingSidecar : IPdfSidecar
    {
        public string Code { get; set; } = "failed";

        public Task<SidecarAnalysis> AnalyseAsync(ReadOnlyMemory<byte> pdf, int maxPages, CancellationToken ct) => throw new SidecarException(Code, "no");

        public Task<Picture> RenderPageAsync(ReadOnlyMemory<byte> pdf, int page, StepLimits target, Crop? crop, CancellationToken ct) => throw new SidecarException(Code, "no");

        public Task<Picture> RenderImageAsync(ReadOnlyMemory<byte> image, StepLimits target, Crop? crop, CancellationToken ct) => throw new SidecarException(Code, "no");
    }

    [Fact]
    public async Task A_stored_analysis_and_its_text_are_read_without_the_sidecar()
    {
        var sidecar = new FakePdfSidecar { Analysis = _ => FakePdfSidecar.Canned(30) };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("paper.pdf", Pdf("thirty pages"));
        var state = await StateAsync(host, user, "paper.pdf");
        var pixels = Pixels(host, user);
        await EventuallyAsync(async () => await pixels.AnalysisAsync(At(user, "paper.pdf"), state, false, Ct) is not null, "the analysis is stored");
        sidecar.Down = true;

        var analysis = await pixels.AnalysisAsync(At(user, "paper.pdf"), state, true, Ct);
        var text = await pixels.PageTextAsync(At(user, "paper.pdf"), state, 7, 9, Ct);

        Assert.Equal(30, analysis!.PageCount);
        Assert.Equal(["Page 7 text", "Page 8 text", "Page 9 text"], text);
        Assert.Equal(1, sidecar.Analyses);
    }

    [Fact]
    public async Task A_pending_analysis_runs_on_demand()
    {
        var sidecar = new FakePdfSidecar { Down = true };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("paper.pdf", Pdf("asked for before its turn"));
        var state = await StateAsync(host, user, "paper.pdf");
        var pixels = Pixels(host, user);
        Assert.Null(await pixels.AnalysisAsync(At(user, "paper.pdf"), state, false, Ct));
        Assert.Null(await pixels.PageTextAsync(At(user, "paper.pdf"), state, 1, 3, Ct));

        // Still down: no analysis, and the job waits.
        Assert.Null(await pixels.AnalysisAsync(At(user, "paper.pdf"), state, true, Ct));
        sidecar.Down = false;
        var analysis = await pixels.AnalysisAsync(At(user, "paper.pdf"), state, true, Ct);

        Assert.Equal(3, analysis!.PageCount);
        Assert.Equal(["Page 2 text"], await pixels.PageTextAsync(At(user, "paper.pdf"), state, 2, 2, Ct));
    }

    [Fact]
    public async Task An_on_demand_run_past_its_wait_stays_queued_and_finishes_later()
    {
        var sidecar = new FakePdfSidecar { Down = true };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("paper.pdf", Pdf("a slow paper"));
        var state = await StateAsync(host, user, "paper.pdf");
        var pixels = Pixels(host, user);
        var runner = host.Services.GetRequiredService<PdfAnalysisRunner>();
        runner.OnDemandWait = TimeSpan.FromMilliseconds(300);
        (sidecar.Down, sidecar.Delay) = (false, TimeSpan.FromSeconds(2));

        Assert.Null(await pixels.AnalysisAsync(At(user, "paper.pdf"), state, true, Ct));

        await EventuallyAsync(async () => await pixels.AnalysisAsync(At(user, "paper.pdf"), state, false, Ct) is not null, "the analysis is stored");
    }

    [Fact]
    public async Task A_password_protected_pdf_says_so()
    {
        await using var host = Host(app, new FakePdfSidecar { FailWith = SidecarException.Encrypted });
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("locked.pdf", Pdf("locked"));
        var state = await StateAsync(host, user, "locked.pdf");

        var e = await Assert.ThrowsAsync<PixelsUnavailableException>(() => Pixels(host, user).AnalysisAsync(At(user, "locked.pdf"), state, true, Ct));

        Assert.Equal("this PDF is password-protected; Kumbu² can't read it", e.Message);
        Assert.Null(await Pixels(host, user).PageTextAsync(At(user, "locked.pdf"), state, 1, 1, Ct));
    }

    [Fact]
    public async Task A_sidecar_of_another_release_analyses_once_and_its_analysis_stays_current()
    {
        var sidecar = new FakePdfSidecar
        {
            Analysis = _ => FakePdfSidecar.Canned(2) is var c ? c with { Analysis = c.Analysis with { Analyser = "pdfjs-dist@7.0.0+rules.2" } } : c,
        };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("other.pdf", Pdf("analysed by a newer sidecar"));
        var state = await StateAsync(host, user, "other.pdf");
        var pixels = Pixels(host, user);
        await EventuallyAsync(async () => await pixels.AnalysisAsync(At(user, "other.pdf"), state, false, Ct) is not null, "the analysis is stored");

        for (var i = 0; i < 3; i++) Assert.Equal("pdfjs-dist@7.0.0+rules.2", (await pixels.AnalysisAsync(At(user, "other.pdf"), state, true, Ct))!.Analyser);

        Assert.Equal(1, sidecar.Analyses);
    }

    [Fact]
    public async Task An_analysis_stored_by_an_older_server_is_made_again_when_asked_for_and_kept_when_not()
    {
        var sidecar = new FakePdfSidecar { Down = true };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("old.pdf", Pdf("analysed before an upgrade"));
        var state = await StateAsync(host, user, "old.pdf");
        var vault = await VaultAsync(host, user);
        var analyses = host.Services.GetRequiredService<PdfAnalyses>();
        await analyses.StoreAsync(vault, Convert.FromBase64String(state.BlobId), FakePdfSidecar.Canned(5), "pdfjs-dist@5.0.0+rules.1", Ct);
        var pixels = Pixels(host, user);
        Assert.Equal(5, (await pixels.AnalysisAsync(At(user, "old.pdf"), state, false, Ct))!.PageCount);
        sidecar.Down = false;

        var again = await pixels.AnalysisAsync(At(user, "old.pdf"), state, true, Ct);

        Assert.Equal(3, again!.PageCount);
        Assert.True((await analyses.ReadAsync(vault, Convert.FromBase64String(state.BlobId), Ct))!.Current);
    }
}
