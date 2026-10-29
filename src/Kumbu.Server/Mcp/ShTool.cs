// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using Kumbu.Core.Sh;
using Kumbu.Server.Metrics;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kumbu.Server.Mcp;

/// <summary>The shell: a thin adapter from a command line to <see cref="Shell"/>.</summary>
[McpServerToolType]
public static class ShTool
{
    // Not read-only since rm and restore; not destructive, because both can be undone.
    [McpServerTool(Name = "sh", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(Manual.ToolDescription)]
    public static async Task<CallToolResult> Sh(
        Shell shell,
        [Description("A command line, e.g. ls /  or  rg -i kumbu | head -5  or  cat \"/Work/Meeting Notes.md\"")] string command,
        CancellationToken ct) =>
        await ToolResult.RunAsync(KumbuMetrics.Tool.Sh, () => shell.RunAsync(command, ct));
}
