// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Server.Accounts;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Welcome;

/// <summary>
/// The vault "Welcome" a new account gets when it joins (demo round design §3): the notes embedded from
/// <c>Welcome/Notes/</c>, with <see cref="Placeholder"/> filled in, written as <see cref="Writers.Seed"/>. Only
/// <c>POST /api/join</c> calls it, so a user who deletes or renames the vault never gets it back.
/// </summary>
public sealed class WelcomeVault(VaultCatalog catalog, IOptions<KumbuOptions> options, TimeProvider time, ILogger<WelcomeVault> log, Search.SearchIndex? index = null)
{
    public const string Name = "Welcome";

    /// <summary>The only placeholder: the server's public address, without a trailing slash.</summary>
    public const string Placeholder = "{{server}}";

    /// <summary>What <see cref="Placeholder"/> becomes without <c>Kumbu:PublicUrl</c>; the texts point at the browser's address bar.</summary>
    public const string NoAddress = "https://<your Kumbu² address>";

    private const string ResourcePrefix = "Kumbu.Server.Welcome.Notes.";

    private static readonly Lazy<IReadOnlyList<(string Name, string Content)>> Embedded = new(Load);

    /// <summary>For tests: runs after the vault is created and before its notes are written.</summary>
    internal Func<string, Task>? AfterVaultCreated { get; set; }

    /// <summary>The embedded notes in text order: file name with <c>.md</c>, content with LF line ends.</summary>
    public static IReadOnlyList<(string Name, string Content)> Texts() => Embedded.Value;

    public static string Fill(string content, string? publicUrl) =>
        content.Replace(Placeholder, string.IsNullOrEmpty(publicUrl) ? NoAddress : publicUrl.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>Creates the vault and writes every note. Never throws, except when <paramref name="ct"/> is cancelled: it
    /// logs, and removes a vault whose notes were not all written.</summary>
    public async Task CreateAsync(User user, byte[] userKey, DateTime credentialReadAt, CancellationToken ct)
    {
        UnlockedVault? vault = null;
        try
        {
            vault = await catalog.CreateForUserAsync(Name, user.Id, userKey, ct, credentialReadAt);
            if (AfterVaultCreated is { } hook) await hook(vault.Id);
            var store = new EncryptedNoteStore(catalog, new UserCredential(user.Id, userKey), index);
            var now = time.GetUtcNow().UtcDateTime;
            var publicUrl = options.Value.PublicUrl;
            foreach (var (name, content) in Texts())
            {
                var filled = Fill(content, publicUrl);
                if (!await store.TryCreateAsync(new StoredNote(VaultPath.Root.Child(Name).Child(name), filled, 1, Writers.Seed, now, [], filled), ct))
                {
                    throw new InvalidOperationException("A welcome note exists already.");
                }
            }
        }
        catch (Exception e)
        {
            // Deliberately broad: the account exists by now, and an exception here would keep the recovery key from the user.
            // Only a cancellation the caller asked for goes on, and even then no half-written vault stays.
            var cancelled = e is OperationCanceledException && ct.IsCancellationRequested;
            // The type, not the message, which may name the vault.
            if (!cancelled) log.LogWarning("Could not create the welcome vault for user {UserId}: {Reason}", user.Id, e.GetType().Name);
            if (vault is not null) await RemoveAsync(vault.Id, user.Id);
            if (cancelled) throw;
        }
    }

    private async Task RemoveAsync(string vaultId, string userId)
    {
        try
        {
            await catalog.DeleteVaultsAsync([vaultId], (_, _) => Task.CompletedTask, CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogWarning("Could not remove the half-written welcome vault {VaultId} of user {UserId}: {Reason}", vaultId, userId, e.GetType().Name);
        }
    }

    private static IReadOnlyList<(string Name, string Content)> Load()
    {
        var assembly = typeof(WelcomeVault).Assembly;
        var texts = new List<(string Name, string Content)>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            texts.Add((resource[ResourcePrefix.Length..], reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal)));
        }

        return [.. texts.OrderBy(t => t.Name, TextOrder.Default)];
    }
}
