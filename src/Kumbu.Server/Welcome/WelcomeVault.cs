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

    /// <summary>How long creating the vault may hold up the join, whose answer carries the recovery key: past it, the
    /// creation counts as failed. Ten writes on a healthy MongoDB take well under a second.</summary>
    internal TimeSpan Limit { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long removing a half-written vault may take on top of <see cref="Limit"/>.</summary>
    internal TimeSpan CleanupLimit { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>For tests: runs after the vault is created and before its notes are written, with the bounded token.</summary>
    internal Func<string, CancellationToken, Task>? AfterVaultCreated { get; set; }

    /// <summary>The embedded notes in text order: file name with <c>.md</c>, content with LF line ends.</summary>
    public static IReadOnlyList<(string Name, string Content)> Texts() => Embedded.Value;

    public static string Fill(string content, string? publicUrl) =>
        content.Replace(Placeholder, string.IsNullOrEmpty(publicUrl) ? NoAddress : publicUrl.TrimEnd('/'), StringComparison.Ordinal);

    /// <summary>Creates the vault and writes every note, within <see cref="Limit"/>. Never throws, except when
    /// <paramref name="ct"/> is cancelled: it logs, and removes a vault whose notes were not all written.</summary>
    public async Task CreateAsync(User user, byte[] userKey, DateTime credentialReadAt, CancellationToken ct)
    {
        UnlockedVault? vault = null;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(Limit);
        var token = bounded.Token;
        try
        {
            vault = await catalog.CreateForUserAsync(Name, user.Id, userKey, token, credentialReadAt);
            if (AfterVaultCreated is { } hook) await hook(vault.Id, token);
            var store = new EncryptedNoteStore(catalog, new UserCredential(user.Id, userKey), index);
            var now = time.GetUtcNow().UtcDateTime;
            var publicUrl = options.Value.PublicUrl;
            foreach (var (name, content) in Texts())
            {
                var filled = Fill(content, publicUrl);
                if (!await store.TryCreateAsync(new StoredNote(VaultPath.Root.Child(Name).Child(name), filled, 1, Writers.Seed, now, [], filled), token))
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
            // The type, not the message, which may name the vault; a time-out reads as the cancellation it causes.
            if (!cancelled) log.LogWarning("Could not create the welcome vault for user {UserId}: {Reason}", user.Id, e.GetType().Name);
            if (vault is not null) await RemoveAsync(vault.Id, user.Id);
            if (cancelled) throw;
        }
    }

    private async Task RemoveAsync(string vaultId, string userId)
    {
        try
        {
            using var limit = new CancellationTokenSource(CleanupLimit);
            await catalog.DeleteVaultsAsync([vaultId], (_, _) => Task.CompletedTask, limit.Token);
        }
        catch (Exception e)
        {
            // Left over, it is an empty or partial vault "Welcome" the user can delete; the join must not wait longer.
            log.LogWarning("Could not remove the half-written welcome vault of user {UserId}: {Reason}", userId, e.GetType().Name);
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
