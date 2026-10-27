// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

/// <summary>Who is asking: the label their writes carry, and the vaults they may write to.</summary>
public interface ICaller
{
    string Writer { get; }

    /// <summary>How long the caller's changeset stays open without a write (concept §4): an agent's ten minutes, or
    /// two for a person, whose editor saves every few seconds.</summary>
    TimeSpan ChangesetIdle => Changeset.Idle;

    bool CanWrite(string vault);

    /// <summary>The refusal for a write by <paramref name="command"/> to <paramref name="path"/>, or null if allowed.
    /// Asked once per entry point, before the store is touched.</summary>
    ShellResult? Refuse(string command, VaultPath path) =>
        path.Vault is { } vault && !CanWrite(vault)
            ? ShellResult.Error($"{command}: this key can only read vault {vault}")
            : null;
}

/// <summary>A caller with a fixed label; may write everywhere unless <paramref name="CanWriteVault"/> says otherwise.
/// <paramref name="Human"/>: a person rather than an agent, for <see cref="ICaller.ChangesetIdle"/>.</summary>
public sealed record FixedCaller(string Writer, Func<string, bool>? CanWriteVault = null, bool Human = false) : ICaller
{
    public bool CanWrite(string vault) => CanWriteVault?.Invoke(vault) ?? true;

    public TimeSpan ChangesetIdle => Human ? Changeset.HumanIdle : Changeset.Idle;
}
