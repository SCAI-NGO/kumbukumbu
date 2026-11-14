// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tags;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary>A note or attachment as it is now, for Recent changes: <c>title</c> a note's name without <c>.md</c> (an attachment's
/// whole name), <c>folder</c> where it lies with its vault (<c>Work/Pump</c>), <c>kind</c> as in the tree, <c>flag</c> a note's
/// flag, <c>writer</c> and <c>writerKind</c> its last writer as Activity shows them.</summary>
public sealed record RecentItemView(
    string Path, string Vault, string Title, string Folder, string Kind, string? Flag, string Writer, string? WriterKind, DateTime WrittenAt);

/// <summary>The newest files first; <c>more</c> when there are further ones; <c>vaults</c> every vault the caller has (for the
/// chips), switched off or not.</summary>
public sealed record RecentView(IReadOnlyList<RecentItemView> Items, bool More, IReadOnlyList<string> Vaults);

/// <summary>Recent changes (spec §4): one row per note and attachment across the caller's vaults, by last write, read on demand
/// from the session's store. Not Activity: no history, no trash, nothing that is no more.</summary>
public static class RecentApi
{
    public const int DefaultRows = 50;

    public const int MaxRows = 500;

    public static void MapRecentApi(this IEndpointRouteBuilder app) =>
        // Sessions only: it names the writers, as Activity does.
        app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter)
            .MapGet("/recent", RecentAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 401, 403, 503);

    /// <summary>
    /// <c>words</c>: each must be in the path with its vault, without a note's <c>.md</c> (<c>Work/Pump/Certificate</c>), ignoring
    /// case. <c>flag</c>: <c>any</c> or a flag's name. <c>skip</c>: the vaults switched off, repeated. <c>limit</c>: 1 to 500,
    /// default 50. The vaults are those the session's store lists, so a hidden discreet vault is never read. Not rate-limited as
    /// Activity is: it reads the vaults' notes in memory, as What's on does, not their history.
    /// </summary>
    private static async Task<Ok<RecentView>> RecentAsync(
        string? words, string? flag, string[]? skip, int? limit, INoteStore store, WriterLabels labels, CancellationToken ct)
    {
        var max = limit ?? DefaultRows;
        if (max is < 1 or > MaxRows) throw ApiErrors.BadRequest($"limit is 1 to {MaxRows}.");
        if (flag is not (null or "any") && !Flags.IsFlag(flag)) throw ApiErrors.BadRequest($"A flag is any or one of {string.Join(", ", Flags.Names)}.");
        var terms = (words ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var off = new HashSet<string>(skip ?? [], StringComparer.Ordinal);

        var vaults = await store.ListVaultsAsync(ct);
        var files = new List<(VaultPath Path, string Kind, string? Flag, string Writer, WriterRef? Ref, DateTime At)>();
        foreach (var vault in vaults.Where(v => !off.Contains(v)))
        {
            var root = VaultPath.Root.Child(vault);
            foreach (var path in await store.ListNotesUnderAsync(root, ct))
            {
                if (ArchiveFolder.Contains(path) || !Matches(path, terms)) continue;
                // One gone in between is left out.
                if (await store.GetAsync(path, ct) is not { } note) continue;
                var noteFlag = Flags.Of(note.Content)?.Name;
                if (flag is not null && (noteFlag is null || (flag != "any" && noteFlag != flag))) continue;
                files.Add((path, "note", noteFlag, note.LastWriter, note.Ref, note.LastWriteAt));
            }

            // An attachment has no flag.
            if (flag is not null) continue;
            foreach (var file in await StoreAttachments.UnderAsync(store, root, ct))
            {
                if (ArchiveFolder.Contains(file.Path) || FolderMarker.Is(file.Path) || !Matches(file.Path, terms)) continue;
                files.Add((file.Path, AttachmentFormat.KindOf(file.Path.Value), null, file.LastWriter, file.Ref, file.LastWriteAt));
            }
        }

        var rows = files.OrderByDescending(f => f.At).ThenBy(f => f.Path.Value, TextOrder.Default).Take(max + 1).ToList();
        var items = new List<RecentItemView>();
        foreach (var f in rows.Take(max))
        {
            // Only the rows answered are rendered: the names are read once per vault.
            var vault = f.Path.Segments[0];
            var (writer, kind) = await labels.RenderAsync(vault, Writers.Identity(f.Writer), f.Ref, ct);
            var name = f.Path.Name;
            items.Add(new RecentItemView(
                f.Path.Value, vault, f.Kind == "note" && name.EndsWith(".md", StringComparison.Ordinal) ? name[..^3] : name,
                string.Join('/', f.Path.Segments.SkipLast(1)), f.Kind, f.Flag, writer, kind, f.At));
        }

        return TypedResults.Ok(new RecentView(items, rows.Count > max, vaults));
    }

    /// <summary>Every word in the path as the row shows it, ignoring case.</summary>
    private static bool Matches(VaultPath path, string[] terms)
    {
        if (terms.Length == 0) return true;
        var text = path.Value.TrimStart('/');
        if (text.EndsWith(".md", StringComparison.Ordinal)) text = text[..^3];
        return terms.All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    }
}
