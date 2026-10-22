// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Sh;

public interface ICommand
{
    string Name { get; }

    /// <summary>How to call it, e.g. "head [-n N] [path]".</summary>
    string Usage { get; }

    /// <summary>What it does, in a few words, for the command list of <c>help</c>.</summary>
    string Summary { get; }

    /// <summary>The text of <c>help &lt;command&gt;</c>.</summary>
    string Help { get; }

    /// <summary>Whether the command may stand right of a pipe (a filter).</summary>
    bool ReadsInput { get; }

    /// <param name="input">The previous stage's output, or null if the command starts the pipeline.</param>
    Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct);
}
