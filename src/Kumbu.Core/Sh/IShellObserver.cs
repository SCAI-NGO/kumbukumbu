// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Sh;

/// <summary>What the shell ran, for metrics; never given arguments.</summary>
public interface IShellObserver
{
    /// <summary>A parsed command line: each stage's command name as typed, in order.</summary>
    void Ran(IReadOnlyList<string> commands);

    /// <summary>One stage that ran (a known command), and how long it took.</summary>
    void Timed(string command, TimeSpan elapsed);
}
