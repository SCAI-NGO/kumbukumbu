// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kumbu.Server.Mcp;

/// <summary>The two tools that carry content. Markdown goes in JSON parameters, never through the shell.</summary>
[McpServerToolType]
public static class WriteTools
{
    [McpServerTool(Name = "write", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(Manual.WriteDescription)]
    public static async Task<CallToolResult> Write(
        NoteWriter writer,
        [Description("Absolute path of the note, e.g. /Work/projects/plan.md")] string path,
        [Description("The note's full Markdown content")] string content,
        [Description("Version token from cat or the last write/edit; leave out to create a new note")] string? version = null,
        CancellationToken ct = default) =>
        ToolResult.From(await writer.WriteAsync(path, content, version, Writers.Dev, ct));

    [McpServerTool(Name = "edit", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(Manual.EditDescription)]
    public static async Task<CallToolResult> Edit(
        NoteWriter writer,
        [Description("Absolute path of the note, e.g. /Work/projects/plan.md")] string path,
        [Description("Version token from cat or the last write/edit")] string version,
        [Description("The replacement text, or the text to append")] string new_text,
        [Description("Text to replace; must occur exactly once. Leave out to append new_text at the end")] string? old_text = null,
        CancellationToken ct = default) =>
        ToolResult.From(await writer.EditAsync(path, version, old_text, new_text, Writers.Dev, ct));
}
