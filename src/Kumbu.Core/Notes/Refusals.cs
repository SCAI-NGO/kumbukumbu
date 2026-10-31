// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

/// <summary>The machine-readable codes of a refusal (collab design §3.5), sent beside the sentence; clients branch
/// on these, never on the text.</summary>
public static class Refusals
{
    /// <summary>The caller's level is below <see cref="AccessLevel.Write"/>.</summary>
    public const string ReadOnly = "read-only";

    /// <summary>The change touches the vault's structure, and the caller's level is below <see cref="AccessLevel.Maintain"/>.</summary>
    public const string Structural = "structural";

    /// <summary>The owner is deleting the account; the vault only reads until it goes.</summary>
    public const string Closing = "closing";

    /// <summary>The owner's account is deactivated.</summary>
    public const string OwnerInactive = "owner-inactive";

    /// <summary>A refusal with its code. The text starts with the command's first word, as every error of it does
    /// (<c>board rename_column</c> answers as <c>board:</c>).</summary>
    public static ShellResult Of(string command, string code, string sentence) =>
        ShellResult.Error($"{command.Split(' ')[0]}: {sentence}") with { Code = code };

    /// <summary>The refusal of a structural change in <paramref name="vault"/>, saying who can make it.</summary>
    public static ShellResult StructuralChange(string command, string vault) => Of(command, Structural, command is "mkcp" or "rmcp"
        ? $"Only those who may maintain *{vault}* can make or remove its checkpoints."
        : $"Only those who may maintain *{vault}* can change its board's columns.");
}
