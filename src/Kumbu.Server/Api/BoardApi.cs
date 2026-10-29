// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Board;
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Server.Metrics;
using Microsoft.AspNetCore.Http.HttpResults;
using BoardModel = Kumbu.Core.Board.Board;

namespace Kumbu.Server.Api;

/// <summary><c>due</c>: <c>YYYY-MM-DD</c>; <c>overdue</c>: before today (UTC). <c>reason</c>: why the card is in its reason column.</summary>
public sealed record CardView(string Path, string Title, string? Due, bool Overdue, IReadOnlyList<string> Tags, string? Reason);

/// <summary><c>archivable</c>: cards leave it for the archive after a while; <c>needsReason</c>: a move into it takes a reason.</summary>
public sealed record ColumnView(string Name, bool Archivable, bool NeedsReason, IReadOnlyList<CardView> Cards);

/// <summary>A note in the board's folder the board can't place, with what is wrong.</summary>
public sealed record ProblemView(string Path, string What);

/// <summary>The board of a vault: <c>title</c> is the definition's, else the vault's name.</summary>
public sealed record BoardView(string Title, string? Instructions, IReadOnlyList<ColumnView> Columns, IReadOnlyList<ProblemView> Problems);

/// <summary><c>position</c>: top, bottom (the default), <c>before:&lt;card&gt;</c> or <c>after:&lt;card&gt;</c>.</summary>
public sealed record AddCardRequest(string Title, string Column, string? Text = null, string? Position = null, string? Due = null, string[]? Tags = null);

public sealed record MoveCardRequest(string Card, string Column, string? Position = null, string? Reason = null);

/// <summary>A field left out is kept; <c>clearDue</c> and <c>clearTags</c> remove, and not together with their field.</summary>
public sealed record UpdateCardRequest(string Card, string? Due = null, string[]? Tags = null, bool ClearDue = false, bool ClearTags = false);

public sealed record RenameColumnRequest(string From, string To);

