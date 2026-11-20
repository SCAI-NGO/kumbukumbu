// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Server.Attachments;
using Kumbu.Core.Graph;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;
using Kumbu.Core.Sh;
using Kumbu.Server.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Publishing;

/// <summary>A note a publication takes, in walk order, at the depth it was first reached (the start is 0).</summary>
public sealed record BundleNoteLine(VaultPath Path, string Title, int Depth);

/// <summary>A note the walk reached but leaves behind, and why (<see cref="BundleReasons"/>).</summary>
public sealed record BundleLeftOut(VaultPath Path, string Reason);

/// <summary>
/// What a publication would take (Publish 2.0 decision 2), as the publisher sees it before anything goes out: the notes,
/// the notes left behind, the embeds that become plain text (<see cref="PublishOutcome.PdfsNotYet"/> last when PDFs are
/// among them), the images' size, and whether it is past a limit.
/// </summary>
public sealed record BundlePreview(
    IReadOnlyList<BundleNoteLine> Notes, IReadOnlyList<BundleLeftOut> LeftOut, IReadOnlyList<string> Plain, long Bytes, bool TooMany, bool TooLarge)
{
    /// <summary>The notes' paths, sorted: what <c>expected</c> must name for the publication to go out.</summary>
    public IReadOnlyList<string> Set => [.. Notes.Select(n => n.Path.Value).Order(StringComparer.Ordinal)];
}

public static class BundleReasons
{
    /// <summary>Marked unpublishable, under a marked folder, or in a skill's place.</summary>
    public const string Unpublishable = "unpublishable";

    /// <summary>Reached only through a note that stays out.</summary>
    public const string Behind = "behind";

    /// <summary>Taken out by the publisher.</summary>
    public const string Without = "without";
}

/// <summary>The walk changed between what the publisher saw and the publication (decision 2): nothing went out.</summary>
public sealed class BundleChangedException(BundlePreview now) : Exception("the notes this publication takes changed meanwhile; check the list again")
{
    public BundlePreview Now { get; } = now;
}

internal sealed partial class Publications
{
    /// <summary>The deepest a publication goes (decision 1).</summary>
    internal const int MaxDepth = 5;

    /// <summary>The most notes one publication takes (decision 9).</summary>
    internal int NoteLimit { get; init; } = 500;

    /// <summary>What publishing <paramref name="note"/> to <paramref name="depth"/> without <paramref name="without"/>
    /// would take; checked as publishing is, and nothing is written.</summary>
    public async Task<BundlePreview> PreviewAsync(
        VaultAccess access, EncryptedNoteStore notes, VaultPath note, int depth, IReadOnlyList<VaultPath> without, CancellationToken ct)
    {
        Check(access, publishing: true);
        CheckDepth(depth);
        var stored = access.ToVault(note);
        RequireNotInSkillPlace(access.Vault, stored, note);
        var record = (await store.ReadAsync(null, access.Vault, ct)).Record;
        RequireNotMarked(record, stored, note);
        if (await notes.GetAsync(note, ct) is null) throw NoSuchNote(note);
        var walk = await WalkAsync(access, notes, record, note, depth, without, ct);
        return await PreviewOfAsync(access, notes, record, walk, ct);
    }

    internal static void CheckDepth(int depth)
    {
        if (depth is < 0 or > MaxDepth) throw new PublishRefusedException($"the depth goes from 0 to {MaxDepth}", ShellErrorKind.Invalid);
    }

    /// <summary>The notes of a walk to <paramref name="Depth"/> (caller's view), the start first, and the links between
    /// them by index.</summary>
    internal sealed record Walk(IReadOnlyList<BundleNoteLine> Notes, IReadOnlyList<BundleLeftOut> LeftOut, IReadOnlyList<int[]> Edges, int Depth);

