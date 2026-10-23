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
    private long _lastUsed;

    public UnlockedVault(string id, string name, ReadOnlySpan<byte> vaultKey, DateTime now)
    {
        Id = id;
        Name = name;
        _contentKey = VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []);
        _pathKey = VaultCrypto.Derive(vaultKey, "kumbu/v1/path-id", []);
        Touch(now);
    }

    public string Id { get; }

    public string Name { get; }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public ImmutableDictionary<VaultPath, (ObjectId Id, StoredNote Note)> Notes { get; set; } =
        ImmutableDictionary<VaultPath, (ObjectId, StoredNote)>.Empty;

    public ImmutableList<(ObjectId Id, TrashedNote Trashed)> Trash { get; set; } = [];

    public ImmutableList<MoveRecord> Moves { get; set; } = [];

    public DateTime LastUsed => new(Interlocked.Read(ref _lastUsed), DateTimeKind.Utc);

    /// <summary>Roughly what the working set holds in memory (UTF-16, history and trash included).</summary>
    public long ApproxBytes =>
        Notes.Values.Sum(n => Size(n.Note)) + Trash.Sum(t => Size(t.Trashed.Note));

    public void Touch(DateTime now) => Interlocked.Exchange(ref _lastUsed, now.Ticks);

    /// <summary>A keyed hash of the path: equal paths give equal ids, but the id reveals nothing else.</summary>
    public byte[] PathId(VaultPath path) => VaultCrypto.Mac(_pathKey, path.Value);

    public byte[] SealNote(ObjectId docId, StoredNote note) => Seal($"note:{Id}:{docId}", Payload.From(note));

    public StoredNote OpenNote(SealedDocument doc) => Open<NotePayload>($"note:{Id}:{doc.Id}", doc.Blob).ToNote(doc.Revision);

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

    private byte[] Seal<T>(string context, T payload) =>
        VaultCrypto.Seal(_contentKey, JsonSerializer.SerializeToUtf8Bytes(payload), context);

    private T Open<T>(string context, byte[] blob) =>
        JsonSerializer.Deserialize<T>(VaultCrypto.Open(_contentKey, blob, context))
        ?? throw new InvalidDataException($"Empty payload in {context}");

    private static long Size(StoredNote n) => 2L * (n.Content.Length + n.History.Sum(h => h.Content.Length));
}

internal sealed record HistoryPayload(string Content, string WrittenBy, DateTime WrittenAt);

internal sealed record NotePayload(
    string Path, string Content, string LastWriter, DateTime LastWriteAt, List<HistoryPayload> History, string? SeedContent)
{
    public StoredNote ToNote(long revision) => new(
        Payload.Path(Path), Content, revision, LastWriter, DateTime.SpecifyKind(LastWriteAt, DateTimeKind.Utc),
        [.. History.Select(h => new HistoryEntry(h.Content, h.WrittenBy, DateTime.SpecifyKind(h.WrittenAt, DateTimeKind.Utc)))],
        SeedContent);
}

internal sealed record TrashPayload(NotePayload Note, long Revision, string DeletedBy, DateTime DeletedAt);

internal sealed record MovePayload(string From, string To, string MovedBy, DateTime MovedAt);

internal sealed record VaultMeta(string Name);

internal static class Payload
{
    public static NotePayload From(StoredNote n) => new(
        n.Path.Value, n.Content, n.LastWriter, n.LastWriteAt,
        [.. n.History.Select(h => new HistoryPayload(h.Content, h.WrittenBy, h.WrittenAt))], n.SeedContent);

    public static VaultPath Path(string value) =>
        VaultPath.TryParse(value, out var path) ? path : throw new InvalidDataException($"Invalid path in a sealed document: {value}");
}