/// <summary>The vault's board for the web app, mirroring the <c>board</c> tool: the same <see cref="BoardService"/>, the same rights.</summary>
public static class BoardApi
{
    public static void MapBoardApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/vaults/{vault}/board").AddEndpointFilter(ApiErrors.Filter);
        api.MapGet("", GetAsync).Errors(400, 401, 404, 409, 503);
        api.MapPost("", CreateAsync).Errors(400, 401, 403, 404, 409, 503);
        api.MapPost("cards", AddAsync).Errors(400, 401, 403, 404, 409, 503);
        api.MapPost("move", MoveAsync).Errors(400, 401, 403, 404, 409, 503);
        api.MapPost("update", UpdateAsync).Errors(400, 401, 403, 404, 409, 503);
        api.MapPost("rename-column", RenameColumnAsync).Errors(400, 401, 403, 404, 409, 503);
    }

    private static async Task<Ok<BoardView>> GetAsync(
        string vault, string? checkpoint, INoteStore store, Checkpoints checkpoints, TimeProvider time, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        INoteReader reader = store;
        if (checkpoint is not null) reader = await HistoryApi.OpenCheckpointAsync(root, checkpoint, store, checkpoints, ct);
        else await NoteApi.RequireVaultAsync(store, root, ct);
        return TypedResults.Ok(await ReadAsync(reader, root.Segments[0], time, ct));
    }

    private static async Task<Created<BoardView>> CreateAsync(
        string vault, INoteStore store, BoardService board, TimeProvider time, CancellationToken ct)
    {
        var root = await VaultAsync(vault, store, ct);
        Check(await board.CreateAsync(root, ct));
        return TypedResults.Created(Url(root), await ReadAsync(store, root, time, ct));
    }

    private static async Task<Created<BoardView>> AddAsync(
        string vault, AddCardRequest body, INoteStore store, BoardService board, TimeProvider time, CancellationToken ct)
    {
        var root = await VaultAsync(vault, store, ct);
        Check(await board.AddAsync(root, body.Title, body.Column, body.Text, body.Position, Day(body.Due), body.Tags, ct));
        return TypedResults.Created(Url(root), await ReadAsync(store, root, time, ct));
    }

    private static async Task<Ok<BoardView>> MoveAsync(
        string vault, MoveCardRequest body, INoteStore store, BoardService board, TimeProvider time, CancellationToken ct)
    {
        var root = await VaultAsync(vault, store, ct);
        Check(await board.MoveAsync(root, body.Card, body.Column, body.Position, body.Reason, ct));
        return TypedResults.Ok(await ReadAsync(store, root, time, ct));
    }

    private static async Task<Ok<BoardView>> UpdateAsync(
        string vault, UpdateCardRequest body, INoteStore store, BoardService board, TimeProvider time, CancellationToken ct)
    {
        var root = await VaultAsync(vault, store, ct);
        if (body.Due is not null && body.ClearDue) throw ApiErrors.BadRequest("Give due or clearDue, not both.");
        if (body.Tags is not null && body.ClearTags) throw ApiErrors.BadRequest("Give tags or clearTags, not both.");
        if (body.Due is null && body.Tags is null && !body.ClearDue && !body.ClearTags)
            throw ApiErrors.BadRequest("An update needs due (or clearDue) or tags (or clearTags).");

        // A field left out is "not given"; removal is asked for explicitly.
        var day = Day(body.Due);
        Optional<DateOnly?> due = day is { } d ? new Optional<DateOnly?>(d) : body.ClearDue ? new Optional<DateOnly?>(null) : default;
        Optional<IReadOnlyList<string>?> tags = body.Tags is not null ? new Optional<IReadOnlyList<string>?>(body.Tags) : body.ClearTags ? new Optional<IReadOnlyList<string>?>(null) : default;
        Check(await board.UpdateAsync(root, body.Card, due, tags, ct));
        return TypedResults.Ok(await ReadAsync(store, root, time, ct));
    }

    private static async Task<Ok<BoardView>> RenameColumnAsync(
        string vault, RenameColumnRequest body, INoteStore store, BoardService board, TimeProvider time, CancellationToken ct)
    {
        var root = await VaultAsync(vault, store, ct);
        Check(await board.RenameColumnAsync(root, body.From, body.To, ct));
        return TypedResults.Ok(await ReadAsync(store, root, time, ct));
    }

    /// <summary>The vault's name, once the vault is there for the caller.</summary>
    private static async Task<string> VaultAsync(string vault, INoteStore store, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        await NoteApi.RequireVaultAsync(store, root, ct);
        return root.Segments[0];
    }

    private static string Url(string vault) => $"/api/vaults/{Uri.EscapeDataString(vault)}/board";

    private static async Task<BoardView> ReadAsync(INoteReader reader, string vault, TimeProvider time, CancellationToken ct)
    {
        var (board, problem) = await BoardReader.ReadAsync(reader, vault, ct);
        if (board is null)
        {
            throw problem == "no board"
                ? ApiErrors.NotFound($"There is no board in {vault}.")
                : new ApiException(StatusCodes.Status409Conflict, problem!);
        }

        return View(board, vault, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
    }

    private static BoardView View(BoardModel board, string vault, DateOnly today) => new(
        board.Columns.Title ?? vault,
        board.Columns.Instructions,
        [.. board.Lanes.Select(lane => new ColumnView(
            lane.Column.Name, lane.Column.Archivable, lane.Column.NeedsReason,
            [.. lane.Cards.Select(c => new CardView(
                c.Path.Value, c.Title, c.Due is { } due ? due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                c.Due < today, c.Tags, c.Reason))]))],
        [.. board.Problems.Select(p => new ProblemView(p.Path.Value, p.What))]);

    private static DateOnly? Day(string? text)
    {
        if (text is null) return null;
        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return day;
        throw ApiErrors.BadRequest($"due '{text}' is not a date; write it as YYYY-MM-DD.");
    }

    /// <summary>A failed write becomes its status by the result's kind, with the sentence minus the tool's prefix.</summary>
    private static void Check(ShellResult result)
    {
        if (!result.IsError) return;
        var status = result.Kind switch
        {
            ShellErrorKind.Refused => StatusCodes.Status403Forbidden,
            ShellErrorKind.NotFound => StatusCodes.Status404NotFound,
            ShellErrorKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };
        if (status == StatusCodes.Status409Conflict) KumbuMetrics.WriteConflict(KumbuMetrics.WriteVia.Api);
        const string prefix = "board: ";
        throw new ApiException(status, result.Output.StartsWith(prefix, StringComparison.Ordinal) ? result.Output[prefix.Length..] : result.Output);
    }
}