    /// <summary>
    /// Breadth-first along outgoing links and embeds to <paramref name="depth"/>, entering only notes that no mark covers,
    /// outside every skill's place and not in <paramref name="without"/>: a refused note is not walked through
    /// (decision 3). One vault's graph, so nothing crosses a vault. The notes the same walk would reach without the
    /// predicate, and doesn't take, are left out with their reason.
    /// </summary>
    internal async Task<Walk> WalkAsync(
        VaultAccess access, EncryptedNoteStore notes, PublishingRecord record, VaultPath note, int depth, IReadOnlyList<VaultPath> without, CancellationToken ct)
    {
        var graph = await notes.GraphAsync(VaultPath.Root.Child(access.Name), ct);
        var excluded = without.Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
        bool Blocked(VaultPath p) => Covered(record.Unpublishable, access.ToVault(p).Value) || InSkillPlace(access.Vault, access.ToVault(p));
        bool Admit(VaultPath p) => !Blocked(p) && !excluded.Contains(p.Value);

        var taken = Bfs(graph, note, depth, Admit);
        var reached = Bfs(graph, note, depth, _ => true);
        var ids = taken.Select((t, i) => (t.Node, i)).ToDictionary(t => t.Node, t => t.i);
        var left = reached.Where(r => !ids.ContainsKey(r.Node)).Select(r => graph.Nodes[r.Node].Path!)
            .Select(p => new BundleLeftOut(p, Blocked(p) ? BundleReasons.Unpublishable : excluded.Contains(p.Value) ? BundleReasons.Without : BundleReasons.Behind))
            .OrderBy(l => l.Path.Value, StringComparer.Ordinal).ToList();
        var edges = new HashSet<(int, int)>();
        foreach (var (node, _) in taken)
        {
            foreach (var e in graph.OutEdges(node))
            {
                if (ids.TryGetValue(graph.Edges[e].To, out var to) && to != ids[node]) edges.Add((ids[node], to));
            }
        }

        return new Walk(
            [.. taken.Select(t => new BundleNoteLine(graph.Nodes[t.Node].Path!, graph.Nodes[t.Node].Title, t.Depth))],
            left,
            [.. edges.Order().Select(e => new[] { e.Item1, e.Item2 })],
            depth);
    }

    private static List<(int Node, int Depth)> Bfs(LinkGraph g, VaultPath note, int depth, Func<VaultPath, bool> admit)
    {
        if (g.IndexOf(note) is not { } start) return [];
        var order = new List<(int, int)> { (start, 0) };
        var seen = new HashSet<int> { start };
        var level = new List<int> { start };
        for (var d = 1; d <= depth && level.Count > 0; d++)
        {
            var next = new List<int>();
            foreach (var node in level)
            {
                foreach (var e in g.OutEdges(node))
                {
                    var to = g.Edges[e].To;
                    if (g.Nodes[to].Kind is not (NodeKind.Note or NodeKind.Day) || !admit(g.Nodes[to].Path!) || !seen.Add(to)) continue;
                    next.Add(to);
                    order.Add((to, d));
                }
            }

            level = next;
        }

        return order;
    }

    /// <summary>The preview of a walk: the fair copies made as for publishing, without reading any image.</summary>
    private async Task<BundlePreview> PreviewOfAsync(VaultAccess access, EncryptedNoteStore notes, PublishingRecord record, Walk walk, CancellationToken ct)
    {
        var (attachments, resolver) = await LinksAsync(access, notes, ct);
        var images = new HashSet<VaultPath>();
        var plain = new List<string>();
        foreach (var line in walk.Notes)
        {
            if (await notes.GetAsync(line.Path, ct) is not { } current) continue;
            var fair = Fair(access, current, record, attachments, resolver, _ => true, _ => null);
            images.UnionWith(fair.Files);
            plain.AddRange(fair.Plain.Where(p => !plain.Contains(p)));
        }

        var bytes = images.Sum(p => attachments[p].Current.Size);
        if (plain.Any(n => AttachmentFormat.ForName(n)?.Kind == AttachmentKind.Pdf)) plain.Add(PdfsNotYet);
        return new BundlePreview(walk.Notes, walk.LeftOut, plain, bytes, walk.Notes.Count > NoteLimit, walk.Notes.Count > 1 && bytes > CopyLimit);
    }

