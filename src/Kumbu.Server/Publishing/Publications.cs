// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using Kumbu.Core.Attachments;
using Kumbu.Core.Crypto;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;
using Kumbu.Core.Sh;
using Kumbu.Server.Attachments;
using Kumbu.Server.OAuth;
using Kumbu.Server.Sharing;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Kumbu.Server.Publishing;

/// <summary>A new publication's link, shown once. Not logged: <see cref="ToString"/> leaves the link out.</summary>
public sealed record PublishedNote(string Link, DateTime SnapshotAt, IReadOnlyList<string> Plain)
{
    public override string ToString() => $"PublishedNote {{ SnapshotAt = {SnapshotAt:O}, Plain = [{string.Join(", ", Plain)}] }}";
}

/// <summary>A published note as the caller may see it: for a member only <see cref="Note"/> (publish spec §3.10);
/// <see cref="Link"/> only when asked for, and only for the owner; <see cref="ToString"/> leaves it out.</summary>
public sealed record PublicationState(
    VaultPath Note, DateTime? SnapshotAt = null, bool? Older = null, DateTime? ChangedAt = null, string? PublishedBy = null, string? Link = null)
{
    public override string ToString() =>
        $"PublicationState {{ Note = {Note}, SnapshotAt = {SnapshotAt:O}, Older = {Older}, ChangedAt = {ChangedAt:O}, PublishedBy = {PublishedBy} }}";
}

/// <summary>
/// Publishing, updating and withdrawing one note (publish spec §3.1–3.5). Only the vault's owner, and only through a
/// caller that may publish there (<see cref="VaultAccess.MayPublish"/>); never what an unpublishable mark covers. The
/// copy is the note's fair copy (<see cref="FairCopies"/>) with the images it embeds, their metadata stripped, at most
/// <see cref="CopyLimit"/> bytes of them. Each change is one transaction under the vault's gate: the copy, its files,
/// the vault's record and the owner's vault event together. Paths in and out are in the caller's view; the record
/// holds them as the vault stores them. The code is never logged.
/// </summary>
internal sealed class Publications(VaultCatalog catalog, PublicationStore store, IMongoDatabase db, IOptions<KumbuOptions> options, TimeProvider time)
{
    public const string PdfsNotYet = PublishOutcome.PdfsNotYet;

    private const string ConflictSentence = "the vault's publications changed meanwhile; try again";

    /// <summary>The most entries the record's log keeps, the newest: the record is one document, and the full history
    /// stays in the vault's events (an event whose entry is gone reads as a publication that changed).</summary>
    internal const int LogLimit = 500;

    /// <summary>How long a kept removal of the same note by the same writer is folded into the last one recorded
    /// (<see cref="RecordKeptAsync"/>): the log keeps only its newest <see cref="LogLimit"/> entries, and a share that
    /// deletes a published note over and over mustn't push the owner's history out of it. An hour still tells her of a
    /// client that keeps at it, without one entry a minute.</summary>
    internal static readonly TimeSpan KeptFold = TimeSpan.FromHours(1);

    // A record that changes under each attempt is a vault being worked on hard; then the caller tries again.
    private const int Attempts = 3;

    /// <summary><c>Kumbu:Publish:Enabled</c>: off, nothing is published or updated; withdrawing still works.</summary>
    public bool Enabled => options.Value.Publish.Enabled;

    /// <summary>The most bytes of images one copy takes, after stripping; an image past it is plain text.</summary>
    internal long CopyLimit { get; init; } = 64L * 1024 * 1024;

    /// <summary>Where links point: <c>Kumbu:PublicUrl</c>, else the request's scheme and host.</summary>
    public static string BaseUrl(HttpContext http, KumbuOptions options) => OAuthUrls.Origin(http, options.PublicUrl);

    /// <summary>A mark covers its own path and, for a folder, everything below it; never a sibling that only starts alike.</summary>
    internal static bool Covered(IReadOnlyList<string> marks, string path) =>
        marks.Any(m => path.StartsWith(m, StringComparison.Ordinal) && (path.Length == m.Length || path[m.Length] == '/'));

