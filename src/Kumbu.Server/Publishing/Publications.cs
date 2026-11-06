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
    public const string PdfsNotYet = "PDFs are not published yet";

    private const string ConflictSentence = "the vault's publications changed meanwhile; try again";

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
                Log = [.. record.Log, LogEntry(PublishingKinds.Published, stored, at, by, byRef, null)],
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
                Log = [.. record.Log, LogEntry(PublishingKinds.Updated, stored, at, by, byRef, null)],
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
                Log = [.. record.Log, LogEntry(PublishingKinds.Withdrawn, stored, Now, by, byRef, WithdrawalReasons.ByRight)],
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
                Log = [.. record.Log, .. logged],
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
                $"this key may not publish in {access.Name}; its owner can allow it (\"May publish\") under API keys or Connections in the web app");
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
                [.. fair.Files.Select((p, n) => new PublishedCopyFile(n, p.Name, attachments[p].Current.MediaType))],
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