    private static async Task<(Dictionary<VaultPath, StoredAttachment> Attachments, LinkResolver Resolver)> LinksAsync(
        VaultAccess access, EncryptedNoteStore notes, CancellationToken ct)
    {
        var root = VaultPath.Root.Child(access.Name);
        var attachments = (await notes.ListAttachmentsUnderAsync(root, ct)).ToDictionary(a => access.ToVault(a.Path));
        var resolver = new LinkResolver(access.Vault.Name, (await notes.ListNotesUnderAsync(root, ct)).Select(access.ToVault), attachments.Keys);
        return (attachments, resolver);
    }

    /// <summary>A note's fair copy with the images <paramref name="usable"/> lets it take, its links to the notes
    /// <paramref name="noteId"/> names inside the copy (to a heading of its own too, when it names the note itself).</summary>
    private FairCopy Fair(
        VaultAccess access, StoredNote note, PublishingRecord record, Dictionary<VaultPath, StoredAttachment> attachments, LinkResolver resolver,
        Func<VaultPath, bool> usable, Func<VaultPath, int?> noteId, string? ownOrigin = null)
    {
        var source = access.ToVault(note.Path);
        VaultPath? Attachment(string target) => resolver.Resolve(source, target) is { Kind: LinkKind.Attachment, Target: { } t } ? t : null;
        int? NoteLink(string target) =>
            target.Length == 0 ? noteId(source) : resolver.Resolve(source, target) is { Kind: LinkKind.Note, Target: { } t } ? noteId(t) : null;
        bool MayCopy(VaultPath p) =>
            usable(p) && attachments.TryGetValue(p, out var a) && AttachmentFormat.ForPath(p)?.Kind == AttachmentKind.Image
            // An image larger than 15 MB is kept, not read (files spec, decision 12): left out as a file is, by its name.
            && !AttachmentFormat.IsLarge(a.Current.Size) && !Covered(record.Unpublishable, p.Value)
            && !InSkillPlace(access.Vault, p);
        return FairCopies.Make(source, note.Content, Attachment, MayCopy, ownOrigin, NoteLink);
    }

    /// <summary>The walk, refused past the note limit, and compared with what the publisher saw.</summary>
    private async Task<Walk> CheckedWalkAsync(
        VaultAccess access, EncryptedNoteStore notes, PublishingRecord record, VaultPath note, int depth, IReadOnlyList<VaultPath> without,
        IReadOnlyList<string>? expected, CancellationToken ct)
    {
        var walk = await WalkAsync(access, notes, record, note, depth, without, ct);
        if (expected is not null && !walk.Notes.Select(n => n.Path.Value).Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new BundleChangedException(await PreviewOfAsync(access, notes, record, walk, ct));
        }

        if (walk.Notes.Count > NoteLimit)
        {
            throw new PublishRefusedException($"{note} reaches {walk.Notes.Count} notes at this depth, more than {NoteLimit}; choose a smaller depth or leave notes out", ShellErrorKind.Invalid);
        }

        return walk;
    }

