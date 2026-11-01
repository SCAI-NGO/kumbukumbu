// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Server.Api;

/// <summary>
/// The rule of a vault name, for an own vault's and for a member's alias alike: both are vault names to their user and
/// share one name index (collab design §2.3), so they must never follow two rules.
/// </summary>
public static class VaultNames
{
    public const int MaxLength = 64;

    /// <summary><paramref name="requested"/> in NFC and trimmed, if it can be a vault name; else a 400.</summary>
    public static string Check(string? requested)
    {
        var name = (requested ?? "").Normalize(NormalizationForm.FormC).Trim();
        if (name.Length is 0 or > MaxLength) throw ApiErrors.BadRequest($"A vault name has 1 to {MaxLength} characters.");
        if (name.Contains('/')) throw ApiErrors.BadRequest("A vault name can't contain '/'.");
        // A vault is the top folder of its notes, so its name follows the rules of every other path segment.
        if (NoteNames.Problem(VaultPath.Root.Child(name).Child("note.md")) is { } problem) throw ApiErrors.BadRequest($"That can't be a vault name: {problem}.");
        return name;
    }
}
