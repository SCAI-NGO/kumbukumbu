// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using System.Globalization;
using Kumbu.Core.Board;
using Kumbu.Core.Sh;
using Kumbu.Server.Metrics;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Kumbu.Server.Mcp;

/// <summary>The board's write actions for agents: a thin adapter over <see cref="BoardService"/>.</summary>
[McpServerToolType]
public static class BoardTool
{
    private const string Actions = "add, move, update, create or rename_column";

    [McpServerTool(Name = "board", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false), Description(Manual.BoardDescription)]
    public static async Task<CallToolResult> Board(
        BoardService board,
        [Description("The vault with the board, e.g. Work")] string vault,
        [Description("add, move, update, create (the board's Board.md) or rename_column")] string action,
        [Description("add: the new card's title, which becomes the note's name")] string? title = null,
        [Description("move, update: the card's title (or its path)")] string? card = null,
        [Description("add, move: the column the card goes to")] string? column = null,
        [Description("add: the card's text, Markdown")] string? text = null,
        [Description("add, move: top, bottom (the default), before:<card> or after:<card>")] string? position = null,
        [Description("add, update: the due date, YYYY-MM-DD")] string? due = null,
        [Description("add, update: the tags, replacing the card's; leave out to keep them")] string[]? tags = null,
        [Description("update: remove the due date (leave due out)")] bool clear_due = false,
        [Description("update: remove all tags (leave tags out)")] bool clear_tags = false,
        [Description("move: why, kept on the card while it stays in that column; required for a column that asks for a reason, not written for others")] string? reason = null,
        [Description("rename_column: the column's current name")] string? from = null,
        [Description("rename_column: the new name")] string? to = null,
        CancellationToken ct = default) =>
        await ToolResult.RunAsync(KumbuMetrics.Tool.Board, async () =>
        {
            switch (action.Trim())
            {
                case "add":
                    if (Missing("add", ("title", title), ("column", column)) is { } noAdd) return noAdd;
                    if (!TryDay(due, out var addDue, out var addError)) return addError;
                    return await board.AddAsync(vault, title!, column!, text, position, addDue, tags, ct);
                case "move":
                    if (Missing("move", ("card", card), ("column", column)) is { } noMove) return noMove;
                    return await board.MoveAsync(vault, card!, column!, position, reason, ct);
                case "update":
                    if (Missing("update", ("card", card)) is { } noUpdate) return noUpdate;
                    return await UpdateAsync(board, vault, card!, due, tags, clear_due, clear_tags, ct);
                case "create":
                    return await board.CreateAsync(vault, ct);
                case "rename_column":
                    if (Missing("rename_column", ("from", from), ("to", to)) is { } noRename) return noRename;
                    return await board.RenameColumnAsync(vault, from!, to!, ct);
                default:
                    return ShellResult.Error($"board: unknown action '{action}'; use {Actions}");
            }
        });

    private static async Task<ShellResult> UpdateAsync(
        BoardService board, string vault, string card, string? due, string[]? tags, bool clearDue, bool clearTags, CancellationToken ct)
    {
        if (due is not null && clearDue) return ShellResult.Error("board: give due or clear_due, not both");
        if (tags is not null && clearTags) return ShellResult.Error("board: give tags or clear_tags, not both");
        if (due is null && tags is null && !clearDue && !clearTags)
            return ShellResult.Error("board: update needs at least one of due, tags, clear_due, clear_tags; to remove the due date or the tags, set clear_due or clear_tags");
        if (!TryDay(due, out var day, out var error)) return error;

        // A parameter left out is "not given"; removal is asked for explicitly, as the tool binds an absent and a null alike.
        Optional<DateOnly?> newDue = day is { } d ? new Optional<DateOnly?>(d) : clearDue ? new Optional<DateOnly?>(null) : default;
        Optional<IReadOnlyList<string>?> newTags = tags is not null ? new Optional<IReadOnlyList<string>?>(tags) : clearTags ? new Optional<IReadOnlyList<string>?>(null) : default;
        return await board.UpdateAsync(vault, card, newDue, newTags, ct);
    }

    private static ShellResult? Missing(string action, params (string Name, string? Value)[] required) =>
        required.FirstOrDefault(r => string.IsNullOrWhiteSpace(r.Value)) is { Name: not null } missing
            ? ShellResult.Error($"board: {missing.Name} is required for {action}")
            : null;

    private static bool TryDay(string? text, out DateOnly? day, out ShellResult error)
    {
        day = null;
        error = ShellResult.Ok("");
        if (text is null) return true;
        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            day = parsed;
            return true;
        }

        error = ShellResult.Error($"board: due '{text}' is not a date; write it as YYYY-MM-DD");
        return false;
    }
}
