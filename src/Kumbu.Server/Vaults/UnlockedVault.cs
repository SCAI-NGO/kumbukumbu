// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Immutable;
using System.Text.Json;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using MongoDB.Bson;

namespace Kumbu.Server.Vaults;

/// <summary>
/// An unlocked vault: its keys and its decrypted working set (concept §7). Reads use immutable snapshots;
/// writes take <see cref="Gate"/>, change MongoDB first and the snapshot after.
/// </summary>
public sealed class UnlockedVault
{
    private readonly byte[] _contentKey;
    private readonly byte[] _pathKey;
    private readonly byte[] _textKey;
    private long _lastUsed;

    public UnlockedVault(string id, string name, ReadOnlySpan<byte> vaultKey, DateTime now)
    {
        Id = id;
        Name = name;
        _contentKey = VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []);
        KeyId = VaultCrypto.Derive(vaultKey, "kumbu/v1/key-id", []);
        _pathKey = VaultCrypto.Derive(vaultKey, "kumbu/v1/path-id", []);
        _textKey = VaultCrypto.Derive(vaultKey, "kumbu/v1/text-id", []);
        Touch(now);
    }

    public string Id { get; }

    /// <summary>Derived from the vault key; a slot that unwraps to another key doesn't get this vault.</summary>
    public byte[] KeyId { get; }

    public string Name { get; }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public ImmutableDictionary<VaultPath, (ObjectId Id, StoredNote Note)> Notes { get; set; } =
        ImmutableDictionary<VaultPath, (ObjectId, StoredNote)>.Empty;

    public ImmutableList<(ObjectId Id, TrashedNote Trashed)> Trash { get; set; } = [];

    public ImmutableList<MoveRecord> Moves { get; set; } = [];

    /// <summary>The named checkpoints; their notes' content stays in MongoDB until a command reads it.</summary>
    public ImmutableList<StoredCheckpoint> Checkpoints { get; set; } = [];

    /// <summary>Path ids of notes whose documents don't open. Their paths are sealed, so the path id is all
    /// that's known; it is enough to tell a request that asks for that path.</summary>
    public ImmutableHashSet<string> DamagedPathIds { get; set; } = [];

    public bool IsDamaged(VaultPath path) => DamagedPathIds.Contains(Convert.ToBase64String(PathId(path)));

    public DateTime LastUsed => new(Interlocked.Read(ref _lastUsed), DateTimeKind.Utc);

    /// <summary>Roughly what the working set holds in memory (UTF-16, history and trash included).</summary>
    public long ApproxBytes =>
        Notes.Values.Sum(n => Size(n.Note)) + Trash.Sum(t => Size(t.Trashed.Note));

    public void Touch(DateTime now) => Interlocked.Exchange(ref _lastUsed, now.Ticks);

    /// <summary>A keyed hash of the path: equal paths give equal ids, but the id reveals nothing else.</summary>
    public byte[] PathId(VaultPath path) => VaultCrypto.Mac(_pathKey, path.Value);

    /// <summary>The revision is part of the context, so a blob only opens next to the revision it was written with.</summary>
    public byte[] SealNote(ObjectId docId, StoredNote note) => Seal($"note:{Id}:{docId}:{note.Revision}", Payload.From(note));

    /// <summary>Opens a note and checks that the document's path id belongs to the sealed path, so a
    /// swapped path id can't show a note under another path.</summary>
    public StoredNote OpenNote(SealedDocument doc)
    {
        var note = Open<NotePayload>($"note:{Id}:{doc.Id}:{doc.Revision}", doc.Blob).ToNote(doc.Revision);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(PathId(note.Path), doc.PathId))
        {
            throw new InvalidDataException($"Path id of document {doc.Id} does not match its sealed path");
        }

        return note;
    }

    public byte[] SealTrash(ObjectId docId, TrashedNote trashed) =>
        Seal($"trash:{Id}:{docId}", new TrashPayload(Payload.From(trashed.Note), trashed.Note.Revision, trashed.DeletedBy, trashed.DeletedAt));

    public TrashedNote OpenTrash(SealedDocument doc)
    {
        var p = Open<TrashPayload>($"trash:{Id}:{doc.Id}", doc.Blob);
        return new TrashedNote(p.Note.ToNote(p.Revision), p.DeletedBy, p.DeletedAt);
    }

    public byte[] SealMove(ObjectId docId, MoveRecord move) =>
        Seal($"move:{Id}:{docId}", new MovePayload(move.From.Value, move.To.Value, move.MovedBy, move.MovedAt));

    public MoveRecord OpenMove(SealedDocument doc)
    {
        var p = Open<MovePayload>($"move:{Id}:{doc.Id}", doc.Blob);
        return new MoveRecord(Payload.Path(p.From), Payload.Path(p.To), p.MovedBy, p.MovedAt);
    }

    /// <summary>A keyed hash of a checkpoint's name, for the unique index: names are unique per vault.</summary>
    public byte[] NameId(string name) => VaultCrypto.Mac(_pathKey, "checkpoint:" + name);

    /// <summary>A keyed hash of a note's content. Checkpoints store each content once per vault under it.</summary>
    public byte[] TextId(string content) => VaultCrypto.Mac(_textKey, content);

    public byte[] SealCheckpoint(ObjectId docId, Checkpoint checkpoint, IReadOnlyDictionary<VaultPath, byte[]> textIds) =>
        Seal($"checkpoint:{Id}:{docId}", new CheckpointPayload(
            checkpoint.Name, checkpoint.Path.Value, checkpoint.Folder, checkpoint.CreatedBy, checkpoint.CreatedAt,
            [.. checkpoint.Notes.Select(n => new CheckpointNotePayload(n.Path.Value, n.Version, Convert.ToBase64String(textIds[n.Path])))]));

    /// <summary>Opens a checkpoint and checks its name against the document's name id, as for notes and paths.</summary>
    public StoredCheckpoint OpenCheckpoint(SealedDocument doc)
    {
        var p = Open<CheckpointPayload>($"checkpoint:{Id}:{doc.Id}", doc.Blob);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(NameId(p.Name), doc.PathId))
        {
            throw new InvalidDataException($"Name id of checkpoint {doc.Id} does not match its sealed name");
        }

        var checkpoint = new Checkpoint(
            p.Name, Payload.Path(p.Path), p.Folder, p.CreatedBy, DateTime.SpecifyKind(p.CreatedAt, DateTimeKind.Utc),
            [.. p.Notes.Select(n => new CheckpointNote(Payload.Path(n.Path), n.Version))]);
        return new StoredCheckpoint(doc.Id, checkpoint, p.Notes.ToDictionary(n => Payload.Path(n.Path), n => Convert.FromBase64String(n.TextId)));
    }

    public byte[] SealText(byte[] textId, string content) => Seal($"cptext:{Id}:{Convert.ToBase64String(textId)}", new TextPayload(content));

    /// <summary>Opens a checkpoint's text; its id is the keyed hash of the content, so a changed text doesn't open.</summary>
    public string OpenText(SealedDocument doc)
    {
        var content = Open<TextPayload>($"cptext:{Id}:{Convert.ToBase64String(doc.PathId)}", doc.Blob).Content;
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(TextId(content), doc.PathId))
        {
            throw new InvalidDataException($"Text {doc.Id} does not match its id");
        }

        return content;
    }

    private byte[] Seal<T>(string context, T payload) =>
        VaultCrypto.Seal(_contentKey, JsonSerializer.SerializeToUtf8Bytes(payload), context);

    private T Open<T>(string context, byte[] blob) =>
        JsonSerializer.Deserialize<T>(VaultCrypto.Open(_contentKey, blob, context))
        ?? throw new InvalidDataException($"Empty payload in {context}");

    private static long Size(StoredNote n) => 2L * (n.Content.Length + n.History.Sum(h => h.Content.Length));
}

