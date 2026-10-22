// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;
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
            content.Add(new TextContentBlock { Text = $"version of {version.Path}: {version.Token}" });
        }

        return new CallToolResult { IsError = result.IsError, Content = content };
    }
}
