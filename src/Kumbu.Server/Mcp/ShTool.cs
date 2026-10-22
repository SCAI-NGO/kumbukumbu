// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using Kumbu.Core.Sh;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kumbu.Server.Mcp;

/// <summary>The single MCP tool: a thin adapter from a command line to <see cref="Shell"/>.</summary>
[McpServerToolType]
public static class ShTool
{
    [McpServerTool(Name = "sh", ReadOnly = true, Idempotent = true, OpenWorld = false), Description(Manual.ToolDescription)]
    public static async Task<CallToolResult> Sh(
        Shell shell,
        [Description("A command line, e.g. ls /  or  rg -i kumbu | head -5  or  cat \"/Work/Meeting Notes.md\"")] string command,
        CancellationToken ct)
    {
        // Errors are tool results (isError: true), not JSON-RPC errors: a wrong path is a normal answer.
        var result = await shell.RunAsync(command, ct);
        return new CallToolResult
        {
            IsError = result.IsError,
            Content = [new TextContentBlock { Text = result.Output }],
        };
    }
}
