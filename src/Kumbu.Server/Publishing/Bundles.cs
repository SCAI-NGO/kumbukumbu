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

    /// <summary>The notes of a walk (caller's view), the start first, and the links between them by index.</summary>
    internal sealed record Walk(IReadOnlyList<BundleNoteLine> Notes, IReadOnlyList<BundleLeftOut> LeftOut, IReadOnlyList<int[]> Edges);

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
            [.. edges.Order().Select(e => new[] { e.Item1, e.Item2 })]);
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
    /// <paramref name="noteId"/> names inside the copy.</summary>
    private FairCopy Fair(
        VaultAccess access, StoredNote note, PublishingRecord record, Dictionary<VaultPath, StoredAttachment> attachments, LinkResolver resolver,
        Func<VaultPath, bool> usable, Func<VaultPath, int?> noteId, string? ownOrigin = null)
    {
        var source = access.ToVault(note.Path);
        VaultPath? Attachment(string target) => resolver.Resolve(source, target) is { Kind: LinkKind.Attachment, Target: { } t } ? t : null;
        int? NoteLink(string target) => resolver.Resolve(source, target) is { Kind: LinkKind.Note, Target: { } t } ? noteId(t) : null;
        bool MayCopy(VaultPath p) =>
            usable(p) && attachments.ContainsKey(p) && AttachmentFormat.ForPath(p)?.Kind == AttachmentKind.Image && !Covered(record.Unpublishable, p.Value)
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

    /// <summary>A copy as it is made: a single note's as before; a bundle's notes, and its members for the record.</summary>
    private sealed record MadeAny(Made Made, IReadOnlyList<PublishedBundleNote>? Notes, IReadOnlyList<BundleMember>? Members)
    {
        public PublishedCopy Copy(DateTime at, Walk walk) => Notes is null ? Made.Copy(at) : Made.Copy(at) with { Notes = Notes, Edges = walk.Edges };
    }

    /// <summary>The note alone as before, or the bundle the walk took.</summary>
    private async Task<MadeAny> MakeAnyAsync(
        VaultAccess access, EncryptedNoteStore notes, StoredNote start, PublishingRecord record, Walk walk, string baseUrl, CancellationToken ct) =>
        walk.Notes.Count == 1 ? new MadeAny(await MakeAsync(access, notes, start, record, baseUrl, ct), null, null) : await MakeBundleAsync(access, notes, start, record, walk, baseUrl, ct);

    /// <summary>
    /// Every note of the walk as its fair copy, links to the others inside the copy (<see cref="FairCopies.NoteLinkPrefix"/>),
    /// the images numbered across the bundle, each read and stripped once. Past <see cref="CopyLimit"/> together the
    /// bundle is refused (decision 9); an image whose bytes are missing or don't strip is plain text, as for one note.
    /// </summary>
    private async Task<MadeAny> MakeBundleAsync(
        VaultAccess access, EncryptedNoteStore notes, StoredNote start, PublishingRecord record, Walk walk, string baseUrl, CancellationToken ct)
    {
        var (attachments, resolver) = await LinksAsync(access, notes, ct);
        var ownOrigin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        var ids = walk.Notes.Select((n, i) => (Stored: access.ToVault(n.Path), i)).ToDictionary(n => n.Stored, n => n.i);
        var current = new List<StoredNote?> { start };
        foreach (var line in walk.Notes.Skip(1)) current.Add(await notes.GetAsync(line.Path, ct));
        // A note gone since the walk leaves the bundle; links to it are plain text.
        int? NoteId(VaultPath p) => ids.TryGetValue(p, out var id) && current[id] is not null ? id : null;

        var unusable = new HashSet<VaultPath>();
        var stripped = new Dictionary<VaultPath, byte[]?>();
        var global = new Dictionary<VaultPath, int>();
        var files = new List<VaultPath>();
        var bytes = new List<byte[]>();
        var plain = new List<string>();
        var made = new List<(int Id, FairCopy Fair, List<int> Files)>();
        var total = 0L;
        for (var id = 0; id < current.Count; id++)
        {
            if (current[id] is not { } stored) continue;
            while (true)
            {
                var fair = Fair(access, stored, record, attachments, resolver, p => !unusable.Contains(p), NoteId, ownOrigin);
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
                        stripped[path] = file = raw is null ? null : ImageMetadata.Strip(raw, state.MediaType);
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
                made.Add((id, fair, local));
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
        PublishedBundleNote[] bundle = [.. made.Select(m => new PublishedBundleNote(m.Id, m.Fair.Title, m.Fair.Markdown, walk.Notes[m.Id].Depth, m.Files))];
        BundleMember[] members = [.. made.Skip(1).Select(m => new BundleMember(m.Id, access.ToVault(walk.Notes[m.Id].Path).Value, current[m.Id]!.Version))];
        return new MadeAny(result, bundle, members);
    }

    /// <summary>
    /// <paramref name="copy"/> without the notes <paramref name="dropped"/> and the ones the stored edges reach only through
    /// them (decision 3): their links in the others become plain text, edges to them go, and the files only they used
    /// are freed. Null when nothing leaves.
    /// </summary>
    /// <remarks>The copy's files and the entry's <see cref="PublicationEntry.Files"/> stand in the same order, and are cut
    /// alike: <c>Positions</c> are the freed files' places in both.</remarks>
    internal static (PublishedCopy Copy, IReadOnlyCollection<int> Freed, IReadOnlySet<int> Positions, IReadOnlySet<int> Gone)? Pruned(PublishedCopy copy, IReadOnlySet<int> dropped)
    {
        if (copy.Notes is not { } notes || dropped.Count == 0) return null;
        var edges = copy.Edges ?? [];
        var reached = new HashSet<int> { 0 };
        var queue = new Queue<int>([0]);
        while (queue.TryDequeue(out var from))
        {
            foreach (var e in edges)
            {
                if (e[0] == from && !dropped.Contains(e[1]) && reached.Add(e[1])) queue.Enqueue(e[1]);
            }
        }

        var gone = notes.Select(n => n.Id).Where(id => !reached.Contains(id)).ToHashSet();
        if (gone.Count == 0) return null;
        PublishedBundleNote[] kept = [.. notes.Where(n => !gone.Contains(n.Id)).Select(n => n with { Markdown = FairCopies.Unlink(n.Markdown, id => !gone.Contains(id)) })];
        var used = kept.SelectMany(n => n.Files).ToHashSet();
        var freed = copy.Files.Select(f => f.Index).Where(i => !used.Contains(i)).ToList();
        var positions = copy.Files.Select((f, i) => (f, i)).Where(x => !used.Contains(x.f.Index)).Select(x => x.i).ToHashSet();
        var start = kept[0];
        var pruned = copy with
        {
            Markdown = start.Markdown,
            Files = [.. copy.Files.Where(f => used.Contains(f.Index))],
            Notes = kept,
            Edges = [.. edges.Where(e => !gone.Contains(e[0]) && !gone.Contains(e[1]))],
        };
        return (pruned, freed, positions, gone);
    }

    /// <summary>
    /// Takes the members <paramref name="leaving"/> (stored paths) out of <paramref name="entry"/>'s copy in the open
    /// transaction, with the notes reached only through them; the entry as it is afterwards and how many notes left.
    /// </summary>
    private async Task<(PublicationEntry Entry, int Left)> PruneAsync(
        IClientSessionHandle session, PublicationEntry entry, IReadOnlySet<string> leaving, CancellationToken ct)
    {
        var dropped = (entry.Members ?? []).Where(m => leaving.Contains(m.Note)).Select(m => m.Id).ToHashSet();
        IReadOnlySet<int> gone = new HashSet<int>();
        IReadOnlySet<int> positions = new HashSet<int>();
        await store.PruneCopyAsync(session, entry.Code, entry.Key, copy =>
        {
            if (Pruned(copy, dropped) is not { } p) return null;
            (gone, positions) = (p.Gone, p.Positions);
            return (p.Copy, p.Freed);
        }, ct);
        // The record follows the copy; a member the copy no longer holds goes from it either way.
        var members = (entry.Members ?? []).Where(m => !gone.Contains(m.Id) && !dropped.Contains(m.Id)).ToList();
        IReadOnlyList<PublishedFile> files = positions.Count == 0 ? entry.Files : [.. entry.Files.Where((_, i) => !positions.Contains(i))];
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