    public async Task<PublishedNote> PublishAsync(
        VaultAccess access, EncryptedNoteStore notes, VaultPath note, string ownerId, string by, WriterRef byRef, string baseUrl, CancellationToken ct)
    {
        Check(access, publishing: true);
        var stored = access.ToVault(note);
        for (var attempt = 1; ; attempt++)
        {
            var current = await notes.GetAsync(note, ct) ?? throw NoSuchNote(note);
            var (doc, record) = await store.ReadAsync(null, access.Vault, ct);
            if (Find(record, stored) is not null)
            {
                throw new PublishRefusedException($"{note} is already published; publish update makes a new copy under the same link", ShellErrorKind.Conflict);
            }

            RequireNotMarked(record, stored, note);
            var made = await MakeAsync(access, notes, current, record, baseUrl, ct);
            var (code, key, at) = (ShareCode.New(), VaultCrypto.NewKey(), Now);
            var entry = new PublicationEntry(NewId(), stored.Value, code, key, at, current.Version, made.Files, by, Ref(byRef));
            var next = record with
            {
                Publications = [.. record.Publications, entry],
                Log = Appended(record.Log, LogEntry(PublishingKinds.Published, stored, at, by, byRef, null)),
            };
            var written = await WriteAsync(access, attempt, async session =>
            {
                await store.InsertCopyAsync(session, access.Id, code, key, ownerId, made.Copy(at), made.Bytes, ct);
                await EventAsync(session, access, next.Log[^1], ct);
                await store.WriteAsync(session, access.Vault, doc, next, ct);
            }, ct);
            if (written) return new PublishedNote(ShareCode.Link(baseUrl, code), at, made.Plain);
        }
    }

    /// <summary>A new copy under the same code, lookup and key (decision 15).</summary>
    public async Task<PublishedNote> UpdateAsync(
        VaultAccess access, EncryptedNoteStore notes, VaultPath note, string by, WriterRef byRef, string baseUrl, CancellationToken ct)
    {
        Check(access, publishing: true);
        var stored = access.ToVault(note);
        for (var attempt = 1; ; attempt++)
        {
            var current = await notes.GetAsync(note, ct) ?? throw NoSuchNote(note);
            var (doc, record) = await store.ReadAsync(null, access.Vault, ct);
            var entry = Find(record, stored) ?? throw NotPublished(note);
            RequireNotMarked(record, stored, note);
            var made = await MakeAsync(access, notes, current, record, baseUrl, ct);
            var at = Now;
            var updated = entry with { SnapshotAt = at, NoteVersion = current.Version, Files = made.Files, By = by, ByRef = Ref(byRef) };
            var next = record with
            {
                Publications = [.. record.Publications.Select(p => p.Id == entry.Id ? updated : p)],
                Log = Appended(record.Log, LogEntry(PublishingKinds.Updated, stored, at, by, byRef, null)),
            };
            var written = await WriteAsync(access, attempt, async session =>
            {
                await store.ReplaceCopyAsync(session, entry.Code, entry.Key, made.Copy(at), made.Bytes, ct);
                await EventAsync(session, access, next.Log[^1], ct);
                await store.WriteAsync(session, access.Vault, doc, next, ct);
            }, ct);
            if (written) return new PublishedNote(ShareCode.Link(baseUrl, entry.Code), at, made.Plain);
        }
    }

    /// <summary>The copy, its files and its entry go; the log keeps the withdrawal. Also while switched off: it only
    /// reduces what is out. By the record's entry, so also when the note is gone.</summary>
    public async Task WithdrawAsync(VaultAccess access, EncryptedNoteStore notes, VaultPath note, string by, WriterRef byRef, CancellationToken ct)
    {
        Check(access, publishing: false);
        var stored = access.ToVault(note);
        for (var attempt = 1; ; attempt++)
        {
            var (doc, record) = await store.ReadAsync(null, access.Vault, ct);
            var entry = Find(record, stored) ?? throw (await notes.GetAsync(note, ct) is null ? NoSuchNote(note) : NotPublished(note));
            var next = record with
            {
                Publications = [.. record.Publications.Where(p => p.Id != entry.Id)],
                Log = Appended(record.Log, LogEntry(PublishingKinds.Withdrawn, stored, Now, by, byRef, WithdrawalReasons.ByRight)),
            };
            var written = await WriteAsync(access, attempt, async session =>
            {
                await store.DeleteCopyAsync(session, entry.Code, ct);
                await EventAsync(session, access, next.Log[^1], ct);
                await store.WriteAsync(session, access.Vault, doc, next, ct);
            }, ct);
            if (written) return;
        }
    }

