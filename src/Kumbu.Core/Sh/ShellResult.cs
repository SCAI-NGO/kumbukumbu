// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Sh;

/// <summary>Outcome of a shell command; commands never throw for user input.</summary>
public sealed record ShellResult(string Output, bool IsError)
{
    public static ShellResult Ok(string output) => new(output, false);

    public static ShellResult Error(string message) => new(message, true);
}
