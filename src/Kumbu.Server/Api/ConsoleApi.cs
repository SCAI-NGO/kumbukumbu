// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Frozen;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Server.Auth;
using Kumbu.Server.Mcp;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Kumbu.Server.Api;

/// <summary>One call of the console: <c>call</c> is <c>sh</c>, with <c>command</c> the line to run, or <c>cd</c>, with
/// <c>path</c> the absolute path of a directory.</summary>
public sealed record ConsoleRequest(string? Call = null, string? Command = null, string? Path = null);

/// <summary>A piece of the answer, in order: <c>type</c> <c>text</c> with <c>text</c>, or <c>image</c> with
/// <c>mediaType</c> and <c>data</c> (base64).</summary>
public sealed record ConsolePartView(string Type, string? Text, string? MediaType, string? Data);

/// <summary>The version token of the note a command read; shown as <c>version of {path}: {token}</c>.</summary>
public sealed record ConsoleVersionView(string Path, string Token);

/// <summary>What the shell answered, as an MCP client gets it: a failed command is a 200 with <c>isError</c>.</summary>
public sealed record ConsoleView(ConsolePartView[] Parts, bool IsError, ConsoleVersionView? Version);

/// <summary>
/// The web app's console (console design §4, §5): the shell for a session, reading only. Every stage's command is
/// checked against <see cref="Allowed"/> before anything of the line runs; a command the shell doesn't have, and a
/// line that doesn't parse, go on to the shell, so that the error is its own. A line with <c>history</c> in any stage
/// takes a permit of <see cref="AccountLimits.HistoryScan"/>. <c>cd</c> says whether a path is a directory, answering
/// it normalised. Neither the line nor what it answers is logged, and MCP's tool metrics don't count it.
/// </summary>
public static class ConsoleApi
{
    /// <summary>The commands the console runs; every other command of the shell is refused.</summary>
    internal static readonly FrozenSet<string> Allowed = FrozenSet.Create(
        StringComparer.Ordinal,
        "ls", "cat", "outline", "rg", "semsearch", "img", "pdf", "links", "backlinks", "tags", "board", "head", "tail", "wc", "sort",
        "history", "diff", "trash", "df", "du", "lscp", "publish", "skills", "uname", "help");

    /// <summary>The commands that write. Not what the check reads (that is <see cref="Allowed"/>): it is for the test
    /// that every command of the shell has been decided on, and for the web app's help.</summary>
    internal static readonly FrozenSet<string> Refused = FrozenSet.Create(StringComparer.Ordinal, "pdfimages", "restore", "mv", "mkdir", "rm", "mkcp", "rmcp");

    public static void MapConsoleApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/console").AddEndpointFilter(ApiErrors.Filter);
        api.MapPost("", RunAsync)
            .WithMetadata(SessionOnlyMetadata.Instance)
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .Errors(400, 401, 403, 413, 429, 503);
    }

    private static async Task<Ok<ConsoleView>> RunAsync(
        ConsoleRequest request, Shell shell, INoteStore notes, RequestCaller caller, AccountLimits limits, CancellationToken ct)
    {
        switch (request.Call)
        {
            case "sh":
                if (request.Command is not { } command) throw ApiErrors.BadRequest("Give a command line (command).");
                return TypedResults.Ok(View(await ShAsync(command, shell, caller, limits, ct)));
            case "cd":
                if (request.Path is not { } path) throw ApiErrors.BadRequest("Give a path (path).");
                return TypedResults.Ok(View(await GuardAsync(() => CdAsync(path, notes, ct))));
            default:
                throw ApiErrors.BadRequest("The console takes the calls sh and cd.");
        }
    }

    private static async Task<ShellResult> ShAsync(string command, Shell shell, RequestCaller caller, AccountLimits limits, CancellationToken ct)
    {
        if (Pipeline.TryParse(command, out var stages, out _))
        {
            if (stages.Select(s => s[0]).FirstOrDefault(n => shell.HasCommand(n) && !Allowed.Contains(n)) is { } refused)
            {
                return ShellResult.Error($"{refused}: not in this console, which only reads");
            }

            // Of publish, only ls reads (publish spec §3.9).
            if (stages.FirstOrDefault(s => s[0] == "publish" && (s.Count < 2 || s[1] != "ls")) is { } publish)
            {
                return ShellResult.Error($"{string.Join(' ', publish.Take(2))}: not in this console, which only reads");
            }

            // Of skills, copy writes; ls, find, cat and check read.
            if (stages.Any(s => s[0] == "skills" && s.Count > 1 && s[1] == "copy"))
            {
                return ShellResult.Error("skills copy: not in this console, which only reads");
            }

            // Also when the shell then refuses the line: the check stays as simple as the read-only one.
            if (stages.Any(s => s[0] == "history") && limits.HistoryScan(caller.User!.Id) is { } retryAfter)
            {
                throw ApiErrors.TooMany(retryAfter, AccountLimits.HistoryScansRefused);
            }
        }

        return await GuardAsync(() => shell.RunAsync(command, ct));
    }

    /// <summary>A directory is the root, a vault, or a path with notes or files below it, a kept folder's marker among
    /// them, so an empty folder is one too (as <c>ls</c> finds it). A note's or file's own path isn't.</summary>
    private static async Task<ShellResult> CdAsync(string input, INoteStore notes, CancellationToken ct)
    {
        var refused = ShellResult.Error($"cd: {input}: not a folder");
        if (!VaultPath.TryParse(input, out var path) || FolderMarker.Is(path)) return refused;
        if (path.IsRoot) return ShellResult.Ok(path.Value);
        if (path.Segments.Count == 1) return (await notes.ListVaultsAsync(ct)).Contains(path.Segments[0]) ? ShellResult.Ok(path.Value) : refused;
        if (await notes.ReadAsync(path, ct) is not null || await StoreAttachments.GetAsync(notes, path, ct) is not null) return refused;
        return (await notes.ListNotesUnderAsync(path, ct)).Count > 0 || (await StoreAttachments.UnderAsync(notes, path, ct)).Count > 0
            ? ShellResult.Ok(path.Value)
            : refused;
    }

    private static async Task<ShellResult> GuardAsync(Func<Task<ShellResult>> body)
    {
        var (result, _) = await ToolResult.GuardAsync(async () =>
        {
            try
            {
                return await body();
            }
            catch (VaultBusyException e)
            {
                // GuardAsync would answer it as an error of the command, as MCP does; here it keeps its status.
                throw new ApiException(StatusCodes.Status503ServiceUnavailable, e.Message, ApiErrors.BusyRetryAfter);
            }
        });
        return result;
    }

    internal static ConsoleView View(ShellResult result)
    {
        ConsolePartView[] parts = result.Parts is { } shown
            ? [.. shown.Select(Part)]
            : [new ConsolePartView("text", result.Output, null, null)];
        var version = result.Version is { } v ? new ConsoleVersionView(v.Path, v.Token) : null;
        return new ConsoleView(parts, result.IsError, version);
    }

    private static ConsolePartView Part(ShellPart part) => part switch
    {
        TextPart text => new ConsolePartView("text", text.Text, null, null),
        ImagePart image => new ConsolePartView("image", null, image.MediaType, Convert.ToBase64String(image.Data)),
        _ => throw new InvalidOperationException("unknown shell part"),
    };
}
