// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Skills;

/// <summary>What the server knows of the copies' origins, without fetching: the folders the last check in the Skills
/// screen found an update for, and those whose frontmatter disagrees with the sealed origin (Decisions 19, 22, 33).</summary>
public interface ISkillUpdates
{
    Task<SkillMarks> MarksAsync(CancellationToken ct);
}

/// <summary>The folders with an update known, and those whose origin was edited; an edited one offers no update.</summary>
public sealed record SkillMarks(IReadOnlySet<VaultPath> Available, IReadOnlySet<VaultPath> OriginEdited)
{
    public static readonly SkillMarks None = new(new HashSet<VaultPath>(), new HashSet<VaultPath>());

    /// <summary>The skill with <see cref="Skill.OriginEdited"/> set from these marks.</summary>
    public Skill Apply(Skill skill) => skill.Folder is { } folder && OriginEdited.Contains(folder) ? skill with { OriginEdited = true } : skill;

    public bool HasUpdate(Skill skill) => skill.Folder is { } folder && Available.Contains(folder) && !OriginEdited.Contains(folder);
}

/// <summary>Nothing known: a shell outside the server.</summary>
public sealed class NoSkillUpdates : ISkillUpdates
{
    public static readonly NoSkillUpdates Instance = new();

    public Task<SkillMarks> MarksAsync(CancellationToken ct) => Task.FromResult(SkillMarks.None);
}
