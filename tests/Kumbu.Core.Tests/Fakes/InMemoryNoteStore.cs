// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Fakes;

public sealed class InMemoryNoteStore : INoteStore, IAttachmentStore
{
    public static readonly DateTime Seeded = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<VaultPath, StoredNote> _notes = [];
    private readonly List<TrashedNote> _trash = [];
    private readonly List<MoveRecord> _moves = [];
    private readonly HashSet<string> _vaults = new(StringComparer.Ordinal);
    private readonly Dictionary<VaultPath, StoredAttachment> _attachments = [];
    private readonly List<TrashedAttachment> _attachmentTrash = [];
    private readonly Dictionary<(string Vault, string BlobId), byte[]> _blobs = [];

    public InMemoryNoteStore(params (string Path, string Content)[] notes)
    {
        foreach (var (path, content) in notes)
        {
            if (!VaultPath.TryParse(path, out var parsed)) throw new ArgumentException($"Bad path: {path}");
            _notes[parsed] = new StoredNote(parsed, content, 1, Writers.Seed, Seeded, [], content, Ref: WriterRef.Server);
            _vaults.Add(parsed.Vault!);
        }
    }

    /// <summary>Puts a note directly, as a store holds one it read: written before 0.3.0 (<see cref="WriterRef.Legacy"/>)
    /// or sealed without a reference (<see cref="WriterRef.Unknown"/>), which no write may store.</summary>
    public void Put(StoredNote note)
    {
        _notes[note.Path] = note;
        _vaults.Add(note.Path.Vault!);
    }

    /// <summary>Records a move directly, for tests that need a chosen writer and time.</summary>
    public void AddMove(MoveRecord move) => _moves.Add(move);

    public void AddVault(string name) => _vaults.Add(name);

