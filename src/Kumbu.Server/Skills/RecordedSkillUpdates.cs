// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Skills;
using Kumbu.Server.Publishing;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Skills;

/// <summary>
/// What the last check in the Skills screen found (<see cref="SkillUpdates"/>), for <c>skills ls</c> (Decision 22): the
/// folders with an update in the records of the vaults this credential opens, in its view. Reads only; nothing is fetched.
/// </summary>
internal sealed class RecordedSkillUpdates(EncryptedNoteStore store, PublicationStore publishing) : ISkillUpdates
{
    public async Task<IReadOnlySet<VaultPath>> AvailableAsync(CancellationToken ct)
    {
        var folders = new HashSet<VaultPath>();
        foreach (var access in await store.UnlockedVaultsAsync(ct))
        {
            foreach (var folder in (await publishing.ReadAsync(null, access.Vault, ct)).Record.Updates)
            {
                if (VaultPath.TryParse(folder, out var stored)) folders.Add(access.ToCaller(stored));
            }
        }

        return folders;
    }
}