    private static IReadOnlyList<string>? StoredWithout(VaultAccess access, IReadOnlyList<VaultPath>? without) =>
        without is null || without.Count == 0 ? null : [.. without.Select(p => access.ToVault(p).Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>A copy as it is made: a single note's as before; a bundle's notes and the links between them, and its
    /// members for the record.</summary>
    private sealed record MadeAny(Made Made, IReadOnlyList<PublishedBundleNote>? Notes, IReadOnlyList<int[]>? Edges, IReadOnlyList<BundleMember>? Members)
    {
        public PublishedCopy Copy(DateTime at) => Notes is null ? Made.Copy(at) : Made.Copy(at) with { Notes = Notes, Edges = Edges };
    }

    /// <summary>
    /// Breadth-first from note 0 along <paramref name="edges"/> to <paramref name="depth"/>, entering only the notes
    /// <paramref name="admit"/> lets in: the notes and depths a fresh walk would give once some of a walk's notes are
    /// gone, in its order.
    /// </summary>
    private static List<(int Id, int Depth)> Rewalk(IReadOnlyList<int[]> edges, Func<int, bool> admit, int depth)
    {
        var order = new List<(int Id, int Depth)> { (0, 0) };
        var seen = new HashSet<int> { 0 };
        for (var at = 0; at < order.Count; at++)
        {
            var (from, d) = order[at];
            if (d == depth) continue;
            foreach (var e in edges)
            {
                if (e[0] == from && admit(e[1]) && seen.Add(e[1])) order.Add((e[1], d + 1));
            }
        }

        return order;
    }

    /// <summary>The note alone as before, or the bundle the walk took.</summary>
    private async Task<MadeAny> MakeAnyAsync(
        VaultAccess access, EncryptedNoteStore notes, StoredNote start, PublishingRecord record, Walk walk, string baseUrl, CancellationToken ct) =>
        walk.Notes.Count == 1 ? new MadeAny(await MakeAsync(access, notes, start, record, baseUrl, ct), null, null, null) : await MakeBundleAsync(access, notes, start, record, walk, baseUrl, ct);

    /// <summary>
    /// Every note of the walk as its fair copy, links to the others inside the copy (<see cref="FairCopies.NoteLinkPrefix"/>),
    /// the images numbered across the bundle, each read and stripped once. Past <see cref="CopyLimit"/> together the
    /// bundle is refused (decision 9); an image whose bytes are missing or don't strip is plain text, as for one note.
    /// Notes and images are numbered from 0 without gaps, so the reader can't tell that one is missing (decision 3).
    /// </summary>
    private async Task<MadeAny> MakeBundleAsync(
        VaultAccess access, EncryptedNoteStore notes, StoredNote start, PublishingRecord record, Walk walk, string baseUrl, CancellationToken ct)
    {
        var (attachments, resolver) = await LinksAsync(access, notes, ct);
        var ownOrigin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        var current = new List<StoredNote?> { start };
        foreach (var line in walk.Notes.Skip(1)) current.Add(await notes.GetAsync(line.Path, ct));
        // A note gone since the walk leaves the bundle with what it alone reached, as from a copy (Pruned); links to them
        // are plain text. The others are numbered in walk order: the copy's ids, not the walk's.
        var taken = current.All(c => c is not null)
            ? [.. walk.Notes.Select((n, i) => (i, n.Depth))]
            : Rewalk(walk.Edges, i => current[i] is not null, walk.Depth);
        var ids = taken.Select((t, id) => (t.Id, id)).ToDictionary(t => t.Id, t => t.id);
        var stored = taken.Select((t, id) => (Path: access.ToVault(walk.Notes[t.Id].Path), id)).ToDictionary(t => t.Path, t => t.id);
        int? NoteId(VaultPath p) => stored.TryGetValue(p, out var id) ? id : null;

        var unusable = new HashSet<VaultPath>();
        var stripped = new Dictionary<VaultPath, byte[]?>();
        var global = new Dictionary<VaultPath, int>();
        var files = new List<VaultPath>();
        var bytes = new List<byte[]>();
        var plain = new List<string>();
        var made = new List<(int Id, int Depth, FairCopy Fair, List<int> Files)>();
        var total = 0L;
        foreach (var (w, depth) in taken)
        {
            while (true)
            {
                var fair = Fair(access, current[w]!, record, attachments, resolver, p => !unusable.Contains(p), NoteId, ownOrigin);
                var local = new List<int>();
                var retry = false;
                foreach (var path in fair.Files)
                {
                    if (global.TryGetValue(path, out var n))
                    {
                        local.Add(n);
                        continue;
                    }

                    if (!stripped.TryGetValue(path, out var file))
                    {
                        var state = attachments[path].Current;
                        var raw = EncryptedNoteStore.TryParseBlobId(state.BlobId, out var blob) ? await catalog.ReadBlobAsync(access.Vault, blob, ct) : null;
                        stripped[path] = file = raw is null ? null : ImageMetadata.Strip(raw, state.MediaType, ct);
                    }

                    if (file is null)
                    {
                        unusable.Add(path);
                        retry = true;
                        break;
                    }

                    if (total + file.Length > CopyLimit)
                    {
                        throw new PublishRefusedException(
                            $"the images of these {walk.Notes.Count} notes are larger than {CopyLimit / (1024 * 1024)} MB together; choose a smaller depth or leave notes out",
                            ShellErrorKind.Invalid);
                    }

                    total += file.Length;
                    global[path] = files.Count;
                    local.Add(files.Count);
                    files.Add(path);
                    bytes.Add(file);
                }

                if (retry) continue;
                plain.AddRange(fair.Plain.Where(p => !plain.Contains(p)));
                made.Add((ids[w], depth, fair, local));
                break;
            }
        }

        if (plain.Any(n => AttachmentFormat.ForName(n)?.Kind == AttachmentKind.Pdf)) plain.Add(PdfsNotYet);
        var startFair = made[0].Fair;
        var result = new Made(
            startFair,
            [.. files.Select(p => new PublishedFile(p.Value, attachments[p].Version))],
            [.. files.Select((p, n) => new PublishedCopyFile(n, attachments[p].Current.MediaType))],
            bytes,
            plain);
        PublishedBundleNote[] bundle = [.. made.Select(m => new PublishedBundleNote(m.Id, m.Fair.Title, m.Fair.Markdown, m.Depth, m.Files))];
        BundleMember[] members = [.. taken.Skip(1).Select(t => new BundleMember(ids[t.Id], access.ToVault(walk.Notes[t.Id].Path).Value, current[t.Id]!.Version))];
        int[][] edges = [.. walk.Edges.Where(e => ids.ContainsKey(e[0]) && ids.ContainsKey(e[1])).Select(e => new[] { ids[e[0]], ids[e[1]] }).OrderBy(e => e[0]).ThenBy(e => e[1])];
        return new MadeAny(result, bundle, edges, members);
    }

    /// <summary>A copy with notes taken out: the files freed, the kept ones whose index changed, the notes' new ids by
    /// their old ones, and the freed files' places in the copy's (and the entry's) list of files.</summary>
    internal sealed record PrunedCopy(
        PublishedCopy Copy, IReadOnlyCollection<int> Freed, IReadOnlyList<(int From, int To)> Moved, IReadOnlyDictionary<int, int> Ids, IReadOnlySet<int> Positions);

    /// <summary>
    /// <paramref name="copy"/> without the notes <paramref name="dropped"/> and what a fresh walk to
    /// <paramref name="depth"/> over the stored edges no longer takes (decision 3): the notes only they reached, and the
    /// ones now further than <paramref name="depth"/>. Their links in the others become plain text, edges to them go,
    /// the files only they used are freed, and what stays is numbered again from 0, notes in the new walk's order and
    /// at its depths, files in their order: ids with gaps would tell the reader a note left. Null when nothing leaves.
    /// </summary>
    /// <remarks>The copy's files and the entry's <see cref="PublicationEntry.Files"/> stand in the same order, and are cut
    /// alike: <c>Positions</c> are the freed files' places in both.</remarks>
    internal static PrunedCopy? Pruned(PublishedCopy copy, IReadOnlySet<int> dropped, int depth)
    {
        if (copy.Notes is not { } notes || dropped.Count == 0) return null;
        var edges = copy.Edges ?? [];
        var byId = notes.ToDictionary(n => n.Id);
        var taken = Rewalk(edges, id => !dropped.Contains(id) && byId.ContainsKey(id), depth);
        if (taken.Count == notes.Count) return null;

        var ids = taken.Select((t, id) => (t.Id, id)).ToDictionary(t => t.Id, t => t.id);
        var used = taken.SelectMany(t => byId[t.Id].Files).ToHashSet();
        var files = copy.Files.Where(f => used.Contains(f.Index)).Select((f, n) => (From: f.Index, To: n, f.MediaType)).ToList();
        var fileIds = files.ToDictionary(f => f.From, f => f.To);
        string? Relinked(int id, string subpath) =>
            !ids.TryGetValue(id, out var to) ? "" : to == id ? null : FairCopies.NoteLinkPrefix + to + (subpath.Length > 0 ? "/" + subpath : "");
        PublishedBundleNote[] kept =
        [
            .. taken.Select(t => byId[t.Id] with
            {
                Id = ids[t.Id], Depth = t.Depth, Markdown = FairCopies.Relink(byId[t.Id].Markdown, Relinked), Files = [.. byId[t.Id].Files.Select(f => fileIds[f])],
            }),
        ];
        var pruned = copy with
        {
            Markdown = kept[0].Markdown,
            Files = [.. files.Select(f => new PublishedCopyFile(f.To, f.MediaType))],
            Notes = kept,
            Edges = [.. edges.Where(e => ids.ContainsKey(e[0]) && ids.ContainsKey(e[1])).Select(e => new[] { ids[e[0]], ids[e[1]] }).OrderBy(e => e[0]).ThenBy(e => e[1])],
        };
        return new PrunedCopy(
            pruned,
            [.. copy.Files.Select(f => f.Index).Where(i => !used.Contains(i))],
            [.. files.Where(f => f.From != f.To).Select(f => (f.From, f.To))],
            ids,
            copy.Files.Select((f, i) => (f, i)).Where(x => !used.Contains(x.f.Index)).Select(x => x.i).ToHashSet());
    }

    /// <summary>
    /// Takes the members <paramref name="leaving"/> (stored paths) out of <paramref name="entry"/>'s copy in the open
    /// transaction, with the notes reached only through them; the entry as it is afterwards and how many notes left.
    /// </summary>
    private async Task<(PublicationEntry Entry, int Left)> PruneAsync(
        IClientSessionHandle session, PublicationEntry entry, IReadOnlySet<string> leaving, CancellationToken ct)
    {
        var dropped = (entry.Members ?? []).Where(m => leaving.Contains(m.Note)).Select(m => m.Id).ToHashSet();
        PrunedCopy? pruned = null;
        await store.PruneCopyAsync(session, entry.Code, entry.Key, copy =>
        {
            if ((pruned = Pruned(copy, dropped, entry.Depth ?? 0)) is not { } p) return null;
            return (p.Copy, p.Freed, p.Moved);
        }, ct);
        // The record follows the copy, ids and all; a member the copy no longer holds goes from it either way.
        List<BundleMember> members = pruned is null
            ? [.. (entry.Members ?? []).Where(m => !dropped.Contains(m.Id))]
            : [.. (entry.Members ?? []).Where(m => pruned.Ids.ContainsKey(m.Id)).Select(m => m with { Id = pruned.Ids[m.Id] }).OrderBy(m => m.Id)];
        IReadOnlyList<PublishedFile> files = pruned is null ? entry.Files : [.. entry.Files.Where((_, i) => !pruned.Positions.Contains(i))];
        return (entry with { Members = members, Files = files }, (entry.Members?.Count ?? 0) - members.Count);
    }

    /// <summary><paramref name="record"/> with <paramref name="entry"/> pruned of <paramref name="leaving"/>, and the log
    /// entry that says so: an update of the bundle, with the reason and how many notes left.</summary>
    private async Task<(PublishingRecord Record, PublishingLogEntry Log)> PrunedRecordAsync(
        IClientSessionHandle session, PublishingRecord record, PublicationEntry entry, IReadOnlySet<string> leaving, string reason, DateTime at, string by,
        WriterRef byRef, CancellationToken ct)
    {
        var (after, left) = await PruneAsync(session, entry, leaving, ct);
        var log = LogEntry(PublishingKinds.Updated, Parse(entry.Note), at, by, byRef, reason) with { Notes = after.Members!.Count + 1, Added = 0, Removed = left };
        return (record with { Publications = [.. record.Publications.Select(p => p.Id == entry.Id ? after : p)], Log = Appended(record.Log, log) }, log);
    }
}