    /// <summary>
    /// A caller that may not withdraw the publication of <paramref name="note"/> tried to remove the note, which stays
    /// where it is (WebDAV design §3.6 case 6: a share's <c>DELETE</c>, held and not carried out): the attempt in the log,
    /// and the owner's vault event for it, in one transaction; the publication is as it was. Nothing if the note isn't
    /// published (any longer). No check of who may publish: it changes nothing that is out, and it only tells the owner.
    /// A share that tries again and again (a client retrying its sync) is recorded once per note per <see cref="KeptFold"/>.
    /// </summary>
    public async Task RecordKeptAsync(VaultAccess access, VaultPath note, string by, WriterRef byRef, CancellationToken ct)
    {
        var stored = access.ToVault(note);
        var reference = Ref(byRef);
        for (var attempt = 1; ; attempt++)
        {
            var (doc, record) = await store.ReadAsync(null, access.Vault, ct);
            if (Find(record, stored) is null) return;
            var now = Now;
            // The last attempt on this note by this writer, not the last of all: alternating between notes mustn't defeat
            // the fold.
            if (record.Log.LastOrDefault(l => l.Kind == PublishingKinds.Kept && l.Note == stored.Value && l.By == by && l.ByRef == reference) is { } last
                && now - last.At < KeptFold)
            {
                return;
            }

            var next = record with { Log = Appended(record.Log, LogEntry(PublishingKinds.Kept, stored, now, by, byRef, null)) };
            var written = await WriteAsync(access, attempt, async session =>
            {
                await EventAsync(session, access, next.Log[^1], ct);
                await store.WriteAsync(session, access.Vault, doc, next, ct);
            }, ct);
            if (written) return;
        }
    }

    /// <summary>The published notes at or below <paramref name="under"/>, in the caller's view: what a caller that may
    /// not withdraw them leaves where they are when it removes a folder (WebDAV design §3.6 case 6). A member learns no
    /// more of them than that they are published (publish spec §3.10).</summary>
    public async Task<IReadOnlyList<VaultPath>> PublishedUnderAsync(VaultAccess access, VaultPath under, CancellationToken ct)
    {
        var scope = access.ToVault(under).Value;
        var record = (await store.ReadAsync(null, access.Vault, ct)).Record;
        return [.. record.Publications.Where(p => Covered([scope], p.Note)).Select(p => access.ToCaller(Parse(p.Note)))];
    }

    /// <summary>The vault's unpublishable marks, in the caller's view; none for a member.</summary>
    public async Task<IReadOnlyList<VaultPath>> MarksAsync(VaultAccess access, CancellationToken ct) =>
        IsOwner(access) ? [.. (await store.ReadAsync(null, access.Vault, ct)).Record.Unpublishable.Select(m => access.ToCaller(Parse(m)))] : [];

