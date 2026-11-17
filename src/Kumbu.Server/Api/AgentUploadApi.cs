// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Server.Auth;
using Kumbu.Server.Mcp;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary>An agent's upload as stored: the file, what to write into a note to show it (an image) or link to it
/// (<c>markdown</c>), and its link into the web app (<c>link</c>; null for the dev token, which has no vault ids).</summary>
public sealed record AgentUploadView(FileStateView File, string Markdown, string? Link);

/// <summary>
/// Binary upload for agents (binary upload design): <c>PUT /api/agent-upload/{token}</c> stores the body at the one
/// path of the ticket the <c>upload</c> tool handed out. <see cref="CallerMiddleware"/> has taken the ticket and
/// resolved its credential as a bearer request's before this runs; the token alone authorises, no cookie or header.
/// </summary>
public static class AgentUploadApi
{
    /// <summary>Ends every refusal of a ticket or its credential: the ticket is used, whatever went wrong.</summary>
    public const string AskAgain = "Ask for a new upload URL with the upload tool.";

    public static void MapAgentUploadApi(this IEndpointRouteBuilder app)
    {
        // AskAgainFilter outside ApiErrors.Filter, so that it sees the refusals as answered.
        app.MapPut("/api/agent-upload/{token}", UploadAsync)
            .AddEndpointFilter(AskAgainFilter)
            .AddEndpointFilter(ApiErrors.Filter)
            .TakesFileBody()
            .Errors(400, 401, 403, 404, 409, 413, 429, 503, 507);
    }

    private static async Task<Results<Created<AgentUploadView>, Ok<AgentUploadView>, Conflict<FileConflictView>>> UploadAsync(
        HttpContext context, RequestCaller caller, INoteStore store, NoteWriter notes, WriterLabels labels, DeepLinks links, CancellationToken ct)
    {
        var ticket = context.Features.Get<UploadTicket>() ?? throw new InvalidOperationException("An agent upload reached its endpoint without CallerMiddleware's ticket");
        // As the tool parsed it when it issued the ticket.
        if (!VaultPath.TryParse(ticket.Path, out var asked) || asked.Segments.Count < 2) throw new InvalidOperationException("A ticket holds no file path");
        var filePath = ApiPaths.Parse(asked.Segments[0], string.Join('/', asked.Segments.Skip(1)), PathKind.Attachment);

        var stored = await FileApi.StoreAsync(filePath, ticket.Version, context.Request, caller, store, notes, labels, ct);
        return stored.Result switch
        {
            Created<FileStateView> created => TypedResults.Created(created.Location, View(created.Value!, filePath, links)),
            Ok<FileStateView> ok => TypedResults.Ok(View(ok.Value!, filePath, links)),
            Conflict<FileConflictView> conflict => conflict,
            _ => throw new InvalidOperationException("StoreAsync answered something else"),
        };
    }

    /// <summary>The link as the manual teaches it (the path inside the vault, encoded where Markdown would misread it),
    /// an image's embedded with <c>!</c>.</summary>
    private static AgentUploadView View(FileStateView file, VaultPath path, DeepLinks links)
    {
        var text = path.Name.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("[", @"\[", StringComparison.Ordinal).Replace("]", @"\]", StringComparison.Ordinal);
        var image = AttachmentFormat.ForPath(path) is { Kind: AttachmentKind.Image };
        return new AgentUploadView(file, $"{(image ? "!" : "")}[{text}]({MarkdownLink.Encode(path)})", links.For(path.Value));
    }

    /// <summary>A refusal of who may write there (401, 403, 404 for a vault the credential no longer opens, or 503 while a vault is re-encrypted) says to ask
    /// again; format, size, conflict and quota answers keep the files API's own.</summary>
    private static async ValueTask<object?> AskAgainFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        return result is JsonHttpResult<ApiError> { StatusCode: StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden or StatusCodes.Status404NotFound or StatusCodes.Status503ServiceUnavailable, Value: { } error } refused
            ? TypedResults.Json(error with { Error = $"{error.Error} {AskAgain}" }, statusCode: refused.StatusCode)
            : result;
    }
}
