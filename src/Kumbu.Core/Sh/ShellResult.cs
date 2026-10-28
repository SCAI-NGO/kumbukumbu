// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Sh;

/// <summary>The version token of a note a command read or wrote; sent next to the output, never in it.</summary>
public sealed record NoteVersion(string Path, string Token);

/// <summary>Why a command failed, for callers that answer with a status; a message is never parsed for it.</summary>
public enum ShellErrorKind
{
    /// <summary>The input is wrong (a name, a date, a missing reason).</summary>
    Invalid,

    /// <summary>The caller's credential has no right to do it.</summary>
    Refused,

    /// <summary>What it names (a vault, a board, a card) is not there.</summary>
    NotFound,

    /// <summary>The state of the vault stands in the way (a definition problem, a name taken, changes that kept racing).</summary>
    Conflict,
}

/// <summary>Outcome of a shell command; commands never throw for user input.</summary>
public sealed record ShellResult(string Output, bool IsError)
{
    public NoteVersion? Version { get; init; }

    /// <summary>Why an error happened, where the command says; null otherwise.</summary>
    public ShellErrorKind? Kind { get; init; }

    /// <summary>
    /// A line for an empty result, shown only when the command stands alone: right of a pipe or on the left of one,
    /// the next command reads the output, and a hint would be taken for data.
    /// </summary>
    public string? NoMatchHint { get; init; }

    /// <summary>
    /// Text and images in the order the model should see them; then <see cref="Output"/> is the parts' text joined
    /// (for logs), and <see cref="OutputLimit"/> leaves the result alone.
    /// </summary>
    public IReadOnlyList<ShellPart>? Parts { get; init; }

    public static ShellResult Ok(string output) => new(output, false);

    public static ShellResult Error(string message) => new(message, true);
}