    /// <summary>
    /// Sets or lifts the mark on <paramref name="path"/>: a note, a file, or a folder with something below it (publish
    /// spec §3.6). Setting one withdraws, in the same transaction, every publication whose note or any file it covers;
    /// lifting removes only that exact mark. Also while switched off: marks only reduce what can go out.
    /// </summary>
    public async Task SetUnpublishableAsync(
        VaultAccess access, EncryptedNoteStore notes, VaultPath path, bool marked, string by, WriterRef byRef, CancellationToken ct)
    {
        Check(access, publishing: false);
        var stored = access.ToVault(path);
        if (marked && !await ExistsAsync(notes, path, ct)) throw new PublishRefusedException($"{path}: no such note, file or folder", ShellErrorKind.NotFound);
        for (var attempt = 1; ; attempt++)
        {
            var (doc, record) = await store.ReadAsync(null, access.Vault, ct);
            var has = record.Unpublishable.Contains(stored.Value, StringComparer.Ordinal);
            if (has == marked) return;
            if (!marked)
            {
                var lifted = record with { Unpublishable = [.. record.Unpublishable.Where(m => !string.Equals(m, stored.Value, StringComparison.Ordinal))] };
                if (await WriteAsync(access, attempt, session => store.WriteAsync(session, access.Vault, doc, lifted, ct), ct)) return;
                continue;
            }

            string[] marks = [.. record.Unpublishable, stored.Value];
            var withdrawn = record.Publications.Where(p => Covered(marks, p.Note) || p.Files.Any(f => Covered(marks, f.Path))).ToList();
            var at = Now;
            var logged = withdrawn.Select(p => LogEntry(PublishingKinds.Withdrawn, Parse(p.Note), at, by, byRef, WithdrawalReasons.Unpublishable)).ToList();
            var next = record with
            {
                Publications = [.. record.Publications.Except(withdrawn)],
                Unpublishable = marks,
                Log = Appended(record.Log, [.. logged]),
            };
            var written = await WriteAsync(access, attempt, async session =>
            {
                foreach (var (entry, log) in withdrawn.Zip(logged))
                {
                    await store.DeleteCopyAsync(session, entry.Code, ct);
                    await EventAsync(session, access, log, ct);
                }

                await store.WriteAsync(session, access.Vault, doc, next, ct);
            }, ct);
            if (written) return;
        }
    }

    /// <summary>
    /// What a batch of the note store does to the vault's publications and marks (publish spec §3.6, §3.7), in the
    /// batch's own transaction, under the vault's gate; <paramref name="batch"/> in stored paths. Marks follow what moves:
    /// a note's or file's mark to its target, a folder's to the folder it was renamed to (<see cref="Followed"/>). Only
    /// the owner's session can lift a mark (<paramref name="bySession"/>); for anyone else, each note or file the batch
    /// moves out from under a mark takes a mark of its own along. A publication whose note goes to the trash is withdrawn (<c>removed</c>), one whose note or file lands
    /// under a mark too (<c>unpublishable</c>); otherwise it follows its note and files, under the same link. Edits
    /// never touch one, and only the owner's session, her key with "May publish" or her device may withdraw: anyone else
    /// gets <see cref="PublishedNoteException"/>, which aborts the batch.
    /// </summary>
    internal async Task FollowAsync(
        IClientSessionHandle session, VaultAccess access, bool device, bool bySession, NoteBatch batch, string by, WriterRef byRef, DateTime at, CancellationToken ct)
    {
        if (batch.Trashes.Count == 0 && batch.Moves.Count == 0 && batch.AttachmentMoves.Count == 0) return;
        var (doc, record) = await store.ReadAsync(session, access.Vault, ct);
        if (record.Publications.Count == 0 && record.Unpublishable.Count == 0) return;

        var moved = batch.Moves.Select(m => (From: m.Current.Path.Value, To: m.Target.Path.Value))
            .Concat(batch.AttachmentMoves.Select(m => (From: m.Current.Path.Value, To: m.Target.Path.Value)))
            .ToDictionary(m => m.From, m => m.To, StringComparer.Ordinal);
        var trashed = batch.Trashes.Select(t => t.Path.Value).ToHashSet(StringComparer.Ordinal);
        var paths = new Lazy<BatchPaths>(() => BatchPaths.Of(access.Vault, batch));
        IReadOnlyList<string> marks = [.. record.Unpublishable.SelectMany(m => Followed(m, moved, paths)).Distinct(StringComparer.Ordinal)];
        if (!(bySession && IsOwner(access)))
        {
            marks = [.. marks, .. moved.Where(m => Covered(record.Unpublishable, m.Key) && !Covered(marks, m.Value)).Select(m => m.Value)];
        }

        // A path is newly covered when it moved under a mark, or a mark that moved now covers it.
        bool Newly(string from, string to) => Covered(marks, to) && (from != to || !Covered(record.Unpublishable, to));

        var kept = new List<PublicationEntry>();
        var withdrawn = new List<(PublicationEntry Entry, string Reason)>();
        foreach (var entry in record.Publications)
        {
            var note = moved.GetValueOrDefault(entry.Note, entry.Note);
            var files = entry.Files.Select(f => f with { Path = moved.GetValueOrDefault(f.Path, f.Path) }).ToList();
            if (trashed.Contains(entry.Note)) withdrawn.Add((entry, WithdrawalReasons.Removed));
            else if (Newly(entry.Note, note) || entry.Files.Zip(files).Any(f => Newly(f.First.Path, f.Second.Path))) withdrawn.Add((entry, WithdrawalReasons.Unpublishable));
            else kept.Add(note == entry.Note && files.SequenceEqual(entry.Files) ? entry : entry with { Note = note, Files = files });
        }

        if (withdrawn.Count > 0 && !(IsOwner(access) && (access.MayPublish || device)))
        {
            var shown = access.ToCaller(Parse(withdrawn[0].Entry.Note));
            throw new PublishedNoteException(access.Membership is not null
                ? $"{shown} is published by the vault's owner; only the owner can remove it or move it into an unpublishable folder"
                : $"{shown} is published, and {(access.ViaShare ? "a WebDAV share never publishes" : $"this key may not publish in {access.Name}")}, so it can't withdraw it either; withdraw it first with a key that may publish (publish rm) or in the web app");
        }

        if (withdrawn.Count == 0 && marks.SequenceEqual(record.Unpublishable) && kept.SequenceEqual(record.Publications)) return;

        // Each withdrawal is logged under the path its note had before the batch.
        var logged = withdrawn.Select(w => LogEntry(PublishingKinds.Withdrawn, Parse(w.Entry.Note), at, by, byRef, w.Reason)).ToList();
        foreach (var ((entry, _), log) in withdrawn.Zip(logged))
        {
            await store.DeleteCopyAsync(session, entry.Code, ct);
            await EventAsync(session, access, log, ct);
        }

        await store.WriteAsync(session, access.Vault, doc, record with { Publications = kept, Unpublishable = marks, Log = Appended(record.Log, [.. logged]) }, ct);
    }

