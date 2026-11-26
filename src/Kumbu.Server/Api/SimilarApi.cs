// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary>A suggested link: the other note, the 1-based line its closest section starts on, that section's first line of
/// text, and its cosine with the note.</summary>
public sealed record SimilarNoteView(string Path, int Line, string Text, double Score);

/// <summary>The suggestions for a note. <c>state</c>: <c>ready</c>; <c>unindexed</c> (the note has no embeddings of its vault's
/// model yet); <c>preparing</c> (the model is loading); <c>off</c> (search by meaning is off on the server or for the vault).
/// <c>pending</c>: notes of the vault not indexed yet, so not compared.</summary>
public sealed record SimilarNotesView(string State, IReadOnlyList<SimilarNoteView> Notes, int Pending);

/// <summary>Add link: the note to write it in and the note it leads to.</summary>
public sealed record SimilarLinkRequest(string Path, string Target);

/// <summary>Not related: the note to keep it in and the other one; <c>undo</c> takes it back.</summary>
public sealed record NotRelatedRequest(string Path, string Other, bool Undo = false);

/// <summary>The note a suggestion's answer wrote, with its new version.</summary>
public sealed record SimilarWrittenView(string Path, string Version);

/// <summary>Link prediction for the web app (spec 2026-11-22-link-prediction): the notes of a note's vault close in meaning
/// that it neither links nor is linked from, through <see cref="SimilarSearch"/>, and the two answers to one, through
/// <see cref="SuggestionService"/> with the caller's rights.</summary>
public static class SimilarApi
{
    private const string Tool = "similar";

    /// <summary>At most this many suggestions: a short list asks for a judgement per row.</summary>
    internal const int Max = 5;

    /// <summary>The lowest cosine suggested, until a hold-out measurement on real vaults sets it (spec §3.5).</summary>
    internal const double Floor = 0.5;

    private const int MaxText = 200;

    public static void MapSimilarApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter);
        api.MapGet("/similar/{vault}/{**path}", SimilarAsync).Errors(400, 401, 404, 503);
        api.MapPost("/vaults/{vault}/similar/link", LinkAsync).Errors(400, 401, 403, 404, 409, 503, 507);
        api.MapPost("/vaults/{vault}/similar/not-related", NotRelatedAsync).Errors(400, 401, 403, 404, 409, 503, 507);
    }

    private static async Task<Ok<SimilarNotesView>> SimilarAsync(
        string vault, string? path, INoteStore store, ISearchIndex index, TimeProvider time, CancellationToken ct)
    {
        var notePath = ApiPaths.Parse(vault, path, PathKind.Note);
        var content = await store.ReadAsync(notePath, ct) ?? throw await NoteApi.MissingAsync(store, notePath, ct);

        var own = await index.VectorsAsync(notePath, ct);
        if (own.State != SemanticState.Ready) return TypedResults.Ok(Empty(own.State == SemanticState.Preparing ? "preparing" : "off", 0));
        // A vault whose provider isn't configured any more is left out with a reason: by meaning, it is off.
        if (own.Chunks.Count == 0) return TypedResults.Ok(Empty(own.Reason is null ? "unindexed" : "off", 0));

        // Not the archive, unless the note is in it (spec §3.5).
        var query = new SimilarQuery([new SimilarSource(notePath)], VaultPath.Root.Child(notePath.Vault!), Max, Unlinked: true,
            Archive: ArchiveFolder.Contains(notePath), LeftOut: NotRelated.LeftOutFor(new Note(notePath, content)));
        SimilarResult result;
        using (var call = new HeavyCall(ct, HeavyCall.TimeLimit, time))
        {
            try
            {
                result = await SimilarSearch.RunAsync(store, index, query, call.Token);
            }
            catch (OperationCanceledException) when (call.TimedOut)
            {
                throw ApiErrors.Busy();
            }
        }

        if (result.Error is not null) return TypedResults.Ok(Empty("unindexed", result.Pending));

        var notes = result.Hits.Where(h => h.Score >= Floor)
            .Select(h => new SimilarNoteView(h.Chunk.Path.Value, h.Chunk.StartLine + 1, FirstLine(h.Chunk.Text), Math.Round(h.Score, 4)))
            .ToList();
        return TypedResults.Ok(new SimilarNotesView("ready", notes, result.Pending));
    }

    private static async Task<Ok<SimilarWrittenView>> LinkAsync(string vault, SimilarLinkRequest body, INoteStore store, SuggestionService suggestions, CancellationToken ct)
    {
        var path = await CalendarApi.NoteInAsync(vault, body.Path, store, ct);
        return Written(await suggestions.AddLinkAsync(path.Value, body.Target, ct));
    }

    private static async Task<Ok<SimilarWrittenView>> NotRelatedAsync(string vault, NotRelatedRequest body, INoteStore store, SuggestionService suggestions, CancellationToken ct)
    {
        var path = await CalendarApi.NoteInAsync(vault, body.Path, store, ct);
        return Written(await suggestions.NotRelatedAsync(path.Value, body.Other, body.Undo, ct));
    }

    private static Ok<SimilarWrittenView> Written(Kumbu.Core.Sh.ShellResult result)
    {
        BoardApi.Check(result, Tool);
        if (result.Version is null) throw new InvalidOperationException("A suggestion's write answered without the note's version.");
        return TypedResults.Ok(new SimilarWrittenView(result.Version.Path, result.Version.Token));
    }

    private static SimilarNotesView Empty(string state, int pending) => new(state, [], pending);

    /// <summary>The section's first line of prose: not its heading, unless it holds nothing else; cut at <see cref="MaxText"/>.</summary>
    private static string FirstLine(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var line = lines.FirstOrDefault(l => !l.StartsWith('#')) ?? lines.FirstOrDefault()?.TrimStart('#').Trim() ?? "";
        return line.Length <= MaxText ? line : line[..MaxText].TrimEnd() + "…";
    }
}
