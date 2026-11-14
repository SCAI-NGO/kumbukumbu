// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Calendar;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tags;
using Kumbu.Server.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary>
/// A note's flag, for its header: <c>flag</c> one of <c>red</c>, <c>green</c>, <c>blue</c>, <c>purple</c>, <c>yellow</c>,
/// <c>white</c>, <c>done</c>, <c>important</c>, or null; <c>inText</c> the tag as written (<c>#flag/red</c>) when the flag stands
/// in the note's text rather than its frontmatter, so it is changed there; <c>openTask</c> whether the note is an open to-do in the
/// calendar and no board card (ticking it may close it too); <c>canWrite</c> whether the caller may set the flag.
/// </summary>
public sealed record FlagView(string? Flag, string? InText, bool OpenTask, bool CanWrite);

/// <summary>A note's new flag, or null to take it off. <c>calendar</c>: <c>close</c> marks the open to-do done in the same write
/// (with the flag <c>done</c> only), <c>reopen</c> opens a done one again (Undo).</summary>
public sealed record SetFlagRequest(string Path, string? Flag, string? Calendar = null);

/// <summary>The note a flag's write changed, with its new version and its flag.</summary>
public sealed record FlagWrittenView(string Path, string Version, FlagView Note);

/// <summary>A note's flag for the web app (spec §2.2): read on demand through the session's store, set through
/// <see cref="FlagService"/> with the caller's rights.</summary>
public static class FlagApi
{
    private const string Tool = "flag";

    public static void MapFlagApi(this IEndpointRouteBuilder app)
    {
        var vault = app.MapGroup("/api/vaults/{vault}").AddEndpointFilter(ApiErrors.Filter);
        // Asked whenever a note's header shows, as the calendar's entry is: it doesn't count as the session's use.
        vault.MapGet("flag", FlagAsync).WithMetadata(BackgroundMetadata.Instance).Errors(400, 401, 404, 503);
        vault.MapPost("flag", SetAsync).Errors(400, 401, 403, 404, 409, 503, 507);
    }

    /// <summary>The flag of the note at <paramref name="path"/> (<c>/Work/x.md</c>, in <paramref name="vault"/>).</summary>
    private static async Task<Ok<FlagView>> FlagAsync(string vault, string path, INoteStore store, RequestCaller caller, CancellationToken ct)
    {
        var notePath = await CalendarApi.NoteInAsync(vault, path, store, ct);
        return TypedResults.Ok(await ViewAsync(notePath, store, caller, ct));
    }

    private static async Task<Ok<FlagWrittenView>> SetAsync(
        string vault, SetFlagRequest body, INoteStore store, FlagService flags, RequestCaller caller, CancellationToken ct)
    {
        var path = await CalendarApi.NoteInAsync(vault, body.Path, store, ct);
        var task = body.Calendar switch
        {
            null => FlagTask.None,
            "close" => FlagTask.Close,
            "reopen" => FlagTask.Reopen,
            _ => throw ApiErrors.BadRequest("A flag's calendar is close or reopen."),
        };
        var result = await flags.SetAsync(path.Value, body.Flag, task, ct);
        BoardApi.Check(result, Tool);
        var version = result.Version ?? throw new InvalidOperationException("A flag's write answered without the note's version.");
        return TypedResults.Ok(new FlagWrittenView(path.Value, version.Token, await ViewAsync(path, store, caller, ct)));
    }

    private static async Task<FlagView> ViewAsync(VaultPath path, INoteStore store, RequestCaller caller, CancellationToken ct)
    {
        var (entry, _, _) = await CalendarApi.ReadEntryAsync(path, store, ct);
        var note = await store.GetAsync(path, ct) ?? throw await NoteApi.MissingAsync(store, path, ct);
        var flag = Flags.Of(note.Content);
        return new FlagView(
            flag?.Name, flag is { InText: true } ? "#" + flag.Written : null,
            entry is { Kind: not EntryKind.Card, Task: TaskMark.Open },
            // Exactly the service's own check, so the menu is offered only where the write would pass.
            ((ICaller)caller).Refuse(Tool, path) is null);
    }
}
