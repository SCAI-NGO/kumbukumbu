// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using Kumbu.Core.Crypto;
using Kumbu.Server.Vaults;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kumbu.Server.Publishing;

/// <summary>
/// Where publications are stored (publish spec §4). The vault's publishing record, sealed under the vault key, holds
/// each publication's key (the vault-side slot, decision 6). Each copy is a mini vault: its publication key seals the
/// copy and its files, and a reader slot holds that key, with the owner's id (decision 7), under the reader key
/// derived from the share code. Everything of a copy is bound to its lookup id (L, lower-case hex): the reader slot
/// to <c>publication:{L}:reader</c>, the copy to <c>publication:{L}:copy</c>, file n to <c>publication:{L}:file:{n}</c>.
/// Writing needs the vault unlocked; reading a copy needs only its code. The code is a secret, never logged.
/// </summary>
internal sealed class PublicationStore(VaultCatalog catalog)
{
    private static FilterDefinitionBuilder<SealedDocument> F => Builders<SealedDocument>.Filter;

    private static FilterDefinitionBuilder<PublicationDocument> P => Builders<PublicationDocument>.Filter;

    private static FilterDefinitionBuilder<PublicationFileDocument> Files => Builders<PublicationFileDocument>.Filter;

    /// <summary>The collection of the vaults' records (<see cref="VaultCatalog.Publishing"/>).</summary>
    internal const string RecordCollection = "publishing";

    /// <summary>The vault's record and its document; the empty record and no document if it has none yet.</summary>
    public async Task<(SealedDocument? Doc, PublishingRecord Record)> ReadAsync(IClientSessionHandle? session, UnlockedVault vault, CancellationToken ct)
    {
        var doc = await (session is null ? catalog.Publishing.Find(RecordOf(vault)) : catalog.Publishing.Find(session, RecordOf(vault))).FirstOrDefaultAsync(ct);
        return doc is null ? (null, PublishingRecord.Empty) : (doc, vault.OpenPublishing(doc));
    }

    /// <summary>The vault's record with nothing but the database, for the owner's event texts (<see cref="Sharing.VaultEvents"/>).</summary>
    internal static async Task<PublishingRecord> ReadAsync(IMongoDatabase db, UnlockedVault vault, CancellationToken ct)
    {
        var doc = await db.GetCollection<SealedDocument>(RecordCollection).Find(RecordOf(vault)).FirstOrDefaultAsync(ct);
        return doc is null ? PublishingRecord.Empty : vault.OpenPublishing(doc);
    }

    private static FilterDefinition<SealedDocument> RecordOf(UnlockedVault vault) => F.Eq(d => d.Vault, vault.Id) & F.Eq(d => d.PathId, vault.PublishingId());

    /// <summary>
    /// Inserts the record at revision 1 when <paramref name="current"/> is null, or replaces <paramref name="current"/>
    /// at its revision + 1, only if it still has the revision and blob that were read; else
    /// <see cref="PublishingConflictException"/>.
    /// </summary>
    public async Task WriteAsync(IClientSessionHandle session, UnlockedVault vault, SealedDocument? current, PublishingRecord next, CancellationToken ct)
    {
        catalog.CheckWritable(vault);
        if (current is null)
        {
            var id = ObjectId.GenerateNewId();
            try
            {
                await catalog.Publishing.InsertOneAsync(
                    session, new SealedDocument { Id = id, Vault = vault.Id, PathId = vault.PublishingId(), Revision = 1, Blob = vault.SealPublishing(id, 1, next) },
                    cancellationToken: ct);
            }
            catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                throw new PublishingConflictException();
            }

            return;
        }

