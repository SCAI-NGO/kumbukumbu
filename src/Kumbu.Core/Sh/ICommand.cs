// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Sh;

public interface ICommand
{
    string Name { get; }

    Task<ShellResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct);
}
