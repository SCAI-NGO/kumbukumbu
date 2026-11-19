// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Metrics;
using Kumbu.Server.OAuth;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kumbu.Server.Mcp;

/// <summary>
/// Binary upload for agents: the tool checks what the PUT will check first (path, rights, format, version) and hands out
/// a one-time URL. The bytes never travel through MCP; the PUT (<c>/api/agent-upload/{token}</c>) runs as the credential
/// that made this call.
/// </summary>
[McpServerToolType]
public static class UploadTool
{
    private const string Bearer = "Bearer ";

    [McpServerTool(Name = "upload", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(Manual.UploadDescription)]
    public static async Task<CallToolResult> Upload(
        UploadTickets tickets,
        RequestCaller caller,
        INoteStore store,
        IHttpContextAccessor http,
        IOptions<KumbuOptions> options,
        [Description("Absolute path the file is to have, e.g. /Work/images/diagram.png or /Work/data/report.xlsx; any name but a note's (.md)")] string path,
        [Description("Version token of the file when it exists and is to be replaced; leave out to create a new file")] string? version = null,
        CancellationToken ct = default) =>
        await ToolResult.RunAsync(KumbuMetrics.Tool.Upload, () => IssueAsync(tickets, caller, store, http, options, path, version, ct));

    private static async Task<ShellResult> IssueAsync(
        UploadTickets tickets, RequestCaller caller, INoteStore store, IHttpContextAccessor http, IOptions<KumbuOptions> options,
        string path, string? version, CancellationToken ct)
    {
        if (http.HttpContext is not { } context || BearerOf(context) is not { Length: > 0 } bearer)
            return ShellResult.Error("upload: this connection has no bearer credential to upload with; connect with an API key or an access token");

        VaultPath filePath;
        try
        {
            if (!VaultPath.TryParse(path, out var parsed) || parsed.Segments.Count < 2)
                return ShellResult.Error($"upload: '{path}' is no file path; give an absolute path like /Work/images/diagram.png");
            filePath = ApiPaths.Parse(parsed.Segments[0], string.Join('/', parsed.Segments.Skip(1)), PathKind.Attachment);
            await NoteApi.RequireVaultAsync(store, filePath, ct);
        }
        catch (ApiException e)
        {
            // A note goes through write: say so, instead of only that it is no file.
            return ShellResult.Error(VaultPath.TryParse(path, out var note) && note.IsNote
                ? $"upload: {note.Value} is a note; create or replace it with the write tool"
                : $"upload: {e.Message}") with { Kind = e.Status switch { StatusCodes.Status403Forbidden => ShellErrorKind.Refused, StatusCodes.Status404NotFound => ShellErrorKind.NotFound, _ => ShellErrorKind.Invalid } };
        }

        // After the catch, so a note's path still points at write; the refusal names the tool itself.
        if (((ICaller)caller).Refuse("upload", filePath) is { } refused) return refused with { Kind = ShellErrorKind.Refused };

        // What the PUT refuses by the name alone; a format's bytes it checks then.
        var skillsVault = await store.SkillsVaultAsync(ct);
        var format = AttachmentFormat.ForPath(filePath, skillsVault);
        var refusal = format == AttachmentFormat.Marker ? "a folder is made with sh mkdir, not uploaded"
            : format?.Kind == AttachmentKind.Text ? "a skill's text file is written with the write tool, not uploaded"
            // The path names the file once, before the sentence.
            : format == AttachmentFormat.File && AttachmentFormat.Problem(filePath, [], skillsVault) is not null ? AttachmentFormat.SkillFolderMessage : null;
        if (refusal is not null) return ShellResult.Error($"upload: {filePath.Value}: {refusal}") with { Kind = ShellErrorKind.Invalid };

        var existing = await FileApi.Files(store).GetAttachmentAsync(filePath, ct);
        if (existing is not null && existing.Version != version)
        {
            return ShellResult.Error(version is null
                ? $"upload: {filePath.Value} exists already; to replace it, pass its version (sh: cat shows it)"
                : $"upload: {filePath.Value} has changed since version {version} (it is now {existing.Version})") with { Kind = ShellErrorKind.Conflict };
        }

        if (existing is null && version is not null)
            return ShellResult.Error($"upload: {filePath.Value} doesn't exist, so it has no version; leave version out to create it") with { Kind = ShellErrorKind.NotFound };

        var (holder, connectionId) = caller.Credential switch
        {
            ApiKeyCredential key => (key.KeyId, (string?)null),
            ConnectionCredential connection => (connection.ConnectionId, connection.ConnectionId),
            _ => ("dev", null),
        };

        // The PUT's own resolution refuses a credential that has expired meanwhile.
        var (token, expiresAt) = tickets.Issue(Encoding.UTF8.GetBytes(bearer), connectionId, filePath.Value, version, holder);
        var url = $"{OAuthUrls.Origin(context, options.Value.PublicUrl)}/api/agent-upload/{token}";
        return ShellResult.Ok($"""
            {url}
            expires {expiresAt:yyyy-MM-ddTHH:mm:ssZ}
            curl -sS -X PUT --data-binary @<local file> '{url}'

            Run that curl line in a shell on the machine that holds the file, with <local file> its path. The URL works once and until the time above; at most {FileTooLargeException.Shown(options.Value.Vaults.MaxFileBytes)}{(format == AttachmentFormat.File ? "" : $", and its content must be what the name {filePath.Name} says (the format is checked from the bytes)")}. If the PUT fails, call upload again for a new URL.
            """);
    }

    /// <summary>The credential the request authenticated with, as <c>CallerMiddleware</c> read it; null for a cookie or none.</summary>
    private static string? BearerOf(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase) ? header[Bearer.Length..] : null;
    }
}
