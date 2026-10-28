// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Kumbu.Core.Sh;
using Kumbu.Server.Metrics;
using Kumbu.Server.Vaults;
using ModelContextProtocol.Protocol;

namespace Kumbu.Server.Mcp;

internal static class ToolResult
{
    /// <summary>
    /// Errors are tool results (isError: true), not JSON-RPC errors: a wrong path is a normal answer.
    /// A version token travels as its own content block, so it never mixes with the note's text.
    /// </summary>
    public static CallToolResult From(ShellResult result)
    {
        List<ContentBlock> content = [new TextContentBlock { Text = result.Output }];
        if (result.Version is { } version)
        {
            // Starts on its own line: some clients join the blocks without a separator.
            content.Add(new TextContentBlock { Text = $"\nversion of {version.Path}: {version.Token}" });
        }

        return new CallToolResult { IsError = result.IsError, Content = content };
    }

    /// <summary>Runs a tool body; vaults that can't be used at all become a tool error with the reason. Counts and times the call.</summary>
    public static async Task<CallToolResult> RunAsync(KumbuMetrics.Tool tool, Func<Task<ShellResult>> body)
    {
        var started = Stopwatch.GetTimestamp();
        var (result, kind) = await RunBodyAsync(body);
        KumbuMetrics.ToolCalled(tool, Stopwatch.GetElapsedTime(started));
        if (result.IsError)
        {
            KumbuMetrics.ToolFailed(tool, kind);
            if (kind == KumbuMetrics.ToolError.Conflict) KumbuMetrics.WriteConflict(KumbuMetrics.WriteVia.Mcp);
        }

        return From(result);
    }

    private static async Task<(ShellResult Result, KumbuMetrics.ToolError Kind)> RunBodyAsync(Func<Task<ShellResult>> body)
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
                _ => KumbuMetrics.ToolError.Other,
            });
        }
        catch (VaultUnavailableException e)
        {
            return (ShellResult.Error(e.Message), KumbuMetrics.ToolError.Unavailable);
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
