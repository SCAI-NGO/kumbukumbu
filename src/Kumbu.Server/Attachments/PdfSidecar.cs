// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Kumbu.Core.Attachments;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Attachments;

/// <summary>
/// The PDF sidecar (<c>pdf/</c>): the only place untrusted PDFs are parsed and untrusted images decoded, outside the
/// process that holds vault keys. <see cref="PdfSidecar"/> speaks to it over HTTP; tests replace it.
/// </summary>
public interface IPdfSidecar
{
    /// <summary>The analysis of at most <paramref name="maxPages"/> pages, with each page's text.</summary>
    Task<SidecarAnalysis> AnalyseAsync(ReadOnlyMemory<byte> pdf, int maxPages, CancellationToken ct);

    /// <summary>Page <paramref name="page"/> (1-based) rendered to fit <paramref name="target"/>, optionally a region in displayed points.</summary>
    Task<Picture> RenderPageAsync(ReadOnlyMemory<byte> pdf, int page, StepLimits target, Crop? crop, CancellationToken ct);

    /// <summary>The image scaled down to fit <paramref name="target"/>, optionally cropped in its (oriented) pixels.</summary>
    Task<Picture> RenderImageAsync(ReadOnlyMemory<byte> image, StepLimits target, Crop? crop, CancellationToken ct);
}

/// <summary>An analysis as the sidecar answers it: the measurements, and the text of each analysed page (index 0 = page 1).</summary>
public sealed record SidecarAnalysis(PdfAnalysis Analysis, IReadOnlyList<string> PageTexts);

/// <summary>
/// A request the sidecar refused or couldn't answer. <see cref="Code"/> is the sidecar's (<c>not_pdf</c>,
/// <c>encrypted</c>, <c>no_such_page</c>, <c>bad_crop</c>, <c>busy</c>, <c>failed</c>, …), or <see cref="Unavailable"/>
/// (no URL configured, no connection, a gateway's error) or <see cref="Timeout"/>.
/// </summary>
public sealed class SidecarException(string code, string message) : Exception(message)
{
    public const string Unavailable = "unavailable";
    public const string Timeout = "timeout";
    public const string Busy = "busy";
    public const string Encrypted = "encrypted";
    public const string Failed = "failed";

    public string Code { get; } = code;

    /// <summary>Not the file's fault: the same request may succeed later.</summary>
    public bool Transient => Code is Unavailable or Busy;
}

/// <summary>
/// <see cref="IPdfSidecar"/> over HTTP (plan: Decisions, sidecar API). What comes back is checked before anything
/// uses it: an analysis is bounded in size and must add up (pages 1…N, coverages 0…1, finite numbers); an image must be
/// the PNG or JPEG it says it is, of the size it says, within the step. Logs no content, no file names: outcomes only.
/// </summary>
public sealed class PdfSidecar(HttpClient http, IOptions<KumbuOptions> options, ILogger<PdfSidecar> log) : IPdfSidecar
{
    public const string UnavailableMessage = "PDF rendering is unavailable right now";

    /// <summary>The largest analysis answer read: 2,000 pages of text and measurements stay far below it.</summary>
    internal const int MaxAnalysisBytes = 64 * 1024 * 1024;

    /// <summary>The largest image read: a page that doesn't fit its byte target even at the lowest quality is sent as it is.</summary>
    internal const int MaxImageBytes = 16 * 1024 * 1024;

    private const int MaxErrorBytes = 64 * 1024;
    private const int MaxMetadataChars = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The sidecar's codes with the status each comes with (plan: Decisions, sidecar API).</summary>
    private static readonly Dictionary<string, HttpStatusCode> Codes = new()
    {
        ["bad_request"] = HttpStatusCode.BadRequest,
        ["not_pdf"] = HttpStatusCode.BadRequest,
        ["not_image"] = HttpStatusCode.BadRequest,
        ["too_large"] = HttpStatusCode.RequestEntityTooLarge,
        ["no_such_page"] = HttpStatusCode.UnprocessableEntity,
        ["bad_crop"] = HttpStatusCode.UnprocessableEntity,
        [SidecarException.Encrypted] = HttpStatusCode.UnprocessableEntity,
        [SidecarException.Busy] = HttpStatusCode.ServiceUnavailable,
        [SidecarException.Timeout] = HttpStatusCode.GatewayTimeout,
        [SidecarException.Failed] = HttpStatusCode.InternalServerError,
    };

    private PdfOptions Pdf => options.Value.Pdf;

    /// <summary>The sidecar's longest analysis (20 minutes), with a margin for its queue and the transfer.</summary>
    internal TimeSpan AnalysisTimeout => TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(Pdf.AnalysisSeconds + 35);

    internal TimeSpan RenderTimeout => TimeSpan.FromSeconds(Pdf.RenderSeconds + 5);

