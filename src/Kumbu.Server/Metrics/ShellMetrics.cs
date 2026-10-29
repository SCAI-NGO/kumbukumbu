// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;

namespace Kumbu.Server.Metrics;

/// <summary>Counts the shell's commands; sees names only, and anything unknown becomes "other".</summary>
public sealed class ShellMetrics : IShellObserver
{
    public void Ran(IReadOnlyList<string> commands) => KumbuMetrics.ShellRan(commands);

    public void Timed(string command, TimeSpan elapsed) => KumbuMetrics.ShellTimed(command, elapsed);
}
