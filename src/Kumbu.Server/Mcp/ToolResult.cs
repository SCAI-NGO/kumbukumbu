// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Kumbu.Core.Sh;
using Kumbu.Server.Metrics;
using Kumbu.Server.Vaults;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Mcp;

internal static class ToolResult
{
    private const int MaxLinks = 10;

    /// <summary>
    /// Errors are tool results (isError: true), not JSON-RPC errors: a wrong path is a normal answer.
    /// A version token travels as its own content block, so it never mixes with the note's text; the note's link into the web app, when there is one, follows it.
    /// </summary>
    public static CallToolResult From(ShellResult result, Func<string, string?>? linkOf = null)
    {
        List<ContentBlock> content = result.Parts is { } parts
            ? [.. parts.Select(Block)]
            : [new TextContentBlock { Text = result.Output }];
        // Each starts on its own line: some clients join the blocks without a separator.
        // Every version is kept (the agent needs each for a follow-up edit); links are for the person to tap, so past MaxLinks they only cost context.
        var linked = 0;
        var left = 0;
        foreach (var version in result.Versions ?? (result.Version is { } one ? [one] : []))
        {
            content.Add(new TextContentBlock { Text = $"\nversion of {version.Path}: {version.Token}" });
            if (linkOf?.Invoke(version.Path) is not { } link) continue;
            if (linked < MaxLinks)
            {
                linked++;
                content.Add(new TextContentBlock { Text = $"\nlink to {version.Path}: {link}" });
            }
            else
            {
                left++;
            }
        }

        if (left > 0) content.Add(new TextContentBlock { Text = $"\n…and {left} more {(left == 1 ? "link" : "links")}: cat a note for its link" });

        return new CallToolResult { IsError = result.IsError, Content = content };
    }

    // FromBytes keeps the bytes and encodes them for Data (base64 text on the wire).
    private static ContentBlock Block(ShellPart part) => part switch
    {
        TextPart text => new TextContentBlock { Text = text.Text },
        ImagePart image => ImageContentBlock.FromBytes(image.Data, image.MediaType),
        _ => throw new InvalidOperationException("unknown shell part"),
    };

    /// <summary>Runs a tool body; vaults that can't be used at all become a tool error with the reason. Counts and times the call.</summary>
    public static async Task<CallToolResult> RunAsync(KumbuMetrics.Tool tool, Func<Task<ShellResult>> body, DeepLinks? links = null)
    {
        var started = Stopwatch.GetTimestamp();
        ShellResult result;
        KumbuMetrics.ToolError kind;
        try
        {
            (result, kind) = await GuardAsync(body);
        }
        catch
        {
            // Unexpected: the MCP library answers it; it still counts as a call that failed.
            KumbuMetrics.ToolFailed(tool, KumbuMetrics.ToolError.Other);
            throw;
        }
        finally
        {
            KumbuMetrics.ToolCalled(tool, Stopwatch.GetElapsedTime(started));
        }

        if (result.IsError)
        {
            KumbuMetrics.ToolFailed(tool, kind);
            if (kind == KumbuMetrics.ToolError.Conflict) KumbuMetrics.WriteConflict(KumbuMetrics.WriteVia.Mcp);
        }

        return From(result, links is null ? null : links.For);
    }

    /// <summary>Runs a body; vaults that can't be used at all become an error result with the reason, and the kind of
    /// error it is. Shared by MCP and the web app's console, so both say the same.</summary>
    internal static async Task<(ShellResult Result, KumbuMetrics.ToolError Kind)> GuardAsync(Func<Task<ShellResult>> body)
    {
        try
        {
            var result = await body();
            return (result, result.Kind switch
            {
                ShellErrorKind.Invalid => KumbuMetrics.ToolError.Invalid,
                ShellErrorKind.Refused => KumbuMetrics.ToolError.Refused,
                ShellErrorKind.NotFound => KumbuMetrics.ToolError.NotFound,
                ShellErrorKind.Conflict => KumbuMetrics.ToolError.Conflict,
                ShellErrorKind.Unavailable => KumbuMetrics.ToolError.Unavailable,
                _ => KumbuMetrics.ToolError.Other,
            });
        }
        catch (VaultUnavailableException e)
        {
            return (ShellResult.Error(e.Message), KumbuMetrics.ToolError.Unavailable);
        }
        catch (VaultReadOnlyException e)
        {
            return (ShellResult.Error(e.Message), KumbuMetrics.ToolError.Refused);
        }
        // Only the owner is pointed at df (WebDAV design §3.10): a member's agent gets the sentence alone.
        catch (Kumbu.Core.Notes.QuotaExceededException e)
        {
            var sentence = e.Owner ? e.Message + " See `df`." : e.Message;
            return (ShellResult.Error(sentence) with { Kind = ShellErrorKind.Refused, Code = e.Owner ? Kumbu.Core.Notes.Refusals.Quota : null }, KumbuMetrics.ToolError.Refused);
        }
        catch (Kumbu.Core.Publishing.PublishedNoteException e)
        {
            return (ShellResult.Error(e.Message) with { Kind = ShellErrorKind.Refused, Code = e.Code }, KumbuMetrics.ToolError.Refused);
        }
                catch (VaultDeletedException e)
        {
            return (ShellResult.Error(e.Message), KumbuMetrics.ToolError.Unavailable);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // A backstop: the stores mark damaged documents, so this should not happen.
            return (ShellResult.Error("stored data doesn't open (changed or damaged outside Kumbu²); the server log has details"), KumbuMetrics.ToolError.Damaged);
        }
    }
}
