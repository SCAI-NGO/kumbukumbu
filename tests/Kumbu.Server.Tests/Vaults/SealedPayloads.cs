// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json.Nodes;
using Kumbu.Core.Crypto;
using Kumbu.Server.Vaults;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Vaults;

/// <summary>The sealed payloads of a vault's documents as JSON, opened with the vault key, to see what is stored.</summary>
public static class SealedPayloads
{
    /// <summary>The collections whose payloads carry a writer.</summary>
    public static readonly string[] WithWriter = ["notes", "trash", "moves", "attachments", "attachment_trash", "checkpoints"];

    /// <summary>The vault key of <paramref name="vaultId"/> from the dev token's slot.</summary>
    public static async Task<byte[]> KeyOfTokenVaultAsync(IMongoDatabase db, string vaultId, string token, CancellationToken ct)
    {
        var slot = await db.GetCollection<SlotDocument>("slots")
            .Find(s => s.Vault == vaultId && s.Kind == SlotDocument.ApiKey).FirstAsync(ct);
        return VaultCrypto.Open(VaultCrypto.Derive(token, "kumbu/v1/key-kek", Encoding.UTF8.GetBytes(vaultId)), slot.Wrapped, $"slot:{vaultId}:{slot.Kind}");
    }

    /// <summary>The vault key of a user's vault from its user slot.</summary>
    public static async Task<byte[]> KeyOfUserVaultAsync(IMongoDatabase db, string vaultId, byte[] userKey, CancellationToken ct)
    {
        var slot = await db.GetCollection<SlotDocument>("slots")
            .Find(s => s.Vault == vaultId && s.Kind == SlotDocument.User).FirstAsync(ct);
        return VaultCrypto.Open(VaultCrypto.Derive(userKey, "kumbu/v1/user-slot-kek", Encoding.UTF8.GetBytes(vaultId)), slot.Wrapped, $"slot:{vaultId}:{slot.Kind}");
    }

    /// <summary>Every payload of <paramref name="collection"/> in the vault.</summary>
    public static async Task<IReadOnlyList<JsonObject>> OpenAllAsync(IMongoDatabase db, string vaultId, byte[] vaultKey, string collection, CancellationToken ct)
    {
        var docs = await db.GetCollection<SealedDocument>(collection).Find(d => d.Vault == vaultId).ToListAsync(ct);
        return [.. docs.Select(d => Open(vaultId, vaultKey, collection, d))];
    }

    public static JsonObject Open(string vaultId, byte[] vaultKey, string collection, SealedDocument doc) =>
        JsonNode.Parse(VaultCrypto.Open(ContentKey(vaultKey), doc.Blob, Context(vaultId, collection, doc)))!.AsObject();

    /// <summary>Seals <paramref name="payload"/> as <paramref name="collection"/>'s document <paramref name="doc"/>
    /// holds it, for payloads in shapes the server no longer writes.</summary>
    public static byte[] Seal(string vaultId, byte[] vaultKey, string collection, SealedDocument doc, JsonObject payload) =>
        VaultCrypto.Seal(ContentKey(vaultKey), Encoding.UTF8.GetBytes(payload.ToJsonString()), Context(vaultId, collection, doc));

    private static byte[] ContentKey(byte[] vaultKey) => VaultCrypto.Derive(vaultKey, "kumbu/v1/content", []);

    private static string Context(string vaultId, string collection, SealedDocument doc) => collection switch
    {
        "notes" => $"note:{vaultId}:{doc.Id}:{doc.Revision}",
        "trash" => $"trash:{vaultId}:{doc.Id}",
        "moves" => $"move:{vaultId}:{doc.Id}",
        "attachments" => $"attachment:{vaultId}:{doc.Id}:{doc.Revision}",
        "attachment_trash" => $"attachtrash:{vaultId}:{doc.Id}",
        "checkpoints" => $"checkpoint:{vaultId}:{doc.Id}",
        _ => throw new ArgumentException(collection, nameof(collection)),
    };
}
