// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kumbu.Core.Attachments;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using MongoDB.Bson;

namespace Kumbu.Server.Vaults;

/// <summary>
/// An unlocked vault: its keys and its decrypted working set (concept §7). Reads use immutable snapshots;
/// writes take <see cref="Gate"/>, change MongoDB first and the snapshot after. The gates are the vault's, not the
/// object's (<see cref="VaultCatalog.LocksFor"/>): every object of one vault id, an older one a request still holds
/// included, waits on the same pair, so a re-keying run that holds them excludes them all.
/// </summary>
public sealed class UnlockedVault
{
    private readonly byte[] _contentKey;
    private readonly byte[] _pathKey;
    private readonly byte[] _textKey;
    private readonly byte[] _blobKey;
    private long _lastUsed;

    public UnlockedVault(string id, string name, ReadOnlySpan<byte> vaultKey, DateTime now, VaultLocks locks)
    {
        Id = id;
        Name = name;
        Gate = locks.Gate;
        IndexGate = locks.IndexGate;
        _contentKey = VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []);
        KeyId = VaultCrypto.Derive(vaultKey, "kumbu/v1/key-id", []);
        _pathKey = VaultCrypto.Derive(vaultKey, "kumbu/v1/path-id", []);
        _textKey = VaultCrypto.Derive(vaultKey, "kumbu/v1/text-id", []);
        _blobKey = VaultCrypto.Derive(vaultKey, "kumbu/v1/blob-id", []);
        Horizon = now;
        Touch(now);
    }

    /// <summary>
    /// Since when <see cref="ChangedAt"/> knows every change: this object's creation. A sync client's cursor from
    /// before it (a restart, the vault locked and unlocked, re-keyed) gets the full list instead (sync spec §5).
    /// </summary>
    public DateTime Horizon { get; }

    /// <summary>
    /// When each path last changed by a commit through this object (or, for a request still holding an older one, by
    /// that commit: <see cref="EncryptedNoteStore"/> records it here too). Set under <see cref="Gate"/>, after the
    /// commit, so a time is never earlier than the change it stands for became visible.
    /// </summary>
    public ImmutableDictionary<VaultPath, DateTime> ChangedAt { get; set; } = ImmutableDictionary<VaultPath, DateTime>.Empty;

    public string Id { get; }

    /// <summary>Derived from the vault key; a slot that unwraps to another key doesn't get this vault.</summary>
    public byte[] KeyId { get; }

    public string Name { get; }

    /// <summary>Writes of the vault, one at a time.</summary>
    public SemaphoreSlim Gate { get; }

    /// <summary>
    /// A placeholder of a vault in its two-key period (rekeying design §3): named, with its embedding provider, but
    /// no working set. The name lists keep it; every other path refuses it (<see cref="VaultCatalog.CheckWritable"/>).
    /// </summary>
    public bool Pending { get; init; }

    public ImmutableDictionary<VaultPath, (ObjectId Id, StoredNote Note)> Notes { get; set; } =
        ImmutableDictionary<VaultPath, (ObjectId, StoredNote)>.Empty;

    public ImmutableList<(ObjectId Id, TrashedNote Trashed)> Trash { get; set; } = [];

    public ImmutableList<MoveRecord> Moves { get; set; } = [];

    /// <summary>The attachments' records, small; their bytes stay in MongoDB until a request reads them.</summary>
    public ImmutableDictionary<VaultPath, (ObjectId Id, StoredAttachment Attachment)> Attachments { get; set; } =
        ImmutableDictionary<VaultPath, (ObjectId, StoredAttachment)>.Empty;

    public ImmutableList<(ObjectId Id, TrashedAttachment Trashed)> AttachmentTrash { get; set; } = [];

    /// <summary>The embeddings of the notes, for search by meaning; filled and kept current by the search index.</summary>
    public ImmutableDictionary<VaultPath, NoteEmbeddings> Embeddings { get; set; } = ImmutableDictionary<VaultPath, NoteEmbeddings>.Empty;

    /// <summary>
    /// The embedding provider of this vault: the one its owner chose, or the instance's default
    /// (<see cref="EmbeddingsOptions.Default"/>) if they never chose. The choice is sealed in the vault's meta: someone
    /// who can write to the database can't point a vault at a provider and have its notes sent there.
    /// </summary>
    public string EmbeddingProvider { get; set; } = Search.EmbeddingProviders.Internal;

    /// <summary>One note is indexed at a time per vault, so two updates of a note can't overtake each other.</summary>
    public SemaphoreSlim IndexGate { get; }

    /// <summary>The named checkpoints; their notes' content stays in MongoDB until a command reads it.</summary>
    public ImmutableList<StoredCheckpoint> Checkpoints { get; set; } = [];

    /// <summary>Path ids of notes and attachment records whose documents don't open. Their paths are sealed, so the
    /// path id is all that's known; it is enough to tell a request that asks for that path.</summary>
    public ImmutableHashSet<string> DamagedPathIds { get; set; } = [];

    /// <summary>Those of <see cref="DamagedPathIds"/> that are attachment records: a client that knows no attachments
    /// isn't told about them.</summary>
    public ImmutableHashSet<string> DamagedAttachmentIds { get; set; } = [];

    /// <summary>How many notes (not attachment records) don't open.</summary>
    public int DamagedNotes => DamagedPathIds.Except(DamagedAttachmentIds).Count;

    public bool IsDamaged(VaultPath path) => DamagedPathIds.Contains(Convert.ToBase64String(PathId(path)));

    public DateTime LastUsed => new(Interlocked.Read(ref _lastUsed), DateTimeKind.Utc);

    /// <summary>Roughly what the working set holds in memory (UTF-16, history and trash included); of attachments only
    /// their records, never their bytes.</summary>
    public long ApproxBytes =>
        Notes.Values.Sum(n => Size(n.Note)) + Trash.Sum(t => Size(t.Trashed.Note)) + Embeddings.Values.Sum(e => 4L * e.Chunks.Sum(c => c.Vector.Length))
        + Attachments.Values.Sum(a => Size(a.Attachment)) + AttachmentTrash.Sum(t => Size(t.Trashed.Attachment));

    public void Touch(DateTime now) => Interlocked.Exchange(ref _lastUsed, now.Ticks);

    private volatile bool _deleted;

    /// <summary>Set once the vault is deleted (<see cref="VaultCatalog.DeleteVaultsAsync"/>), under both gates: a
    /// write or an indexing that was waiting for a gate then writes nothing, so nothing outlives the vault.</summary>
    public bool Deleted
    {
        get => _deleted;
        set => _deleted = value;
    }

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

    /// <summary>The vault's name and the embedding provider its owner chose. An explicit <c>internal</c> is kept as
    /// such, so that the vault stays on this server whatever the instance's default.</summary>
    public byte[] SealMeta(string embeddingProvider) => Seal($"vault:{Id}", new VaultMeta(Name, embeddingProvider));

    public byte[] SealTrash(ObjectId docId, TrashedNote trashed) =>
        Seal($"trash:{Id}:{docId}", new TrashPayload(
            Payload.From(trashed.Note), trashed.Note.Revision, trashed.DeletedBy, trashed.DeletedAt, ChangesetPayload.From(trashed.Changeset),
            WriterRefPayload.VersionOf(trashed.Ref), WriterRefPayload.From(trashed.Ref)));

    public TrashedNote OpenTrash(SealedDocument doc)
    {
        var p = Open<TrashPayload>($"trash:{Id}:{doc.Id}", doc.Blob);
        return new TrashedNote(p.Note.ToNote(p.Revision), p.DeletedBy, p.DeletedAt, p.Changeset?.ToChangeset(), WriterRefPayload.Read(p.V, p.Ref));
    }

    public byte[] SealMove(ObjectId docId, MoveRecord move) =>
        Seal($"move:{Id}:{docId}", new MovePayload(
            move.From.Value, move.To.Value, move.MovedBy, move.MovedAt, ChangesetPayload.From(move.Changeset),
            WriterRefPayload.VersionOf(move.Ref), WriterRefPayload.From(move.Ref)));

    public MoveRecord OpenMove(SealedDocument doc)
    {
        var p = Open<MovePayload>($"move:{Id}:{doc.Id}", doc.Blob);
        return new MoveRecord(Payload.Path(p.From), Payload.Path(p.To), p.MovedBy, p.MovedAt, p.Changeset?.ToChangeset(), WriterRefPayload.Read(p.V, p.Ref));
    }

    /// <summary>An attachment's record, bound to its revision as a note is (<see cref="SealNote"/>).</summary>
    public byte[] SealAttachment(ObjectId docId, StoredAttachment attachment) =>
        Seal($"attachment:{Id}:{docId}:{attachment.Revision}", AttachmentPayload.From(attachment));

    /// <summary>Opens an attachment's record and checks its path against the document's path id, as for notes.</summary>
    public StoredAttachment OpenAttachment(SealedDocument doc)
    {
        var attachment = Open<AttachmentPayload>($"attachment:{Id}:{doc.Id}:{doc.Revision}", doc.Blob).ToAttachment(doc.Revision);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(PathId(attachment.Path), doc.PathId))
        {
            throw new InvalidDataException($"Path id of attachment {doc.Id} does not match its sealed path");
        }

        return attachment;
    }

    public byte[] SealAttachmentTrash(ObjectId docId, TrashedAttachment trashed) =>
        Seal($"attachtrash:{Id}:{docId}", new AttachmentTrashPayload(
            AttachmentPayload.From(trashed.Attachment), trashed.Attachment.Revision, trashed.DeletedBy, trashed.DeletedAt, ChangesetPayload.From(trashed.Changeset),
            WriterRefPayload.VersionOf(trashed.Ref), WriterRefPayload.From(trashed.Ref)));

    public TrashedAttachment OpenAttachmentTrash(SealedDocument doc)
    {
        var p = Open<AttachmentTrashPayload>($"attachtrash:{Id}:{doc.Id}", doc.Blob);
        return new TrashedAttachment(
            p.Attachment.ToAttachment(p.Revision), p.DeletedBy, DateTime.SpecifyKind(p.DeletedAt, DateTimeKind.Utc), p.Changeset?.ToChangeset(), WriterRefPayload.Read(p.V, p.Ref));
    }

    /// <summary>A keyed hash of an attachment's bytes: the id its blob is stored under. Equal bytes give equal ids
    /// within the vault, so they are stored once; without the vault key the id can't be matched to known content.</summary>
    public byte[] BlobId(ReadOnlySpan<byte> bytes) => VaultCrypto.Mac(_blobKey, bytes);

    /// <summary>The bytes as they are, sealed under the blob's id: the context binds them to it and to the vault.</summary>
    public byte[] SealBlob(byte[] blobId, ReadOnlySpan<byte> bytes) => VaultCrypto.Seal(_contentKey, bytes, BlobContext(blobId));

    /// <summary>Opens a blob and checks the bytes against its id, so bytes changed in the database don't open.</summary>
    public byte[] OpenBlob(BlobDocument doc)
    {
        var bytes = VaultCrypto.Open(_contentKey, doc.Blob, BlobContext(doc.BlobId));
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(BlobId(bytes), doc.BlobId))
        {
            throw new InvalidDataException($"Blob {doc.Id} does not match its id");
        }

        return bytes;
    }

    private string BlobContext(byte[] blobId) => $"blob:{Id}:{Convert.ToBase64String(blobId)}";

    /// <summary>A part of a PDF's analysis, bound to its blob's id and its part number: moved to another blob or part,
    /// it doesn't open.</summary>
    internal byte[] SealPdfAnalysis<T>(byte[] blobId, int part, T payload) => Seal(PdfAnalysisContext(blobId, part), payload);

    internal T OpenPdfAnalysis<T>(PdfAnalysisDocument doc) => Open<T>(PdfAnalysisContext(doc.BlobId, doc.Part), doc.Blob);

    private string PdfAnalysisContext(byte[] blobId, int part) => $"pdfa:{Id}:{Convert.ToBase64String(blobId)}:{part}";

    /// <summary>The embeddings of a note, under the note's path id; the context binds them to that path.</summary>
    public byte[] SealEmbeddings(VaultPath path, NoteEmbeddings embeddings) =>
        Seal($"embeddings:{Id}:{Convert.ToBase64String(PathId(path))}", new EmbeddingsPayload(
            path.Value, embeddings.Version,
            [.. embeddings.Chunks.Select(c => new EmbeddedChunkPayload(c.StartLine, c.Hash, Convert.ToBase64String(Halves(c.Vector))))],
            embeddings.Model));

    public (VaultPath Path, NoteEmbeddings Embeddings) OpenEmbeddings(SealedDocument doc)
    {
        var p = Open<EmbeddingsPayload>($"embeddings:{Id}:{Convert.ToBase64String(doc.PathId)}", doc.Blob);
        var path = Payload.Path(p.Path);
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(PathId(path), doc.PathId))
        {
            throw new InvalidDataException($"Path id of embeddings {doc.Id} does not match its sealed path");
        }

        return (path, new NoteEmbeddings(p.Model ?? "arctic-m", p.Version, [.. p.Chunks.Select(c => new EmbeddedChunk(c.StartLine, c.Hash, Floats(Convert.FromBase64String(c.Vector))))]));
    }

    /// <summary>Vectors are stored as 16-bit floats, half the size; search precision doesn't need more.</summary>
    private static byte[] Halves(float[] vector)
    {
        var bytes = new byte[vector.Length * 2];
        for (var i = 0; i < vector.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (Half)vector[i]);
        return bytes;
    }

    private static float[] Floats(byte[] bytes)
    {
        var vector = new float[bytes.Length / 2];
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)BitConverter.ToHalf(bytes, i * 2);
        return vector;
    }

    /// <summary>A keyed hash of a checkpoint's name, for the unique index: names are unique per vault.</summary>
    public byte[] NameId(string name) => VaultCrypto.Mac(_pathKey, "checkpoint:" + name);

    /// <summary>A keyed hash of a note's content. Checkpoints store each content once per vault under it.</summary>
    public byte[] TextId(string content) => VaultCrypto.Mac(_textKey, content);

    public byte[] SealCheckpoint(ObjectId docId, Checkpoint checkpoint, IReadOnlyDictionary<VaultPath, byte[]> textIds) =>
        Seal($"checkpoint:{Id}:{docId}", new CheckpointPayload(
            checkpoint.Name, checkpoint.Path.Value, checkpoint.Folder, checkpoint.CreatedBy, checkpoint.CreatedAt,
            [.. checkpoint.Notes.Select(n => new CheckpointNotePayload(n.Path.Value, n.Version, Convert.ToBase64String(textIds[n.Path])))],
            checkpoint.Attachments.Count == 0
                ? null
                : [.. checkpoint.Attachments.Select(a => new CheckpointAttachmentPayload(a.Path.Value, AttachmentStatePayload.From(a.State)))],
            WriterRefPayload.VersionOf(checkpoint.Ref), WriterRefPayload.From(checkpoint.Ref)));

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
            [.. p.Notes.Select(n => new CheckpointNote(Payload.Path(n.Path), n.Version))])
        {
            Attachments = [.. (p.Attachments ?? []).Select(a => new CheckpointAttachment(Payload.Path(a.Path), a.State.ToState()))],
            Ref = WriterRefPayload.Read(p.V, p.Ref),
        };
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

    /// <summary>
    /// For a re-keying run (<see cref="VaultCatalog"/>, rekeying design §3): opens a document of
    /// <paramref name="collection"/> under this object's key, with the AAD its clear fields give and without checking its
    /// <c>pid</c>, and seals what it holds under <paramref name="to"/>'s key, keeping <c>_id</c> and <c>rev</c>. The pid
    /// is computed under <paramref name="to"/> from what is sealed inside, whatever the stored one says; where the AAD
    /// holds the pid (embeddings, texts, blobs, analyses), the new seal has the new one; a blob's <c>bid</c> stands in for
    /// its pid, and an analysis's too, its part number for the revision (the blob's new id comes from <paramref name="ids"/>).
    /// A checkpoint or an attachment record sealed under another key than <paramref name="to"/>'s gets its text or blob
    /// ids through <paramref name="ids"/>; one not found there stays as it is, and is named in <c>Unmapped</c>. Null if
    /// the document doesn't open.
    /// </summary>
    internal Resealed? Reseal(string collection, SealedDocument doc, UnlockedVault to, RekeyIds ids, bool seal = true)
    {
        var context = collection switch
        {
            "notes" => $"note:{Id}:{doc.Id}:{doc.Revision}",
            "trash" => $"trash:{Id}:{doc.Id}",
            "moves" => $"move:{Id}:{doc.Id}",
            "embeddings" => $"embeddings:{Id}:{Convert.ToBase64String(doc.PathId)}",
            "checkpoints" => $"checkpoint:{Id}:{doc.Id}",
            "checkpoint_texts" => $"cptext:{Id}:{Convert.ToBase64String(doc.PathId)}",
            "attachments" => $"attachment:{Id}:{doc.Id}:{doc.Revision}",
            "attachment_trash" => $"attachtrash:{Id}:{doc.Id}",
            "blobs" => BlobContext(doc.PathId),
            "pdf_analyses" => PdfAnalysisContext(doc.PathId, (int)doc.Revision),
            _ => throw new ArgumentException($"No sealed documents in {collection}", nameof(collection)),
        };
        byte[] plain;
        try
        {
            plain = VaultCrypto.Open(_contentKey, doc.Blob, context);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }

        try
        {
            switch (collection)
            {
                case "notes":
                    return Same(Payload.Path(Parse<NotePayload>(plain).Path));
                case "trash":
                    return Same(Payload.Path(Parse<TrashPayload>(plain).Note.Path));
                case "moves":
                    return Same(Payload.Path(Parse<MovePayload>(plain).From));
                case "embeddings":
                {
                    var path = Payload.Path(Parse<EmbeddingsPayload>(plain).Path);
                    var pid = to.PathId(path);
                    return new Resealed(pid, PathId(path), SealPlain($"embeddings:{Id}:{Convert.ToBase64String(pid)}"), null, []);
                }

                case "checkpoints":
                {
                    var p = Parse<CheckpointPayload>(plain);
                    var (pid, from) = (to.NameId(p.Name), NameId(p.Name));
                    if (ReferenceEquals(this, to)) return new Resealed(pid, from, SealPlain(context), null, []);
                    var unmapped = new List<string>();
                    var notes = p.Notes.Select(n => n with { TextId = Map(n.TextId, ids.Texts, unmapped) }).ToList();
                    var attachments = p.Attachments?.Select(a => a with { State = MapBlobId(a.State, ids, unmapped) }).ToList();
                    return new Resealed(pid, from, seal ? to.Seal(context, p with { Notes = notes, Attachments = attachments }) : [], null, unmapped);
                }

                case "attachments":
                {
                    var p = Parse<AttachmentPayload>(plain);
                    var path = Payload.Path(p.Path);
                    if (ReferenceEquals(this, to)) return Same(path);
                    var unmapped = new List<string>();
                    var mapped = MapBlobIds(p, ids, unmapped);
                    return new Resealed(to.PathId(path), PathId(path), seal ? to.Seal(context, mapped) : [], null, unmapped);
                }

                case "attachment_trash":
                {
                    var p = Parse<AttachmentTrashPayload>(plain);
                    var path = Payload.Path(p.Attachment.Path);
                    if (ReferenceEquals(this, to)) return Same(path);
                    var unmapped = new List<string>();
                    var mapped = p with { Attachment = MapBlobIds(p.Attachment, ids, unmapped) };
                    return new Resealed(to.PathId(path), PathId(path), seal ? to.Seal(context, mapped) : [], null, unmapped);
                }

                case "blobs":
                {
                    var pid = to.BlobId(plain);
                    return new Resealed(pid, BlobId(plain), SealPlain(to.BlobContext(pid)), null, []) { Bytes = seal ? null : plain };
                }

                case "pdf_analyses":
                {
                    // Bound to its blob's id, which only the blobs' pass can map; the part number stays.
                    if (ReferenceEquals(this, to)) return new Resealed(doc.PathId, doc.PathId, SealPlain(context), null, []);
                    var unmapped = new List<string>();
                    var pid = Convert.FromBase64String(Map(Convert.ToBase64String(doc.PathId), ids.Blobs, unmapped));
                    return new Resealed(pid, doc.PathId, SealPlain(to.PdfAnalysisContext(pid, (int)doc.Revision)), null, unmapped);
                }

                default:
                {
                    var content = Parse<TextPayload>(plain).Content;
                    var pid = to.TextId(content);
                    return new Resealed(pid, TextId(content), SealPlain($"cptext:{Id}:{Convert.ToBase64String(pid)}"), content, []);
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or NullReferenceException)
        {
            // It opened, but what it holds can't be read: no pid can be computed for it.
            return null;
        }

        Resealed Same(VaultPath path) => new(to.PathId(path), PathId(path), SealPlain(context), null, []);

        // Without seal (only the pids are wanted), the blob stays empty.
        byte[] SealPlain(string newContext) => seal ? VaultCrypto.Seal(to._contentKey, plain, newContext) : [];
    }

    /// <summary>
    /// The ids keyed by content that a re-keying run has learnt, base64 of the id under the old key → the id under the
    /// new one: checkpoint texts' (<see cref="TextId"/>) and blobs' (<see cref="BlobId"/>). Their passes come first, so
    /// the documents that name them find every one.
    /// </summary>
    internal sealed class RekeyIds
    {
        public Dictionary<string, byte[]> Texts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, byte[]> Blobs { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>What <see cref="Reseal"/> made of a document: its pid and blob under the new key, the pid it should have
    /// had under the key it opened with, a text's content, and the text or blob ids it names that had no new one.</summary>
    internal sealed record Resealed(byte[] PathId, byte[] FromPathId, byte[] Blob, string? Content, IReadOnlyList<string> Unmapped)
    {
        /// <summary>A blob's bytes when it was opened but not sealed (<c>seal: false</c>): its id under the old key is
        /// computed from them.</summary>
        public byte[]? Bytes { get; init; }
    }

    /// <summary>Every blob id an attachment record names, current and history, mapped to the new key's.</summary>
    private static AttachmentPayload MapBlobIds(AttachmentPayload p, RekeyIds ids, List<string> unmapped) => p with
    {
        Current = MapBlobId(p.Current, ids, unmapped),
        History = [.. p.History.Select(h => h with { State = MapBlobId(h.State, ids, unmapped) })],
    };

    /// <summary>
    /// The one place a sealed reference to a blob is mapped to the new key's id when a vault is re-keyed: whatever comes
    /// to hold attachment states (records, their trash, checkpoints) maps each state through it.
    /// </summary>
    private static AttachmentStatePayload MapBlobId(AttachmentStatePayload state, RekeyIds ids, List<string> unmapped) =>
        state with { BlobId = Map(state.BlobId, ids.Blobs, unmapped) };

    /// <summary>An id under the old key mapped to the new key's; one not found stays as it is, named in <paramref name="unmapped"/>.</summary>
    private static string Map(string id, Dictionary<string, byte[]> ids, List<string> unmapped)
    {
        if (ids.TryGetValue(id, out var mapped)) return Convert.ToBase64String(mapped);
        unmapped.Add(id);
        return id;
    }

    private static T Parse<T>(byte[] plain) => JsonSerializer.Deserialize<T>(plain) ?? throw new InvalidDataException("Empty payload");

    private byte[] Seal<T>(string context, T payload) =>
        VaultCrypto.Seal(_contentKey, JsonSerializer.SerializeToUtf8Bytes(payload), context);

    private T Open<T>(string context, byte[] blob) =>
        JsonSerializer.Deserialize<T>(VaultCrypto.Open(_contentKey, blob, context))
        ?? throw new InvalidDataException($"Empty payload in {context}");

    private static long Size(StoredNote n) => 2L * (n.Content.Length + n.History.Sum(h => h.Content.Length));

    /// <summary>A record's strings and a rough allowance per state for the rest.</summary>
    private static long Size(StoredAttachment a) => 2L * (a.Path.Value.Length + a.LastWriter.Length) + StateBytes * (1 + a.History.Count);

    private const long StateBytes = 256;
}

internal sealed record ChangesetPayload(string Id, DateTime Started)
{
    public static ChangesetPayload? From(Changeset? c) => c is null ? null : new(c.Id, c.Started);

    public Changeset ToChangeset() => new(Id, DateTime.SpecifyKind(Started, DateTimeKind.Utc));
}

/// <summary>
/// A writer reference as sealed in every payload that carries a writer (collab design §8.1), beside a payload version
/// <c>v</c>: the owner's <c>{owner: true, kind, id}</c> (no id for a session or the server), a member's
/// <c>{writer, kind}</c> and nothing else. Without <c>v</c> a payload was written before 0.3.0 and is read as the
/// owner's (<see cref="WriterRef.Legacy"/>); with <c>v</c> but no reference, as unknown, never as the owner's.
/// </summary>
internal sealed record WriterRefPayload(
    [property: JsonPropertyName("owner"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Owner,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id = null,
    [property: JsonPropertyName("writer"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Writer = null)
{
    /// <summary>The payload version of 0.3.0, the first with references.</summary>
    public const int Version = 2;

    public static WriterRef Read(int? v, WriterRefPayload? r) =>
        v is null ? WriterRef.Legacy
        : r is null ? WriterRef.Unknown
        : r.Owner ? WriterRef.ForOwner(r.Kind, r.Id)
        : WriterRef.ForMember(r.Writer ?? "", r.Kind);

    /// <summary>The <c>v</c> to seal with <paramref name="reference"/>: none for a legacy entry, so that it still reads
    /// as the owner's after a rewrap (a note's next write keeps its history entries).</summary>
    public static int? VersionOf(WriterRef? reference) => reference is { IsLegacy: true } ? null : Version;

    /// <summary>The reference to seal; none for a legacy or unknown entry (and for none, which a store refuses first).</summary>
    public static WriterRefPayload? From(WriterRef? reference) =>
        reference is null || reference.IsLegacy || reference.IsUnknown ? null
        : reference.Owner ? new(true, reference.Kind, reference.Id)
        : new(false, reference.Kind, Writer: reference.Writer);
}

/// <remarks>V and Ref (<see cref="WriterRefPayload"/>) per entry: an entry keeps the reference it was written with.</remarks>
internal sealed record HistoryPayload(
    string Content, string WrittenBy, DateTime WrittenAt, ChangesetPayload? Changeset = null,
    [property: JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? V = null,
    [property: JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WriterRefPayload? Ref = null);

/// <remarks>Changeset is missing in notes written before step 6b; such a state joins no changeset.</remarks>
internal sealed record NotePayload(
    string Path, string Content, string LastWriter, DateTime LastWriteAt, List<HistoryPayload> History, string? SeedContent,
    ChangesetPayload? Changeset = null,
    [property: JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? V = null,
    [property: JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WriterRefPayload? Ref = null)
{
    public StoredNote ToNote(long revision) => new(
        Payload.Path(Path), Content, revision, LastWriter, DateTime.SpecifyKind(LastWriteAt, DateTimeKind.Utc),
        [.. History.Select(h => new HistoryEntry(
            h.Content, h.WrittenBy, DateTime.SpecifyKind(h.WrittenAt, DateTimeKind.Utc), h.Changeset?.ToChangeset(), WriterRefPayload.Read(h.V, h.Ref)))],
        SeedContent, Changeset?.ToChangeset(), WriterRefPayload.Read(V, Ref));
}

/// <remarks>Changeset is missing in records written before moves and removals carried one (and omitted when null).</remarks>
internal sealed record TrashPayload(
    NotePayload Note, long Revision, string DeletedBy, DateTime DeletedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ChangesetPayload? Changeset = null,
    [property: JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? V = null,
    [property: JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WriterRefPayload? Ref = null);

/// <remarks>Changeset as in <see cref="TrashPayload"/>.</remarks>
internal sealed record MovePayload(
    string From, string To, string MovedBy, DateTime MovedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ChangesetPayload? Changeset = null,
    [property: JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? V = null,
    [property: JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WriterRefPayload? Ref = null);

/// <remarks>Embeddings: the embedding provider the owner chose; missing (vaults before the overnight round, or never
/// chosen) or null is the instance's default (<see cref="EmbeddingsOptions.Default"/>). Up to 0.2.2 an explicit
/// <c>internal</c> was stored as null too.</remarks>
internal sealed record VaultMeta(
    string Name,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Embeddings = null);

internal sealed record EmbeddedChunkPayload(int StartLine, string Hash, string Vector);

/// <remarks>Model is missing in embeddings written before step 7b; step 7 had only arctic-m.</remarks>
internal sealed record EmbeddingsPayload(string Path, string Version, List<EmbeddedChunkPayload> Chunks, string? Model = null);

/// <summary>A chunk's vector, with the hash of the text it was made from, to reuse it while the text stays.</summary>
public sealed record EmbeddedChunk(int StartLine, string Hash, float[] Vector);

/// <summary>The embeddings of one note at one version, made by one model (its key): vectors of different models
/// don't compare.</summary>
public sealed record NoteEmbeddings(string Model, string Version, IReadOnlyList<EmbeddedChunk> Chunks);

internal sealed record AttachmentStatePayload(string MediaType, long Size, int? Width, int? Height, string Version, string BlobId)
{
    public static AttachmentStatePayload From(AttachmentState s) => new(s.MediaType, s.Size, s.Width, s.Height, s.Version, s.BlobId);

    public AttachmentState ToState() => new(MediaType, Size, Width, Height, Version, BlobId);
}

internal sealed record AttachmentVersionPayload(
    AttachmentStatePayload State, string WrittenBy, DateTime WrittenAt, ChangesetPayload? Changeset,
    [property: JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? V = null,
    [property: JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WriterRefPayload? Ref = null);

internal sealed record AttachmentPayload(
    string Path, AttachmentStatePayload Current, string LastWriter, DateTime LastWriteAt, List<AttachmentVersionPayload> History, ChangesetPayload? Changeset,
    [property: JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? V = null,
    [property: JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WriterRefPayload? Ref = null)
{
    public static AttachmentPayload From(StoredAttachment a) => new(
        a.Path.Value, AttachmentStatePayload.From(a.Current), a.LastWriter, a.LastWriteAt,
        [.. a.History.Select(h => new AttachmentVersionPayload(
            AttachmentStatePayload.From(h.State), h.WrittenBy, h.WrittenAt, ChangesetPayload.From(h.Changeset), WriterRefPayload.VersionOf(h.Ref), WriterRefPayload.From(h.Ref)))],
        ChangesetPayload.From(a.Changeset), WriterRefPayload.VersionOf(a.Ref), WriterRefPayload.From(a.Ref));

    public StoredAttachment ToAttachment(long revision) => new(
        Payload.Path(Path), Current.ToState(), revision, LastWriter, DateTime.SpecifyKind(LastWriteAt, DateTimeKind.Utc),
        [.. History.Select(h => new AttachmentVersion(
            h.State.ToState(), h.WrittenBy, DateTime.SpecifyKind(h.WrittenAt, DateTimeKind.Utc), h.Changeset?.ToChangeset(), WriterRefPayload.Read(h.V, h.Ref)))],
        Changeset?.ToChangeset(), WriterRefPayload.Read(V, Ref));
}

internal sealed record AttachmentTrashPayload(
    AttachmentPayload Attachment, long Revision, string DeletedBy, DateTime DeletedAt, ChangesetPayload? Changeset,
    [property: JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? V = null,
    [property: JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WriterRefPayload? Ref = null);

internal sealed record CheckpointNotePayload(string Path, string Version, string TextId);

internal sealed record CheckpointAttachmentPayload(string Path, AttachmentStatePayload State);

/// <remarks>Attachments is missing in checkpoints made before attachments existed, and omitted when there are none.</remarks>
internal sealed record CheckpointPayload(
    string Name, string Path, bool Folder, string CreatedBy, DateTime CreatedAt, List<CheckpointNotePayload> Notes,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<CheckpointAttachmentPayload>? Attachments = null,
    [property: JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? V = null,
    [property: JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WriterRefPayload? Ref = null);

internal sealed record TextPayload(string Content);

/// <summary>A checkpoint as the working set holds it: its document and where each note's content is (its attachments
/// name their blobs themselves).</summary>
public sealed record StoredCheckpoint(ObjectId Id, Checkpoint Checkpoint, IReadOnlyDictionary<VaultPath, byte[]> TextIds);

internal static class Payload
{
    public static NotePayload From(StoredNote n) => new(
        n.Path.Value, n.Content, n.LastWriter, n.LastWriteAt,
        [.. n.History.Select(h => new HistoryPayload(
            h.Content, h.WrittenBy, h.WrittenAt, ChangesetPayload.From(h.Changeset), WriterRefPayload.VersionOf(h.Ref), WriterRefPayload.From(h.Ref)))],
        n.SeedContent, ChangesetPayload.From(n.Changeset), WriterRefPayload.VersionOf(n.Ref), WriterRefPayload.From(n.Ref));

    public static VaultPath Path(string value) =>
        VaultPath.TryParse(value, out var path) ? path : throw new InvalidDataException($"Invalid path in a sealed document: {value}");
}
