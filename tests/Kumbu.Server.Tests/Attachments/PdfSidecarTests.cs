// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Attachments;
using Kumbu.Server.Attachments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Tests.Attachments;

/// <summary>The server's client of the PDF sidecar: what it sends, and what it accepts back.</summary>
public sealed class PdfSidecarTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Pdf = "%PDF-1.4\n% a test\n%%EOF\n"u8.ToArray();

    private static readonly StepLimits Medium = SizeSteps.Of(SizeStep.Medium);

    private sealed class Stub(Func<HttpRequestMessage, byte[], HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Seen.Add((request, body));
            return answer(request, body);
        }
    }

    private static PdfSidecar Sidecar(HttpMessageHandler handler, string url = "http://pdf.test:8090") =>
        new(new HttpClient(handler), Options.Create(new KumbuOptions { Pdf = new PdfOptions { Url = url } }), NullLogger<PdfSidecar>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Image(byte[] bytes, string mediaType, int width, int height, params (string Name, string Value)[] extra)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        response.Headers.Add("X-Kumbu-Width", width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("X-Kumbu-Height", height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (name, value) in extra) response.Headers.Add(name, value);
        return response;
    }

    /// <summary>The sidecar's answer for two pages, as <c>pdf/src/analyse.ts</c> shapes it.</summary>
    private static JsonObject TwoPages() => JsonNode.Parse("""
        {
          "analyser": "pdfjs-dist@6.4.299+rules.1", "pageCount": 5, "analysedPages": 2, "title": "Report", "author": null,
          "pages": [
            { "n": 1, "width": 595, "height": 842, "rotation": 0, "chars": 12, "text": "Hello world.",
              "type": "text", "textCoverage": 0.31, "imageCoverage": 0, "vectorCoverage": 0, "images": 0, "regions": [] },
            { "n": 2, "width": 842, "height": 595, "rotation": 90, "chars": 0, "text": "",
              "type": "mixed", "textCoverage": 0, "imageCoverage": 0.1, "vectorCoverage": 0.35, "images": 1,
              "regions": [ { "kind": "image", "x": 80, "y": 90, "w": 200, "h": 120 }, { "kind": "drawing", "x": 72, "y": 300, "w": 451, "h": 260 } ] }
          ]
        }
        """)!.AsObject();

    [Fact]
    public async Task Without_a_url_every_call_is_unavailable()
    {
        var stub = new Stub((_, _) => throw new InvalidOperationException("no request expected"));
        var sidecar = Sidecar(stub, url: "");

        foreach (var call in new Func<Task>[]
        {
            () => sidecar.AnalyseAsync(Pdf, 2000, Ct),
            () => sidecar.RenderPageAsync(Pdf, 1, Medium, null, Ct),
            () => sidecar.RenderImageAsync(FakePdfSidecar.SolidPng(4, 4), Medium, null, Ct),
        })
        {
            var e = await Assert.ThrowsAsync<SidecarException>(call);
            Assert.Equal(SidecarException.Unavailable, e.Code);
            Assert.Equal("PDF rendering is unavailable right now", e.Message);
        }

        Assert.Empty(stub.Seen);
    }

    [Fact]
    public async Task A_refused_connection_is_unavailable()
    {
        // Port 9 (discard) on loopback: nothing listens there.
        var sidecar = Sidecar(new SocketsHttpHandler(), url: "http://127.0.0.1:9");

        var e = await Assert.ThrowsAsync<SidecarException>(() => sidecar.AnalyseAsync(Pdf, 2000, Ct));

        Assert.Equal(SidecarException.Unavailable, e.Code);
    }

    [Fact]
    public async Task An_analysis_posts_the_pdf_and_reads_pages_regions_and_text()
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, TwoPages().ToJsonString()));

        // Five pages, analysed up to the second.
        var result = await Sidecar(stub).AnalyseAsync(Pdf, 2, Ct);

        var (request, body) = Assert.Single(stub.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://pdf.test:8090/analyse?maxPages=2", request.RequestUri!.ToString());
        Assert.Equal(Pdf, body);
        var a = result.Analysis;
        Assert.Equal(("pdfjs-dist@6.4.299+rules.1", 5, 2, "Report", (string?)null), (a.Analyser, a.PageCount, a.AnalysedPages, a.Title, a.Author));
        Assert.Equal(new PdfPage(1, 595, 842, 0, PageType.Text, 12, 0.31, 0, 0, 0, []), a.Pages[0] with { Regions = [] });
        var two = a.Pages[1];
        Assert.Equal((2, 842d, 595d, 90, PageType.Mixed, 0.35, 1), (two.Number, two.Width, two.Height, two.Rotation, two.Type, two.VectorCoverage, two.Images));
        Assert.Equal([new PdfRegion("image", 80, 90, 200, 120), new PdfRegion("drawing", 72, 300, 451, 260)], two.Regions);
        Assert.Equal(["Hello world.", ""], result.PageTexts);
    }

    [Fact]
    public void The_analyser_is_the_sidecars()
    {
        // pdf/src/analyse.ts: `pdfjs-dist@${version}+rules.${RULES_VERSION}`, with the version pinned in pdf/package.json.
        var root = new DirectoryInfo(KumbuAppFactory.SamplesPath).Parent!.FullName;
        var package = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "pdf", "package.json")))!;
        var rules = File.ReadAllText(Path.Combine(root, "pdf", "src", "classify.ts"));
        var version = package["dependencies"]!["pdfjs-dist"]!.GetValue<string>().TrimStart('^', '~');
        var rulesVersion = System.Text.RegularExpressions.Regex.Match(rules, @"RULES_VERSION = (\d+);").Groups[1].Value;

        Assert.Equal(PdfAnalyses.Analyser, $"pdfjs-dist@{version}+rules.{rulesVersion}");
    }

    public static TheoryData<string, string> Broken() => new()
    {
        { "pages out of order", """{"op":"set","path":"pages.1.n","value":3}""" },
        { "a coverage above 1", """{"op":"set","path":"pages.0.textCoverage","value":1.5}""" },
        { "a negative coverage", """{"op":"set","path":"pages.1.imageCoverage","value":-0.1}""" },
        { "fewer pages than analysed", """{"op":"set","path":"analysedPages","value":3}""" },
        { "more analysed than there are", """{"op":"set","path":"pageCount","value":1}""" },
        { "an odd rotation", """{"op":"set","path":"pages.0.rotation","value":45}""" },
        { "an unknown type", """{"op":"set","path":"pages.0.type","value":"poster"}""" },
        { "no text", """{"op":"remove","path":"pages.0.text"}""" },
        { "an unknown region kind", """{"op":"set","path":"pages.1.regions.0.kind","value":"chart"}""" },
        { "a negative width", """{"op":"set","path":"pages.1.width","value":-1}""" },
        { "a huge number", """{"op":"set","path":"pages.1.regions.0.w","value":1e400}""" },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public async Task An_analysis_that_doesnt_add_up_is_refused(string what, string change)
    {
        var json = TwoPages();
        var op = JsonNode.Parse(change)!;
        var steps = op["path"]!.GetValue<string>().Split('.');
        JsonNode node = json;
        foreach (var step in steps[..^1]) node = int.TryParse(step, out var i) ? node[i]! : node[step]!;
        var last = steps[^1];
        if (op["op"]!.GetValue<string>() == "remove") node.AsObject().Remove(last);
        else node[last] = op["value"]!.DeepClone();
        var text = json.ToJsonString().Replace("1E+400", "1e400", StringComparison.Ordinal);
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, text));

        var e = await Assert.ThrowsAsync<SidecarException>(() => Sidecar(stub).AnalyseAsync(Pdf, 2, Ct));

        Assert.True(e.Code == SidecarException.Failed, what);
    }

    [Fact]
    public async Task An_answer_that_isnt_json_or_is_larger_than_64_MiB_is_refused()
    {
        var notJson = new Stub((_, _) => Json(HttpStatusCode.OK, "<html>oops</html>"));
        Assert.Equal(SidecarException.Failed, (await Assert.ThrowsAsync<SidecarException>(() => Sidecar(notJson).AnalyseAsync(Pdf, 2000, Ct))).Code);

        var huge = new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Endless()) });
        Assert.Equal(SidecarException.Failed, (await Assert.ThrowsAsync<SidecarException>(() => Sidecar(huge).AnalyseAsync(Pdf, 2000, Ct))).Code);
    }

    /// <summary>A body that never ends: the client must stop reading it at its limit.</summary>
    private sealed class Endless : Stream
    {
        public long Served { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)' ', offset, count);
            Served += count;
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(422, """{"error":"The PDF is password-protected.","code":"encrypted"}""", "encrypted")]
    [InlineData(400, """{"error":"The body is not a PDF.","code":"not_pdf"}""", "not_pdf")]
    [InlineData(422, """{"error":"There is no page 9.","code":"no_such_page"}""", "no_such_page")]
    [InlineData(503, """{"error":"The sidecar is busy.","code":"busy"}""", "busy")]
    [InlineData(504, """{"error":"The job took too long.","code":"timeout"}""", "timeout")]
    [InlineData(504, "<html>gateway</html>", "timeout")]
    [InlineData(500, """{"error":"The sidecar could not process the request.","code":"failed"}""", "failed")]
    [InlineData(502, "<html>bad gateway</html>", "unavailable")]
    [InlineData(500, "not json", "unavailable")]
    [InlineData(500, """{"error":"The sidecar is busy.","code":"busy"}""", "failed")]
    [InlineData(400, """{"error":"The PDF is password-protected.","code":"encrypted"}""", "failed")]
    [InlineData(503, """{"error":"The body is not a PDF.","code":"not_pdf"}""", "failed")]
    [InlineData(422, """{"error":"Something new.","code":"novel"}""", "failed")]
    public async Task Error_answers_map_to_codes(int status, string body, string code)
    {
        var stub = new Stub((_, _) => Json((HttpStatusCode)status, body));

        var e = await Assert.ThrowsAsync<SidecarException>(() => Sidecar(stub).RenderPageAsync(Pdf, 9, Medium, null, Ct));

        Assert.Equal(code, e.Code);
    }

    [Fact]
    public async Task A_page_is_asked_for_with_its_step_and_crop_and_comes_back_as_a_picture()
    {
        var png = FakePdfSidecar.SolidPng(1109, 1568);
        var stub = new Stub((_, _) => Image(png, "image/png", 1109, 1568, ("X-Kumbu-Page-Width", "595.28"), ("X-Kumbu-Page-Height", "841.89")));

        var picture = await Sidecar(stub).RenderPageAsync(Pdf, 4, Medium, new Crop(72, 300.5, 451, 260), Ct);

        var (request, body) = Assert.Single(stub.Seen);
        Assert.Equal("http://pdf.test:8090/render/page?page=4&longEdge=1568&pixels=1200000&bytes=409600&crop=72,300.5,451,260", request.RequestUri!.ToString());
        Assert.Equal(Pdf, body);
        Assert.Equal(png, picture.Data);
        Assert.Equal(("image/png", 1109, 1568, 595.28, 841.89), (picture.MediaType, picture.Width, picture.Height, picture.OriginalWidth, picture.OriginalHeight));
    }

    [Fact]
    public async Task An_image_comes_back_with_its_original_size()
    {
        var jpeg = Jpeg(724, 1568);
        var stub = new Stub((_, _) => Image(jpeg, "image/jpeg", 724, 1568, ("X-Kumbu-Original-Width", "1440"), ("X-Kumbu-Original-Height", "3120")));
        var image = FakePdfSidecar.SolidPng(1440, 3120);

        var picture = await Sidecar(stub).RenderImageAsync(image, Medium, null, Ct);

        Assert.Equal("http://pdf.test:8090/render/image?longEdge=1568&pixels=1200000&bytes=409600", Assert.Single(stub.Seen).Request.RequestUri!.ToString());
        Assert.Equal(("image/jpeg", 724, 1568, 1440d, 3120d), (picture.MediaType, picture.Width, picture.Height, picture.OriginalWidth, picture.OriginalHeight));
    }

    public static TheoryData<string, byte[], string, int, int> Wrong() => new()
    {
        { "JPEG bytes said to be PNG", Jpeg(100, 100), "image/png", 100, 100 },
        { "a type that isn't an image", FakePdfSidecar.SolidPng(100, 100), "text/html", 100, 100 },
        { "a header that disagrees with the size given", FakePdfSidecar.SolidPng(100, 100), "image/png", 100, 101 },
        { "a long edge over the step's", FakePdfSidecar.SolidPng(1570, 10), "image/png", 1570, 10 },
        { "bytes that aren't an image", "GIF89a......"u8.ToArray(), "image/png", 4, 4 },
    };

    [Theory]
    [MemberData(nameof(Wrong))]
    public async Task An_image_that_isnt_what_it_says_is_refused(string what, byte[] bytes, string mediaType, int width, int height)
    {
        var stub = new Stub((_, _) => Image(bytes, mediaType, width, height, ("X-Kumbu-Page-Width", "595"), ("X-Kumbu-Page-Height", "842")));

        var e = await Assert.ThrowsAsync<SidecarException>(() => Sidecar(stub).RenderPageAsync(Pdf, 1, Medium, null, Ct));

        Assert.True(e.Code == SidecarException.Failed, what);
    }

    [Fact]
    public async Task One_pixel_over_the_long_edge_is_rounding_and_accepted()
    {
        var stub = new Stub((_, _) => Image(FakePdfSidecar.SolidPng(1569, 10), "image/png", 1569, 10, ("X-Kumbu-Page-Width", "595"), ("X-Kumbu-Page-Height", "4")));

        Assert.Equal(1569, (await Sidecar(stub).RenderPageAsync(Pdf, 1, Medium, null, Ct)).Width);
    }

    /// <summary>A JPEG of SOI, one SOF0 frame header and EOI: enough for its header to say the size.</summary>
    private static byte[] Jpeg(int width, int height) =>
        [0xFF, 0xD8, 0xFF, 0xC0, 0, 11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0, 0xFF, 0xD9];
}
