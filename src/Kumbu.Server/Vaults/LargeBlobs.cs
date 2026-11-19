// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Vaults;

/// <summary>
/// What the head of a file larger than 15 MB seals (files spec, decision 10): the file's own random data key, its size,
/// how many chunks of what size hold it, and the SHA-256 of all its bytes, from which its blob id is computed
/// (<see cref="UnlockedVault.LargeBlobId"/>). <see cref="Format"/> is always <see cref="Marker"/>: a blob's own bytes that
/// happen to be JSON don't read as a head. Re-keying a vault reseals only this, under the new content key and id; the
/// chunks stay as they are.
/// </summary>
internal sealed record LargeBlobHead(string Format, byte[] Key, long Size, int Chunks, int ChunkSize, byte[] Sha256)
{
    public const string Marker = "kumbu/v1/large-blob";

    /// <summary>4 MiB: well inside MongoDB's 16 MB per document, and few enough documents for a gigabyte (256).</summary>
    public const int ChunkBytes = 4 * 1024 * 1024;
}

/// <summary>
/// A large file's bytes as they are read: one chunk at a time, each read and opened under one of the server's
/// <see cref="VaultCatalog.FileReads"/> slots, which goes back before its bytes are handed out, so a slow download holds
/// one chunk's bytes and no slot. Each chunk must open in its place (<see cref="UnlockedVault.OpenChunk"/>) and have its
/// size; anything else is <see cref="VaultUnavailableException"/>, never other bytes. The hash of the whole file can only
/// be checked once its last chunk is in: it is checked then, before that chunk's bytes are handed out, so a stream that
/// ends normally has given exactly the file's bytes, and one that doesn't match throws instead of ending.
/// </summary>
internal sealed class LargeBlobStream(VaultCatalog catalog, UnlockedVault vault, ObjectId headId, LargeBlobHead head) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[] _chunk = [];
    private int _offset;
    private int _next;
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        if (_offset == _chunk.Length)
        {
            if (_next == head.Chunks) return 0;
            _chunk = await NextChunkAsync(cancellationToken);
            _offset = 0;
        }

        var n = Math.Min(buffer.Length, _chunk.Length - _offset);
        _chunk.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        _position += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private async Task<byte[]> NextChunkAsync(CancellationToken ct)
    {
        var n = _next;
        var last = n == head.Chunks - 1;
        byte[] bytes;
        await catalog.FileReads.WaitAsync(ct);
        try
        {
            var c = Builders<BlobChunkDocument>.Filter;
            var doc = await catalog.BlobChunks.Find(c.Eq(d => d.Vault, vault.Id) & c.Eq(d => d.Head, headId) & c.Eq(d => d.N, n)).FirstOrDefaultAsync(ct)
                ?? throw VaultCatalog.Damaged();
            bytes = vault.OpenChunk(head.Key, headId, n, last, doc.Blob);
        }
        catch (CryptographicException)
        {
            throw VaultCatalog.Damaged();
        }
        finally
        {
            catalog.FileReads.Release();
        }

        if (bytes.Length != (last ? head.Size - (long)n * head.ChunkSize : head.ChunkSize)) throw VaultCatalog.Damaged();
        _hash.AppendData(bytes);
        if (last && !CryptographicOperations.FixedTimeEquals(_hash.GetHashAndReset(), head.Sha256)) throw VaultCatalog.Damaged();
        _next = n + 1;
        return bytes;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hash.Dispose();
        base.Dispose(disposing);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
