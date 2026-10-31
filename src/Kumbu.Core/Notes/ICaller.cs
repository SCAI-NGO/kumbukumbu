// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

/// <summary>Who is asking: the label their writes carry, and what they may do in each vault.</summary>
public interface ICaller
{
    string Writer { get; }

    /// <summary>How long the caller's changeset stays open without a write (concept §4): an agent's ten minutes, or
    /// two for a person, whose editor saves every few seconds.</summary>
    TimeSpan ChangesetIdle => Changeset.Idle;

    /// <summary>The caller's level on <paramref name="vault"/> (named as the store names it), or null if it has none
    /// there: fails closed.</summary>
    AccessLevel? Level(string vault);

    bool CanWrite(string vault) => Level(vault) >= AccessLevel.Write;

    /// <summary>The refusal for a write by <paramref name="command"/> to <paramref name="path"/>, or null if allowed.
    /// Asked once per entry point, before the store is touched.</summary>
    ShellResult? Refuse(string command, VaultPath path) =>
        path.Vault is { } vault && !CanWrite(vault)
            ? ShellResult.Error($"{command}: this key can only read vault {vault}")
            : null;
}

/// <summary>A caller with a fixed label; owner of every vault unless <paramref name="LevelOf"/> says otherwise, or
/// <paramref name="CanWriteVault"/> (write where it says so, else read). <paramref name="Human"/>: a person rather
/// than an agent, for <see cref="ICaller.ChangesetIdle"/>.</summary>
public sealed record FixedCaller(string Writer, Func<string, bool>? CanWriteVault = null, bool Human = false, Func<string, AccessLevel?>? LevelOf = null) : ICaller
{
    public AccessLevel? Level(string vault) =>
        LevelOf is not null ? LevelOf(vault)
        : CanWriteVault is not null ? (CanWriteVault(vault) ? AccessLevel.Write : AccessLevel.Read)
        : AccessLevel.Owner;

    public TimeSpan ChangesetIdle => Human ? Changeset.HumanIdle : Changeset.Idle;
}
