// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

internal static class CommandArgs
{
    /// <summary>Returns an error for the first option-like argument, or null if there is none.</summary>
    public static ShellResult? CheckOptions(string command, IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (arg.Length > 1 && arg[0] == '-')
            {
                return ShellResult.Error($"{command}: invalid option -- '{arg[1]}'");
            }
        }

        return null;
    }

    public static bool TryParsePath(string command, string arg, out VaultPath path, out ShellResult? error)
    {
        if (VaultPath.TryParse(arg, out var parsed))
        {
            path = parsed;
            error = null;
            return true;
        }

        path = VaultPath.Root;
        error = ShellResult.Error($"{command}: {arg}: paths must be absolute (there is no working directory)");
        return false;
    }
}