    /// <summary>Puts an attachment at <paramref name="path"/> directly, written by the seed, with a PNG's or PDF's
    /// signature as its bytes.</summary>
    public StoredAttachment AddAttachment(string path)
    {
        if (!VaultPath.TryParse(path, out var parsed) || AttachmentFormat.ForPath(parsed) is not { } format) throw new ArgumentException($"Bad path: {path}");
        byte[] bytes = format.Kind == AttachmentKind.Pdf ? "%PDF-1.7"u8.ToArray() : [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var id = PutBlobAsync(parsed.Vault!, bytes, CancellationToken.None).Result;
        var attachment = new StoredAttachment(
            parsed, new AttachmentState(format.MediaType, bytes.Length, null, null, VersionToken.Of(bytes), id), 1, Writers.Seed, Seeded, [], null, WriterRef.Server);
        _attachments[parsed] = attachment;
        _vaults.Add(parsed.Vault!);
        return attachment;
    }

    /// <summary>Paths whose stored document doesn't open.</summary>
    public HashSet<VaultPath> Damaged { get; } = [];

    public int LockedVaults { get; set; }

    public Task<bool> IsDamagedAsync(VaultPath path, CancellationToken ct) => Task.FromResult(Damaged.Contains(path));

    public Task<int> CountDamagedAsync(VaultPath vaultOrRoot, CancellationToken ct) =>
        Task.FromResult(Damaged.Count(d => vaultOrRoot.IsRoot || d.Vault == vaultOrRoot.Vault));

    public Task<int> CountLockedVaultsAsync(CancellationToken ct) => Task.FromResult(LockedVaults);

    public Task<IReadOnlyList<string>> ListVaultsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([.. _vaults]);

    public Task<MoveRecord?> FindMoveAsync(VaultPath from, CancellationToken ct) =>
        Task.FromResult(_moves.Where(m => m.From == from).OrderByDescending(m => m.MovedAt).FirstOrDefault());

    public Task<IReadOnlyList<MoveRecord>> ListMovesAsync(VaultPath dir, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MoveRecord>>([.. _moves.Where(m => IsBelow(m.From, dir) || IsBelow(m.To, dir))]);

    /// <summary>Makes the next compare-and-swap fail once, as if another writer got there first.</summary>
    public Func<StoredNote, StoredNote>? Interfere { get; set; }

    /// <summary>Paths where every compare-and-swap fails, as if another writer always got there first.</summary>
    public Func<VaultPath, bool>? Contended { get; set; }

    public Task<string?> ReadVersionAsync(VaultPath path, string version, CancellationToken ct) =>
        Task.FromResult(_notes.TryGetValue(path, out var n) ? Versions.Find(n, version) : null);

    public Task<string?> ReadAsync(VaultPath path, CancellationToken ct) =>
        Task.FromResult(_notes.TryGetValue(path, out var note) ? note.Content : null);

    public Task<IReadOnlyList<VaultPath>> ListNotesUnderAsync(VaultPath dir, CancellationToken ct)
    {
        IReadOnlyList<VaultPath> result = [.. _notes.Keys.Where(p => IsBelow(p, dir))];
        return Task.FromResult(result);
    }

    // Reversed on purpose: callers must not rely on the store's order.
    public Task<IReadOnlyList<Note>> ReadNotesUnderAsync(VaultPath dir, CancellationToken ct)
    {
        IReadOnlyList<Note> result =
            [.. _notes.Where(n => IsBelow(n.Key, dir)).Select(n => new Note(n.Key, n.Value.Content)).Reverse()];
        return Task.FromResult(result);
    }

    public Task<StoredNote?> GetAsync(VaultPath path, CancellationToken ct) =>
        Task.FromResult(_notes.GetValueOrDefault(path));

    public Task<(Changeset Changeset, DateTime LastWrite)?> LatestChangesetAsync(string vault, string changesetKey, CancellationToken ct) =>
        Task.FromResult(Changeset.Latest(
            _notes.Values.Where(n => n.Path.Vault == vault), _trash.Where(t => t.Note.Path.Vault == vault), _moves.Where(m => m.From.Vault == vault), changesetKey,
            _attachments.Values.Where(a => a.Path.Vault == vault), _attachmentTrash.Where(t => t.Attachment.Path.Vault == vault)));

    public Task<bool> TryCreateAsync(StoredNote note, CancellationToken ct) =>
        Task.FromResult(RequireRef(note.Ref) && _vaults.Contains(note.Path.Vault!) && !Damaged.Contains(note.Path) && _notes.TryAdd(note.Path, note));

    public Task<bool> TryReplaceAsync(StoredNote current, StoredNote next, CancellationToken ct)
    {
        WriterRef.RequireStorable(next.Ref, kept: WriterRef.Keeps(current, next));
        Race(current.Path);
        if (!_notes.TryGetValue(current.Path, out var stored) || stored.Revision != current.Revision) return Task.FromResult(false);
        _notes[current.Path] = next;
        return Task.FromResult(true);
    }

    /// <summary>How many batches <see cref="TryCommitAsync"/> wrote.</summary>
    public int Commits { get; private set; }

    public Task<IReadOnlyList<VaultPath>> TryCommitAsync(NoteBatch batch, string by, WriterRef byRef, DateTime at, CancellationToken ct)
    {
        // As the real store: every entry the batch stores has a reference, or nothing is written.
        // A move or a carried state keeps the reference it was read with; anything the caller writes now needs its own.
        foreach (var (current, next) in batch.Replaces.Concat(batch.Moves)) WriterRef.RequireStorable(next.Ref, kept: WriterRef.Keeps(current, next));
        foreach (var note in batch.Creates) RequireRef(note.Ref);
        foreach (var (current, next) in batch.AttachmentReplaces.Concat(batch.AttachmentMoves)) WriterRef.RequireStorable(next.Ref, kept: WriterRef.Keeps(current, next));
        foreach (var attachment in batch.AttachmentCreates.Concat(batch.AttachmentRestores.Select(r => r.Restored))) RequireRef(attachment.Ref);

        if (batch.Trashes.Count > 0 || batch.Moves.Count > 0 || batch.AttachmentTrashes.Count > 0 || batch.AttachmentMoves.Count > 0) RequireRef(byRef);
        var existing = batch.Replaces.Select(r => r.Current).Concat(batch.Moves.Select(m => m.Current)).Concat(batch.Trashes).ToList();
        foreach (var current in existing) Race(current.Path);

        // Check everything first, then write everything: all or nothing.
        var conflicts = new List<VaultPath>();
        foreach (var current in existing)
        {
            if (!_notes.TryGetValue(current.Path, out var stored) || stored.Revision != current.Revision) conflicts.Add(current.Path);
        }

        var leaving = batch.Moves.Select(m => m.Current.Path).Concat(batch.Trashes.Select(t => t.Path)).ToHashSet();
        foreach (var target in batch.Moves.Select(m => m.Target).Concat(batch.Creates))
        {
            if ((_notes.ContainsKey(target.Path) && !leaving.Contains(target.Path)) || Damaged.Contains(target.Path)) conflicts.Add(target.Path);
        }

        foreach (var current in batch.AttachmentReplaces.Select(r => r.Current).Concat(batch.AttachmentMoves.Select(m => m.Current)).Concat(batch.AttachmentTrashes))
        {
            if (!_attachments.TryGetValue(current.Path, out var stored) || stored.Revision != current.Revision) conflicts.Add(current.Path);
        }

        var attachmentsLeaving = batch.AttachmentTrashes.Concat(batch.AttachmentMoves.Select(m => m.Current)).Select(t => t.Path).ToHashSet();
        var gone = batch.AttachmentRestores.Where(r => !_attachmentTrash.Any(t => t.IsSameEntry(r.Trashed))).Select(r => r.Restored).ToHashSet();
        foreach (var target in batch.AttachmentCreates.Concat(batch.AttachmentRestores.Select(r => r.Restored)).Concat(batch.AttachmentMoves.Select(m => m.Target)))
        {
            if ((_attachments.ContainsKey(target.Path) && !attachmentsLeaving.Contains(target.Path)) || Damaged.Contains(target.Path) || gone.Contains(target))
            {
                conflicts.Add(target.Path);
            }
        }

        if (conflicts.Count > 0) return Task.FromResult<IReadOnlyList<VaultPath>>(conflicts);

        Commits++;
        foreach (var (current, next) in batch.Replaces) _notes[current.Path] = next;
        foreach (var current in batch.Trashes)
        {
            _notes.Remove(current.Path);
            _trash.Add(new TrashedNote(current, by, at, batch.Changeset, byRef));
        }

        foreach (var (current, _) in batch.Moves) _notes.Remove(current.Path);
        foreach (var created in batch.Creates) _notes[created.Path] = created;
        foreach (var (current, target) in batch.Moves)
        {
            _notes[target.Path] = target;
            _moves.Add(new MoveRecord(current.Path, target.Path, by, at, batch.Changeset, byRef));
        }

        foreach (var (current, next) in batch.AttachmentReplaces) _attachments[current.Path] = next;
        foreach (var current in batch.AttachmentTrashes)
        {
            _attachments.Remove(current.Path);
            _attachmentTrash.Add(new TrashedAttachment(current, by, at, batch.Changeset, byRef));
        }

        foreach (var (current, _) in batch.AttachmentMoves) _attachments.Remove(current.Path);
        foreach (var created in batch.AttachmentCreates) _attachments[created.Path] = created;
        foreach (var (current, target) in batch.AttachmentMoves)
        {
            _attachments[target.Path] = target;
            _moves.Add(new MoveRecord(current.Path, target.Path, by, at, batch.Changeset, byRef));
        }

        foreach (var (trashed, restored) in batch.AttachmentRestores)
        {
            _attachmentTrash.RemoveAt(_attachmentTrash.FindIndex(t => t.IsSameEntry(trashed)));
            _attachments[restored.Path] = restored;
        }

        return Task.FromResult<IReadOnlyList<VaultPath>>([]);
    }

    private readonly List<(Checkpoint Checkpoint, IReadOnlyList<Note> Notes)> _checkpoints = [];

    public Task<IReadOnlyList<Checkpoint>> ListCheckpointsAsync(VaultPath vaultOrRoot, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Checkpoint>>([.. _checkpoints.Select(c => c.Checkpoint).Where(c => vaultOrRoot.IsRoot || c.Vault == vaultOrRoot.Vault)]);

    public Task<bool> TryAddCheckpointAsync(Checkpoint checkpoint, IReadOnlyList<Note> notes, CancellationToken ct)
    {
        RequireRef(checkpoint.Ref);
        if (_checkpoints.Any(c => c.Checkpoint.Vault == checkpoint.Vault && c.Checkpoint.Name == checkpoint.Name)) return Task.FromResult(false);
        _checkpoints.Add((checkpoint, notes));
        return Task.FromResult(true);
    }

    public Task<bool> TryRemoveCheckpointAsync(string vault, string name, CancellationToken ct) =>
        Task.FromResult(_checkpoints.RemoveAll(c => c.Checkpoint.Vault == vault && c.Checkpoint.Name == name) > 0);

    public Task<IReadOnlyList<Note>> ReadCheckpointAsync(Checkpoint checkpoint, CancellationToken ct) =>
        Task.FromResult(_checkpoints.Single(c => c.Checkpoint == checkpoint).Notes);

    public Task<bool> TryTrashAsync(StoredNote current, string deletedBy, WriterRef deletedByRef, DateTime deletedAt, CancellationToken ct)
    {
        RequireRef(deletedByRef);
        Race(current.Path);
        if (!_notes.TryGetValue(current.Path, out var stored) || stored.Revision != current.Revision) return Task.FromResult(false);
        _notes.Remove(current.Path);
        _trash.Add(new TrashedNote(stored, deletedBy, deletedAt, Ref: deletedByRef));
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<TrashedNote>> ListTrashAsync(VaultPath path, CancellationToken ct)
    {
        IReadOnlyList<TrashedNote> result =
            [.. _trash.Where(t => t.Note.Path == path || IsBelow(t.Note.Path, path)).OrderByDescending(t => t.DeletedAt)];
        return Task.FromResult(result);
    }

    public Task<StoredNote?> TryRestoreFromTrashAsync(VaultPath path, string? version, string writer, WriterRef reference, DateTime at, CancellationToken ct)
    {
        RequireRef(reference);
        var latest = _trash
            .Where(t => t.Note.Path == path && (version is null || t.Note.Version == version))
            .OrderByDescending(t => t.DeletedAt)
            .FirstOrDefault();
        if (latest is null || _notes.ContainsKey(path)) return Task.FromResult<StoredNote?>(null);

        var restored = latest.Note with { Revision = latest.Note.Revision + 1, LastWriter = writer, LastWriteAt = at, Changeset = null, Ref = reference };
        _trash.Remove(latest);
        _notes[path] = restored;
        return Task.FromResult<StoredNote?>(restored);
    }

    public Task<StoredAttachment?> GetAttachmentAsync(VaultPath path, CancellationToken ct) =>
        Task.FromResult(_attachments.GetValueOrDefault(path));

    public Task<IReadOnlyList<StoredAttachment>> ListAttachmentsUnderAsync(VaultPath folder, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StoredAttachment>>([.. _attachments.Values.Where(a => IsBelow(a.Path, folder))]);

    public Task<Stream?> OpenBlobAsync(string vault, string blobId, CancellationToken ct) =>
        Task.FromResult<Stream?>(_blobs.TryGetValue((vault, blobId), out var bytes) ? new MemoryStream(bytes, writable: false) : null);

    /// <summary>Blobs keyed by a plain SHA-256: fine for a fake, never for the real store.</summary>
    public Task<string> PutBlobAsync(string vault, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.Span));
        _blobs.TryAdd((vault, id), bytes.ToArray());
        return Task.FromResult(id);
    }

    public Task<IReadOnlyList<TrashedAttachment>> ListAttachmentTrashAsync(VaultPath? path, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TrashedAttachment>>(
        [
            .. _attachmentTrash
                .Where(t => path is not { } p || t.Attachment.Path == p || IsBelow(t.Attachment.Path, p))
                .OrderByDescending(t => t.DeletedAt),
        ]);

    private void Race(VaultPath path)
    {
        if (Contended?.Invoke(path) == true && _notes.TryGetValue(path, out var contended))
        {
            _notes[path] = contended with { Revision = contended.Revision + 1 };
            return;
        }

        if (Interfere is not { } interfere || !_notes.TryGetValue(path, out var stored)) return;
        Interfere = null;
        _notes[path] = interfere(stored) with { Revision = stored.Revision + 1 };
    }

    /// <summary>Fails closed as the real store does (<see cref="WriterRef.RequireStorable"/>), so a Core write path that
    /// forgets the writer reference fails here too.</summary>
    private static bool RequireRef(WriterRef? reference)
    {
        WriterRef.RequireStorable(reference);
        return true;
    }

    private static bool IsBelow(VaultPath path, VaultPath dir) =>
        path.Value.StartsWith(dir.IsRoot ? "/" : dir.Value + "/", StringComparison.Ordinal);
}
