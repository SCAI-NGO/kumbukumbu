// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Crypto;
using Kumbu.Server.Accounts;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Kumbu.Server.Publishing;

/// <summary>
/// The token of an AI link (AI link spec §3.4): made as a share code is, 160 random bits as 32 characters of
/// <see cref="Pairings.Alphabet"/>, but derived under labels of its own, so a token is never a share code nor a share
/// code a token. Its lookup id is all the database stores to find the link; its key seals the link's slot. It opens
/// one copy for <see cref="Lifetime"/> and <see cref="Ceiling"/> requests. The token is a secret, never logged.
/// </summary>
public static class AiLinkToken
{
    public const int Length = 32;
    private const int MaxTyped = 64;
    public const string LookupLabel = "kumbu/v1/ai-link-lookup";
    public const string KeyLabel = "kumbu/v1/ai-link-key";
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    public const int BaseRequests = 50;
    public const int RequestsPerImage = 5;

    /// <summary>As <see cref="ShareCode.New"/>: the generation is the same, the derivations are not.</summary>
    public static string New() => ShareCode.New();

    /// <summary>The normalised token, or null if it isn't one.</summary>
    public static string? Parse(string typed)
    {
        if (typed.Length > MaxTyped)
        {
            return null;
        }

        var normal = Pairings.Normalise(typed);
        return normal.Length == Length && normal.All(c => Pairings.Alphabet.Contains(c)) ? normal : null;
    }

    public static byte[] Lookup(string normalised) => VaultCrypto.Derive(normalised, LookupLabel, []);

    public static byte[] Key(string normalised) => VaultCrypto.Derive(normalised, KeyLabel, []);

    /// <summary>How many requests the link answers: the Markdown and each image a few times over.</summary>
    public static int Ceiling(int files) => BaseRequests + RequestsPerImage * files;
}

/// <summary>
/// An AI link (collection <c>ai_links</c>, <see cref="Vaults.VaultCatalog.AiLinks"/>). In the clear only what MongoDB
/// needs: the vault (for deletion, never for storage figures), the publication it opens (for withdrawal), the lookup id
/// derived from the token, the expiry and the request count. The slot holds the publication key and the owner's id
/// under the token's key; nothing names the owner, the note or the share code.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class AiLinkDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement("vault")]
    public string Vault { get; set; } = "";

    [BsonElement("publication")]
    public ObjectId Publication { get; set; }

    [BsonElement("lookup")]
    public byte[] Lookup { get; set; } = [];

    [BsonElement("slot")]
    public byte[] Slot { get; set; } = [];

    /// <summary>Checked in code on every request; the TTL index only tidies up.</summary>
    [BsonElement("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    [BsonElement("count")]
    public int Count { get; set; }
}

/// <summary>What an AI link's slot holds, as the reader slot holds it: the publication key and the owner's id.</summary>
internal sealed record AiLinkSlot(byte[] Key, string Owner);