        var revision = current.Revision + 1;
        var replaced = await catalog.Publishing.ReplaceOneAsync(
            session,
            F.Eq(d => d.Id, current.Id) & F.Eq(d => d.Vault, vault.Id) & F.Eq(d => d.PathId, vault.PublishingId())
                & F.Eq(d => d.Revision, current.Revision) & F.Eq(d => d.Blob, current.Blob),
            new SealedDocument { Id = current.Id, Vault = vault.Id, PathId = vault.PublishingId(), Revision = revision, Blob = vault.SealPublishing(current.Id, revision, next) },
            cancellationToken: ct);
        if (replaced.MatchedCount == 0) throw new PublishingConflictException();
    }

    /// <summary>A new copy under <paramref name="code"/>, sealed under <paramref name="key"/>; file n is <paramref name="files"/>[n].</summary>
    public async Task InsertCopyAsync(
        IClientSessionHandle session, string vaultId, string code, byte[] key, string ownerId, PublishedCopy copy, IReadOnlyList<byte[]> files, CancellationToken ct)
    {
        var lookup = ShareCode.Lookup(code);
        var l = Convert.ToHexStringLower(lookup);
        var readerKey = ShareCode.ReaderKey(code);
        byte[] reader;
        try
        {
            reader = VaultCrypto.Seal(readerKey, JsonSerializer.SerializeToUtf8Bytes(new ReaderSlot(key, ownerId)), ReaderContext(l));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(readerKey);
        }

        var id = ObjectId.GenerateNewId();
        await catalog.Publications.InsertOneAsync(
            session, new PublicationDocument { Id = id, Vault = vaultId, Lookup = lookup, Reader = reader, Blob = SealCopy(key, l, copy), At = catalog.Now }, cancellationToken: ct);
        await InsertFilesAsync(session, vaultId, id, l, key, files, ct);
    }

    /// <summary>An update (decision 15): the same document, lookup, reader slot and time, a new copy and new files.</summary>
    public async Task ReplaceCopyAsync(IClientSessionHandle session, string code, byte[] key, PublishedCopy copy, IReadOnlyList<byte[]> files, CancellationToken ct)
    {
        var lookup = ShareCode.Lookup(code);
        var l = Convert.ToHexStringLower(lookup);
        var doc = await catalog.Publications.Find(session, P.Eq(d => d.Lookup, lookup)).FirstOrDefaultAsync(ct) ?? throw new PublishingConflictException();
        // The reader slot stays: a copy under another key than the slot's would never open for a reader.
        var readerKey = ShareCode.ReaderKey(code);
        try
        {
            var slot = JsonSerializer.Deserialize<ReaderSlot>(VaultCrypto.Open(readerKey, doc.Reader, ReaderContext(l)));
            if (slot?.Key is not { } slotKey || !CryptographicOperations.FixedTimeEquals(slotKey, key)) throw new PublishingConflictException();
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            throw new PublishingConflictException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(readerKey);
        }

        var updated = await catalog.Publications.UpdateOneAsync(
            session, P.Eq(d => d.Id, doc.Id) & P.Eq(d => d.Blob, doc.Blob), Builders<PublicationDocument>.Update.Set(d => d.Blob, SealCopy(key, l, copy)), cancellationToken: ct);
        if (updated.MatchedCount == 0) throw new PublishingConflictException();
        await catalog.PublicationFiles.DeleteManyAsync(session, Files.Eq(d => d.Publication, doc.Id), cancellationToken: ct);
        await InsertFilesAsync(session, doc.Vault, doc.Id, l, key, files, ct);
    }

    public async Task DeleteCopyAsync(IClientSessionHandle session, string code, CancellationToken ct)
    {
        var lookup = ShareCode.Lookup(code);
        if (await catalog.Publications.Find(session, P.Eq(d => d.Lookup, lookup)).FirstOrDefaultAsync(ct) is not { } doc) return;
        await catalog.PublicationFiles.DeleteManyAsync(session, Files.Eq(d => d.Publication, doc.Id), cancellationToken: ct);
        await catalog.Publications.DeleteOneAsync(session, P.Eq(d => d.Id, doc.Id), cancellationToken: ct);
    }

    /// <summary>Every copy of the vaults with its files, without opening anything; returns how many documents went, as
    /// <see cref="VaultCatalog.DeleteVaultsAsync"/> counts them.</summary>
    public async Task<long> DeleteCopiesOfVaultsAsync(IClientSessionHandle session, IReadOnlyCollection<string> vaultIds, CancellationToken ct)
    {
        var files = (await catalog.PublicationFiles.DeleteManyAsync(session, Files.In(d => d.Vault, vaultIds), cancellationToken: ct)).DeletedCount;
        return files + (await catalog.Publications.DeleteManyAsync(session, P.In(d => d.Vault, vaultIds), cancellationToken: ct)).DeletedCount;
    }

    /// <summary>The copy under <paramref name="code"/>, with its document's id and its owner; null if no document has the
    /// code's lookup, or its reader slot or copy doesn't open.</summary>
    public async Task<(ObjectId Id, string Owner, PublishedCopy Copy)?> OpenCopyAsync(string code, CancellationToken ct) =>
        await OpenAsync(code, ct) is { } opened ? (opened.Doc.Id, opened.Owner, opened.Copy) : null;

    /// <summary>File <paramref name="index"/> of the copy under <paramref name="code"/>; null if the copy doesn't open, names
    /// no such file, or the file's document is missing or doesn't open at that index.</summary>
    public async Task<(byte[] Bytes, string MediaType)?> OpenFileAsync(string code, int index, CancellationToken ct)
    {
        if (await OpenAsync(code, ct) is not { } opened) return null;
        if (opened.Copy.Files.FirstOrDefault(f => f.Index == index) is not { } file) return null;
        var doc = await catalog.PublicationFiles.Find(Files.Eq(d => d.Publication, opened.Doc.Id) & Files.Eq(d => d.Index, index)).FirstOrDefaultAsync(ct);
        if (doc is null) return null;
        try
        {
            return (VaultCrypto.Open(opened.Key, doc.Blob, FileContext(Convert.ToHexStringLower(opened.Doc.Lookup), index)), file.MediaType);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private async Task<(PublicationDocument Doc, byte[] Key, string Owner, PublishedCopy Copy)?> OpenAsync(string code, CancellationToken ct)
    {
        var lookup = ShareCode.Lookup(code);
        if (await catalog.Publications.Find(P.Eq(d => d.Lookup, lookup)).FirstOrDefaultAsync(ct) is not { } doc) return null;
        var l = Convert.ToHexStringLower(lookup);
        var readerKey = ShareCode.ReaderKey(code);
        try
        {
            var slot = JsonSerializer.Deserialize<ReaderSlot>(VaultCrypto.Open(readerKey, doc.Reader, ReaderContext(l)));
            if (slot?.Key is not { } key || slot.Owner is not { } owner) return null;
            var copy = JsonSerializer.Deserialize<PublishedCopy>(VaultCrypto.Open(key, doc.Blob, CopyContext(l)));
            return copy is null ? null : (doc, key, owner, copy);
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(readerKey);
        }
    }

    private async Task InsertFilesAsync(IClientSessionHandle session, string vaultId, ObjectId publication, string l, byte[] key, IReadOnlyList<byte[]> files, CancellationToken ct)
    {
        if (files.Count == 0) return;
        await catalog.PublicationFiles.InsertManyAsync(
            session,
            files.Select((bytes, n) => new PublicationFileDocument { Id = ObjectId.GenerateNewId(), Vault = vaultId, Publication = publication, Index = n, Blob = VaultCrypto.Seal(key, bytes, FileContext(l, n)) }),
            cancellationToken: ct);
    }

    private static byte[] SealCopy(byte[] key, string l, PublishedCopy copy) => VaultCrypto.Seal(key, JsonSerializer.SerializeToUtf8Bytes(copy), CopyContext(l));

    private static string ReaderContext(string l) => $"publication:{l}:reader";

    private static string CopyContext(string l) => $"publication:{l}:copy";

    private static string FileContext(string l, int n) => $"publication:{l}:file:{n}";
}
