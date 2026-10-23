// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;
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

    /// <summary>Runs a tool body; vaults that can't be used at all become a tool error with the reason.</summary>
    public static async Task<CallToolResult> RunAsync(Func<Task<ShellResult>> body)
    {
        try
        {
            return From(await body());
        }
        catch (VaultUnavailableException e)
        {
            return From(ShellResult.Error(e.Message));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // A backstop: the stores mark damaged documents, so this should not happen.
            return From(ShellResult.Error("stored data doesn't open (changed or damaged outside Kumbu²); the server log has details"));
        }
    }
}
