// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tags;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary>A tag in the spelling used most often, without '#', and how many notes carry it.</summary>
public sealed record TagCountView(string Tag, int Count);

/// <summary>A line where a tag stands, 1-based; <c>tag</c> as written there (a nested one for a parent asked for).</summary>
public sealed record TagLineView(string Path, int Line, string Text, string Tag);

/// <summary>Tags for the web app, read as <c>tags</c> in <c>sh</c> reads them: from the notes, nothing stored.</summary>
public static class TagApi
{
    public static void MapTagApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/tags").AddEndpointFilter(ApiErrors.Filter);
        api.MapGet("", ListAsync).Errors(400, 401, 404, 503);
        api.MapGet("notes", NotesAsync).Errors(400, 401, 404, 503);
    }

    /// <summary>The tags of the notes below <c>path</c> (a vault, folder or note; default every vault the caller
    /// reads), most notes first.</summary>
    private static async Task<Ok<TagCountView[]>> ListAsync(string? path, INoteStore store, CancellationToken ct)
    {
        var (_, notes) = await NoteApi.ReadScopeAsync(path, store, ct);
        return TypedResults.Ok(TagFilter.Count(notes).Select(t => new TagCountView(t.Tag, t.Count)).ToArray());
    }

    /// <summary>The lines below <c>path</c> where <c>tag</c> (with or without '#') or a tag nested below it stands, by
    /// path. 404 when no note there carries it.</summary>
    private static async Task<Ok<TagLineView[]>> NotesAsync(string? tag, string? path, INoteStore store, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tag)) throw ApiErrors.BadRequest("Give a tag (tag).");
        var (scope, notes) = await NoteApi.ReadScopeAsync(path, store, ct);
        var carrying = Narrow(notes, [tag], scope);
        var asked = TagFilter.Parse(tag)!;
        var lines = new List<TagLineView>();
        foreach (var note in carrying.OrderBy(n => n.Path.Value, TextOrder.Default))
        {
            var text = Lines.Split(note.Content);
            lines.AddRange(TagFilter.Uses(note, asked).DistinctBy(u => u.Line).Select(u => new TagLineView(note.Path.Value, u.Line + 1, text[u.Line], u.Tag)));
        }

        return TypedResults.Ok(lines.ToArray());
    }

    /// <summary>The notes that carry every tag asked for; 400 for one that can't be a tag, 404 for one no note carries.</summary>
    internal static IReadOnlyList<Note> Narrow(IReadOnlyList<Note> notes, IReadOnlyList<string> tags, VaultPath scope)
    {
        IReadOnlyList<Note> result = notes;
        foreach (var text in tags)
        {
            if (TagFilter.Parse(text) is not { } tag)
            {
                throw ApiErrors.BadRequest($"'{text}' can't be a tag: a tag is letters, digits, _, - and / without spaces, and not only digits.");
            }

            if (!notes.Any(n => TagFilter.Carries(n, tag)))
            {
                throw ApiErrors.NotFound(scope.IsRoot ? $"No note carries #{tag}." : $"No note below {scope.Value} carries #{tag}.");
            }

            result = [.. result.Where(n => TagFilter.Carries(n, tag))];
        }

        return result;
    }
}