    public async Task<SidecarAnalysis> AnalyseAsync(ReadOnlyMemory<byte> pdf, int maxPages, CancellationToken ct)
    {
        var body = await SendAsync("analyse", $"maxPages={maxPages.ToString(CultureInfo.InvariantCulture)}", pdf, "application/pdf", AnalysisTimeout, ct,
            async (response, token) => await ReadAsync(response.Content, MaxAnalysisBytes, token) ?? throw Invalid("The analysis is larger than 64 MiB"));
        return ParseAnalysis(body, maxPages);
    }

    public Task<Picture> RenderPageAsync(ReadOnlyMemory<byte> pdf, int page, StepLimits target, Crop? crop, CancellationToken ct) =>
        SendAsync("render/page", $"page={page.ToString(CultureInfo.InvariantCulture)}&{Query(target, crop)}", pdf, "application/pdf", RenderTimeout, ct,
            (response, token) => PictureAsync(response, target, "X-Kumbu-Page-Width", "X-Kumbu-Page-Height", token));

    public Task<Picture> RenderImageAsync(ReadOnlyMemory<byte> image, StepLimits target, Crop? crop, CancellationToken ct) =>
        SendAsync("render/image", Query(target, crop), image, "application/octet-stream", RenderTimeout, ct,
            (response, token) => PictureAsync(response, target, "X-Kumbu-Original-Width", "X-Kumbu-Original-Height", token));

    private static string Query(StepLimits target, Crop? crop) =>
        string.Create(CultureInfo.InvariantCulture, $"longEdge={target.LongEdge}&pixels={target.Pixels}&bytes={target.Bytes}")
        + (crop is null ? "" : "&crop=" + crop);

