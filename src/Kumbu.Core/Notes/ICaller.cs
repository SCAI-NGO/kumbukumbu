// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Notes;

/// <summary>Who is asking: the label their writes carry, and what they may do in each vault.</summary>
public interface ICaller
{
    string Writer { get; }

    /// <summary>The reference sealed beside <see cref="Writer"/> in every entry the caller writes (collab design §8.1).</summary>
    WriterRef Reference { get; }

    /// <summary>How long the caller's changeset stays open without a write (concept §4): an agent's ten minutes, or
    /// two for a person, whose editor saves every few seconds.</summary>
    TimeSpan ChangesetIdle => Changeset.Idle;

    /// <summary>The caller's level on <paramref name="vault"/> (named as the store names it), or null if it has none
    /// there: fails closed.</summary>
    AccessLevel? Level(string vault);

    bool CanWrite(string vault) => Level(vault) >= AccessLevel.Write;

    /// <summary>The refusal for a write by <paramref name="command"/> to <paramref name="path"/>, or null if allowed.
    /// Asked once per entry point, before the store is touched: first whether the caller may write at all, then
    /// whether it may change the vault's structure (<see cref="StructuralFiles"/>).</summary>
    ShellResult? Refuse(string command, VaultPath path)
    {
        if (path.Vault is not { } vault) return null;
        if (!CanWrite(vault)) return Refusals.Of(command, Refusals.ReadOnly, $"this key can only read vault {vault}");
        if (Level(vault) >= AccessLevel.Maintain) return null;
        if (StructuralFiles.StructuralCommands.Contains(command) || StructuralFiles.IsStructural(path))
        {
            return Refusals.StructuralChange(command, vault);
        }

        return null;
    }

    /// <summary><see cref="Refuse"/> for a move or removal of the folder <paramref name="folder"/>, which takes the
    /// structural files below it along.</summary>
    ShellResult? RefuseFolder(string command, VaultPath folder) =>
        Refuse(command, folder)
        ?? (folder.Vault is { } vault && StructuralFiles.ContainsStructural(folder) && Level(vault) < AccessLevel.Maintain
            ? Refusals.StructuralChange(command, vault)
            : null);
}

/// <summary>A caller with a fixed label; owner of every vault unless <paramref name="LevelOf"/> says otherwise, or
/// <paramref name="CanWriteVault"/> (write where it says so, else read). <paramref name="Human"/>: a person rather
/// than an agent, for <see cref="ICaller.ChangesetIdle"/>. Its <see cref="Reference"/> is an owner's agent's unless set.</summary>
public sealed record FixedCaller(string Writer, Func<string, bool>? CanWriteVault = null, bool Human = false, Func<string, AccessLevel?>? LevelOf = null) : ICaller
{
    public WriterRef Reference { get; init; } = WriterRef.ForOwner(WriterRef.Agent, null);

    public AccessLevel? Level(string vault) =>
        LevelOf is not null ? LevelOf(vault)
        : CanWriteVault is not null ? (CanWriteVault(vault) ? AccessLevel.Write : AccessLevel.Read)
        : AccessLevel.Owner;

    public TimeSpan ChangesetIdle => Human ? Changeset.HumanIdle : Changeset.Idle;
}
