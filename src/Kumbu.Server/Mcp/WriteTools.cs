// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Server.Metrics;
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
        [Description("Optional: one line on why, kept with this version in the note's history (at most 200 characters)")] string? message = null,
        CancellationToken ct = default) =>
        await ToolResult.RunAsync(KumbuMetrics.Tool.Write, () => writer.WriteAsync(path, content, version, ct, message));

    [McpServerTool(Name = "edit", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(Manual.EditDescription)]
    public static async Task<CallToolResult> Edit(
        NoteWriter writer,
        [Description("Absolute path of the note, e.g. /Work/projects/plan.md, or of one section: /Work/projects/plan.md#Goals")] string path,
        [Description("Version token from cat or the last write/edit")] string version,
        [Description("The replacement text, or the text to append")] string? new_text = null,
        [Description("Text to replace; must occur exactly once (in the section, if the path has one). Leave out to append new_text")] string? old_text = null,
        [Description("Replace the whole section in the path, heading included, with new_text")] bool replace_section = false,
        [Description("Replace every occurrence of old_text, not just a unique one")] bool replace_all = false,
        [Description("Optional: one line on why, kept with this version in the note's history (at most 200 characters)")] string? message = null,
        [Description("Several replacements on the same version, applied in order, all or none: a list of {old_text, new_text}; each old_text must occur exactly once (in the section, if the path has one) when its turn comes. Instead of old_text and new_text")] TextEdit[]? edits = null,
        CancellationToken ct = default) =>
        await ToolResult.RunAsync(KumbuMetrics.Tool.Edit, () => EditAsync(writer, path, version, new_text, old_text, replace_section, replace_all, message, edits, ct));

    private static Task<ShellResult> EditAsync(
        NoteWriter writer, string path, string version, string? new_text, string? old_text, bool replace_section, bool replace_all, string? message, TextEdit[]? edits,
        CancellationToken ct)
    {
        if (edits is null)
        {
            return new_text is null
                ? Task.FromResult(ShellResult.Error("edit: new_text or edits is needed"))
                : writer.EditAsync(path, version, old_text, new_text, ct, replace_section, replace_all, message);
        }

        var refused = new_text is not null || old_text is not null ? "edit: give new_text or edits, not both"
            : replace_section || replace_all ? "edit: edits takes no replace_section or replace_all"
            : null;
        return refused is not null ? Task.FromResult(ShellResult.Error(refused)) : writer.EditManyAsync(path, version, edits, ct, message);
    }

    [McpServerTool(Name = "replace", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(Manual.ReplaceDescription)]
    public static async Task<CallToolResult> Replace(
        Replacer replacer,
        [Description("A vault, folder or note to replace in, e.g. /Work or /Work/projects")] string path,
        [Description("The text to find, literally and case-sensitive")] string old_text,
        [Description("The text to put in its place")] string new_text,
        [Description("Match old_text only as a whole word")] bool word = false,
        [Description("The plan token from the preview; leave out to preview")] string? plan = null,
        [Description("Optional: one line on why, kept with this version in the note's history (at most 200 characters)")] string? message = null,
        CancellationToken ct = default) =>
        await ToolResult.RunAsync(KumbuMetrics.Tool.Replace, () => replacer.RunAsync(path, old_text, new_text, word, plan, ct, message));
}
