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

        var page = await pixels.PageAsync(At(user, "paper.pdf"), await StateAsync(host, user, "paper.pdf"), 4, SizeStep.Large, new Crop(72, 300, 451, 260), 0, Ct);
        var image = await pixels.ImageAsync(At(user, "shot.png"), await StateAsync(host, user, "shot.png"), SizeStep.Small, null, 0, null, Ct);

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

        var e = await Assert.ThrowsAsync<PixelsUnavailableException>(() => Pixels(host, user).ImageAsync(At(user, "huge.png"), state, SizeStep.Medium, null, 0, null, Ct));

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

        var page = await Assert.ThrowsAsync<PixelsUnavailableException>(() => pixels.PageAsync(At(user, "paper.pdf"), pdf, 1, SizeStep.Medium, null, 0, Ct));
        var image = await Assert.ThrowsAsync<PixelsUnavailableException>(() => pixels.ImageAsync(At(user, "shot.png"), png, SizeStep.Medium, null, 0, null, Ct));

        Assert.Equal("PDF rendering is unavailable right now; try again later", page.Message);
        Assert.Equal("image rendering is unavailable right now; try again later", image.Message);
    }

    [Fact]
    public async Task Text_boxes_read_the_blob_with_the_requests_credential()
    {
        var sidecar = new FakePdfSidecar();
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        var bytes = Pdf("boxes of a paper");
        await user.CreateFileAsync("paper.pdf", bytes);
        var state = await StateAsync(host, user, "paper.pdf");
        sidecar.TextBoxes = pages => [.. pages.Select(n => new PageTextBoxes(n, 595, 842, "Hi", [new TextBox(0, 2, 10, 20, 30, 11)]))];

        var boxes = await Pixels(host, user).TextBoxesAsync(At(user, "paper.pdf"), state, [2, 3], Ct);

        Assert.Equal(bytes, Assert.Single(sidecar.TextBoxPdfs));
        Assert.Equal([2, 3], boxes.Select(b => b.Number));
        Assert.Equal(new TextBox(0, 2, 10, 20, 30, 11), boxes[0].Items[0]);
    }

    [Fact]
    public async Task Text_boxes_from_a_stopped_sidecar_are_unavailable()
    {
        await using var host = Host(app, new FakePdfSidecar { Down = true });
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("paper.pdf", Pdf("nobody boxes this"));
        var state = await StateAsync(host, user, "paper.pdf");

        var e = await Assert.ThrowsAsync<PixelsUnavailableException>(() => Pixels(host, user).TextBoxesAsync(At(user, "paper.pdf"), state, [1], Ct));

        Assert.Equal(("PDF rendering is unavailable right now; try again later", PixelsProblem.Unavailable), (e.Message, e.Problem));
    }

    [Fact]
    public async Task Text_boxes_store_nothing()
    {
        var sidecar = new FakePdfSidecar();
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("paper.pdf", Pdf("a paper whose analysis stays as it is"));
        var state = await StateAsync(host, user, "paper.pdf");
        var pixels = Pixels(host, user);
        await EventuallyAsync(async () => await pixels.AnalysisAsync(At(user, "paper.pdf"), state, false, Ct) is not null, "the analysis is stored");
        var before = System.Text.Json.JsonSerializer.Serialize(await pixels.AnalysisAsync(At(user, "paper.pdf"), state, false, Ct));
        var textBefore = await pixels.PageTextAsync(At(user, "paper.pdf"), state, 1, 3, Ct);

        await pixels.TextBoxesAsync(At(user, "paper.pdf"), state, [1, 2], Ct);

        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(await pixels.AnalysisAsync(At(user, "paper.pdf"), state, false, Ct)));
        Assert.Equal(textBefore, await pixels.PageTextAsync(At(user, "paper.pdf"), state, 1, 3, Ct));
        Assert.Equal(1, sidecar.Analyses);
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
            return (await Assert.ThrowsAsync<PixelsUnavailableException>(() => pixels.PageAsync(At(user, "paper.pdf"), state, 40, SizeStep.Medium, crop, 0, Ct))).Message;
        }

        Assert.Equal($"/{user.Vault}/paper.pdf has no page 40", await Says("no_such_page"));
        Assert.Equal("the crop 10,10,5000,5 lies outside page 40", await Says("bad_crop", new Crop(10, 10, 5000, 5)));
        Assert.Equal("this PDF is password-protected; Kumbu² can't read it", await Says("encrypted"));
        Assert.Equal("PDF rendering is unavailable right now; try again later", await Says("busy"));
        Assert.Equal($"rendering /{user.Vault}/paper.pdf took too long; try a smaller --size or a --crop", await Says("timeout"));
        Assert.Equal($"/{user.Vault}/paper.pdf couldn't be rendered", await Says("failed"));
    }

    [Fact]
    public async Task A_picture_not_turned_as_asked_couldnt_be_rendered()
    {
        var sidecar = new RefusingSidecar { Code = "failed", Message = "The image isn't turned as asked" };
        await using var host = Host(app, new FakePdfSidecar());
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("paper.pdf", Pdf("a short paper"));
        var state = await StateAsync(host, user, "paper.pdf");
        var pixels = new AttachmentPixels(
            new EncryptedNoteStore(host.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey)),
            host.Services.GetRequiredService<VaultCatalog>(), sidecar, host.Services.GetRequiredService<PdfAnalyses>(), host.Services.GetRequiredService<PdfAnalysisRunner>());

        var e = await Assert.ThrowsAsync<PixelsUnavailableException>(() => pixels.PageAsync(At(user, "paper.pdf"), state, 1, SizeStep.Medium, null, 90, Ct));

        Assert.Equal($"/{user.Vault}/paper.pdf couldn't be rendered", e.Message);
    }

    [Fact]
    public async Task A_frame_past_the_end_says_so()
    {
        var (e, path) = await RefusedFrameAsync("no_such_frame", 7);

        Assert.Equal($"{path} has no frame 7; img without --frame says how many it has", e.Message);
        Assert.Equal(PixelsProblem.BadFrame, e.Problem);
    }

    [Fact]
    public async Task Too_long_with_a_frame_says_finding_it()
    {
        var (e, path) = await RefusedFrameAsync("timeout", 7);

        Assert.Equal($"finding frame 7 of {path} took too long", e.Message);
        Assert.Equal(PixelsProblem.Failed, e.Problem);
    }

    [Fact]
    public async Task Too_long_without_a_frame_is_as_it_was()
    {
        var (e, path) = await RefusedFrameAsync("timeout", null);

        Assert.Equal($"rendering {path} took too long; try a smaller --size or a --crop", e.Message);
    }

    [Fact]
    public async Task A_GIF_too_large_for_a_frame_says_so()
    {
        var (e, path) = await RefusedFrameAsync("too_large", 7);

        Assert.Equal($"{path} is too large for --frame (at most 4 megapixels and 10,000 frames); img without --frame shows its first frame", e.Message);
        Assert.Equal(PixelsProblem.TooLarge, e.Problem);
    }

    private async Task<(PixelsUnavailableException Refusal, VaultPath Path)> RefusedFrameAsync(string code, int? frame)
    {
        await using var host = Host(app, new FakePdfSidecar());
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("clip.png", FakePdfSidecar.SolidPng(20, 20));
        var state = await StateAsync(host, user, "clip.png");
        var pixels = new AttachmentPixels(
            new EncryptedNoteStore(host.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey)),
            host.Services.GetRequiredService<VaultCatalog>(), new RefusingSidecar { Code = code },
            host.Services.GetRequiredService<PdfAnalyses>(), host.Services.GetRequiredService<PdfAnalysisRunner>());
        var path = At(user, "clip.png");

        return (await Assert.ThrowsAsync<PixelsUnavailableException>(() => pixels.ImageAsync(path, state, SizeStep.Medium, null, 0, frame, Ct)), path);
    }

    private sealed class RefusingSidecar : IPdfSidecar
    {
        public string Code { get; set; } = "failed";

        public string Message { get; set; } = "no";

        public Task<SidecarAnalysis> AnalyseAsync(ReadOnlyMemory<byte> pdf, int maxPages, CancellationToken ct) => throw new SidecarException(Code, Message);

        public Task<Picture> RenderPageAsync(ReadOnlyMemory<byte> pdf, int page, StepLimits target, Crop? crop, int rotate, CancellationToken ct) => throw new SidecarException(Code, Message);

        public Task<Picture> RenderImageAsync(ReadOnlyMemory<byte> image, StepLimits target, Crop? crop, int rotate, int? frame, CancellationToken ct) => throw new SidecarException(Code, Message);

        public Task<IReadOnlyList<PageTextBoxes>> TextBoxesAsync(ReadOnlyMemory<byte> pdf, IReadOnlyList<int> pages, CancellationToken ct) => throw new SidecarException(Code, Message);

        public Task<PdfPageImages> ListImagesAsync(ReadOnlyMemory<byte> pdf, int page, CancellationToken ct) => throw new SidecarException(Code, Message);

        public Task<Picture> ExtractImageAsync(ReadOnlyMemory<byte> pdf, int page, int n, CancellationToken ct) => throw new SidecarException(Code, Message);

        public Task<Picture> RenderRegionAsync(ReadOnlyMemory<byte> pdf, int page, Crop crop, int rotate, bool jpeg, CancellationToken ct) => throw new SidecarException(Code, Message);
    }

    [Fact]
    public async Task A_pdfs_images_and_regions_come_from_the_sidecar_with_the_blob()
    {
        var sidecar = new FakePdfSidecar();
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        var bytes = Pdf("a pump manual with pictures");
        await user.CreateFileAsync("manual.pdf", bytes);
        var state = await StateAsync(host, user, "manual.pdf");
        var pixels = Pixels(host, user);
        var listed = new PdfPageImages(2, 3, 595, 842, 0, [new PdfImageInfo(1, 400, 300, 72, 72, 300, 225, false, new string('a', 64), false)]);
        sidecar.PageImages = (_, page) => page == 2 ? listed : throw new SidecarException("no_such_page", "No.");
        sidecar.ExtractedImage = (_, page, n) => new Picture(FakePdfSidecar.SolidPng(40, 30), "image/png", 40, 30, 400, 300);

        var images = await pixels.PdfImagesAsync(At(user, "manual.pdf"), state, 2, Ct);
        var image = await pixels.PdfImageAsync(At(user, "manual.pdf"), state, 2, 1, Ct);
        var region = await pixels.PdfRegionAsync(At(user, "manual.pdf"), state, 2, new Crop(72, 72, 144, 72), 90, true, Ct);

        Assert.Same(listed, images);
        Assert.Equal((40, 30, 400d, 300d), (image.Width, image.Height, image.OriginalWidth, image.OriginalHeight));
        Assert.Equal("image/jpeg", region.MediaType);
        Assert.Equal([2], sidecar.ImageListRequests);
        Assert.Equal([(2, 1)], sidecar.ExtractRequests);
        Assert.Equal([(2, new Crop(72, 72, 144, 72), 90, true)], sidecar.RegionRequests);
        Assert.Equal(3, sidecar.ExtractionPdfs.Count);
        Assert.All(sidecar.ExtractionPdfs, pdf => Assert.Equal(bytes, pdf));
    }

    [Fact]
    public async Task Refusals_of_a_pdfs_images_and_regions_become_their_own_sentences()
    {
        var sidecar = new RefusingSidecar();
        await using var host = Host(app, new FakePdfSidecar());
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("manual.pdf", Pdf("a pump manual"));
        var state = await StateAsync(host, user, "manual.pdf");
        var pixels = new AttachmentPixels(
            new EncryptedNoteStore(host.Services.GetRequiredService<VaultCatalog>(), new UserCredential(user.User.Id, user.UserKey)),
            host.Services.GetRequiredService<VaultCatalog>(), sidecar,
            host.Services.GetRequiredService<PdfAnalyses>(), host.Services.GetRequiredService<PdfAnalysisRunner>());
        var path = At(user, "manual.pdf");

        async Task<(string, PixelsProblem)> List(string code)
        {
            sidecar.Code = code;
            var e = await Assert.ThrowsAsync<PixelsUnavailableException>(() => pixels.PdfImagesAsync(path, state, 7, Ct));
            return (e.Message, e.Problem);
        }

        async Task<(string, PixelsProblem)> One(string code)
        {
            sidecar.Code = code;
            var e = await Assert.ThrowsAsync<PixelsUnavailableException>(() => pixels.PdfImageAsync(path, state, 7, 3, Ct));
            return (e.Message, e.Problem);
        }

        async Task<(string, PixelsProblem)> Region(string code, bool jpeg = false)
        {
            sidecar.Code = code;
            var e = await Assert.ThrowsAsync<PixelsUnavailableException>(() => pixels.PdfRegionAsync(path, state, 7, new Crop(10, 10, 5000, 5), 0, jpeg, Ct));
            return (e.Message, e.Problem);
        }

        Assert.Equal(($"page 7 of {path} has no image 3", PixelsProblem.NoSuchImage), await One("no_such_image"));
        Assert.Equal(($"image 3 on page 7 of {path} is over 15 MB even as JPEG at quality 60", PixelsProblem.TooLarge), await One("too_large"));
        Assert.Equal(("the region is over 15 MB as PNG; name the target .jpg", PixelsProblem.TooLarge), await Region("too_large"));
        Assert.Equal(("the region is over 15 MB even as JPEG at quality 60; crop a smaller region", PixelsProblem.TooLarge), await Region("too_large", jpeg: true));
        Assert.Equal(("the crop 10,10,5000,5 lies outside page 7", PixelsProblem.BadCrop), await Region("bad_crop"));
        Assert.Equal(($"the images of page 7 of {path} couldn't be read", PixelsProblem.Failed), await List("too_large"));
        Assert.Equal(($"{path} has no page 7", PixelsProblem.NoSuchPage), await List("no_such_page"));
        Assert.Equal(($"{path} has no page 7", PixelsProblem.NoSuchPage), await One("no_such_page"));
        Assert.Equal(("this PDF is password-protected; Kumbu² can't read it", PixelsProblem.Failed), await List("encrypted"));
        Assert.Equal(("PDF image extraction is busy; try again in a moment", PixelsProblem.Busy), await List("busy"));
        Assert.Equal(("PDF image extraction is busy; try again in a moment", PixelsProblem.Busy), await One("busy"));
        Assert.Equal(("PDF image extraction is busy; try again in a moment", PixelsProblem.Busy), await Region("busy"));
        Assert.Equal(("PDF image extraction is unavailable right now; try again later", PixelsProblem.Unavailable), await List("unavailable"));
        Assert.Equal(($"reading the images of page 7 of {path} took too long", PixelsProblem.Failed), await List("timeout"));
        Assert.Equal(($"reading image 3 on page 7 of {path} took too long", PixelsProblem.Failed), await One("timeout"));
        Assert.Equal(($"rendering the region of page 7 of {path} took too long", PixelsProblem.Failed), await Region("timeout"));
        Assert.Equal(($"reading image 3 on page 7 of {path} stopped part way (the renderer may have run out of memory)", PixelsProblem.Failed), await One("aborted"));
        Assert.Equal(($"the images of page 7 of {path} couldn't be read", PixelsProblem.Failed), await List("failed"));
        Assert.Equal(($"image 3 on page 7 of {path} couldn't be read", PixelsProblem.Failed), await One("failed"));
        Assert.Equal(($"the region of page 7 of {path} couldn't be rendered", PixelsProblem.Failed), await Region("failed"));
    }

    [Fact]
    public void Busy_no_such_image_and_no_such_page_have_their_shell_kinds()
    {
        Assert.Equal(Kumbu.Core.Sh.ShellErrorKind.Invalid, new PixelsUnavailableException("no page", PixelsProblem.NoSuchPage).Kind);
        Assert.Equal(Kumbu.Core.Sh.ShellErrorKind.Unavailable, new PixelsUnavailableException("busy", PixelsProblem.Busy).Kind);
        Assert.Equal(Kumbu.Core.Sh.ShellErrorKind.Invalid, new PixelsUnavailableException("no image", PixelsProblem.NoSuchImage).Kind);
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

        // Still down: no analysis, and the job waits; pdf says why.
        var down = await Assert.ThrowsAsync<PixelsUnavailableException>(() => pixels.AnalysisAsync(At(user, "paper.pdf"), state, true, Ct));
        Assert.Equal(("PDF analysis is unavailable right now; try again later", PixelsProblem.AnalysisUnavailable), (down.Message, down.Problem));
        sidecar.Down = false;
        var analysis = await pixels.AnalysisAsync(At(user, "paper.pdf"), state, true, Ct);

        Assert.Equal(3, analysis!.PageCount);
        Assert.Equal(["Page 2 text"], await pixels.PageTextAsync(At(user, "paper.pdf"), state, 2, 2, Ct));
    }

    [Fact]
    public async Task A_busy_sidecar_makes_analysis_unavailable_and_a_failing_try_leaves_it_pending()
    {
        var sidecar = new FakePdfSidecar { FailWith = SidecarException.Busy };
        await using var host = Host(app, sidecar);
        var user = await RestClient.NewAsync(host);
        await user.CreateFileAsync("paper.pdf", Pdf("a paper while the sidecar is busy"));
        var state = await StateAsync(host, user, "paper.pdf");
        var pixels = Pixels(host, user);

        var busy = await Assert.ThrowsAsync<PixelsUnavailableException>(() => pixels.AnalysisAsync(At(user, "paper.pdf"), state, true, Ct));
        sidecar.FailWith = SidecarException.Timeout;

        Assert.Equal(PixelsProblem.AnalysisUnavailable, busy.Problem);
        Assert.Null(await pixels.AnalysisAsync(At(user, "paper.pdf"), state, true, Ct));
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
        // With the sidecar away, the outdated one is better than none.
        Assert.Equal(5, (await pixels.AnalysisAsync(At(user, "old.pdf"), state, true, Ct))!.PageCount);
        sidecar.Down = false;

        var again = await pixels.AnalysisAsync(At(user, "old.pdf"), state, true, Ct);

        Assert.Equal(3, again!.PageCount);
        Assert.True((await analyses.ReadAsync(vault, Convert.FromBase64String(state.BlobId), Ct))!.Current);
    }
}