    /// <summary>
    /// A mark after the batch. A note's or file's goes to its target. A folder's M goes to N only for a real rename:
    /// everything the batch moves from under <c>M/</c> lands under <c>N/</c> with the same rest, and after the batch
    /// nothing else is under <c>N/</c>. While something live stays under <c>M/</c>, M stays alone; while only the trash
    /// holds something there, both stay marked, so that what comes back from the trash is covered too. Folder markers
    /// count as files. The vault's paths are read only for a folder mark with something moved from under it.
    /// </summary>
    private static IEnumerable<string> Followed(string mark, IReadOnlyDictionary<string, string> moved, Lazy<BatchPaths> paths)
    {
        if (moved.TryGetValue(mark, out var target)) return [target];
        var prefix = mark + "/";
        string? folder = null;
        var landed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (from, to) in moved)
        {
            if (!from.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = from[mark.Length..];
            if (!to.EndsWith(rest, StringComparison.Ordinal) || to.Length == rest.Length) return [mark];
            var into = to[..^rest.Length];
            if (folder is not null && folder != into) return [mark];
            folder = into;
            landed.Add(to);
        }

        if (folder is null) return [mark];
        var (after, trash) = paths.Value;
        var under = folder + "/";
        if (after.Any(p => p.StartsWith(prefix, StringComparison.Ordinal) || (p.StartsWith(under, StringComparison.Ordinal) && !landed.Contains(p)))) return [mark];
        return trash.Any(p => p.StartsWith(prefix, StringComparison.Ordinal)) ? [mark, folder] : [folder];
    }