internal sealed record ChangesetPayload(string Id, DateTime Started)
{
    public static ChangesetPayload? From(Changeset? c) => c is null ? null : new(c.Id, c.Started);

    public Changeset ToChangeset() => new(Id, DateTime.SpecifyKind(Started, DateTimeKind.Utc));
}

internal sealed record HistoryPayload(string Content, string WrittenBy, DateTime WrittenAt, ChangesetPayload? Changeset = null);

/// <remarks>Changeset is missing in notes written before step 6b; such a state joins no changeset.</remarks>
internal sealed record NotePayload(
    string Path, string Content, string LastWriter, DateTime LastWriteAt, List<HistoryPayload> History, string? SeedContent,
    ChangesetPayload? Changeset = null)
{
    public StoredNote ToNote(long revision) => new(
        Payload.Path(Path), Content, revision, LastWriter, DateTime.SpecifyKind(LastWriteAt, DateTimeKind.Utc),
        [.. History.Select(h => new HistoryEntry(h.Content, h.WrittenBy, DateTime.SpecifyKind(h.WrittenAt, DateTimeKind.Utc), h.Changeset?.ToChangeset()))],
        SeedContent, Changeset?.ToChangeset());
}

internal sealed record TrashPayload(NotePayload Note, long Revision, string DeletedBy, DateTime DeletedAt);

internal sealed record MovePayload(string From, string To, string MovedBy, DateTime MovedAt);

internal sealed record VaultMeta(string Name);

internal sealed record CheckpointNotePayload(string Path, string Version, string TextId);

internal sealed record CheckpointPayload(string Name, string Path, bool Folder, string CreatedBy, DateTime CreatedAt, List<CheckpointNotePayload> Notes);

internal sealed record TextPayload(string Content);

/// <summary>A checkpoint as the working set holds it: its document and where each note's content is.</summary>
public sealed record StoredCheckpoint(ObjectId Id, Checkpoint Checkpoint, IReadOnlyDictionary<VaultPath, byte[]> TextIds);

internal static class Payload
{
    public static NotePayload From(StoredNote n) => new(
        n.Path.Value, n.Content, n.LastWriter, n.LastWriteAt,
        [.. n.History.Select(h => new HistoryPayload(h.Content, h.WrittenBy, h.WrittenAt, ChangesetPayload.From(h.Changeset)))], n.SeedContent,
        ChangesetPayload.From(n.Changeset));

    public static VaultPath Path(string value) =>
        VaultPath.TryParse(value, out var path) ? path : throw new InvalidDataException($"Invalid path in a sealed document: {value}");
}
