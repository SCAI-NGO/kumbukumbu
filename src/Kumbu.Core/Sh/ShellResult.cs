// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Sh;

/// <summary>The version token of a note a command read or wrote; sent next to the output, never in it.</summary>
public sealed record NoteVersion(string Path, string Token);

/// <summary>Outcome of a shell command; commands never throw for user input.</summary>
public sealed record ShellResult(string Output, bool IsError)
{
    public NoteVersion? Version { get; init; }

    public static ShellResult Ok(string output) => new(output, false);

    public static ShellResult Error(string message) => new(message, true);
}