    /// <summary>
    /// One request with its own time limit. A time-out of this client, a refused connection or a failed name lookup,
    /// and a gateway's error without the sidecar's JSON, say what they are; the caller's cancellation stays a cancellation.
    /// </summary>
    private async Task<T> SendAsync<T>(
        string route, string query, ReadOnlyMemory<byte> body, string mediaType, TimeSpan limit, CancellationToken ct, Func<HttpResponseMessage, CancellationToken, Task<T>> read)
    {
        if (Pdf.Url == "") throw new SidecarException(SidecarException.Unavailable, UnavailableMessage);
        var baseUrl = Pdf.Url.EndsWith('/') ? Pdf.Url : Pdf.Url + "/";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), $"{route}?{query}"))
            {
                Content = new ReadOnlyMemoryContent(body) { Headers = { ContentType = new MediaTypeHeaderValue(mediaType) } },
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw await ErrorAsync(response, timeout.Token);
            return await read(response, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("The PDF sidecar did not answer within {Seconds} s", (int)limit.TotalSeconds);
            throw new SidecarException(SidecarException.Timeout, "The PDF sidecar did not answer in time");
        }
        catch (HttpRequestException e)
        {
            log.LogWarning("The PDF sidecar can't be reached ({Error})", e.HttpRequestError);
            throw new SidecarException(SidecarException.Unavailable, UnavailableMessage);
        }
        catch (IOException)
        {
            log.LogWarning("The PDF sidecar's answer broke off");
            throw new SidecarException(SidecarException.Unavailable, UnavailableMessage);
        }
    }

    private static async Task<SidecarException> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? code = null, message = null;
        try
        {
            if (await ReadAsync(response.Content, MaxErrorBytes, ct) is { } bytes)
            {
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                    && doc.RootElement.TryGetProperty("error", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    (code, message) = (c.GetString(), m.GetString());
                }
            }
        }
        catch (JsonException)
        {
            // Not the sidecar's answer (a gateway's page, say): decided by the status below.
        }

        if (response.StatusCode == HttpStatusCode.GatewayTimeout) return new SidecarException(SidecarException.Timeout, message ?? "The PDF sidecar did not answer in time");
        if (code is null) return new SidecarException(SidecarException.Unavailable, UnavailableMessage);
        // The sidecar's JSON with a code it doesn't give with this status: not an answer to trust as that code.
        return Codes.TryGetValue(code, out var status) && status == response.StatusCode
            ? new SidecarException(code, message!)
            : new SidecarException(SidecarException.Failed, "The PDF sidecar's answer doesn't add up");
    }

    /// <summary>The body, or null if it is longer than <paramref name="max"/>; reading stops there.</summary>
    private static async Task<byte[]?> ReadAsync(HttpContent content, int max, CancellationToken ct)
    {
        if (content.Headers.ContentLength > max) return null;
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var piece = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(piece, ct)) > 0)
        {
            if (buffer.Length + n > max) return null;
            buffer.Write(piece, 0, n);
        }

        return buffer.ToArray();
    }

    private static async Task<Picture> PictureAsync(HttpResponseMessage response, StepLimits target, string originalWidth, string originalHeight, CancellationToken ct)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not ("image/png" or "image/jpeg")) throw Invalid("The answer is not a PNG or JPEG image");
        var data = await ReadAsync(response.Content, MaxImageBytes, ct) ?? throw Invalid("The image is larger than 16 MiB");
        var png = data.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var jpeg = data.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF });
        if (mediaType == "image/png" ? !png : !jpeg) throw Invalid("The image's bytes are not what its type says");

        var width = Whole(response, "X-Kumbu-Width");
        var height = Whole(response, "X-Kumbu-Height");
        if (ImageHeader.Read(data, out _) is not var (w, h) || w != width || h != height)
        {
            throw Invalid("The image's header doesn't match the size it was given");
        }

        // The sidecar rounds the scaled size; a pixel over is rounding, more is not the step asked for.
        if (Math.Max(w, h) > target.LongEdge + 1) throw Invalid("The image is larger than the size step");
        return new Picture(data, mediaType, w, h, Size(response, originalWidth), Size(response, originalHeight));
    }

    private static int Whole(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values) && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : throw Invalid($"The answer has no valid {header}");

    private static double Size(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values)
        && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && v > 0
            ? v
            : throw Invalid($"The answer has no valid {header}");

    private static SidecarException Invalid(string why) => new(SidecarException.Failed, why);

    private sealed record AnalysisJson(string? Analyser, int PageCount, int AnalysedPages, string? Title, string? Author, List<PageJson>? Pages);

    private sealed record PageJson(
        int N, double Width, double Height, int Rotation, int Chars, string? Text, string? Type,
        double TextCoverage, double ImageCoverage, double VectorCoverage, int Images, List<RegionJson>? Regions);

    private sealed record RegionJson(string? Kind, double X, double Y, double W, double H);

    /// <summary>The sidecar's JSON (<c>pdf/src/analyse.ts</c>), checked to add up; <see cref="SidecarException.Failed"/> if it doesn't.</summary>
    internal static SidecarAnalysis ParseAnalysis(byte[] body, int maxPages)
    {
        AnalysisJson a;
        try
        {
            a = JsonSerializer.Deserialize<AnalysisJson>(body, Json) ?? throw Invalid("The analysis is empty");
        }
        catch (JsonException)
        {
            throw Invalid("The analysis is not valid JSON");
        }

        if (a.Analyser is not { Length: > 0 and <= 100 } || a.Pages is null || a.PageCount < 1
            || a.AnalysedPages != Math.Min(a.PageCount, maxPages) || a.Pages.Count != a.AnalysedPages)
        {
            throw Invalid("The analysis doesn't add up");
        }

        var pages = new List<PdfPage>(a.Pages.Count);
        var texts = new List<string>(a.Pages.Count);
        for (var i = 0; i < a.Pages.Count; i++)
        {
            var p = a.Pages[i];
            if (p.N != i + 1 || p.Text is null || p.Regions is null || p.Chars < 0 || p.Images < 0
                || p.Rotation is not (0 or 90 or 180 or 270) || !Point(p.Width) || !Point(p.Height)
                || !Share(p.TextCoverage) || !Share(p.ImageCoverage) || !Share(p.VectorCoverage))
            {
                throw Invalid($"Page {i + 1} of the analysis doesn't add up");
            }

            var type = p.Type switch
            {
                "text" => PageType.Text,
                "mixed" => PageType.Mixed,
                "image" => PageType.Image,
                _ => throw Invalid($"Page {i + 1} of the analysis has no known type"),
            };
            var regions = new List<PdfRegion>(p.Regions.Count);
            foreach (var r in p.Regions)
            {
                if (r.Kind is not ("image" or "drawing") || !Point(r.X) || !Point(r.Y) || !Point(r.W) || !Point(r.H))
                {
                    throw Invalid($"A region on page {i + 1} of the analysis doesn't add up");
                }

                regions.Add(new PdfRegion(r.Kind, r.X, r.Y, r.W, r.H));
            }

            pages.Add(new PdfPage(p.N, p.Width, p.Height, p.Rotation, type, p.Chars, p.TextCoverage, p.ImageCoverage, p.VectorCoverage, p.Images, regions));
            texts.Add(p.Text);
        }

        return new SidecarAnalysis(new PdfAnalysis(a.Analyser, a.PageCount, a.AnalysedPages, Metadata(a.Title), Metadata(a.Author), pages), texts);

        static bool Point(double v) => double.IsFinite(v) && v >= 0;
        static bool Share(double v) => double.IsFinite(v) && v is >= 0 and <= 1;
        static string? Metadata(string? v) => v is null ? null : v.Length > MaxMetadataChars ? v[..MaxMetadataChars] : v;
    }
}
