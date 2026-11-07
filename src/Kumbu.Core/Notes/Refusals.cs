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

    /// <summary>Only the vault's owner publishes its notes; a member never does (publish design §3.5).</summary>
    public const string NotOwner = "not-owner";

    /// <summary>The owner's credential wasn't given "May publish" on the vault (publish design §3.8).</summary>
    public const string NoPublishRight = "no-publish-right";

    /// <summary>A mark covers the note: nobody publishes or updates it (publish design §3.6).</summary>
    public const string Unpublishable = "unpublishable";

    /// <summary>The server's switch, <c>Kumbu:Publish:Enabled</c>, is off (publish design §3.13).</summary>
    public const string PublishingOff = "publishing-off";

    /// <summary>The note is published, and this caller may not withdraw it (publish design §3.7).</summary>
    public const string Published = "published";

    /// <summary>The vault's owner's storage is full (WebDAV design §3.10); sent to the owner only, a member gets the
    /// sentence alone (<see cref="QuotaExceededException"/>).</summary>
    public const string Quota = "quota";

    /// <summary>A refusal with its code. The text starts with the command's first word, as every error of it does
    /// (<c>board rename_column</c> answers as <c>board:</c>).</summary>
    public static ShellResult Of(string command, string code, string sentence) =>
        ShellResult.Error($"{command.Split(' ')[0]}: {sentence}") with { Code = code };

    /// <summary>The refusal of a structural change in <paramref name="vault"/>, saying who can make it and, in
    /// <paramref name="limit"/> (<see cref="ICaller.StructuralLimit"/>), what holds this caller back.</summary>
    public static ShellResult StructuralChange(string command, string vault, string limit = "") => Of(command, Structural, command switch
    {
        "mkcp" or "rmcp" => $"Only those who may maintain *{vault}* can make or remove its checkpoints.",
        // By any other command it is the file, of which the columns are only a part.
        _ when command.Split(' ')[0] == "board" => $"Only those who may maintain *{vault}* can change its board's columns.",
        _ => $"Only those who may maintain *{vault}* can change its board's file, Board/Board.md.",
    } + (limit.Length == 0 ? "" : " " + limit));
}
