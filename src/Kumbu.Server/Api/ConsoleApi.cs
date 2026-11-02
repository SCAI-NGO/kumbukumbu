// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Frozen;
using Kumbu.Core.Sh;
using Kumbu.Server.Mcp;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Kumbu.Server.Api;

/// <summary>One call of the console: <c>call</c> is <c>sh</c>, with <c>command</c> the line to run.</summary>
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
/// line that doesn't parse, go on to the shell, so that the error is its own. Neither the line nor what it
/// answers is logged, and MCP's tool metrics don't count it.
/// </summary>
public static class ConsoleApi
{
    /// <summary>The commands the console runs; every other command of the shell is refused.</summary>
    internal static readonly FrozenSet<string> Allowed = FrozenSet.Create(
        StringComparer.Ordinal,
        "ls", "cat", "outline", "rg", "semsearch", "img", "pdf", "links", "backlinks", "tags", "board", "head", "tail", "wc", "sort",
        "history", "diff", "trash", "lscp", "uname", "help");

    /// <summary>The commands that write. Not what the check reads (that is <see cref="Allowed"/>): it is for the test
    /// that every command of the shell has been decided on, and for the web app's help.</summary>
    internal static readonly FrozenSet<string> Refused = FrozenSet.Create(StringComparer.Ordinal, "restore", "mv", "mkdir", "rm", "mkcp", "rmcp");

    public static void MapConsoleApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/console").AddEndpointFilter(ApiErrors.Filter);
        api.MapPost("", RunAsync)
            .WithMetadata(SessionOnlyMetadata.Instance)
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024))
            .Errors(400, 401, 403, 413, 429, 503);
    }

    private static async Task<Ok<ConsoleView>> RunAsync(ConsoleRequest request, Shell shell, CancellationToken ct)
    {
        if (request.Call != "sh") throw ApiErrors.BadRequest("The console takes the call sh.");
        if (request.Command is not { } command) throw ApiErrors.BadRequest("Give a command line (command).");

        if (Pipeline.TryParse(command, out var stages, out _)
            && stages.Select(s => s[0]).FirstOrDefault(n => shell.HasCommand(n) && !Allowed.Contains(n)) is { } refused)
        {
            return TypedResults.Ok(View(ShellResult.Error($"{refused}: not in this console, which only reads")));
        }

        var (result, _) = await ToolResult.GuardAsync(async () =>
        {
            try
            {
                return await shell.RunAsync(command, ct);
            }
            catch (VaultBusyException e)
            {
                // GuardAsync would answer it as an error of the command, as MCP does; here it keeps its status.
                throw new ApiException(StatusCodes.Status503ServiceUnavailable, e.Message, ApiErrors.BusyRetryAfter);
            }
        });
        return TypedResults.Ok(View(result));
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
