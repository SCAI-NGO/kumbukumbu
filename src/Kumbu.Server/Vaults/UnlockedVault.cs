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
/// writes take <see cref="Gate"/>, change MongoDB first and the snapshot after. The gates are the vault's, not the
/// object's (<see cref="VaultCatalog.LocksFor"/>): every object of one vault id, an older one a request still holds
/// included, waits on the same pair, so a re-keying run that holds them excludes them all.
/// </summary>
public sealed class UnlockedVault
{
    private readonly byte[] _contentKey;
    private readonly byte[] _pathKey;
    private readonly byte[] _textKey;
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

    /// <summary>The embeddings of the notes, for search by meaning; filled and kept current by the search index.</summary>
    public ImmutableDictionary<VaultPath, NoteEmbeddings> Embeddings { get; set; } = ImmutableDictionary<VaultPath, NoteEmbeddings>.Empty;

    /// <summary>
    /// The embedding provider the owner chose for this vault (<see cref="Search.EmbeddingProviders.Internal"/> unless
    /// they opted in to an external one). Sealed in the vault's meta: someone who can write to the database can't
    /// point a vault at a provider and have its notes sent there.
    /// </summary>
    public string EmbeddingProvider { get; set; } = Search.EmbeddingProviders.Internal;

    /// <summary>One note is indexed at a time per vault, so two updates of a note can't overtake each other.</summary>
    public SemaphoreSlim IndexGate { get; }

    /// <summary>The named checkpoints; their notes' content stays in MongoDB until a command reads it.</summary>
    public ImmutableList<StoredCheckpoint> Checkpoints { get; set; } = [];

    /// <summary>Path ids of notes whose documents don't open. Their paths are sealed, so the path id is all
    /// that's known; it is enough to tell a request that asks for that path.</summary>
    public ImmutableHashSet<string> DamagedPathIds { get; set; } = [];

    public bool IsDamaged(VaultPath path) => DamagedPathIds.Contains(Convert.ToBase64String(PathId(path)));

    public DateTime LastUsed => new(Interlocked.Read(ref _lastUsed), DateTimeKind.Utc);

    /// <summary>Roughly what the working set holds in memory (UTF-16, history and trash included).</summary>
    public long ApproxBytes =>
        Notes.Values.Sum(n => Size(n.Note)) + Trash.Sum(t => Size(t.Trashed.Note)) + Embeddings.Values.Sum(e => 4L * e.Chunks.Sum(c => c.Vector.Length));

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

    /// <summary>The vault's name and embedding provider, as <see cref="VaultCatalog"/> seals them at creation.</summary>
    public byte[] SealMeta(string embeddingProvider) =>
        Seal($"vault:{Id}", new VaultMeta(Name, embeddingProvider == Search.EmbeddingProviders.Internal ? null : embeddingProvider));

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

    /// <summary>
    /// For a re-keying run (<see cref="VaultCatalog"/>, rekeying design §3): opens a document of
    /// <paramref name="collection"/> under this object's key, with the AAD its clear fields give and without checking its
    /// <c>pid</c>, and seals what it holds under <paramref name="to"/>'s key, keeping <c>_id</c> and <c>rev</c>. The pid
    /// is computed under <paramref name="to"/> from what is sealed inside, whatever the stored one says; where the AAD
    /// holds the pid (embeddings, texts), the new seal has the new one. A checkpoint sealed under another key than
    /// <paramref name="to"/>'s gets its text ids through <paramref name="textIds"/> (base64 of the old id → new id);
    /// one not found there stays as it is, and is named in <c>Unmapped</c>. Null if the document doesn't open.
    /// </summary>
    internal Resealed? Reseal(string collection, SealedDocument doc, UnlockedVault to, IReadOnlyDictionary<string, byte[]> textIds, bool seal = true)
    {
        var context = collection switch
        {
            "notes" => $"note:{Id}:{doc.Id}:{doc.Revision}",
            "trash" => $"trash:{Id}:{doc.Id}",
            "moves" => $"move:{Id}:{doc.Id}",
            "embeddings" => $"embeddings:{Id}:{Convert.ToBase64String(doc.PathId)}",
            "checkpoints" => $"checkpoint:{Id}:{doc.Id}",
            "checkpoint_texts" => $"cptext:{Id}:{Convert.ToBase64String(doc.PathId)}",
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
                    var notes = p.Notes.Select(n =>
                    {
                        if (textIds.TryGetValue(n.TextId, out var mapped)) return n with { TextId = Convert.ToBase64String(mapped) };
                        unmapped.Add(n.TextId);
                        return n;
                    }).ToList();
                    return new Resealed(pid, from, seal ? to.Seal(context, p with { Notes = notes }) : [], null, unmapped);
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

    /// <summary>What <see cref="Reseal"/> made of a document: its pid and blob under the new key, the pid it should have
    /// had under the key it opened with, a text's content, and the text ids of a checkpoint that had no new one.</summary>
    internal sealed record Resealed(byte[] PathId, byte[] FromPathId, byte[] Blob, string? Content, IReadOnlyList<string> Unmapped);

    private static T Parse<T>(byte[] plain) => JsonSerializer.Deserialize<T>(plain) ?? throw new InvalidDataException("Empty payload");

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

/// <remarks>Embeddings: the vault's embedding provider; missing (vaults before the overnight round) or null is the internal model.</remarks>
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
