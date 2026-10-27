// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using Kumbu.Core.Crypto;
using Kumbu.Core.Notes;

namespace Kumbu.Server.Accounts;

/// <summary>
/// The names of API keys and connections: both are writer labels in the history, and a user's names are unique
/// across both (Ruling 9 of the OAuth plan), so both are checked and hashed alike.
/// </summary>
internal static class WriterNames
{
    public const int MaxLength = 64;

    /// <summary>Unchanged since API keys came first, so existing keys keep their name ids.</summary>
    private const string NameIdLabel = "kumbu/v1/api-key-name-id";

    /// <summary>
    /// The name, trimmed. Its writes carry it as their writer label, so it must stay apart from the other labels:
    /// not the user's own username (their writes in the web app) nor <c>dev</c> or <c>seed</c>, and without
    /// <c>" ("</c>, which the history puts between a writer and the tool ("laptop (mv)"). <paramref name="noun"/>
    /// starts the messages: "An API key", "A connection".
    /// </summary>
    public static string Check(User user, string name, string noun)
    {
        var clean = name.Normalize(NormalizationForm.FormC).Trim();
        if (clean.Length is 0 or > MaxLength) throw new ArgumentException($"{noun}'s name has 1 to {MaxLength} characters.", nameof(name));
        // Format characters (zero-width, bidi overrides) are invisible too: "chris​" would show as "chris".
        if (clean.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
        {
            throw new ArgumentException($"{noun}'s name can't contain control or invisible formatting characters.", nameof(name));
        }
        if (clean.Contains(" (", StringComparison.Ordinal)) throw new ArgumentException($"{noun}'s name can't contain \" (\".", nameof(name));
        if (string.Equals(clean, user.Username, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{noun} can't be named like you: its writes would look like your own.", nameof(name));
        }

        if (clean.Equals(Writers.Dev, StringComparison.OrdinalIgnoreCase) || clean.Equals(Writers.Seed, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{clean}' is reserved for the server's own writes.", nameof(name));
        }

        return clean;
    }

    /// <summary>A keyed hash of the lower-cased name, so MongoDB keeps a user's names unique without seeing one.</summary>
    public static byte[] NameId(byte[] userKey, string userId, string name) =>
        VaultCrypto.Mac(VaultCrypto.Derive(userKey, NameIdLabel, Encoding.UTF8.GetBytes(userId)), name.ToLowerInvariant());
}
