// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kumbu.Core.Links;
using Kumbu.Core.Publishing;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Kumbu.Server.Publishing;

/// <summary>
/// The AI link (AI link spec §3.3–§3.5): <c>GET</c> or <c>HEAD</c> <c>/s/t/{token}</c> answers the copy as Markdown,
/// <c>/s/t/{token}/files/{index}</c> its images, for anyone with the token, within its hour and ceiling, while the
/// publication stands and its owner is active. Every other path or method below <see cref="Path"/>, and every end,
/// answers the one plain-text 404; nothing below it reaches the web app. A dead token is no guess at a share code: it
/// isn't counted towards <see cref="AccountLimits.ReadersBlocked"/>. The token is in the path by design; it is never
/// logged, and <see cref="ReaderPaths"/> keeps it out of the metrics. ASP.NET's log scope of a request carries its path,
/// so these handlers write no log line within a request (<see cref="Filter"/>).
/// </summary>
public static class AiLinkApi
{
    public const string Path = "/s/t";
    public const string NotFoundSentence = "This link for AIs has expired or the note is no longer published. Ask for a new one on the note's page.";
    public const string BusySentence = "Too many requests; try again in a minute.";
    public const string FaultSentence = "Something went wrong; try again later.";

    private const string PlainText = "text/plain; charset=utf-8";

    public static void MapAiLinks(this IEndpointRouteBuilder app)
    {
        string[] methods = [HttpMethods.Get, HttpMethods.Head];
        app.MapMethods(Path + "/{token}", methods, MarkdownAsync).AddEndpointFilter(Filter).AllowAnonymous().ExcludeFromDescription();
        app.MapMethods(Path + "/{token}/files/{index}", methods, FileAsync).AddEndpointFilter(Filter)
            .WithMetadata(FileContentMetadata.Instance).AllowAnonymous().ExcludeFromDescription();
        // Anonymous, else CallerMiddleware answers 401; and more specific than the web app's fallback, which would
        // answer HEAD, POST and every other path here with index.html.
        app.MapFallback(Path + "/{**path}", () => Gone).AllowAnonymous();
    }

    private static async Task<IResult> MarkdownAsync(
        string token, HttpContext http, PublicationStore store, UserCatalog users, AccountLimits limits, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var (normal, opened) = await OpenAsync(token, limits.ReaderRead, http, store, users, limits, options.Value, ct);
        var markdown = Markdown(opened.Copy, $"{Publications.BaseUrl(http, options.Value)}{Path}/{normal}/files");
        return new Answer(StatusCodes.Status200OK, "text/markdown; charset=utf-8", Encoding.UTF8.GetBytes(markdown));
    }

    private static async Task<IResult> FileAsync(
        string token, string index, HttpContext http, PublicationStore store, UserCatalog users, AccountLimits limits, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        var (_, opened) = await OpenAsync(token, limits.ReaderFileRead, http, store, users, limits, options.Value, ct);
        return int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && await store.OpenFileAsync(opened, n, ct) is { } file
            ? new Answer(StatusCodes.Status200OK, file.MediaType, file.Bytes)
            : Gone;
    }

    /// <summary>
    /// The read's own limits first (<paramref name="allowance"/>, as on the reader page), then a network that wrong share
    /// codes block, then the switch, then the token: unexpired, its owner active (spec §3.2: deactivation hides it, and
    /// reactivation within the hour shows it again), and counted against its ceiling for the copy served now, so the
    /// ceiling follows an Update. A refusal is thrown, as <see cref="Refusal"/>, for <see cref="Filter"/> to answer.
    /// </summary>
    private static async Task<(string Token, OpenedPublication Opened)> OpenAsync(
        string typed, Func<HttpContext, TimeSpan?> allowance, HttpContext http, PublicationStore store, UserCatalog users, AccountLimits limits,
        KumbuOptions options, CancellationToken ct)
    {
        if (allowance(http) is { } retryAfter)
        {
            ApiErrors.SetRetryAfter(http, retryAfter);
            throw new Refusal(new Answer(StatusCodes.Status429TooManyRequests, PlainText, Encoding.UTF8.GetBytes(BusySentence)));
        }

        if (limits.ReadersBlocked(http)) throw new Refusal(Gone);
        if (!options.Publish.Enabled) throw new Refusal(new Answer(StatusCodes.Status404NotFound, PlainText, Encoding.UTF8.GetBytes(ReaderApi.OffSentence)));
        if (AiLinkToken.Parse(typed) is { } token && await store.OpenAiLinkAsync(token, ct) is { } link
            && await users.Users.Find(u => u.Id == link.Opened.Owner).FirstOrDefaultAsync(ct) is { Active: true }
            && await store.CountAiLinkAsync(link.Id, AiLinkToken.Ceiling(link.Opened.Copy.Files.Count), ct))
        {
            return (token, link.Opened);
        }

        throw new Refusal(Gone);
    }

