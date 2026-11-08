// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Skills;
using Kumbu.Server.Publishing;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Skills;

/// <summary>
/// What the vaults' records say of the copies, for <c>skills ls</c> and <c>skills find</c> (Decisions 19, 22, 33): the folders
/// the last check in the Skills screen found an update for (<see cref="SkillUpdates"/>), and the copies whose
/// <c>SKILL.md</c> now disagrees with the sealed origin, in the vaults this credential opens, in its view. Reads only;
/// nothing is fetched.
/// </summary>
internal sealed class RecordedSkillUpdates(EncryptedNoteStore store, PublicationStore publishing) : ISkillUpdates
{
    public async Task<SkillMarks> MarksAsync(CancellationToken ct)
    {
        var available = new HashSet<VaultPath>();
        var edited = new HashSet<VaultPath>();
        var counts = store.CountsUse;
        store.CountsUse = false;
        try
        {
            foreach (var access in await store.UnlockedVaultsAsync(ct))
            {
                var record = (await publishing.ReadAsync(null, access.Vault, ct)).Record;
                foreach (var origin in record.Origins)
                {
                    if (!VaultPath.TryParse(origin.Path, out var stored)) continue;
                    var folder = access.ToCaller(stored);
                    if (SkillUpdates.IsOriginEdited(folder, await store.ReadAsync(folder.Child(SkillPaths.Manifest), ct), origin)) edited.Add(folder);
                }

                foreach (var folder in record.Updates)
                {
                    if (VaultPath.TryParse(folder, out var stored)) available.Add(access.ToCaller(stored));
                }
            }
        }
        finally
        {
            store.CountsUse = counts;
        }

        return new SkillMarks(available, edited);
    }
}