    /// <summary>The vault's notes and files as the batch leaves them, and the paths its trash holds then, from the
    /// working set as it is before the batch applies.</summary>
    private sealed record BatchPaths(HashSet<string> After, HashSet<string> Trash)
    {
        public static BatchPaths Of(UnlockedVault vault, NoteBatch batch)
        {
            var after = vault.Notes.Keys.Concat(vault.Attachments.Keys).Select(p => p.Value).ToHashSet(StringComparer.Ordinal);
            var trashed = batch.Trashes.Select(n => n.Path.Value).Concat(batch.AttachmentTrashes.Select(a => a.Path.Value)).ToList();
            after.ExceptWith(trashed.Concat(batch.Moves.Select(m => m.Current.Path.Value)).Concat(batch.AttachmentMoves.Select(m => m.Current.Path.Value)));
            after.UnionWith(batch.Creates.Select(n => n.Path.Value).Concat(batch.AttachmentCreates.Select(a => a.Path.Value))
                .Concat(batch.AttachmentRestores.Select(r => r.Restored.Path.Value))
                .Concat(batch.Moves.Select(m => m.Target.Path.Value)).Concat(batch.AttachmentMoves.Select(m => m.Target.Path.Value)));
            var trash = vault.Trash.Select(t => t.Trashed.Note.Path.Value).Concat(vault.AttachmentTrash.Select(t => t.Trashed.Attachment.Path.Value))
                .Concat(trashed).ToHashSet(StringComparer.Ordinal);
            return new BatchPaths(after, trash);
        }
    }

    /// <summary>A note, a file, or a folder with notes or files below it, as <c>cd</c> finds one.</summary>
    private static async Task<bool> ExistsAsync(EncryptedNoteStore notes, VaultPath path, CancellationToken ct) =>
        await notes.GetAsync(path, ct) is not null
        || await notes.GetAttachmentAsync(path, ct) is not null
        || (await notes.ListNotesUnderAsync(path, ct)).Count > 0
        || (await notes.ListAttachmentsUnderAsync(path, ct)).Count > 0;

    /// <summary>The log entry's event, in the same transaction as the record (publish decision 8): its id alone.</summary>
    private Task EventAsync(IClientSessionHandle session, VaultAccess access, PublishingLogEntry log, CancellationToken ct) =>
        VaultEvents.RecordAsync(db, session, access.Id, EventKind(log), null, log.At, null, ct, entry: log.Id);

    private static string EventKind(PublishingLogEntry log) => log.Kind switch
    {
        PublishingKinds.Published => VaultEvents.NotePublished,
        PublishingKinds.Updated => VaultEvents.PublicationUpdated,
        PublishingKinds.Kept => VaultEvents.PublishedNoteKept,
        _ => VaultEvents.PublicationWithdrawn,
    };

    /// <summary>The publications at or below <paramref name="under"/>. With <paramref name="linkBase"/> the owner's
    /// states carry the link (only her session's single-note read gives one).</summary>
    public async Task<IReadOnlyList<PublicationState>> ListAsync(
        VaultAccess access, EncryptedNoteStore notes, VaultPath under, string? linkBase, CancellationToken ct)
    {
        // The root holds the whole vault; the record is the vault's alone.
        var scope = under.IsRoot ? null : access.ToVault(under).Value;
        var record = (await store.ReadAsync(null, access.Vault, ct)).Record;
        var states = new List<PublicationState>();
        foreach (var entry in record.Publications.Where(p => scope is null || Covered([scope], p.Note)).OrderBy(p => p.Note, StringComparer.Ordinal))
        {
            states.Add(await StateOfAsync(access, notes, entry, linkBase, ct));
        }

        return states;
    }

    public async Task<PublicationState?> StateAsync(VaultAccess access, EncryptedNoteStore notes, VaultPath note, string? linkBase, CancellationToken ct) =>
        Find((await store.ReadAsync(null, access.Vault, ct)).Record, access.ToVault(note)) is { } entry ? await StateOfAsync(access, notes, entry, linkBase, ct) : null;