    /// <summary>
    /// The filter of both <c>/s/t</c> endpoints: a <see cref="Refusal"/> becomes its answer; any other fault a plain-text
    /// 500, logged by its type alone and outside the request's execution context, so that no log scope of the request,
    /// which carries its path and so the token, accompanies the line. A request the client dropped is no fault.
    /// </summary>
    private static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (Refusal refusal)
        {
            return refusal.Answer;
        }
        catch (Exception e) when (!(e is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested))
        {
            var log = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AiLinkApi).FullName!);
            var type = e.GetType().FullName;
            using (ExecutionContext.SuppressFlow())
            {
                _ = Task.Run(() => log.LogError("A fetch of an AI link failed: {Type}", type));
            }

            return new Answer(StatusCodes.Status500InternalServerError, PlainText, Encoding.UTF8.GetBytes(FaultSentence));
        }
    }

    /// <summary>A refusal of a fetch, thrown with its plain-text answer.</summary>
    private sealed class Refusal(Answer answer) : Exception
    {
        public Answer Answer { get; } = answer;
    }

    private static Answer Gone => new(StatusCodes.Status404NotFound, PlainText, Encoding.UTF8.GetBytes(NotFoundSentence));

    /// <summary>
    /// The copy as the reader page shows it (spec §3.3): its title as a heading, what this is, then its Markdown byte for
    /// byte but for the embeds of its files. An embed of <c>f/&lt;n&gt;</c> with <c>n</c> among the copy's files points
    /// to <paramref name="fileBase"/><c>/&lt;n&gt;</c>, its media type beside it; with <c>n</c> not among them it is left
    /// out, as the page leaves it out. The server knows no reader's time zone: the snapshot is in UTC, and says so.
    /// </summary>
    internal static string Markdown(PublishedCopy copy, string fileBase)
    {
        var types = copy.Files.ToDictionary(f => f.Index, f => f.MediaType);
        var text = new StringBuilder()
            .Append("# ").Append(copy.Title.ReplaceLineEndings(" ")).Append("\n\n")
            .Append(CultureInfo.InvariantCulture, $"A published note, snapshot of {copy.SnapshotAt:yyyy-MM-dd HH:mm} UTC; links to other notes are plain text.\n\n");
        var source = copy.Markdown;
        // Where each line starts, split on '\n' as MarkdownLink.Parse splits it: a '\r' stays where it is.
        List<int> lineStarts = [0];
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n') lineStarts.Add(i + 1);
        }

        var done = 0;
        foreach (var link in MarkdownLink.Parse(source))
        {
            if (!link.Embed || FileIndex(link.Destination) is not { } n) continue;
            var start = lineStarts[link.Line] + link.Start;
            text.Append(source, done, start - done);
            if (types.TryGetValue(n, out var type))
            {
                text.Append(link.Raw, 0, link.Raw.Length - link.Rest.Length - link.Destination.Length)
                    .Append(CultureInfo.InvariantCulture, $"{fileBase}/{n}").Append(link.Rest).Append(" (").Append(type).Append(')');
            }

            done = start + link.Raw.Length;
        }

        return text.Append(source, done, source.Length - done).ToString();
    }

    /// <summary><c>f/&lt;n&gt;</c>, bare or in angle brackets, as the page reads both.</summary>
    private static int? FileIndex(string destination)
    {
        var bare = destination.Length > 1 && destination[0] == '<' && destination[^1] == '>' ? destination.AsSpan(1, destination.Length - 2) : destination.AsSpan();
        return bare.StartsWith(FairCopies.FilePrefix, StringComparison.Ordinal)
            && int.TryParse(bare[FairCopies.FilePrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n
                : null;
    }

    /// <summary>A body with its length; to a <c>HEAD</c> the same status and headers, without the body. Not
    /// <c>TypedResults.Text</c>: that writes its body to a <c>HEAD</c> as well.</summary>
    private sealed class Answer(int status, string contentType, byte[] body) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = status;
            http.Response.ContentType = contentType;
            http.Response.ContentLength = body.Length;
            return HttpMethods.IsHead(http.Request.Method) ? Task.CompletedTask : http.Response.Body.WriteAsync(body, http.RequestAborted).AsTask();
        }
    }
}
