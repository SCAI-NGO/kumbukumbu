// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using Kumbu.Core.Crypto;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>
/// A random secret of this instance (collection <c>server</c>), made on first use and the same for every process
/// after, from which keys for keyed hashes are derived (invitation codes). It protects no note: the codes it
/// hashes are random and 256 bits long, so the hash is beyond reach without it anyway. It only makes a copy of
/// the <c>invitations</c> collection alone, a backup of it say, useless for checking a guessed code. It lives in
/// MongoDB, not in the configuration, so an instance needs no further secret set up; it is never logged.
/// </summary>
public sealed class ServerSecret(IMongoDatabase db)
{
    private const string Id = "secret";
    private const int SecretBytes = 32;

    private byte[]? _secret;

    private IMongoCollection<ServerSecretDocument> Docs { get; } = db.GetCollection<ServerSecretDocument>("server");

    /// <summary>A key for one purpose, <paramref name="label"/>; the same label gives the same key in every process.</summary>
    public async Task<byte[]> KeyAsync(string label, CancellationToken ct) => VaultCrypto.Derive(await SecretAsync(ct), label, []);

    private async Task<byte[]> SecretAsync(CancellationToken ct)
    {
        if (_secret is { } cached) return cached;

        // An upsert that only sets the secret on insert: of two processes starting at once, both read the one
        // that was written first. Two upserts racing on one _id can still both try to insert; the loser's
        // duplicate key error means the document now exists, so the second round reads it.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var doc = await Docs.FindOneAndUpdateAsync(
                    d => d.Id == Id,
                    Builders<ServerSecretDocument>.Update.SetOnInsert(d => d.Secret, RandomNumberGenerator.GetBytes(SecretBytes)),
                    new FindOneAndUpdateOptions<ServerSecretDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
                    ct);
                return _secret = doc.Secret;
            }
            catch (MongoCommandException e) when (e.Code == 11000 && attempt == 0)
            {
            }
        }
    }
}

[BsonIgnoreExtraElements]
public sealed class ServerSecretDocument
{
    [BsonId]
    public string Id { get; set; } = "";

    [BsonElement("secret")]
    public byte[] Secret { get; set; } = [];
}
