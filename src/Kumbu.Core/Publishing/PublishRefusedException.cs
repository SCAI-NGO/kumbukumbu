// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;

namespace Kumbu.Core.Publishing;

/// <summary>
/// Publishing, updating or withdrawing a note was refused, nothing was written. <see cref="Code"/> is a
/// <see cref="Notes.Refusals"/> code for <see cref="ShellErrorKind.Refused"/>, null otherwise (NotFound: no such note,
/// or not published; Conflict: already published, or the vault's publications kept changing).
/// </summary>
public sealed class PublishRefusedException(string sentence, ShellErrorKind kind, string? code = null) : Exception(sentence)
{
    public ShellErrorKind Kind { get; } = kind;

    public string? Code { get; } = code;
}
