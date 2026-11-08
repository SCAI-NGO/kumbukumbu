// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Skills;

/// <summary>
/// Every skill the caller can see: the system skills, the direct subfolders of her vault Skills (the marked one, never a
/// vault that is only named so) and of <c>_skills</c> at the root of each vault she reaches. A folder is a skill once it
/// holds a note or a file; a missing or broken <c>SKILL.md</c> is a warning, never a reason to leave it out or fail.
/// </summary>
public sealed class SkillDirectory(INoteStore notes, IWriterLabels labels)
{
    private readonly WriterMarks _marks = new(notes, labels);

    /// <summary>The system skills, then the vault Skills, then the other vaults in <see cref="TextOrder"/>, by folder
    /// name within each; with <paramref name="vault"/>, only that vault's (nothing for one the caller doesn't reach).</summary>
    public async Task<IReadOnlyList<Skill>> ListAsync(string? vault, CancellationToken ct)
    {
        var vaults = await notes.ListVaultsAsync(ct);
        var marked = await notes.SkillsVaultAsync(ct) is { } m && vaults.Contains(m, StringComparer.Ordinal) ? m : null;
        var skills = new List<Skill>();
        if (vault is null)
        {
            skills.AddRange(SystemSkills.All.Select(System));
            if (marked is not null) skills.AddRange(await InAsync(marked, marked, ct));
            foreach (var other in vaults.Where(v => v != marked).Order(TextOrder.Default)) skills.AddRange(await InAsync(other, marked, ct));
        }
        else if (vaults.Contains(vault, StringComparer.Ordinal))
        {
            skills.AddRange(await InAsync(vault, marked, ct));
        }

        return skills;
    }

    /// <summary>The skill's files relative to its folder, <c>SKILL.md</c> first, then in <see cref="TextOrder"/>.</summary>
    public async Task<IReadOnlyList<SkillFile>> FilesAsync(Skill skill, CancellationToken ct)
    {
        IEnumerable<SkillFile> files;
        if (skill.Folder is not { } folder)
        {
            files = SystemSkills.Find(skill.Name)?.Files.Select(f => new SkillFile(f.Path, f.Path.EndsWith(".md", StringComparison.Ordinal))) ?? [];
        }
        else
        {
            var prefix = folder.Value + "/";
            files = (await notes.ListNotesUnderAsync(folder, ct)).Select(p => new SkillFile(p.Value[prefix.Length..], true))
                .Concat((await StoreAttachments.UnderAsync(notes, folder, ct)).Select(a => new SkillFile(a.Path.Value[prefix.Length..], false)));
        }

        return [.. files.OrderBy(f => f.Path != SkillPaths.Manifest).ThenBy(f => f.Path, TextOrder.Default)];
    }

    /// <summary>The text of the skill's <c>SKILL.md</c>; null when it has none.</summary>
    public async Task<string?> ManifestAsync(Skill skill, CancellationToken ct) => skill.Folder is { } folder
        ? await notes.ReadAsync(folder.Child(SkillPaths.Manifest), ct)
        : SystemSkills.Find(skill.Name)?.Files.FirstOrDefault(f => f.Path == SkillPaths.Manifest).Content;

    private static Skill System(SystemSkill skill)
    {
        var manifest = SkillManifest.Read(skill.Name, skill.Files.FirstOrDefault(f => f.Path == SkillPaths.Manifest).Content);
        return new Skill(skill.Name, null, manifest.Description ?? "", null, null, false, manifest.Problems);
    }

    private async Task<IReadOnlyList<Skill>> InAsync(string vault, string? marked, CancellationToken ct)
    {
        var location = vault == marked ? VaultPath.Root.Child(vault) : VaultPath.Root.Child(vault).Child(SkillPaths.LocationFolder);
        var depth = location.Segments.Count;
        var paths = (await notes.ListNotesUnderAsync(location, ct)).Concat((await StoreAttachments.UnderAsync(notes, location, ct)).Select(a => a.Path));
        // Only what lies inside a subfolder makes one a skill; a file loose at the location's root is none.
        var folders = paths.Where(p => p.Segments.Count > depth + 1).Select(p => p.Segments[depth]).Distinct(StringComparer.Ordinal).Order(TextOrder.Default);

        var sharedBy = (await notes.VaultInfoAsync(vault, ct))?.Owner;
        var skills = new List<Skill>();
        foreach (var name in folders)
        {
            var folder = location.Child(name);
            var path = folder.Child(SkillPaths.Manifest);
            var note = await notes.GetAsync(path, ct);
            var manifest = SkillManifest.Read(name, note?.Content);
            var editor = note is null ? null : (await labels.RenderAsync(vault, note.LastWriter, note.Ref, ct)).Display;
            var byOther = note is not null && (await _marks.ByOthersAsync([path], ct)).Count > 0;
            // The manifest's name counts only when it keeps every rule, being the folder's name among them: so the folder's
            // name is the skill's, and a manifest can't name its skill after another one.
            skills.Add(new Skill(name, folder, manifest.Description ?? "", manifest.Source, editor, byOther, manifest.Problems) { SharedBy = sharedBy });
        }

        return skills;
    }
}
