// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kumbu.Server.Mcp;

/// <summary>The tools that carry content. Markdown goes in JSON parameters, never through the shell.</summary>
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
        await ToolResult.RunAsync(() => writer.WriteAsync(path, content, version, Writers.Dev, ct));

    [McpServerTool(Name = "edit", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(Manual.EditDescription)]
    public static async Task<CallToolResult> Edit(
        NoteWriter writer,
        [Description("Absolute path of the note, e.g. /Work/projects/plan.md, or of one section: /Work/projects/plan.md#Goals")] string path,
        [Description("Version token from cat or the last write/edit")] string version,
        [Description("The replacement text, or the text to append")] string new_text,
        [Description("Text to replace; must occur exactly once (in the section, if the path has one). Leave out to append new_text")] string? old_text = null,
        [Description("Replace the whole section in the path, heading included, with new_text")] bool replace_section = false,
        [Description("Replace every occurrence of old_text, not just a unique one")] bool replace_all = false,
        CancellationToken ct = default) =>
        await ToolResult.RunAsync(() => writer.EditAsync(path, version, old_text, new_text, Writers.Dev, ct, replace_section, replace_all));

    [McpServerTool(Name = "replace", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(Manual.ReplaceDescription)]
    public static async Task<CallToolResult> Replace(
        Replacer replacer,
        [Description("A vault, folder or note to replace in, e.g. /Work or /Work/projects")] string path,
        [Description("The text to find, literally and case-sensitive")] string old_text,
        [Description("The text to put in its place")] string new_text,
        [Description("Match old_text only as a whole word")] bool word = false,
        [Description("The plan token from the preview; leave out to preview")] string? plan = null,
        CancellationToken ct = default) =>
        await ToolResult.RunAsync(() => replacer.RunAsync(path, old_text, new_text, word, plan, Writers.Dev, ct));
}
