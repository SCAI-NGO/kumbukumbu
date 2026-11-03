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
    public async Task A_redirect_from_the_sidecar_is_an_error()
    {
        // Each redirect carries what would be a good answer, so treating a 3xx as success would show.
        var stub = new Stub((request, _) =>
        {
            var response = request.RequestUri!.AbsolutePath.EndsWith("analyse", StringComparison.Ordinal)
                ? Json(HttpStatusCode.TemporaryRedirect, TwoPages().ToJsonString())
                : Image(FakePdfSidecar.SolidPng(4, 4), "image/png", 4, 4, ("X-Kumbu-Page-Width", "595"), ("X-Kumbu-Page-Height", "842"), ("X-Kumbu-Original-Width", "4"), ("X-Kumbu-Original-Height", "4"));
            response.StatusCode = HttpStatusCode.TemporaryRedirect;
            response.Headers.Location = new Uri("http://elsewhere.test/collect");
            return response;
        });
        var sidecar = Sidecar(stub);

        foreach (var call in new Func<Task>[]
        {
            () => sidecar.AnalyseAsync(Pdf, 2, Ct),
            () => sidecar.RenderPageAsync(Pdf, 1, Medium, null, 0, Ct),
            () => sidecar.RenderImageAsync(FakePdfSidecar.SolidPng(4, 4), Medium, null, 0, null, Ct),
        })
        {
            var e = await Assert.ThrowsAsync<SidecarException>(call);
            Assert.Equal(SidecarException.Failed, e.Code);
        }

        Assert.Equal(3, stub.Seen.Count);
        Assert.All(stub.Seen, seen => Assert.Equal("pdf.test", seen.Request.RequestUri!.Host));
    }

    [Fact]
    public async Task Without_a_url_every_call_is_unavailable()
    {
        var stub = new Stub((_, _) => throw new InvalidOperationException("no request expected"));
        var sidecar = Sidecar(stub, url: "");

        foreach (var call in new Func<Task>[]
        {
            () => sidecar.AnalyseAsync(Pdf, 2000, Ct),
            () => sidecar.RenderPageAsync(Pdf, 1, Medium, null, 0, Ct),
            () => sidecar.RenderImageAsync(FakePdfSidecar.SolidPng(4, 4), Medium, null, 0, null, Ct),
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

    /// <summary>
    /// A sidecar on loopback that reads a request's headers and its whole body, as the real one does before it starts
    /// a job, then resets the connection without answering: what the server sees when the sidecar dies on a file.
    /// </summary>
    private static async Task<int> ResettingSidecarAsync(int requests, CancellationToken ct)
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < requests; i++)
                {
                    using var socket = await listener.AcceptSocketAsync(ct);
                    var seen = new List<byte>();
                    var buffer = new byte[8192];
                    int headerEnd = -1, length = 0;
                    while (headerEnd < 0 || seen.Count < headerEnd + length)
                    {
                        var n = await socket.ReceiveAsync(buffer, ct);
                        if (n == 0) break;
                        seen.AddRange(buffer.AsSpan(0, n).ToArray());
                        if (headerEnd < 0 && Encoding.ASCII.GetString([.. seen]).IndexOf("\r\n\r\n", StringComparison.Ordinal) is var at and >= 0)
                        {
                            headerEnd = at + 4;
                            var headers = Encoding.ASCII.GetString([.. seen], 0, at);
                            var line = headers.Split("\r\n").First(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                            length = int.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }

                    socket.LingerState = new System.Net.Sockets.LingerOption(true, 0);
                    socket.Close();
                }
            }
            finally
            {
                listener.Stop();
            }
        }, ct);
        return port;
    }

    [Fact]
    public async Task A_connection_that_breaks_after_the_file_was_sent_is_aborted_not_unavailable()
    {
        var port = await ResettingSidecarAsync(4, Ct);
        var sidecar = Sidecar(new SocketsHttpHandler(), url: $"http://127.0.0.1:{port}");

        var analysis = await Assert.ThrowsAsync<SidecarException>(() => sidecar.AnalyseAsync(Pdf, 2000, Ct));
        var page = await Assert.ThrowsAsync<SidecarException>(() => sidecar.RenderPageAsync(Pdf, 1, Medium, null, 0, Ct));

        Assert.Equal(SidecarException.Aborted, analysis.Code);
        Assert.False(analysis.Transient);
        Assert.Equal(SidecarException.Aborted, page.Code);
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
        { "a null page", """{"op":"set","path":"pages.1","value":null}""" },
        { "a null region", """{"op":"set","path":"pages.1.regions.0","value":null}""" },
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
        else if (node is JsonArray list) list[int.Parse(last, System.Globalization.CultureInfo.InvariantCulture)] = op["value"]?.DeepClone();
        else node[last] = op["value"]?.DeepClone();
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

    /// <summary>The sidecar's answer to <c>/text-boxes?pages=7,12</c>, as <c>pdf/src/server.ts</c> shapes it.</summary>
    private static JsonObject TwoPagesOfBoxes() => JsonNode.Parse("""
        {
          "pages": [
            { "n": 7, "width": 595.3, "height": 841.9, "text": "Hello world.\nBye",
              "items": [ { "at": 0, "end": 12, "x": 74.2, "y": 300.1, "w": 210.3, "h": 11 }, { "at": 13, "end": 16, "x": 74.2, "y": 312.1, "w": 20, "h": 11 } ] },
            { "n": 12, "width": 842, "height": 595, "text": "", "items": [] }
          ]
        }
        """)!.AsObject();

    private static readonly IReadOnlyList<int> SevenAndTwelve = [7, 12];

    [Fact]
    public async Task Text_boxes_are_asked_by_page_list()
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, TwoPagesOfBoxes().ToJsonString()));

        await Sidecar(stub).TextBoxesAsync(Pdf, SevenAndTwelve, Ct);

        var (request, body) = Assert.Single(stub.Seen);
        Assert.Equal(("POST", "/text-boxes", "?pages=7,12", "application/pdf"), (request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, request.Content!.Headers.ContentType!.MediaType));
        Assert.Equal(Pdf, body);
    }

    [Fact]
    public async Task Text_boxes_parse()
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, TwoPagesOfBoxes().ToJsonString()));

        var pages = await Sidecar(stub).TextBoxesAsync(Pdf, SevenAndTwelve, Ct);

        Assert.Equal(2, pages.Count);
        Assert.Equal((7, 595.3, 841.9, "Hello world.\nBye"), (pages[0].Number, pages[0].Width, pages[0].Height, pages[0].Text));
        Assert.Equal([new TextBox(0, 12, 74.2, 300.1, 210.3, 11), new TextBox(13, 16, 74.2, 312.1, 20, 11)], pages[0].Items);
        Assert.Equal((12, ""), (pages[1].Number, pages[1].Text));
        Assert.Empty(pages[1].Items);
    }

    public static TheoryData<string, string> BrokenBoxes() => new()
    {
        { "pages other than asked", """{"op":"set","path":"pages.1.n","value":13}""" },
        { "pages in another order", """{"op":"set","path":"pages","value":[{"n":12,"width":842,"height":595,"text":"","items":[]},{"n":7,"width":595,"height":842,"text":"","items":[]}]}""" },
        { "a page too few", """{"op":"remove","path":"pages.1"}""" },
        { "an at above its end", """{"op":"set","path":"pages.0.items.0.at","value":13}""" },
        { "an end past the text", """{"op":"set","path":"pages.0.items.1.end","value":17}""" },
        { "an at below the previous end", """{"op":"set","path":"pages.0.items.1.at","value":11}""" },
        { "a negative x", """{"op":"set","path":"pages.0.items.0.x","value":-0.1}""" },
        { "an infinite w", """{"op":"set","path":"pages.0.items.0.w","value":1e999}""" },
        { "no text", """{"op":"remove","path":"pages.0.text"}""" },
        { "no items", """{"op":"remove","path":"pages.1.items"}""" },
        { "a null page", """{"op":"set","path":"pages.1","value":null}""" },
        { "a null item", """{"op":"set","path":"pages.0.items.1","value":null}""" },
        { "an item without at", """{"op":"remove","path":"pages.0.items.0.at"}""" },
        { "an item without end", """{"op":"remove","path":"pages.0.items.0.end"}""" },
        { "an item without x", """{"op":"remove","path":"pages.0.items.0.x"}""" },
        { "an item without y", """{"op":"remove","path":"pages.0.items.0.y"}""" },
        { "an item without w", """{"op":"remove","path":"pages.0.items.0.w"}""" },
        { "an item without h", """{"op":"remove","path":"pages.0.items.0.h"}""" },
    };

    [Theory]
    [MemberData(nameof(BrokenBoxes))]
    public async Task Malformed_text_boxes_are_refused(string what, string change)
    {
        var json = TwoPagesOfBoxes();
        var op = JsonNode.Parse(change)!;
        var steps = op["path"]!.GetValue<string>().Split('.');
        JsonNode node = json;
        foreach (var step in steps[..^1]) node = int.TryParse(step, out var i) ? node[i]! : node[step]!;
        var last = steps[^1];
        if (op["op"]!.GetValue<string>() == "remove")
        {
            if (node is JsonArray array) array.RemoveAt(int.Parse(last, System.Globalization.CultureInfo.InvariantCulture));
            else node.AsObject().Remove(last);
        }
        else if (node is JsonArray list) list[int.Parse(last, System.Globalization.CultureInfo.InvariantCulture)] = op["value"]?.DeepClone();
        else node[last] = op["value"]?.DeepClone();
        var text = json.ToJsonString().Replace("1E+999", "1e999", StringComparison.Ordinal);
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, text));

        var e = await Assert.ThrowsAsync<SidecarException>(() => Sidecar(stub).TextBoxesAsync(Pdf, SevenAndTwelve, Ct));

        Assert.True(e.Code == SidecarException.Failed, what);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12,7")]
    [InlineData("7,7")]
    [InlineData("0")]
    [InlineData("1-51")]
    public async Task Text_boxes_are_only_asked_for_pages_ascending_distinct_and_at_most_50(string asked)
    {
        var pages = asked == "" ? [] : asked == "1-51" ? Enumerable.Range(1, 51).ToList() : asked.Split(',').Select(int.Parse).ToList();
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, TwoPagesOfBoxes().ToJsonString()));

        await Assert.ThrowsAsync<ArgumentException>(() => Sidecar(stub).TextBoxesAsync(Pdf, pages, Ct));

        Assert.Empty(stub.Seen);
    }

    [Fact]
    public async Task Fifty_pages_may_be_asked_for_at_once()
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, """{"pages":[]}"""));

        // The answer has no pages, so it is refused: the request itself was sent.
        await Assert.ThrowsAsync<SidecarException>(() => Sidecar(stub).TextBoxesAsync(Pdf, [.. Enumerable.Range(1, 50)], Ct));

        Assert.Single(stub.Seen);
    }

    [Fact]
    public async Task Text_boxes_that_are_not_json_are_refused()
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.OK, "<html>oops</html>"));

        Assert.Equal(SidecarException.Failed, (await Assert.ThrowsAsync<SidecarException>(() => Sidecar(stub).TextBoxesAsync(Pdf, SevenAndTwelve, Ct))).Code);
    }

    [Fact]
    public async Task Text_boxes_over_16_MiB_are_refused()
    {
        var huge = new Stub((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Endless()) });

        Assert.Equal(SidecarException.Failed, (await Assert.ThrowsAsync<SidecarException>(() => Sidecar(huge).TextBoxesAsync(Pdf, SevenAndTwelve, Ct))).Code);

        // Valid JSON, over the limit: only the limit refuses it.
        var padded = TwoPagesOfBoxes().ToJsonString() + new string(' ', PdfSidecar.MaxTextBoxBytes);
        var over = new Stub((_, _) => Json(HttpStatusCode.OK, padded));
        Assert.Equal(SidecarException.Failed, (await Assert.ThrowsAsync<SidecarException>(() => Sidecar(over).TextBoxesAsync(Pdf, SevenAndTwelve, Ct))).Code);
    }

    [Fact]
    public async Task No_such_page_from_text_boxes_is_that_code()
    {
        var stub = new Stub((_, _) => Json(HttpStatusCode.UnprocessableEntity, """{"error":"There is no page 12.","code":"no_such_page"}"""));

        Assert.Equal("no_such_page", (await Assert.ThrowsAsync<SidecarException>(() => Sidecar(stub).TextBoxesAsync(Pdf, SevenAndTwelve, Ct))).Code);
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

        var e = await Assert.ThrowsAsync<SidecarException>(() => Sidecar(stub).RenderPageAsync(Pdf, 9, Medium, null, 0, Ct));

        Assert.Equal(code, e.Code);
    }

    [Fact]
    public async Task A_page_is_asked_for_with_its_step_and_crop_and_comes_back_as_a_picture()
    {
        // The crop of 451 × 260 pt at medium: 1.2 MP bind before the long edge does.
        var png = FakePdfSidecar.SolidPng(1442, 831);
        var stub = new Stub((_, _) => Image(png, "image/png", 1442, 831, ("X-Kumbu-Page-Width", "595.28"), ("X-Kumbu-Page-Height", "841.89")));

        var picture = await Sidecar(stub).RenderPageAsync(Pdf, 4, Medium, new Crop(72, 300.5, 451, 260), 0, Ct);

        var (request, body) = Assert.Single(stub.Seen);
        Assert.Equal("http://pdf.test:8090/render/page?page=4&longEdge=1568&pixels=1200000&bytes=409600&crop=72,300.5,451,260", request.RequestUri!.ToString());
        Assert.Equal(Pdf, body);
        Assert.Equal(png, picture.Data);
        Assert.Equal(("image/png", 1442, 831, 595.28, 841.89), (picture.MediaType, picture.Width, picture.Height, picture.OriginalWidth, picture.OriginalHeight));
    }

    [Fact]
    public async Task An_image_comes_back_with_its_original_size()
    {
        var jpeg = Jpeg(724, 1568);
        var stub = new Stub((_, _) => Image(jpeg, "image/jpeg", 724, 1568, ("X-Kumbu-Original-Width", "1440"), ("X-Kumbu-Original-Height", "3120")));
        var image = FakePdfSidecar.SolidPng(1440, 3120);

        var picture = await Sidecar(stub).RenderImageAsync(image, Medium, null, 0, null, Ct);

        Assert.Equal("http://pdf.test:8090/render/image?longEdge=1568&pixels=1200000&bytes=409600", Assert.Single(stub.Seen).Request.RequestUri!.ToString());
        Assert.Equal(("image/jpeg", 724, 1568, 1440d, 3120d), (picture.MediaType, picture.Width, picture.Height, picture.OriginalWidth, picture.OriginalHeight));
        Assert.False(picture.Transparent);
    }

    [Fact]
    public async Task An_image_that_was_transparent_says_so()
    {
        var jpeg = Jpeg(724, 1568);
        var stub = new Stub((_, _) => Image(jpeg, "image/jpeg", 724, 1568, ("X-Kumbu-Original-Width", "1440"), ("X-Kumbu-Original-Height", "3120"), ("X-Kumbu-Transparent", "1")));

        var picture = await Sidecar(stub).RenderImageAsync(FakePdfSidecar.SolidPng(1440, 3120), Medium, null, 0, null, Ct);

        Assert.True(picture.Transparent);
    }

    public static TheoryData<string, byte[], string, int, int> Wrong() => new()
    {
        { "JPEG bytes said to be PNG", Jpeg(100, 100), "image/png", 100, 100 },
        { "a type that isn't an image", FakePdfSidecar.SolidPng(100, 100), "text/html", 100, 100 },
        { "a header that disagrees with the size given", FakePdfSidecar.SolidPng(100, 100), "image/png", 100, 101 },
        { "a long edge over the step's", FakePdfSidecar.SolidPng(1570, 10), "image/png", 1570, 10 },
        { "more pixels than the step's budget", FakePdfSidecar.SolidPng(1200, 1200), "image/png", 1200, 1200 },
        { "bytes that aren't an image", "GIF89a......"u8.ToArray(), "image/png", 4, 4 },
    };

    [Theory]
    [MemberData(nameof(Wrong))]
    public async Task An_image_that_isnt_what_it_says_is_refused(string what, byte[] bytes, string mediaType, int width, int height)
    {
        var stub = new Stub((_, _) => Image(bytes, mediaType, width, height, ("X-Kumbu-Page-Width", "595"), ("X-Kumbu-Page-Height", "842")));

        var e = await Assert.ThrowsAsync<SidecarException>(() => Sidecar(stub).RenderPageAsync(Pdf, 1, Medium, null, 0, Ct));

        Assert.True(e.Code == SidecarException.Failed, what);
    }

    [Fact]
    public async Task One_pixel_over_the_long_edge_is_rounding_and_accepted()
    {
        var stub = new Stub((_, _) => Image(FakePdfSidecar.SolidPng(1569, 10), "image/png", 1569, 10, ("X-Kumbu-Page-Width", "595"), ("X-Kumbu-Page-Height", "4")));

        Assert.Equal(1569, (await Sidecar(stub).RenderPageAsync(Pdf, 1, Medium, null, 0, Ct)).Width);
    }

    [Fact]
    public async Task A_turn_goes_into_the_query()
    {
        var png = FakePdfSidecar.SolidPng(100, 50);
        var stub = new Stub((_, _) => Image(png, "image/png", 100, 50, ("X-Kumbu-Rotate", "90"), ("X-Kumbu-Page-Width", "595"), ("X-Kumbu-Page-Height", "842"), ("X-Kumbu-Original-Width", "50"), ("X-Kumbu-Original-Height", "100")));

        await Sidecar(stub).RenderPageAsync(Pdf, 2, Medium, new Crop(1, 2, 3, 4), 90, Ct);
        await Sidecar(stub).RenderImageAsync(png, Medium, null, 90, null, Ct);

        Assert.Equal("http://pdf.test:8090/render/page?page=2&longEdge=1568&pixels=1200000&bytes=409600&crop=1,2,3,4&rotate=90", stub.Seen[0].Request.RequestUri!.ToString());
        Assert.Equal("http://pdf.test:8090/render/image?longEdge=1568&pixels=1200000&bytes=409600&rotate=90", stub.Seen[1].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task No_turn_leaves_the_query_as_it_was()
    {
        var png = FakePdfSidecar.SolidPng(100, 50);
        var stub = new Stub((_, _) => Image(png, "image/png", 100, 50, ("X-Kumbu-Page-Width", "595"), ("X-Kumbu-Page-Height", "842"), ("X-Kumbu-Original-Width", "100"), ("X-Kumbu-Original-Height", "50")));

        await Sidecar(stub).RenderPageAsync(Pdf, 2, Medium, null, 0, Ct);
        await Sidecar(stub).RenderImageAsync(png, Medium, null, 0, null, Ct);

        Assert.DoesNotContain("rotate", stub.Seen[0].Request.RequestUri!.Query);
        Assert.DoesNotContain("rotate", stub.Seen[1].Request.RequestUri!.Query);
    }

    /// <summary>Answers a picture of 100 × 50 px with the given <c>X-Kumbu-Rotate</c> (none if null), on both routes.</summary>
    private static Stub Echoing(string? echo)
    {
        var png = FakePdfSidecar.SolidPng(100, 50);
        (string, string)[] extra =
        [
            ("X-Kumbu-Page-Width", "595"), ("X-Kumbu-Page-Height", "842"), ("X-Kumbu-Original-Width", "100"), ("X-Kumbu-Original-Height", "50"),
            .. echo is null ? Array.Empty<(string, string)>() : [("X-Kumbu-Rotate", echo)],
        ];
        return new Stub((_, _) => Image(png, "image/png", 100, 50, extra));
    }

    private static async Task AssertNotTurnedAsync(Stub stub, int asked)
    {
        var page = await Assert.ThrowsAsync<SidecarException>(() => Sidecar(stub).RenderPageAsync(Pdf, 1, Medium, null, asked, Ct));
        var image = await Assert.ThrowsAsync<SidecarException>(() => Sidecar(stub).RenderImageAsync(FakePdfSidecar.SolidPng(100, 50), Medium, null, asked, null, Ct));

        Assert.All(new[] { page, image }, e => Assert.Equal((SidecarException.Failed, "The image isn't turned as asked"), (e.Code, e.Message)));
    }

    [Fact]
    public async Task An_answer_without_the_echo_is_refused() => await AssertNotTurnedAsync(Echoing(null), 90);

    [Fact]
    public async Task An_answer_turned_otherwise_is_refused() => await AssertNotTurnedAsync(Echoing("180"), 90);

    [Fact]
    public async Task An_answer_turned_unasked_is_refused() => await AssertNotTurnedAsync(Echoing("90"), 0);

    [Theory]
    [InlineData("90.0")]
    [InlineData("abc")]
    public async Task A_malformed_echo_is_refused(string echo) => await AssertNotTurnedAsync(Echoing(echo), 90);

    [Fact]
    public async Task A_matching_echo_is_accepted()
    {
        var turned = Echoing("270");
        var plain = Echoing(null);

        Assert.Equal(100, (await Sidecar(turned).RenderPageAsync(Pdf, 1, Medium, null, 270, Ct)).Width);
        Assert.Equal(100, (await Sidecar(turned).RenderImageAsync(FakePdfSidecar.SolidPng(100, 50), Medium, null, 270, null, Ct)).Width);
        Assert.Equal(100, (await Sidecar(plain).RenderPageAsync(Pdf, 1, Medium, null, 0, Ct)).Width);
        Assert.Equal(100, (await Sidecar(plain).RenderImageAsync(FakePdfSidecar.SolidPng(100, 50), Medium, null, 0, null, Ct)).Width);
    }

    /// <summary>Answers a picture of 100 × 50 px with the given <c>X-Kumbu-Frame</c> and <c>X-Kumbu-Frames</c> (none if null).</summary>
    private static Stub Framing(string? frame, string? frames)
    {
        (string, string)[] extra =
        [
            ("X-Kumbu-Original-Width", "100"), ("X-Kumbu-Original-Height", "50"),
            .. frame is null ? Array.Empty<(string, string)>() : [("X-Kumbu-Frame", frame)],
            .. frames is null ? Array.Empty<(string, string)>() : [("X-Kumbu-Frames", frames)],
        ];
        return new Stub((_, _) => Image(FakePdfSidecar.SolidPng(100, 50), "image/png", 100, 50, extra));
    }

    private static Task<Picture> FramedAsync(Stub stub, int? asked) =>
        Sidecar(stub).RenderImageAsync(FakePdfSidecar.SolidPng(100, 50), Medium, null, 0, asked, Ct);

    [Fact]
    public async Task A_frame_goes_into_the_query()
    {
        var stub = Framing("3", "5");

        await FramedAsync(stub, 3);
        var turned = new Stub((_, _) => Image(FakePdfSidecar.SolidPng(100, 50), "image/png", 100, 50, ("X-Kumbu-Original-Width", "50"), ("X-Kumbu-Original-Height", "100"), ("X-Kumbu-Rotate", "90"), ("X-Kumbu-Frame", "3")));
        await Sidecar(turned).RenderImageAsync(FakePdfSidecar.SolidPng(100, 50), Medium, new Crop(1, 2, 3, 4), 90, 3, Ct);

        Assert.Equal("http://pdf.test:8090/render/image?longEdge=1568&pixels=1200000&bytes=409600&frame=3", stub.Seen[0].Request.RequestUri!.ToString());
        Assert.Equal("http://pdf.test:8090/render/image?longEdge=1568&pixels=1200000&bytes=409600&crop=1,2,3,4&rotate=90&frame=3", turned.Seen[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task No_frame_leaves_the_query_as_it_was()
    {
        var stub = Framing(null, null);

        await FramedAsync(stub, null);

        Assert.DoesNotContain("frame", stub.Seen[0].Request.RequestUri!.Query);
    }

    private static async Task AssertNotTheFrameAsync(Stub stub, int? asked)
    {
        var e = await Assert.ThrowsAsync<SidecarException>(() => FramedAsync(stub, asked));

        Assert.Equal((SidecarException.Failed, "The image isn't the frame asked for"), (e.Code, e.Message));
    }

    [Fact]
    public async Task An_answer_without_the_frame_echo_is_refused() => await AssertNotTheFrameAsync(Framing(null, "5"), 3);

    [Fact]
    public async Task An_answer_with_a_frame_unasked_is_refused() => await AssertNotTheFrameAsync(Framing("3", "5"), null);

    [Theory]
    [InlineData("2")]
    [InlineData("03")]
    [InlineData("3.0")]
    [InlineData("abc")]
    public async Task An_answer_with_another_frame_is_refused(string echo) => await AssertNotTheFrameAsync(Framing(echo, "5"), 3);

    [Fact]
    public async Task The_count_of_frames_reaches_the_picture()
    {
        Assert.Equal(5, (await FramedAsync(Framing("3", "5"), 3)).Frames);
        Assert.Equal(5, (await FramedAsync(Framing(null, "5"), null)).Frames);
    }

    [Fact]
    public async Task A_missing_count_of_frames_is_null()
    {
        Assert.Null((await FramedAsync(Framing("3", null), 3)).Frames);
        Assert.Null((await FramedAsync(Framing(null, null), null)).Frames);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("3.5")]
    [InlineData("abc")]
    public async Task A_malformed_count_of_frames_is_refused(string frames)
    {
        var e = await Assert.ThrowsAsync<SidecarException>(() => FramedAsync(Framing("3", frames), 3));

        Assert.Equal(SidecarException.Failed, e.Code);
        Assert.Equal("The answer has no valid X-Kumbu-Frames", e.Message);
    }

    [Fact]
    public async Task A_frame_past_the_count_is_refused()
    {
        var e = await Assert.ThrowsAsync<SidecarException>(() => FramedAsync(Framing("7", "5"), 7));

        Assert.Equal((SidecarException.Failed, "The frame asked for lies past the frames of the image"), (e.Code, e.Message));
        Assert.Equal(7, (await FramedAsync(Framing("7", "7"), 7)).Frames);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, "no_such_frame", "no_such_frame")]
    [InlineData(HttpStatusCode.InternalServerError, "no_such_frame", "failed")]
    public async Task No_such_frame_is_known_with_its_status(HttpStatusCode status, string code, string known)
    {
        var stub = new Stub((_, _) => Json(status, $$"""{"error":"There is no frame 7.","code":"{{code}}"}"""));

        var e = await Assert.ThrowsAsync<SidecarException>(() => FramedAsync(stub, 7));

        Assert.Equal(known, e.Code);
    }

    /// <summary>A JPEG of SOI, one SOF0 frame header and EOI: enough for its header to say the size.</summary>
    private static byte[] Jpeg(int width, int height) =>
        [0xFF, 0xD8, 0xFF, 0xC0, 0, 11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0, 0xFF, 0xD9];
}
