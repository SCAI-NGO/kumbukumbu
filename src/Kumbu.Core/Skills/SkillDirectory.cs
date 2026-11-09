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

    /// <summary>
    /// Who wrote the skill: the label of the last writer of any of its files, and whether another user (in a shared vault,
    /// by the writer reference, as <see cref="WriterMarks"/> decides it) last wrote one of them. A skill is all its files:
    /// <c>SKILL.md</c> may send the agent to a reference or a script that someone else rewrote.
    /// </summary>
    private async Task<(string? Editor, bool ByOther)> WritersAsync(
        string vault, VaultInfo? info, VaultPath folder, IReadOnlyList<VaultPath> notePaths, IReadOnlyList<StoredAttachment> attachments, CancellationToken ct)
    {
        var prefix = folder.Value + "/";
        var writes = new List<(string Label, WriterRef? Ref, DateTime At)>();
        foreach (var path in notePaths.Where(p => p.Value.StartsWith(prefix, StringComparison.Ordinal)))
        {
            if (await notes.GetAsync(path, ct) is { } note) writes.Add((note.LastWriter, note.Ref, note.LastWriteAt));
        }

        writes.AddRange(attachments.Where(a => a.Path.Value.StartsWith(prefix, StringComparison.Ordinal)).Select(a => (a.LastWriter, a.Ref, a.LastWriteAt)));
        if (writes.Count == 0) return (null, false);
        var last = writes.MaxBy(w => w.At);
        var editor = (await labels.RenderAsync(vault, last.Label, last.Ref, ct)).Display;
        if (info?.Shared != true) return (editor, false);
        foreach (var write in writes)
        {
            if (!await labels.IsOwnAsync(vault, write.Ref, ct)) return (editor, true);
        }

        return (editor, false);
    }

    private static Skill System(SystemSkill skill)
    {
        var manifest = SkillManifest.Read(skill.Name, skill.Files.FirstOrDefault(f => f.Path == SkillPaths.Manifest).Content);
        return new Skill(skill.Name, null, manifest.Description ?? "", null, null, false, manifest.Problems);
    }

    private async Task<IReadOnlyList<Skill>> InAsync(string vault, string? marked, CancellationToken ct)
    {
        var location = vault == marked ? VaultPath.Root.Child(vault) : VaultPath.Root.Child(vault).Child(SkillPaths.LocationFolder);
        var depth = location.Segments.Count;
        var notePaths = await notes.ListNotesUnderAsync(location, ct);
        var attachments = await StoreAttachments.UnderAsync(notes, location, ct);
        // Only what lies inside a subfolder makes one a skill; a file loose at the location's root is none.
        // In the vault Skills the location is the vault's root, so its archive would show as a skill.
        var folders = notePaths.Concat(attachments.Select(a => a.Path)).Where(p => p.Segments.Count > depth + 1)
            .Where(p => vault != marked || !ArchiveFolder.Contains(p))
            .Select(p => p.Segments[depth]).Distinct(StringComparer.Ordinal).Order(TextOrder.Default);

        var info = await notes.VaultInfoAsync(vault, ct);
        var skills = new List<Skill>();
        foreach (var name in folders)
        {
            var folder = location.Child(name);
            var note = await notes.GetAsync(folder.Child(SkillPaths.Manifest), ct);
            var manifest = SkillManifest.Read(name, note?.Content);
            var (editor, byOther) = await WritersAsync(vault, info, folder, notePaths, attachments, ct);
            // The manifest's name counts only when it keeps every rule, being the folder's name among them: so the folder's
            // name is the skill's, and a manifest can't name its skill after another one.
            skills.Add(new Skill(name, folder, manifest.Description ?? "", manifest.Source, editor, byOther, manifest.Problems) { SharedBy = info?.Owner });
        }

        return skills;
    }
}