    /// <summary>A member learns that the note is published, nothing more (publish spec §3.10).</summary>
    private static async Task<PublicationState> StateOfAsync(VaultAccess access, EncryptedNoteStore notes, PublicationEntry entry, string? linkBase, CancellationToken ct)
    {
        var shown = access.ToCaller(Parse(entry.Note));
        if (!IsOwner(access)) return new PublicationState(shown);

        var older = false;
        var changed = new List<DateTime>();
        var current = await notes.GetAsync(shown, ct);
        if (current?.Version != entry.NoteVersion)
        {
            older = true;
            if (current is not null) changed.Add(current.LastWriteAt);
        }

        foreach (var file in entry.Files)
        {
            var path = access.ToCaller(Parse(file.Path));
            if (await notes.GetAttachmentAsync(path, ct) is { } attachment)
            {
                if (attachment.Version == file.Version) continue;
                changed.Add(attachment.LastWriteAt);
            }
            else if ((await notes.ListAttachmentTrashAsync(path, ct)).FirstOrDefault(t => t.Attachment.Path == path) is { } trashed)
            {
                changed.Add(trashed.DeletedAt);
            }

            older = true;
        }

        return new PublicationState(
            shown, entry.SnapshotAt, older, older && changed.Count > 0 ? changed.Max() : null, entry.By,
            linkBase is null ? null : ShareCode.Link(linkBase, entry.Code));
    }

    /// <summary>The checks every change makes first, in this order; the switch only for publishing and updating.</summary>
    private void Check(VaultAccess access, bool publishing)
    {
        if (publishing && !Enabled) throw Refused(Refusals.PublishingOff, "publishing is switched off on this server");
        if (!IsOwner(access)) throw Refused(Refusals.NotOwner, $"only the owner of {access.Name} can publish its notes");
        if (!access.MayPublish)
        {
            throw Refused(
                Refusals.NoPublishRight,
                access.ViaShare
                    ? $"a WebDAV share never publishes, in {access.Name} or any other vault"
                    : $"this key may not publish in {access.Name}; its owner can allow it (\"May publish\") under Access → API keys or Apps in the web app");
        }
    }

    private static bool IsOwner(VaultAccess access) => access.IsOwner && access.Membership is null;

    private static void RequireNotMarked(PublishingRecord record, VaultPath stored, VaultPath note)
    {
        if (Covered(record.Unpublishable, stored.Value))
        {
            throw Refused(Refusals.Unpublishable, $"{note} is marked unpublishable; only the vault's owner can lift the mark, in the web app");
        }
    }

    private sealed record Made(FairCopy Fair, IReadOnlyList<PublishedFile> Files, IReadOnlyList<PublishedCopyFile> CopyFiles, IReadOnlyList<byte[]> Bytes, IReadOnlyList<string> Plain)
    {
        public PublishedCopy Copy(DateTime at) => new(Fair.Title, Fair.Markdown, at, CopyFiles);
    }

