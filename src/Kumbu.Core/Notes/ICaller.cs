// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

/// <summary>Who is asking: the label their writes carry, and the vaults they may write to.</summary>
public interface ICaller
{
    string Writer { get; }

    bool CanWrite(string vault);

    /// <summary>The refusal for a write by <paramref name="command"/> to <paramref name="path"/>, or null if allowed.
    /// Asked once per entry point, before the store is touched.</summary>
    ShellResult? Refuse(string command, VaultPath path) =>
        path.Vault is { } vault && !CanWrite(vault)
            ? ShellResult.Error($"{command}: this key can only read vault {vault}")
            : null;
}

/// <summary>A caller with a fixed label; may write everywhere unless <paramref name="CanWriteVault"/> says otherwise.</summary>
public sealed record FixedCaller(string Writer, Func<string, bool>? CanWriteVault = null) : ICaller
{
    public bool CanWrite(string vault) => CanWriteVault?.Invoke(vault) ?? true;
}
