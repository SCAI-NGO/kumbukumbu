// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;

namespace Kumbu.Core.Skills;

/// <summary>The skill folders the last check in the Skills screen found an update for; read only, nothing is fetched.</summary>
public interface ISkillUpdates
{
    Task<IReadOnlySet<VaultPath>> AvailableAsync(CancellationToken ct);
}

/// <summary>No update known: a shell outside the server, or before any check.</summary>
public sealed class NoSkillUpdates : ISkillUpdates
{
    public static readonly NoSkillUpdates Instance = new();

    private static readonly IReadOnlySet<VaultPath> None = new HashSet<VaultPath>();

    public Task<IReadOnlySet<VaultPath>> AvailableAsync(CancellationToken ct) => Task.FromResult(None);
}