    /// <summary>
    /// The fair copy with the images it may take: present, not covered by a mark, and whose bytes
    /// <see cref="ImageMetadata.Strip"/> takes, as long as they fit in <see cref="CopyLimit"/> in the order they appear.
    /// One it doesn't becomes plain text: the copy is made again without it, each file read and stripped once.
    /// </summary>
    private async Task<Made> MakeAsync(VaultAccess access, EncryptedNoteStore notes, StoredNote note, PublishingRecord record, string baseUrl, CancellationToken ct)
    {
        var root = VaultPath.Root.Child(access.Name);
        var attachments = (await notes.ListAttachmentsUnderAsync(root, ct)).ToDictionary(a => access.ToVault(a.Path));
        var resolver = new LinkResolver(access.Vault.Name, (await notes.ListNotesUnderAsync(root, ct)).Select(access.ToVault), attachments.Keys);
        var source = access.ToVault(note.Path);
        var ownOrigin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        var unusable = new HashSet<VaultPath>();
        var stripped = new Dictionary<VaultPath, byte[]?>();

        VaultPath? Attachment(string target) => resolver.Resolve(source, target) is { Kind: LinkKind.Attachment, Target: { } t } ? t : null;

        bool MayCopy(VaultPath p) =>
            !unusable.Contains(p) && attachments.ContainsKey(p) && AttachmentFormat.ForPath(p)?.Kind == AttachmentKind.Image && !Covered(record.Unpublishable, p.Value);

        while (true)
        {
            var fair = FairCopies.Make(source, note.Content, Attachment, MayCopy, ownOrigin);
            var bytes = new List<byte[]>();
            var total = 0L;
            foreach (var path in fair.Files)
            {
                if (!stripped.TryGetValue(path, out var file))
                {
                    var state = attachments[path].Current;
                    var raw = EncryptedNoteStore.TryParseBlobId(state.BlobId, out var id) ? await catalog.ReadBlobAsync(access.Vault, id, ct) : null;
                    stripped[path] = file = raw is null ? null : ImageMetadata.Strip(raw, state.MediaType);
                }

                if (file is not null && total + file.Length <= CopyLimit)
                {
                    bytes.Add(file);
                    total += file.Length;
                }
                else unusable.Add(path);
            }

            if (bytes.Count < fair.Files.Count) continue;
            var plain = fair.Plain.Any(n => AttachmentFormat.ForName(n)?.Kind == AttachmentKind.Pdf) ? [.. fair.Plain, PdfsNotYet] : fair.Plain;
            return new Made(
                fair,
                [.. fair.Files.Select(p => new PublishedFile(p.Value, attachments[p].Version))],
                [.. fair.Files.Select((p, n) => new PublishedCopyFile(n, attachments[p].Current.MediaType))],
                bytes,
                plain);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction under the vault's gate, as every write of the vault does. False
    /// when the record changed meanwhile, so the caller reads it again; on the last attempt that is a Conflict.
    /// </summary>
    private async Task<bool> WriteAsync(VaultAccess access, int attempt, Func<IClientSessionHandle, Task> work, CancellationToken ct)
    {
        var vault = access.Vault;
        catalog.CheckWritable(vault, access);
        await vault.Gate.WaitAsync(ct);
        try
        {
            catalog.CheckWritable(vault, access);
            using var session = await catalog.StartSessionAsync(ct);
            session.StartTransaction();
            try
            {
                await work(session);
            }
            catch (Exception e) when (e is PublishingConflictException || (e is MongoException m && m.HasErrorLabel("TransientTransactionError")))
            {
                await AbortAsync(session);
                if (attempt >= Attempts) throw new PublishRefusedException(ConflictSentence, ShellErrorKind.Conflict);
                return false;
            }
            catch
            {
                await AbortAsync(session);
                throw;
            }

            await session.CommitTransactionAsync(CancellationToken.None);
            return true;
        }
        finally
        {
            vault.Gate.Release();
        }
    }

    private static async Task AbortAsync(IClientSessionHandle session)
    {
        try
        {
            if (session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None);
        }
        catch (MongoException)
        {
            // The server already aborted it.
        }
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private static PublicationEntry? Find(PublishingRecord record, VaultPath stored) =>
        record.Publications.FirstOrDefault(p => string.Equals(p.Note, stored.Value, StringComparison.Ordinal));

    /// <summary>The log with <paramref name="entries"/> after it, cut to its newest <see cref="LogLimit"/>.</summary>
    private static IReadOnlyList<PublishingLogEntry> Appended(IReadOnlyList<PublishingLogEntry> log, params PublishingLogEntry[] entries)
    {
        PublishingLogEntry[] all = [.. log, .. entries];
        return all.Length > LogLimit ? all[^LogLimit..] : all;
    }

    private static PublishingLogEntry LogEntry(string kind, VaultPath stored, DateTime at, string by, WriterRef byRef, string? reason) =>
        new(NewId(), kind, stored.Value, at, by, Ref(byRef), reason);

    private static WriterRefPayload Ref(WriterRef byRef)
    {
        WriterRef.RequireStorable(byRef);
        return WriterRefPayload.From(byRef) ?? throw new InvalidOperationException("A publication without a writer reference");
    }

    private static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static VaultPath Parse(string path) =>
        VaultPath.TryParse(path, out var parsed) ? parsed : throw new InvalidDataException("A publishing record holds a path that doesn't parse");

    private static PublishRefusedException Refused(string code, string sentence) => new(sentence, ShellErrorKind.Refused, code);

    private static PublishRefusedException NoSuchNote(VaultPath note) => new($"{note}: no such note", ShellErrorKind.NotFound);

    private static PublishRefusedException NotPublished(VaultPath note) => new($"{note} is not published", ShellErrorKind.NotFound);
}
